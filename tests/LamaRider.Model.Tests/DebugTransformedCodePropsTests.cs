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
}
