using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class AnalysisPauseTests
{
    static readonly DateTime Built = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Paused_when_compile_time_code_changed_after_the_last_build()
    {
        var times = new Dictionary<string, DateTime> { ["A.cs"] = Built.AddMinutes(-5), ["B.cs"] = Built.AddMinutes(1) };
        Assert.True(AnalysisPause.Likely(["A.cs", "B.cs"], Built, f => times[f]));
        Assert.False(AnalysisPause.Likely(["A.cs"], Built, f => times[f]));
    }

    [Fact]
    public void Paused_when_never_built() => Assert.True(AnalysisPause.Likely(["A.cs"], null, _ => Built));

    [Fact]
    public void Compile_time_files_of_a_project_are_its_aspect_and_template_files()
    {
        var model = new AspectModel
        {
            AspectClasses =
            [
                new() { Id = "T:A", Location = new() { File = Path.Combine("C:", "App", "LogAttribute.cs") } },
                new() { Id = "T:B", Location = new() { File = Path.Combine("C:", "Other", "X.cs") } },
                new() { Id = "T:C", IsLibrary = true },
            ],
            TemplateClassifications = [new() { File = Path.Combine("C:", "App", "Sub", "Templates.cs") }],
        };
        Assert.Equal([Path.Combine("C:", "App", "LogAttribute.cs"), Path.Combine("C:", "App", "Sub", "Templates.cs")],
            model.CompileTimeFilesIn(Path.Combine("C:", "App")));
    }
}
