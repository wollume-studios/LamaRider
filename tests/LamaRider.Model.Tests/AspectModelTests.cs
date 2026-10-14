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
    public void Error_summary_counts_errors()
    {
        var json = Json("").Replace("\"diagnostics\":[]",
            "\"diagnostics\":[{\"id\":\"CS1061\",\"severity\":\"Error\",\"message\":\"'IMethod' does not contain 'Nme'\"},{\"id\":\"LAMA0001\",\"severity\":\"Error\",\"message\":\"x\"},{\"id\":\"W1\",\"severity\":\"Warning\",\"message\":\"w\"}]");
        Assert.Equal(" · 2 Metalama errors", AspectModel.Parse(json).ErrorSummary); // details go to a notification
    }

    [Fact]
    public void Error_summary_is_empty_without_errors() =>
        Assert.Equal("", AspectModel.Parse(Json("")).ErrorSummary);

    [Fact]
    public void Project_wide_errors_apply_to_own_aspect_classes_of_that_project()
    {
        const string json = """
            {"schema":1,"aspectClasses":[
               {"id":"T:EFA.Class1","name":"Class1","project":"EFA","isLibrary":false,"package":"EFA"},
               {"id":"T:Other.Aspect","name":"Aspect","project":"Other","isLibrary":false,"package":"Other"},
               {"id":"T:Lib.Url","name":"Url","project":"","isLibrary":true,"package":"Lib"}],
             "instances":[],"transformations":[],"templateClassifications":[],"transformedFiles":[],
             "diagnostics":[
               {"id":"LAMA0042","severity":"Error","message":"ordering","project":"EFA"},
               {"id":"CS0103","severity":"Error","message":"located","project":"EFA","file":"C:/x.cs","line":3},
               {"id":"LAMA0999","severity":"Warning","message":"w","project":"EFA"}]}
            """;
        var model = AspectModel.Parse(json);
        Assert.Equal(["LAMA0042"], model.ProjectErrorsFor("T:EFA.Class1").Select(d => d.Id));
        Assert.Empty(model.ProjectErrorsFor("T:Other.Aspect"));
        Assert.Empty(model.ProjectErrorsFor("T:Lib.Url"));
        Assert.Empty(model.ProjectErrorsFor("T:Unknown"));
    }

    [Fact]
    public void Compile_time_files_are_aspect_class_and_template_files()
    {
        var model = AspectModel.Parse(Json("").Replace("\"location\":null", "\"location\":{\"file\":\"C:/src/Aspects/Log.cs\",\"start\":0,\"end\":5}"));
        Assert.True(model.IsCompileTimeFile(@"c:\src\aspects\LOG.cs")); // aspect class
        Assert.True(model.IsCompileTimeFile(@"C:\src\Log.cs"));         // T# template
        Assert.False(model.IsCompileTimeFile(File1));
    }

    [Fact]
    public void Method_hint_counts_aspects_on_its_parameters()
    {
        const string method = "M:EFA.Test.MyMethod(System.String)";
        var model = AspectModel.Parse(Json(Instance(method, 10, 50) + "," + Instance(method + ";Parameter=0", 30, 40, "T:Url")));
        Assert.Equal(["T:Log", "T:Url"], model.InstancesFor(method, File1, 20, 50).Select(i => i.AspectClass).Order());
    }

    [Fact]
    public void Parameter_aspects_alone_still_show_on_the_method()
    {
        const string method = "M:EFA.Test.MyMethod(System.String)";
        var model = AspectModel.Parse(Json(Instance(method + ";Parameter=0", 30, 40, "T:Url")));
        Assert.Equal(["T:Url"], model.InstancesFor(method, File1, 20, 50).Select(i => i.AspectClass));
    }
}
