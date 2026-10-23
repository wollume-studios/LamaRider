using LamaRider.Model;
using Xunit;

namespace LamaRider.Model.Tests;

public class IntroductionFixLogicTests
{
    const string Lama0502 = "The aspect 'Resettable' cannot introduce member 'ShoppingCart.Reset()' into type 'ShoppingCart' because it is already defined in type 'Cart' and is static, non-virtual or sealed.";

    [Fact]
    public void ParsesAspectAndQuotedNames()
    {
        Assert.Equal("Resettable", IntroductionFixLogic.AspectName(Lama0502));
        Assert.Equal("Cart", IntroductionFixLogic.QuotedAfter(Lama0502, "already defined in type"));
        Assert.Equal("ShoppingCart", IntroductionFixLogic.QuotedAfter(Lama0502, "into type"));
        Assert.Null(IntroductionFixLogic.QuotedAfter(Lama0502, "missing phrase"));
        Assert.Equal(["Resettable", "ShoppingCart.Reset()", "ShoppingCart", "Cart"], IntroductionFixLogic.Quoted(Lama0502));
    }

    [Theory]
    [InlineData("ShoppingCart.Reset()", "Reset", "ShoppingCart", 0)]
    [InlineData("ProductCatalog.this[int]", "this", "ProductCatalog", 1)]
    [InlineData("Document.Version", "Version", "Document", null)]
    [InlineData("Ns.Outer.M(int, Dictionary<string, int>)", "M", "Outer", 2)]
    [InlineData("Describe", "Describe", null, null)]
    public void ParsesMembers(string qualified, string member, string? type, int? parameters)
    {
        Assert.Equal(member, IntroductionFixLogic.MemberName(qualified));
        Assert.Equal(type, IntroductionFixLogic.DeclaringType(qualified));
        Assert.Equal(parameters, IntroductionFixLogic.ParameterCount(qualified));
    }

    [Theory]
    [InlineData("nameof(Describe)", "Describe")]
    [InlineData("nameof( this.Validate )", "Validate")]
    [InlineData("\"Snapshot\"", "Snapshot")]
    [InlineData("typeof(IDescribable)", null)]
    public void ReadsReferencedNames(string argument, string? name) => Assert.Equal(name, IntroductionFixLogic.ReferencedName(argument));

    [Fact]
    public void RecognizesStrategies()
    {
        Assert.True(IntroductionFixLogic.IsStrategy("OverrideStrategy.New"));
        Assert.True(IntroductionFixLogic.IsStrategy("Metalama.Framework.Aspects.InterfaceMemberOverrideStrategy.Fail"));
        Assert.False(IntroductionFixLogic.IsStrategy("IntroductionScope.Static"));
    }

    [Fact]
    public void Names()
    {
        Assert.Equal("DescribeType", IntroductionFixLogic.RenamedExisting("Describe", isStatic: true));
        Assert.Equal("DescribeInstance", IntroductionFixLogic.RenamedExisting("Describe", isStatic: false));
        Assert.Equal("Memento", IntroductionFixLogic.IntroducedTypeName("Snapshot", "Memento"));
        Assert.Equal("Snapshot2", IntroductionFixLogic.IntroducedTypeName("Snapshot", "Snapshot"));
    }

    [Fact]
    public void BuildsAutoPropertyAccessors()
    {
        Assert.Equal("{ get; set; }", IntroductionFixLogic.AutoPropertyAccessors(["get"], "set"));
        Assert.Equal("{ get; init; }", IntroductionFixLogic.AutoPropertyAccessors(["get", "set"], "init"));
        Assert.Equal("{ get; set; }", IntroductionFixLogic.AutoPropertyAccessors(["set"], "get"));
        Assert.Equal("{ get; }", IntroductionFixLogic.AutoPropertyAccessors([], "get"));
        Assert.Equal("set", IntroductionFixLogic.AccessorKind("private set { }"));
        Assert.Equal("get", IntroductionFixLogic.AccessorKind("get => meta.Target.Type.Name;"));
    }
}
