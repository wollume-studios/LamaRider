using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class CompileTimeCodeFixLogicTests
{
    [Fact]
    public void Simple_query_becomes_a_select() =>
        Assert.Equal(
            FixSamples.Normalize("meta.Target.Parameters.Select( parameter => parameter.Name )"),
            FixSamples.Normalize(CompileTimeCodeFixLogic.QueryToMethods("meta.Target.Parameters", "parameter", [], "parameter.Name")));

    [Fact]
    public void Identity_select_after_where_is_dropped() =>
        Assert.Equal("xs.Where(x => x > 1)", CompileTimeCodeFixLogic.QueryToMethods("xs", "x", ["x > 1"], "x"));

    [Fact]
    public void ConfigureAwait_gets_one_branch_per_literal()
    {
        const string code = "var result = await meta.ProceedAsync().ConfigureAwait( this.C );\n\n        return result;";
        var start = code.IndexOf("this.C");
        var actual = CompileTimeCodeFixLogic.ConfigureAwaitBranches(code, start, start + "this.C".Length, "this.C", "        ");
        Assert.Equal(
            FixSamples.Normalize("""
                if ( this.C )
                {
                    var result = await meta.ProceedAsync().ConfigureAwait( true );
                    return result;
                }
                else
                {
                    var result = await meta.ProceedAsync().ConfigureAwait( false );
                    return result;
                }
                """),
            FixSamples.Normalize(actual));
        Assert.Contains("\n            return result;", actual);
    }

    [Fact]
    public void Template_name_comes_from_its_parameters() =>
        Assert.Equal("LogWithCategory", CompileTimeCodeFixLogic.TemplateName("Log", ["category"], new HashSet<string> { "Log" }));

    [Fact]
    public void Template_name_is_unique() =>
        Assert.Equal("Log22", CompileTimeCodeFixLogic.TemplateName("Log", [], new HashSet<string> { "Log", "Log2" }));

    [Fact]
    public void Parent_and_child_come_from_the_LAMA0022_message() =>
        Assert.Equal(("LogAllMethods", "Log"), CompileTimeCodeFixLogic.ParentAndChild(
            "The aspect 'LogAllMethods' cannot add a child aspect to of type 'Log' because the 'Log' aspect is processed before 'LogAllMethods'."));

    [Fact]
    public void Layers_and_weaver_come_from_the_LAMA0042_message() =>
        Assert.Equal(("NotNullAttribute", "MakeVirtualAttribute"), CompileTimeCodeFixLogic.LayeredAndWeaver(
            "The aspect 'NotNullAttribute' has several layers, but the low-level aspect 'MakeVirtualAttribute' is ordered between two of these layers."));

    [Theory]
    [InlineData("Doc.LAMA0022.Error.LogAttribute", "Log", true)]
    [InlineData("LogAttribute", "LogAttribute", true)]
    [InlineData("global::Ns.Log", "Log", true)]
    [InlineData("Doc.LAMA0022.Error.LogAllMethodsAttribute", "Log", false)]
    public void Typeof_matches_the_aspect_name(string typeText, string name, bool expected) =>
        Assert.Equal(expected, CompileTimeCodeFixLogic.IsAspectNamed(typeText, name));

    [Fact]
    public void Record_serializer_matches_the_LAMA0210_sample() =>
        Assert.Equal(
            FixSamples.Normalize("""
                public class Serializer : ReferenceTypeSerializer<RetryPolicy>
                {
                    public override RetryPolicy CreateInstance( IArgumentsReader constructorArguments )
                        => new( constructorArguments.GetValue<int>( "MaxAttempts" ) );

                    public override void SerializeObject( RetryPolicy obj, IArgumentsWriter constructorArguments, IArgumentsWriter initializationArguments )
                        => constructorArguments.SetValue( "MaxAttempts", obj.MaxAttempts );
                }
                """),
            FixSamples.Normalize(CompileTimeCodeFixLogic.RecordSerializer("RetryPolicy", [("int", "MaxAttempts")], false, "    ")));

    [Fact]
    public void Record_struct_serializer_sets_every_parameter()
    {
        var code = CompileTimeCodeFixLogic.RecordSerializer("Point", [("int", "X"), ("int", "Y")], true, "");
        Assert.Contains("ValueTypeSerializer<Point>", code);
        Assert.Contains("new( constructorArguments.GetValue<int>( \"X\" ), constructorArguments.GetValue<int>( \"Y\" ) )", code);
        Assert.Contains("constructorArguments.SetValue( \"Y\", obj.Y );", code);
    }
}
