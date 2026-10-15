using System.Text.Json;
using LamaRider.Worker;
using Metalama.Framework.Workspaces;

return await Program.Run(args);

public static partial class Program
{
    /// <summary>Written to stdout after each request in serve mode: "LAMARIDER-DONE &lt;exit code&gt;".</summary>
    public const string DoneMarker = "LAMARIDER-DONE";

    /// <summary>Serve mode: the failure message, on stdout before the done marker (stderr would arrive out of order).</summary>
    public const string ErrorMarker = "LAMARIDER-ERROR";

    static Workspace? _workspace;
    static string? _workspaceKey;

    /// <summary>
    /// LamaRider.Worker &lt;outDir&gt; &lt;solution-or-project&gt;... (.sln, .slnf or .csproj: what Metalama Workspaces accepts), or
    /// LamaRider.Worker --serve: one request per stdin line, a JSON array [outDir, input...]; keeps the workspace loaded and
    /// reloads it for the next request (a few seconds instead of a full load).
    /// </summary>
    public static async Task<int> Run(string[] args)
    {
        if (args is ["--serve"]) return await Serve();
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: LamaRider.Worker <outDir> <solution-or-project>... | --serve");
            return 2;
        }
        return await RunOne(args[0], args[1..], keep: false);
    }

    /// <summary>A failed reload (workspace left in an odd state by edits or a running build) gets one clean load.</summary>
    static async Task<int> RunOrReloadFresh(string outDir, string[] inputs)
    {
        var reloading = _workspace != null;
        var code = await RunOne(outDir, inputs, keep: true, quiet: reloading);
        return code != 0 && reloading ? await RunOne(outDir, inputs, keep: true) : code;
    }

    static async Task<int> Serve()
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            var request = JsonSerializer.Deserialize<string[]>(line);
            var code = request is { Length: >= 2 } ? await RunOrReloadFresh(request[0], request[1..]) : 2;
            Console.Out.WriteLine($"{DoneMarker} {code}");
            Console.Out.Flush();
        }
        return 0; // stdin closed: the IDE is gone
    }

    /// <param name="quiet">Failure not reported to the IDE (a retry follows); still logged on stderr.</param>
    static async Task<int> RunOne(string outDir, string[] inputs, bool keep, bool quiet = false)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            // Generated solution next to the run directories: a reload reads it again after old run directories are deleted.
            if (inputs.Length > 1) inputs = [SolutionFile.Write(Path.GetDirectoryName(Path.GetFullPath(outDir))!, inputs)];
            var key = string.Join("|", inputs);

            Workspace loaded;
            if (keep && _workspace != null && _workspaceKey == key)
            {
                loaded = _workspace = await _workspace.ReloadAsync(restore: false);
            }
            else
            {
                _workspace?.Dispose();
                _workspace = null;
                loaded = await WorkspaceCollection.Default.LoadAsync(inputs);
                if (keep) (_workspace, _workspaceKey) = (loaded, key);
            }

            try
            {
                // Code being edited often doesn't compile; report what Metalama can still analyze (errors land in Diagnostics).
                var output = Extractor.Extract(loaded.WithIgnoreErrors(), outDir);
                await File.WriteAllTextAsync(Path.Combine(outDir, "model.json"), JsonSerializer.Serialize(output, WorkerJson.Options));
            }
            finally
            {
                if (!keep) loaded.Dispose();
            }
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            if (keep && !quiet) Console.Out.WriteLine($"{ErrorMarker} {e.Message.ReplaceLineEndings(" ")}");
            _workspace?.Dispose(); // start from a clean load next time
            _workspace = null;
            return 1;
        }
    }
}
