using LamaRider.Worker;

namespace LamaRider.Worker.Tests;

public sealed class AttributeArgumentsTests
{
    public enum Level { Low, High }

    [Flags]
    public enum Options { None = 0, A = 1, B = 2 }

    public sealed class Plain : Attribute
    {
        public string? Name { get; set; }
        public int Count { get; set; } = 3;
        public Level Level { get; set; }
        public Options Options { get; set; }
        public Type? Kind { get; set; }
        public int[]? Values { get; set; }
        public int ReadOnly => 7;
    }

    public sealed class WithConstructor(int min, string label) : Attribute
    {
        public int Min { get; } = min;
        public string Label { get; } = label;
        public bool Strict { get; set; }
    }

    public sealed class Unmatched(int secret) : Attribute
    {
        public int Twice { get; } = secret * 2;
    }

    public sealed class NotAnAttribute;

    [Fact]
    public void Default_values_give_an_empty_attribute() =>
        Assert.Equal(new AttributeSetup([], []), AttributeArguments.Of(new Plain()));

    [Fact]
    public void Changed_properties_become_named_arguments()
    {
        var setup = AttributeArguments.Of(new Plain
        {
            Name = "a \"b\"", Count = 5, Level = Level.High, Options = Options.A | Options.B, Kind = typeof(List<int>), Values = [1, 2],
        })!;
        Assert.Empty(setup.Arguments);
        Assert.Equal(
            [
                new NamedArgument("Count", "5"),
                new NamedArgument("Kind", "typeof(global::System.Collections.Generic.List<int>)"),
                new NamedArgument("Level", "global::LamaRider.Worker.Tests.AttributeArgumentsTests.Level.High"),
                new NamedArgument("Name", "\"a \\\"b\\\"\""),
                new NamedArgument("Options",
                    "global::LamaRider.Worker.Tests.AttributeArgumentsTests.Options.A | global::LamaRider.Worker.Tests.AttributeArgumentsTests.Options.B"),
                new NamedArgument("Values", "new int[] { 1, 2 }"),
            ],
            setup.Named.OrderBy(n => n.Name));
    }

    [Fact]
    public void Constructor_arguments_come_from_the_members_named_like_the_parameters() =>
        Assert.Equal(new AttributeSetup(["2", "\"x\""], [new NamedArgument("Strict", "true")]),
            AttributeArguments.Of(new WithConstructor(2, "x") { Strict = true }));

    [Fact]
    public void Aspects_that_cannot_be_written_as_attributes_give_null()
    {
        Assert.Null(AttributeArguments.Of(new Unmatched(1))); // the constructor argument is not kept anywhere
        Assert.Null(AttributeArguments.Of(new NotAnAttribute()));
    }
}
