using System.Drawing;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.TextControl.DocumentMarkup;

namespace LamaRider.TSharp;

// Rider builds the "Metalama T#" color settings page from these registrations (same pattern as the bundled Unity plugin).
[RegisterHighlighterGroup(GroupId, "Metalama T#", HighlighterGroupPriority.LANGUAGE_SETTINGS)]
[RegisterHighlighter(CompileTime, GroupId = GroupId, RiderPresentableName = "Compile-time code",
    BackgroundColor = "#EEF3FB", DarkBackgroundColor = "#262D36", EffectType = EffectType.TEXT, Layer = HighlighterLayer.SYNTAX + 1)]
[RegisterHighlighter(CompileTimeVariable, GroupId = GroupId, RiderPresentableName = "Compile-time variable",
    ForegroundColor = "#7A3E9D", DarkForegroundColor = "#C792EA", FontStyle = FontStyle.Bold, EffectType = EffectType.TEXT, Layer = HighlighterLayer.SYNTAX + 2)]
[RegisterHighlighter(TemplateKeyword, GroupId = GroupId, RiderPresentableName = "Template keyword (meta)",
    ForegroundColor = "#B25E00", DarkForegroundColor = "#FFB86C", FontStyle = FontStyle.Bold, EffectType = EffectType.TEXT, Layer = HighlighterLayer.SYNTAX + 2)]
[RegisterHighlighter(Dynamic, GroupId = GroupId, RiderPresentableName = "Dynamic member",
    EffectType = EffectType.SOLID_UNDERLINE, ForegroundColor = "#B25E00", DarkForegroundColor = "#FFB86C", Layer = HighlighterLayer.SYNTAX + 2)]
public static class TSharpHighlighterIds
{
    public const string GroupId = "LamaRider.TSharp";
    public const string CompileTime = "LamaRider.TSharp.CompileTime";
    public const string CompileTimeVariable = "LamaRider.TSharp.CompileTimeVariable";
    public const string TemplateKeyword = "LamaRider.TSharp.TemplateKeyword";
    public const string Dynamic = "LamaRider.TSharp.Dynamic";

    public static string? ForKind(string kind) => kind switch
    {
        "CompileTime" => CompileTime,
        "CompileTimeVariable" => CompileTimeVariable,
        "TemplateKeyword" => TemplateKeyword,
        "Dynamic" => Dynamic,
        _ => null,
    };
}

[StaticSeverityHighlighting(Severity.INFO, typeof(HighlightingGroupIds.IdentifierHighlightings), OverlapResolve = OverlapResolveKind.NONE, ShowToolTipInStatusBar = false)]
public sealed class TSharpHighlighting(string attributeId, DocumentRange range) : ICustomAttributeIdHighlighting
{
    public string AttributeId => attributeId;
    public string? ToolTip => null;
    public string? ErrorStripeToolTip => null;
    public bool IsValid() => range.IsValid();
    public DocumentRange CalculateRange() => range;
}
