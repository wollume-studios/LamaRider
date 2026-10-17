using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class AspectHintsTests
{
    const string Add = "M:Calc.Add(System.Int32,System.Int32)";

    static AspectInstanceInfo On(string target, string aspect, string via, string? by = null) =>
        new() { AspectClass = "T:" + aspect, Target = target, AppliedVia = via, AppliedBy = by };

    static string Name(string classId) => classId.Substring(2);
    static string? NoDocs(string classId) => null;

    static AspectInstanceInfo On(string target, string aspect, string via, string? by, string targetName) =>
        new() { AspectClass = "T:" + aspect, Target = target, AppliedVia = via, AppliedBy = by, TargetName = targetName };

    static readonly AspectHints.Options Default = new();
    static readonly AspectHints.Options Menu = new(TransformedCodeHint: false);
    static readonly AspectHints.Options Count = new(AspectHints.Style.Count);

    static IEnumerable<string> Texts(AspectHints.Options options, params AspectInstanceInfo[] instances) =>
        AspectHints.ForDeclaration(instances, Add, options, Name, NoDocs).Select(h => h.Text);

    static IEnumerable<string> Texts(params AspectInstanceInfo[] instances) => Texts(Default, instances);

    [Fact]
    public void Aspects_added_by_attribute_are_not_named_the_attribute_shows_them()
    {
        Assert.Equal(["transformed code"], Texts(On(Add, "Log", "Attribute")));
        Assert.Equal(["transformed code"], Texts(On(Add, "Log", "Attribute"), On(Add, "Cache", "Attribute")));
    }

    [Fact]
    public void Each_aspect_added_elsewhere_gets_a_hint_of_its_own_and_the_transformed_code_one_of_its_own()
    {
        Assert.Equal(["GreaterThan1", "transformed code"], Texts(On(Add, "GreaterThan1", "Fabric")));
        Assert.Equal(["Audit", "Trace", "transformed code"], Texts(On(Add, "Log", "Attribute"), On(Add, "Audit", "Inherited"), On(Add, "Trace", "Fabric")));
    }

    [Fact]
    public void The_transformed_code_hint_is_on_the_member_also_for_parameter_and_return_value_aspects() =>
        Assert.Equal(["transformed code"], Texts(On(Add + ";Parameter=0", "GreaterThan1", "Fabric"), On(Add + ";Return", "NonNegative", "Fabric")));

    [Fact]
    public void Without_the_transformed_code_hint_a_count_leads_to_the_menu_when_nothing_is_named()
    {
        Assert.Equal(["1 aspect"], Texts(Menu, On(Add, "Log", "Attribute")));
        Assert.Equal(["GreaterThan1"], Texts(Menu, On(Add, "GreaterThan1", "Fabric")));
        Assert.Equal(["2 aspects"], Texts(Menu, On(Add + ";Parameter=0", "GreaterThan1", "Fabric"), On(Add + ";Return", "NonNegative", "Fabric")));
        Assert.All(AspectHints.ForDeclaration([On(Add, "Log", "Attribute")], Add, Menu, Name, NoDocs), h => Assert.False(h.TransformedCode));
    }

    [Fact]
    public void Count_style_counts_the_aspects_of_the_member_its_parameters_and_return_value()
    {
        var hints = AspectHints.ForDeclaration([
            On(Add, "Log", "Attribute", null, "Calc.Add(int, int)"),
            On(Add + ";Parameter=0", "GreaterThan1", "Fabric", "App.Fabric", "Calc.Add(int, int)/a"),
            On(Add + ";Parameter=1", "GreaterThan1", "Fabric", "App.Fabric", "Calc.Add(int, int)/b"),
            On(Add + ";Return", "NonNegative", "Fabric", "App.Fabric", "Calc.Add(int, int)/return"),
        ], Add, Count, Name, NoDocs);
        Assert.Equal(["4 aspects", "transformed code"], hints.Select(h => h.Text));
        Assert.Equal(
            "Log — added by an attribute\n\nGreaterThan1 on a — added by fabric App.Fabric\n\nGreaterThan1 on b — added by fabric App.Fabric\n\n" +
            "NonNegative on the return value — added by fabric App.Fabric", hints[0].ToolTip);
        Assert.Equal(["1 aspect"], Texts(new AspectHints.Options(AspectHints.Style.Count, false), On(Add, "GreaterThan1", "Fabric")));
    }

    [Fact]
    public void Many_aspects_collapse_to_a_count() =>
        Assert.Equal(["3 aspects", "transformed code"], Texts(On(Add, "A", "Fabric"), On(Add, "B", "Fabric"), On(Add, "C", "Inherited"), On(Add, "Log", "Attribute")));

    [Fact]
    public void The_same_aspect_twice_is_one_hint() =>
        Assert.Equal(["GreaterThan1", "transformed code"], Texts(On(Add, "GreaterThan1", "Fabric"), On(Add, "GreaterThan1", "Fabric")));

    [Fact]
    public void A_single_aspect_tooltip_says_where_it_comes_from_then_what_it_does()
    {
        var hint = AspectHints.Hints([On(Add, "GreaterThan1", "Fabric", "EFA.ProjectFabric")], Name,
            _ => "Ensures greater than 1 for the parameter. Throws an exception if the value is less than 2.").Single();
        Assert.Equal(
            "Added by fabric EFA.ProjectFabric\n" +
            "Ensures greater than 1 for the parameter. Throws an exception if the\n" +
            "value is less than 2.",
            hint.ToolTip);
    }

    [Fact]
    public void The_tooltip_says_when_an_aspect_may_offer_code_fixes()
    {
        // Metalama.Extensions.CodeFixes is a Premium component: its code fixes need a Metalama Professional license.
        var hint = AspectHints.Hints([On(Add, "Audit", "Fabric", "App.Fabric")], Name, NoDocs, id => id == "T:Audit").Single();
        Assert.Equal("Added by fabric App.Fabric\nMay offer code fixes (Alt+Enter); they need a Metalama Professional license.", hint.ToolTip);
    }

    [Fact]
    public void A_collapsed_tooltip_names_each_aspect()
    {
        var hint = AspectHints.ForDeclaration([On(Add, "Log", "Attribute"), On(Add, "Cache", "Attribute")], Add, Menu, Name,
            id => id == "T:Log" ? "Writes a line when the method starts." : null).Single();
        Assert.Equal("Log — added by an attribute\nWrites a line when the method starts.\n\nCache — added by an attribute", hint.ToolTip);
    }

    [Theory]
    [InlineData("Fabric", "EFA.ProjectFabric", "Added by fabric EFA.ProjectFabric")]
    [InlineData("Fabric", null, "Added by a fabric")]
    [InlineData("Inherited", "LogAttribute on Base.Run()", "Inherited from LogAttribute on Base.Run()")]
    [InlineData("ChildAspect", "AuditAttribute on Calc", "Added by aspect AuditAttribute on Calc")]
    [InlineData("RequiredAspect", "AuditAttribute on Calc", "Required by aspect AuditAttribute on Calc")]
    [InlineData("Interactive", null, "Added from the IDE")]
    public void Origin_names_what_added_the_aspect(string via, string? by, string expected) =>
        Assert.Equal(expected, AspectHints.Origin(via, by));

    [Fact]
    public void Parameter_aspects_from_fabrics_get_their_own_hint_attribute_ones_none()
    {
        var all = new[]
        {
            On(Add, "Log", "Attribute"), On(Add + ";Return", "NotNull", "Attribute"),
            On(Add + ";Parameter=0", "GreaterThan1", "Fabric"), On(Add + ";Parameter=1", "Positive", "Attribute"),
        };
        Assert.Equal(["T:GreaterThan1"], AspectHints.ForParameter(all, Add, 0).Select(i => i.AspectClass));
        Assert.Empty(AspectHints.ForParameter(all, Add, 1)); // [Positive] is visible on the parameter
        Assert.Empty(AspectHints.ForParameter(all, Add, 2));
    }

    [Fact]
    public void Return_value_aspects_from_fabrics_get_their_own_hint_attribute_ones_none()
    {
        var all = new[] { On(Add, "Log", "Attribute"), On(Add + ";Return", "NonNegative", "Fabric"), On(Add + ";Return", "NotNull", "Attribute") };
        Assert.Equal(["T:NonNegative"], AspectHints.ForReturn(all, Add).Select(i => i.AspectClass));
    }
}
