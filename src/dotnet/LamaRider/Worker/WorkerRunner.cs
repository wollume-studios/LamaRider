using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LamaRider.Model;

namespace LamaRider.Worker;

public sealed class WorkerException(string message) : Exception(message);

/// <summary>
/// Builds LamaRider.Worker against the solution's Metalama version (cached per plugin + Metalama version)
/// and runs it out of process. The worker stays alive between runs (serve mode) and only reloads the workspace.
/// No JetBrains dependencies: unit-tested outside the Rider SDK.
/// </summary>
public sealed class WorkerRunner(string workerSourceDir, string cacheRoot) : IDisposable
{
    const string DoneMarker = "LAMARIDER-DONE", ErrorMarker = "LAMARIDER-ERROR"; // LamaRider.Worker Program
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    readonly object _logLock = new();

    // The serving worker; runs are sequential (one at a time per solution).
    Process? _server;
    string? _serverDll;
    TaskCompletionSource<int>? _pending;
    readonly StringBuilder _serverErrors = new();

    /// <summary>Process id of the serving worker, null when none runs.</summary>
    public int? WorkerProcessId => _server is { HasExited: false } server ? server.Id : null;

    public string LogPath => Path.Combine(cacheRoot, "logs", "last-run.log");

    /// <summary>Directory containing model.json and transformed files of the last successful run.</summary>
    public string LastOutputDir { get; private set; } = "";

    public AspectModel Run(string metalamaVersion, string solutionPath, CancellationToken ct) =>
        Run(metalamaVersion, solutionPath, new[] { solutionPath }, ct);

    /// <param name="inputs">Paths the worker loads (see <see cref="WorkspaceInputs"/>); <paramref name="solutionPath"/> only identifies the output directory.</param>
    public AspectModel Run(string metalamaVersion, string solutionPath, IReadOnlyList<string> inputs, CancellationToken ct)
    {
        if (inputs.Count == 0) throw new WorkerException($"No C# projects to analyze in {Path.GetFileName(solutionPath)}");
        if (!MetalamaVersionDetector.IsSafe(metalamaVersion)) throw new WorkerException($"Refusing unexpected Metalama version '{metalamaVersion}'");
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        TryLog(() => File.WriteAllText(LogPath, $"LamaRider worker run {DateTime.Now:O}{Environment.NewLine}"));

        var dll = EnsureBuilt(metalamaVersion, ct);
        var runsDir = Path.Combine(cacheRoot, "out", Hash(solutionPath));
        var outDir = Path.Combine(runsDir, DateTime.UtcNow.Ticks.ToString());
        Serve(dll, outDir, inputs, ct);

        var modelPath = Path.Combine(outDir, "model.json");
        if (!File.Exists(modelPath)) throw new WorkerException($"Worker produced no model.json. See {LogPath}");
        var model = AspectModel.Parse(File.ReadAllText(modelPath));

        foreach (var old in Directory.GetDirectories(runsDir).Where(d => d != outDir))
        {
            try { Directory.Delete(old, true); }
            catch (IOException) { /* still open in a diff viewer; removed next run */ }
            catch (UnauthorizedAccessException) { }
        }

        LastOutputDir = outDir;
        return model;
    }

    string EnsureBuilt(string metalamaVersion, CancellationToken ct)
    {
        var key = $"{metalamaVersion}-{SourceKey(workerSourceDir)}";
        var binDir = Path.Combine(cacheRoot, "worker", key);
        var dll = Path.Combine(binDir, "LamaRider.Worker.dll");
        if (File.Exists(dll)) return dll;

        // Private source and output dirs, then an atomic move: another Rider instance may build the same key concurrently.
        var unique = Guid.NewGuid().ToString("N");
        var srcDir = Path.Combine(cacheRoot, "worker-src", $"{key}-{unique}");
        var tmpBinDir = Path.Combine(cacheRoot, "worker", $"{key}.tmp-{unique}");
        try
        {
            Directory.CreateDirectory(srcDir);
            foreach (var file in Directory.GetFiles(workerSourceDir, "*", SearchOption.TopDirectoryOnly))
                File.Copy(file, Path.Combine(srcDir, Path.GetFileName(file)));

            var build = $"build \"{Path.Combine(srcDir, "LamaRider.Worker.csproj")}\" -c Release -p:MetalamaVersion={metalamaVersion} -o \"{tmpBinDir}\"";
            var output = Exec(build, srcDir, ct);
            // A successful build without the dll: seen right after a plugin update (other builds running); once more.
            if (!File.Exists(Path.Combine(tmpBinDir, "LamaRider.Worker.dll"))) output = Exec(build, srcDir, ct);
            if (!File.Exists(Path.Combine(tmpBinDir, "LamaRider.Worker.dll")))
                throw new WorkerException($"Worker build produced no LamaRider.Worker.dll. Last output: {output}. See {LogPath}");
            try { Directory.Move(tmpBinDir, binDir); }
            catch (IOException) when (File.Exists(dll)) { /* another instance won the race */ }
            return dll;
        }
        catch (UnauthorizedAccessException e) { throw new WorkerException($"Cannot prepare worker build: {e.Message}"); }
        finally
        {
            TryDelete(srcDir);
            TryDelete(tmpBinDir);
        }
    }

    void Serve(string dll, string outDir, IReadOnlyList<string> inputs, CancellationToken ct)
    {
        if (_server is not { HasExited: false } || _serverDll != dll) StartServer(dll);
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_serverErrors)
        {
            _serverErrors.Clear();
            _pending = done;
        }
        // ASCII-only JSON: stdin uses the console code page, paths may not.
        var request = Newtonsoft.Json.JsonConvert.SerializeObject(new[] { outDir }.Concat(inputs),
            new Newtonsoft.Json.JsonSerializerSettings { StringEscapeHandling = Newtonsoft.Json.StringEscapeHandling.EscapeNonAscii });
        Log($"> serve {request}");
        try
        {
            _server!.StandardInput.WriteLine(request);
            _server.StandardInput.Flush();
        }
        catch (IOException e)
        {
            StopServer();
            throw new WorkerException($"Worker stopped unexpectedly ({e.Message}). See {LogPath}");
        }

        using (ct.Register(StopServer))
        {
            if (!done.Task.Wait(Timeout))
            {
                StopServer();
                throw new WorkerException($"Worker timed out after {Timeout.TotalMinutes} minutes. See {LogPath}");
            }
        }
        ct.ThrowIfCancellationRequested();
        if (done.Task.Result != 0)
        {
            string errors;
            lock (_serverErrors) errors = FirstLines(_serverErrors.ToString());
            throw new WorkerException(errors.Length > 0 ? errors : $"Worker failed ({done.Task.Result}). See {LogPath}");
        }
    }

    void StartServer(string dll)
    {
        StopServer();
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" --serve")
        {
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (e.Data.StartsWith(DoneMarker, StringComparison.Ordinal))
            {
                var code = int.TryParse(e.Data.Substring(DoneMarker.Length).Trim(), out var c) ? c : 1;
                lock (_serverErrors) _pending?.TrySetResult(code);
                return;
            }
            Log(e.Data);
            if (e.Data.StartsWith(ErrorMarker, StringComparison.Ordinal))
                lock (_serverErrors) _serverErrors.Insert(0, e.Data.Substring(ErrorMarker.Length).Trim() + Environment.NewLine);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Log(e.Data); // the stack trace; the message comes on stdout (ErrorMarker)
        };
        process.Exited += (_, _) => { lock (_serverErrors) _pending?.TrySetResult(-1); };

        Log($"> dotnet {psi.Arguments}");
        try { process.Start(); }
        catch (Exception e) { throw new WorkerException($"Cannot start 'dotnet' ({e.Message}). Is the .NET SDK on PATH?"); }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        (_server, _serverDll) = (process, dll);
    }

    void StopServer()
    {
        var server = _server;
        _server = null;
        if (server == null) return;
        Kill(server);
        server.Dispose();
    }

    public void Dispose() => StopServer();

    /// <returns>The last lines of the output, for error messages.</returns>
    string Exec(string arguments, string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var stderr = new StringBuilder();
        var buildErrors = new StringBuilder(); // dotnet build / NuGet report errors on stdout
        var lastLines = new Queue<string>();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Log(e.Data);
            lock (lastLines)
            {
                if (e.Data.Trim().Length > 0) lastLines.Enqueue(e.Data.Trim());
                if (lastLines.Count > 3) lastLines.Dequeue();
            }
            if (e.Data.Contains(": error ")) lock (buildErrors) buildErrors.AppendLine(e.Data.Trim());
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Log(e.Data);
            lock (stderr) stderr.AppendLine(e.Data);
        };

        Log($"> dotnet {arguments}");
        try { process.Start(); }
        catch (Exception e) { throw new WorkerException($"Cannot start 'dotnet' ({e.Message}). Is the .NET SDK on PATH?"); }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (ct.Register(() => Kill(process)))
        {
            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                Kill(process);
                throw new WorkerException($"Worker timed out after {Timeout.TotalMinutes} minutes. See {LogPath}");
            }
            process.WaitForExit(); // flush the async readers
        }

        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            var message = FirstLines(buildErrors.Length > 0 ? buildErrors.ToString() : stderr.ToString());
            throw new WorkerException(message.Length > 0 ? message : $"dotnet exited with {process.ExitCode}. See {LogPath}");
        }
        lock (lastLines) return string.Join(" | ", lastLines);
    }

    static void Kill(Process process)
    {
        try { process.Kill(); }
        catch (Exception) { /* already exited or exiting */ }
    }

    static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Runs on thread-pool callbacks: an exception here would take down the Rider backend.
    void Log(string line) => TryLog(() => File.AppendAllText(LogPath, line + Environment.NewLine));

    void TryLog(Action write)
    {
        lock (_logLock)
        {
            try { write(); }
            catch (IOException) { /* log shared with another Rider instance or locked by a scanner */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    static string FirstLines(string text) => string.Join("\n", text.Split('\n').Take(5)).Trim();

    /// <summary>Hash of the worker sources: a plugin update with a changed worker gets a fresh build.</summary>
    internal static string SourceKey(string dir)
    {
        using var sha = SHA1.Create();
        var sb = new StringBuilder();
        foreach (var file in Directory.GetFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            sb.Append(Path.GetFileName(file)).Append(' ').Append(File.ReadAllText(file)).Append(' ');
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "").Substring(0, 12);
    }

    static string Hash(string path)
    {
        using var sha = SHA1.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return BitConverter.ToString(bytes).Replace("-", "").Substring(0, 12);
    }
}
