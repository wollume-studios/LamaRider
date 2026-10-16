using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class AspectHintsTests
{
    const string Add = "M:Calc.Add(System.Int32,System.Int32)";

    static AspectInstanceInfo On(string target, string aspect, string via) =>
        new() { AspectClass = "T:" + aspect, Target = target, AppliedVia = via };

    static string Name(string classId) => classId.Substring(2);

    [Fact]
    public void Aspects_added_by_attribute_are_not_named_the_attribute_shows_them()
    {
        Assert.Equal("transformed code", AspectHints.Text([On(Add, "Log", "Attribute")], Name));
        Assert.Equal("transformed code", AspectHints.Text([On(Add, "Log", "Attribute"), On(Add, "Cache", "Attribute")], Name));
    }

    [Fact]
    public void Aspects_added_elsewhere_are_named_with_their_origin()
    {
        Assert.Equal("GreaterThan1 (fabric)", AspectHints.Text([On(Add, "GreaterThan1", "Fabric")], Name));
        Assert.Equal("Audit (inherited), Trace (fabric)",
            AspectHints.Text([On(Add, "Log", "Attribute"), On(Add, "Audit", "Inherited"), On(Add, "Trace", "Fabric")], Name));
    }

    [Fact]
    public void Many_aspects_collapse_to_a_count()
    {
        var many = new[] { On(Add, "A", "Fabric"), On(Add, "B", "Fabric"), On(Add, "C", "Inherited"), On(Add, "Log", "Attribute") };
        Assert.Equal("4 aspects", AspectHints.Text(many, Name));
    }

    [Fact]
    public void The_same_aspect_twice_is_named_once()
    {
        // e.g. a fabric adding the aspect to a member and Metalama reporting it per target framework
        Assert.Equal("GreaterThan1 (fabric)", AspectHints.Text([On(Add, "GreaterThan1", "Fabric"), On(Add, "GreaterThan1", "Fabric")], Name));
    }

    [Fact]
    public void Parameter_aspects_from_fabrics_get_their_own_hint_attribute_ones_stay_with_the_method()
    {
        var all = new[]
        {
            On(Add, "Log", "Attribute"), On(Add + ";Return", "NotNull", "Attribute"),
            On(Add + ";Parameter=0", "GreaterThan1", "Fabric"), On(Add + ";Parameter=1", "Positive", "Attribute"),
        };
        // The method's hint: its own aspects plus parameter aspects the code shows as attributes (the hint offers their transformed code).
        Assert.Equal(["T:Log", "T:NotNull", "T:Positive"], AspectHints.ForMember(all, Add).Select(i => i.AspectClass));
        Assert.Equal(["T:GreaterThan1"], AspectHints.ForParameter(all, Add, 0).Select(i => i.AspectClass));
        Assert.Empty(AspectHints.ForParameter(all, Add, 1)); // [Positive] is visible on the parameter
        Assert.Empty(AspectHints.ForParameter(all, Add, 2));
    }
}
