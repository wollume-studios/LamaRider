using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class DebugTransformedCodePropsTests
{
    const string UserProps = "<Project>\n  <PropertyGroup>\n    <LangVersion>latest</LangVersion>\n  </PropertyGroup>\n</Project>\n";

    [Fact]
    public void Enable_creates_props_when_missing()
    {
        var text = DebugTransformedCodeProps.Enable(null);
        Assert.True(DebugTransformedCodeProps.IsEnabled(text));
        Assert.Contains("<MetalamaDebugTransformedCode>true</MetalamaDebugTransformedCode>", text);
    }

    [Fact]
    public void Enable_keeps_existing_content()
    {
        var text = DebugTransformedCodeProps.Enable(UserProps);
        Assert.Contains("<LangVersion>latest</LangVersion>", text);
        Assert.True(DebugTransformedCodeProps.IsEnabled(text));
        Assert.EndsWith("</Project>\n", text);
    }

    [Fact]
    public void Enable_is_idempotent() =>
        Assert.Equal(DebugTransformedCodeProps.Enable(UserProps), DebugTransformedCodeProps.Enable(DebugTransformedCodeProps.Enable(UserProps)));

    [Fact]
    public void Disable_restores_user_content() =>
        Assert.Equal(UserProps, DebugTransformedCodeProps.Disable(DebugTransformedCodeProps.Enable(UserProps)));

    [Fact]
    public void Disable_returns_null_when_only_our_group_was_there() =>
        Assert.Null(DebugTransformedCodeProps.Disable(DebugTransformedCodeProps.Enable(null)));

    [Fact]
    public void Enable_rejects_props_without_project_end_tag() =>
        Assert.Throws<InvalidDataException>(() => DebugTransformedCodeProps.Enable("<Project>"));

    [Fact]
    public void Formatting_off_is_written_without_debugging()
    {
        var text = DebugTransformedCodeProps.Write(null, new DebugTransformedCodeProps.Options(DebugTransformedCode: false, FormatOutput: false))!;
        Assert.Contains("<MetalamaFormatOutput>false</MetalamaFormatOutput>", text);
        Assert.DoesNotContain("MetalamaDebugTransformedCode", text);
        Assert.Equal(new DebugTransformedCodeProps.Options(false, false), DebugTransformedCodeProps.Read(text));
    }

    [Fact]
    public void Both_settings_share_one_group()
    {
        var text = DebugTransformedCodeProps.Write(DebugTransformedCodeProps.Enable(null), new DebugTransformedCodeProps.Options(true, false))!;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "Label=\"LamaRider\""));
        Assert.Equal(new DebugTransformedCodeProps.Options(true, false), DebugTransformedCodeProps.Read(text));
    }

    [Fact]
    public void Defaults_remove_the_group() =>
        Assert.Null(DebugTransformedCodeProps.Write(DebugTransformedCodeProps.Enable(null), new DebugTransformedCodeProps.Options(false, true)));

    [Fact]
    public void Readable_compile_time_code_is_written_on_its_own()
    {
        // Metalama formats its compiled copy of aspects, templates and fabrics: what the debugger shows when stopping there.
        var text = DebugTransformedCodeProps.Write(null, new DebugTransformedCodeProps.Options(false, FormatCompileTimeCode: true))!;
        Assert.Contains("<MetalamaFormatCompileTimeCode>true</MetalamaFormatCompileTimeCode>", text);
        Assert.DoesNotContain("MetalamaDebugTransformedCode", text);
        Assert.Equal(new DebugTransformedCodeProps.Options(false, FormatCompileTimeCode: true), DebugTransformedCodeProps.Read(text));
        Assert.Null(DebugTransformedCodeProps.Write(text, new DebugTransformedCodeProps.Options(false)));
    }
}
