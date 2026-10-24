using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class PatternsFixLogicTests
{
    [Fact]
    public void Quoted_parts_and_member_names_come_from_the_message()
    {
        var quoted = PatternsFixLogic.Quoted("The [InvalidateCache] aspect applied to 'ProductCatalogue.UpdatePrice(string, decimal)' cannot invalidate 'ProductCatalogue.GetPrice(string)': x.");
        Assert.Equal(["ProductCatalogue.UpdatePrice(string, decimal)", "ProductCatalogue.GetPrice(string)"], quoted);
        Assert.Equal("GetPrice", PatternsFixLogic.MemberName(quoted[1]));
        Assert.Equal("ProductCatalogue", PatternsFixLogic.DeclaringTypeName(quoted[1]));
        Assert.Equal("Email", PatternsFixLogic.MemberName("Customer.Email"));
        Assert.Null(PatternsFixLogic.DeclaringTypeName("Person"));
    }

    [Fact]
    public void Parameter_types_split_at_top_level_commas()
    {
        Assert.Equal(["string", "Dictionary<string, int>"], PatternsFixLogic.ParameterTypes("A.M(string, Dictionary<string, int>)"));
        Assert.Empty(PatternsFixLogic.ParameterTypes("A.M()")!);
        Assert.Null(PatternsFixLogic.ParameterTypes("A.P"));
    }

    [Fact]
    public void Ambiguous_inequalities_have_a_strict_and_a_non_strict_replacement()
    {
        Assert.Equal(("StrictlyPositive", "NonNegative"), PatternsFixLogic.InequalityAlternatives("Positive"));
        Assert.Equal(("StrictlyLessThan", "LessThanOrEqual"), PatternsFixLogic.InequalityAlternatives("LessThan"));
        Assert.Null(PatternsFixLogic.InequalityAlternatives("Range"));
    }

    [Fact]
    public void Wider_numeric_types_hold_the_range()
    {
        Assert.Equal(["ushort", "int", "uint"],
            PatternsFixLogic.WiderNumericTypes("The [Range] contract cannot be applied to 'ServerSettings.Port' because the value range [1024, 65535] cannot be satisfied by the type Byte."));
        // A negative range on an unsigned type: signed types of at least the same size.
        Assert.Equal(["int", "long"],
            PatternsFixLogic.WiderNumericTypes("The [StrictlyNegative] contract cannot be applied to 'A.X' because the value range [-∞, 0[ cannot be satisfied by the type UInt32."));
        Assert.Empty(PatternsFixLogic.WiderNumericTypes("Something else."));
    }

    [Fact]
    public void Mutable_collections_have_immutable_counterparts()
    {
        Assert.Equal(new PatternsFixLogic.ImmutableCollection("ImmutableArray<string>", "ToImmutableArray"), PatternsFixLogic.ImmutableCollectionFor("List<string>"));
        Assert.Equal("ImmutableArray<int>", PatternsFixLogic.ImmutableCollectionFor("int[]")!.Type);
        Assert.Equal("ImmutableDictionary<string, List<int>>", PatternsFixLogic.ImmutableCollectionFor("System.Collections.Generic.Dictionary<string, List<int>>")!.Type);
        Assert.Equal("ToImmutableHashSet", PatternsFixLogic.ImmutableCollectionFor("HashSet<Guid>")!.Conversion);
        Assert.Null(PatternsFixLogic.ImmutableCollectionFor("Customer"));
    }
}
