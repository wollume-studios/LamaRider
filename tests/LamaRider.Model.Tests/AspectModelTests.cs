using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class AspectModelTests
{
    const string File1 = @"C:\src\Calc.cs";
    const string Add = "M:Calc.Add(System.Int32)";

    static string Json(string instances, int schema = 1) => $$$"""
        {"schema":{{{schema}}},"metalamaVersion":"2026.1.30",
         "aspectClasses":[{"id":"T:Log","name":"LogAttribute","project":"P","isLibrary":false,"location":null}],
         "instances":[{{{instances}}}],
         "transformations":[],"diagnostics":[],
         "templateClassifications":[{"file":"C:\\src\\Log.cs","length":10,"spans":[{"start":1,"end":5,"kind":"TemplateKeyword"}]}],
         "transformedFiles":[]}
        """;

    static string Instance(string target, int start, int end, string cls = "T:Log") =>
        $$$"""{"aspectClass":"{{{cls}}}","target":"{{{target}}}","targetName":"x","targetType":"Calc","targetKind":"Method","project":"P","appliedVia":"Attribute","location":{"file":"C:\\src\\Calc.cs","start":{{{start}}},"end":{{{end}}}}}""";

    [Fact]
    public void Rejects_unknown_schema() =>
        Assert.Throws<InvalidDataException>(() => AspectModel.Parse(Json("", schema: 2)));

    [Fact]
    public void Finds_instances_by_doc_id()
    {
        var model = AspectModel.Parse(Json(Instance(Add, 10, 50)));
        Assert.Single(model.InstancesFor(Add, "other.cs", 0, 0));
    }

    [Fact]
    public void Falls_back_to_span_when_doc_id_differs()
    {
        var model = AspectModel.Parse(Json(Instance("M:Weird", 10, 50)));
        Assert.Single(model.InstancesFor("M:Calc.Add(int)", @"c:\SRC\calc.cs", 20, 50));
    }

    [Fact]
    public void Span_fallback_does_not_leak_container_aspects()
    {
        // class instance spans 0..100, member declaration ends at 50 -> must not match
        var model = AspectModel.Parse(Json(Instance("T:Calc", 0, 100)));
        Assert.Empty(model.InstancesFor("M:Calc.Sub(System.Int32)", File1, 20, 50));
    }

    [Fact]
    public void Duplicate_instances_are_collapsed()
    {
        var model = AspectModel.Parse(Json(Instance(Add, 10, 50) + "," + Instance(Add, 10, 50)));
        Assert.Single(model.InstancesFor(Add, File1, 0, 0));
    }

    [Fact]
    public void Counts_targets_of_aspect_class()
    {
        var model = AspectModel.Parse(Json(Instance(Add, 10, 50) + "," + Instance("M:Calc.Sub", 60, 90)));
        Assert.Equal(2, model.TargetCountOf("T:Log"));
        Assert.True(model.IsAspectClass("T:Log"));
    }

    [Fact]
    public void Finds_classification_by_path_case_insensitively() =>
        Assert.NotNull(AspectModel.Parse(Json("")).ClassificationFor(@"c:\src\LOG.cs", 10));

    [Fact]
    public void Ignores_classification_when_document_length_changed() =>
        Assert.Null(AspectModel.Parse(Json("")).ClassificationFor(@"C:\src\Log.cs", 11));

    [Fact]
    public void Malformed_json_is_reported_as_invalid_data() =>
        Assert.Throws<InvalidDataException>(() => AspectModel.Parse("{ not json"));

    [Fact]
    public void Click_target_prefers_the_worker_target_over_resharper_doc_id()
    {
        var model = AspectModel.Parse(Json(Instance("M:Weird", 10, 50)));
        var instances = model.InstancesFor("M:Calc.Add(int)", File1, 20, 50);
        Assert.Equal("M:Weird", AspectModel.ClickTarget("M:Calc.Add(int)", instances));
        Assert.Equal("T:Log", AspectModel.ClickTarget("T:Log", Array.Empty<AspectInstanceInfo>()));
    }

    [Fact]
    public void Error_summary_names_count_and_first_error()
    {
        var json = Json("").Replace("\"diagnostics\":[]",
            "\"diagnostics\":[{\"id\":\"CS1061\",\"severity\":\"Error\",\"message\":\"'IMethod' does not contain 'Nme'\"},{\"id\":\"LAMA0001\",\"severity\":\"Error\",\"message\":\"x\"},{\"id\":\"W1\",\"severity\":\"Warning\",\"message\":\"w\"}]");
        Assert.Equal(" · 2 Metalama errors, first: CS1061 'IMethod' does not contain 'Nme'", AspectModel.Parse(json).ErrorSummary);
    }

    [Fact]
    public void Error_summary_is_empty_without_errors() =>
        Assert.Equal("", AspectModel.Parse(Json("")).ErrorSummary);
}
