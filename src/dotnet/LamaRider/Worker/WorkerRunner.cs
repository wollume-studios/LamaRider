using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using LamaRider.Model;

namespace LamaRider.Worker;

public sealed class WorkerException(string message) : Exception(message);

/// <summary>
/// Builds LamaRider.Worker against the solution's Metalama version (cached per plugin + Metalama version)
/// and runs it out of process. No JetBrains dependencies: unit-tested outside the Rider SDK.
/// </summary>
public sealed class WorkerRunner(string workerSourceDir, string cacheRoot, string pluginVersion)
{
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    readonly object _logLock = new();

    public string LogPath => Path.Combine(cacheRoot, "logs", "last-run.log");

    /// <summary>Directory containing model.json and transformed files of the last successful run.</summary>
    public string LastOutputDir { get; private set; } = "";

    public AspectModel Run(string metalamaVersion, string solutionPath, CancellationToken ct)
    {
        if (!MetalamaVersionDetector.IsSafe(metalamaVersion)) throw new WorkerException($"Refusing unexpected Metalama version '{metalamaVersion}'");
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, $"LamaRider worker run {DateTime.Now:O}{Environment.NewLine}");

        var dll = EnsureBuilt(metalamaVersion, ct);
        var runsDir = Path.Combine(cacheRoot, "out", Hash(solutionPath));
        var outDir = Path.Combine(runsDir, DateTime.UtcNow.Ticks.ToString());
        Exec($"\"{dll}\" \"{solutionPath}\" \"{outDir}\"", Path.GetDirectoryName(dll)!, ct);

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
        var key = $"{pluginVersion}-{metalamaVersion}";
        var binDir = Path.Combine(cacheRoot, "worker", key);
        var dll = Path.Combine(binDir, "LamaRider.Worker.dll");
        if (File.Exists(dll)) return dll;

        var srcDir = Path.Combine(cacheRoot, "worker-src", key);
        if (Directory.Exists(srcDir)) Directory.Delete(srcDir, true);
        Directory.CreateDirectory(srcDir);
        foreach (var file in Directory.GetFiles(workerSourceDir, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(srcDir, Path.GetFileName(file)));

        Exec($"build \"{Path.Combine(srcDir, "LamaRider.Worker.csproj")}\" -c Release -p:MetalamaVersion={metalamaVersion} -o \"{binDir}\"", srcDir, ct);
        if (!File.Exists(dll)) throw new WorkerException($"Worker build failed. See {LogPath}");
        return dll;
    }

    void Exec(string arguments, string workingDir, CancellationToken ct)
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
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
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
            var message = FirstLines(stderr.ToString());
            throw new WorkerException(message.Length > 0 ? message : $"dotnet exited with {process.ExitCode}. See {LogPath}");
        }
    }

    static void Kill(Process process)
    {
        try { process.Kill(); }
        catch (InvalidOperationException) { /* already exited */ }
    }

    void Log(string line)
    {
        lock (_logLock) File.AppendAllText(LogPath, line + Environment.NewLine);
    }

    static string FirstLines(string text) => string.Join("\n", text.Split('\n').Take(5)).Trim();

    static string Hash(string path)
    {
        using var sha = SHA1.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return BitConverter.ToString(bytes).Replace("-", "").Substring(0, 12);
    }
}
