using System.Text.Json;
using LamaRider.Worker;
using Metalama.Framework.Workspaces;

return await Program.Run(args);

public static partial class Program
{
    public static async Task<int> Run(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: LamaRider.Worker <solution-or-project> <outDir>");
            return 2;
        }
        try
        {
            Directory.CreateDirectory(args[1]);
            using var workspace = await WorkspaceCollection.Default.LoadAsync(args[0]);
            var output = Extractor.Extract(workspace, args[1]);
            await File.WriteAllTextAsync(Path.Combine(args[1], "model.json"), JsonSerializer.Serialize(output, WorkerJson.Options));
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }
}
