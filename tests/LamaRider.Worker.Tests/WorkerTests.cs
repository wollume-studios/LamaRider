using LamaRider.Worker;
using Metalama.Framework.Workspaces;

namespace LamaRider.Worker.Tests;

public sealed class WorkerTests : IAsyncLifetime
{
    const string Add = "M:MetalamaSample.Calculator.Add(System.Int32,System.Int32)";
    const string Multiply = "M:MetalamaSample.Calculator.Multiply(System.Int32,System.Int32)";
    static readonly string SampleDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../samples/MetalamaSample"));
    readonly string _outDir = Path.Combine(Path.GetTempPath(), "lamarider-tests", Guid.NewGuid().ToString("N"));
    WorkerOutput _output = null!;

    public async Task InitializeAsync()
    {
        using var workspace = await WorkspaceCollection.Default.LoadAsync(Path.Combine(SampleDir, "MetalamaSample.sln"));
        _output = Extractor.Extract(workspace, _outDir);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Reports_attribute_and_fabric_instances_once()
    {
        var log = _output.Instances.Where(i => i.AspectClass == "T:MetalamaSample.LogAttribute").ToList();
        Assert.Equal(2, log.Count);
        Assert.Equal("Attribute", log.Single(i => i.Target == Add).AppliedVia);
        Assert.Equal("Fabric", log.Single(i => i.Target == Multiply).AppliedVia);
        Assert.Equal("MetalamaSample.Calculator", log[0].TargetType);
    }

    [Fact]
    public void Aspect_class_is_located_in_source()
    {
        var log = _output.AspectClasses.Single(c => c.Id == "T:MetalamaSample.LogAttribute");
        Assert.False(log.IsLibrary);
        Assert.EndsWith("LogAttribute.cs", log.Location!.File);
    }

    [Fact]
    public void Offsets_are_lf_normalized()
    {
        var add = _output.Instances.Single(i => i.Target == Add);
        var raw = File.ReadAllText(add.Location!.File);
        Assert.Contains("\r\n", raw); // fixture really is CRLF
        var lf = raw.Replace("\r\n", "\n");
        var text = lf.Substring(add.Location.Start, add.Location.End - add.Location.Start);
        Assert.StartsWith("[Log]", text);
        Assert.EndsWith("a + b;", text);
    }

    [Fact]
    public void Writes_transformed_file_with_introduced_member()
    {
        var calc = _output.TransformedFiles.Single(f => f.File.EndsWith("Calculator.cs"));
        var transformed = File.ReadAllText(calc.TransformedPath);
        Assert.Contains("Guid Id", transformed);
        Assert.Contains("Entering", transformed);
    }

    [Fact]
    public void Lists_transformations_for_targets()
    {
        Assert.Contains(_output.Transformations, t => t.Target == Add);
        Assert.Contains(_output.Transformations, t => t.Target == "T:MetalamaSample.Calculator");
    }

    [Fact]
    public void Classifies_template_code()
    {
        var file = _output.TemplateClassifications.Single(f => f.File.EndsWith("LogAttribute.cs"));
        var lf = File.ReadAllText(file.File).Replace("\r\n", "\n");
        Assert.Contains(file.Spans, s => s.Kind == "TemplateKeyword" && lf.Substring(s.Start, s.End - s.Start).StartsWith("meta."));
        Assert.Contains(file.Spans, s => s.Kind == "CompileTimeVariable");
    }

    [Fact]
    public async Task Main_writes_model_json()
    {
        var dir = Path.Combine(_outDir, "cli");
        var code = await Program.Run([dir, Path.Combine(SampleDir, "MetalamaSample/MetalamaSample.csproj"), Path.Combine(SampleDir, "MetalamaSample.AspectTests/MetalamaSample.AspectTests.csproj")]);
        Assert.Equal(0, code);
        Assert.Contains("\"schema\":1", File.ReadAllText(Path.Combine(dir, "model.json")));
    }

    [Theory]
    [InlineData("M:A.B(System.Int32)~System.Int32", "M:A.B(System.Int32)")]
    [InlineData("M:A.B(System.Int32)~System.Int32;Parameter=0", "M:A.B(System.Int32);Parameter=0")]
    [InlineData("M:A.B(System.Int32)~System.Int32;Return", "M:A.B(System.Int32);Return")]
    [InlineData("M:A.op_Implicit(A)~System.Int32", "M:A.op_Implicit(A)~System.Int32")]
    [InlineData("T:A", "T:A")]
    public void Doc_ids_drop_return_type_but_keep_parameter_suffix(string metalamaId, string expected) =>
        Assert.Equal(expected, Extractor.NormalizeId(metalamaId));

    [Fact]
    public void Classification_records_lf_length_of_the_file()
    {
        var file = _output.TemplateClassifications.Single(f => f.File.EndsWith("LogAttribute.cs"));
        Assert.Equal(File.ReadAllText(file.File).Replace("\r\n", "\n").Length, file.Length);
    }

    [Fact]
    public async Task Projects_referenced_by_earlier_inputs_are_not_loaded_twice()
    {
        // AspectTests references MetalamaSample: loading it first already adds MetalamaSample to the workspace.
        var dir = Path.Combine(_outDir, "cli-order");
        var code = await Program.Run([dir,
            Path.Combine(SampleDir, "MetalamaSample.AspectTests/MetalamaSample.AspectTests.csproj"),
            Path.Combine(SampleDir, "MetalamaSample/MetalamaSample.csproj")]);
        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(dir, "model.json")));
    }
}
