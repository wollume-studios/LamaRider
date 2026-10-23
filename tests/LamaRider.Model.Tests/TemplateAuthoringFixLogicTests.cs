using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class TemplateAuthoringFixLogicTests
{
    [Fact]
    public void The_template_member_is_read_from_LAMA0033() =>
        Assert.Equal(("Doc.LAMA0033.Error.DescribeAttribute", "Describe"), TemplateAuthoringFixLogic.TemplateMember(
            "The class 'Doc.LAMA0033.Error.DescribeAttribute' defines a member named 'Describe', but the member is not annotated with the [Template] custom attribute."));

    [Theory]
    [InlineData("The expression 'index' is run-time but it is expected to be compile-time because the expression appears in element.", true)]
    [InlineData("The expression 'x' is compile-time, but it is expected to be run-time because the expression appears in a call.", false)]
    [InlineData("Something else.", null)]
    public void The_expected_scope_is_read_from_LAMA0104(string message, bool? expected) =>
        Assert.Equal(expected, TemplateAuthoringFixLogic.ExpectsCompileTime(message));

    [Theory]
    [InlineData("Execution scope mismatch in the expression `meta.Target.Parameters[0] as Customer`: the expression is run-time, but the sub-expression `meta.Target.Parameters[0]` is compile-time")]
    [InlineData("Execution scope mismatch in the expression 'meta.Target.Parameters[0] as Customer': the type of the expression is run-time, but the sub-expression 'meta.Target.Parameters[0]' is compile-time.")]
    public void The_sub_expression_is_read_from_LAMA0242(string message) =>
        Assert.Equal("meta.Target.Parameters[0]", TemplateAuthoringFixLogic.SubExpression(message));

    [Fact]
    public void Attribute_names_lose_their_suffix() =>
        Assert.Equal("Template", TemplateAuthoringFixLogic.AttributeName("The 'TemplateAttribute' attribute is not allowed on a local function."));

    [Fact]
    public void A_method_is_passed_by_name_or_as_MethodInfo()
    {
        var type = TemplateAuthoringFixLogic.CompileTimeType("Compile-time-only type 'IMethod' cannot be used in the invocation of run-time method 'AuditLog.Write'.");
        Assert.Equal(["Name", "ToMethodInfo()"], TemplateAuthoringFixLogic.RunTimeMembers(type));
    }

    [Theory]
    [InlineData("List<dynamic?>", false, "List<object?>")]
    [InlineData("Dictionary<string, dynamic>", false, "Dictionary<string, object>")]
    [InlineData("void PrintResult( dynamic? value )", true, "void PrintResult( object? value )")]
    [InlineData("dynamic F( dynamic x, dynamicValue y )", true, "object? F( object? x, dynamicValue y )")]
    public void Dynamic_becomes_object(string text, bool alwaysNullable, string expected) =>
        Assert.Equal(expected, TemplateAuthoringFixLogic.ReplaceDynamic(text, alwaysNullable));

    [Theory]
    [InlineData("category", true)]
    [InlineData("category.ToUpper()", true)]
    [InlineData("this.category", false)]
    [InlineData("subcategory", false)]
    public void Uses_finds_the_identifier(string code, bool expected) =>
        Assert.Equal(expected, TemplateAuthoringFixLogic.Uses(code, "category"));

    [Fact]
    public void Dedent_keeps_the_first_line_and_removes_columns_from_the_others() =>
        Assert.Equal("class A\n{\n    int x;\n}", TemplateAuthoringFixLogic.Dedent("class A\n    {\n        int x;\n    }", 4));

    [Fact]
    public void Indent_skips_blank_lines() =>
        Assert.Equal("\n\n    a();\n\n    b();", TemplateAuthoringFixLogic.Indent("\n\na();\n\nb();", "    "));

    [Theory]
    [InlineData(": Base( x ), IFoo", 6, 10)]
    [InlineData(": Base<Dictionary<int, int>>(a, (b))", 28, 35)]
    [InlineData(": Base, IFoo(x)", -1, -1)]
    [InlineData(": OverrideMethodAspect", -1, -1)]
    public void Base_arguments_are_found(string extendsList, int open, int close) =>
        Assert.Equal(open < 0 ? null : (open, close), TemplateAuthoringFixLogic.BaseArguments(extendsList));

    [Fact]
    public void A_declaration_is_split_per_variable() =>
        Assert.Equal("int a = meta.CompileTime( 0 );\n        int b = 0;",
            TemplateAuthoringFixLogic.SplitDeclaration("int ", ["a = meta.CompileTime( 0 )", " b = 0"], "        "));

    [Theory]
    [InlineData(0, "() => items.First()")]
    [InlineData(1, "arg => items.First(arg)")]
    [InlineData(2, "(arg1, arg2) => items.First(arg1, arg2)")]
    public void A_method_group_becomes_a_lambda(int parameters, string expected) =>
        Assert.Equal(expected, TemplateAuthoringFixLogic.LambdaFor("items.First", parameters));

    [Fact]
    public void Parameter_modifiers_are_not_part_of_the_type() =>
        Assert.Equal("int[]", TemplateAuthoringFixLogic.ParameterType(" params int[] "));
}
