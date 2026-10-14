using System.Text.Json;
using LamaRider.Worker;
using Metalama.Framework.Workspaces;

return await Program.Run(args);

public static partial class Program
{
    /// <summary>LamaRider.Worker &lt;outDir&gt; &lt;solution-or-project&gt;... (.sln, .slnf or .csproj: what Metalama Workspaces accepts)</summary>
    public static async Task<int> Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: LamaRider.Worker <outDir> <solution-or-project>...");
            return 2;
        }
        try
        {
            var outDir = args[0];
            Directory.CreateDirectory(outDir);
            var inputs = args[1..];
            if (inputs.Length > 1) inputs = [SolutionFile.Write(outDir, inputs)];
            using var loaded = await WorkspaceCollection.Default.LoadAsync(inputs);
            // Code being edited often doesn't compile; report what Metalama can still analyze (errors land in Diagnostics).
            var workspace = loaded.WithIgnoreErrors();
            var output = Extractor.Extract(workspace, outDir);
            await File.WriteAllTextAsync(Path.Combine(outDir, "model.json"), JsonSerializer.Serialize(output, WorkerJson.Options));
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }
}
