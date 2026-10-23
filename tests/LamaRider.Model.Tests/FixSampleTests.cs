using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class FixSampleTests
{
    [Fact]
    public void Code_compares_without_comments_whitespace_and_the_sample_namespace() =>
        Assert.Equal(
            FixSamples.Normalize("// This is public domain Metalama sample code.\nnamespace Doc.LAMA0221.Error;\n\nclass A { /* note */ void M() => meta.This.X(); }\n"),
            FixSamples.Normalize("namespace Doc.LAMA0221.Fixed;\nclass A\n{\n    // fixed\n    void M() => meta.This.X();\n}"));

    [Fact]
    public void Different_code_differs() =>
        Assert.NotEqual(FixSamples.Normalize("class A { void M() => this.X(); }"), FixSamples.Normalize("class A { void M() => meta.This.X(); }"));

    [Fact]
    public void Strings_keep_their_content() =>
        Assert.NotEqual(FixSamples.Normalize("var s = \"a // b\";"), FixSamples.Normalize("var s = \"a\";"));

    [Theory]
    [InlineData("LAMA0037_Error.cs", "LAMA0037_Fixed.cs.expected")]
    [InlineData("LAMA0037_Error.Aspect.cs", "LAMA0037_Fixed.Aspect.cs.expected")]
    public void Each_error_file_has_its_expected_file(string file, string expected) => Assert.Equal(expected, FixSamples.ExpectedName(file));
}
