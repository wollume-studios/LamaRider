using System.Collections.Generic;
using System.Linq;
using JetBrains.Application.Parts;
using JetBrains.Application.UI.Controls.BulbMenu.Items;
using JetBrains.Application.UI.Controls.Utils;
using JetBrains.Application.UI.PopupLayout;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Daemon;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.InlayHints;
using JetBrains.TextControl.DocumentMarkup;
using JetBrains.TextControl.DocumentMarkup.Adornments;
using JetBrains.UI.RichText;
using JetBrains.Util;

namespace LamaRider.CodeVision;

/// <summary>Inlay hint right after a parameter's name: the aspects on that parameter (Code Vision is per line).</summary>
[DaemonAdornmentProvider(typeof(AspectParameterHintAdornmentProvider))]
[DaemonTooltipProvider(typeof(InlayHintTooltipProvider))]
[StaticSeverityHighlighting(Severity.INFO, typeof(HighlightingGroupIds.IntraTextAdornments), AttributeId = "ReSharper Parameter Name Hint",
    OverlapResolve = OverlapResolveKind.NONE, ShowToolTipInStatusBar = false)]
public sealed class AspectParameterHint(DocumentRange range, string text, string toolTip, string target) : IInlayHintWithDescriptionHighlighting
{
    public string Text => text;

    /// <summary>Worker target id of the parameter ("M:...;Parameter=0"), what a click opens.</summary>
    public string Target => target;

    public string ToolTip => toolTip;
    public string ErrorStripeToolTip => toolTip;
    public RichText Description => toolTip;
    public bool IsValid() => range.IsValid();
    public DocumentRange CalculateRange() => range;
}

[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public sealed class AspectParameterHintAdornmentProvider(AspectModelHost host) : IHighlighterAdornmentProvider
{
    public bool IsValid(IHighlighter highlighter) => highlighter.UserData is AspectParameterHint;

    public IAdornmentDataModel? CreateDataModel(IHighlighter highlighter) =>
        highlighter.UserData is AspectParameterHint hint ? new DataModel(hint, host) : null;

    sealed class DataModel(AspectParameterHint hint, AspectModelHost host) : IAdornmentDataModel
    {
        public AdornmentData Data => new(hint.Text, null,
            AdornmentFlags.IsNavigable | AdornmentFlags.IsCtrlNotRequiredForNavigation, new AdornmentPlacement(), PushToHintMode.Default, AdornmentKind.Default);

        public IPresentableItem? ContextMenuTitle => null;
        public IEnumerable<BulbMenuItem> ContextMenuItems => Enumerable.Empty<BulbMenuItem>();
        public TextRange? SelectionRange => null;

        public void ExecuteNavigation(PopupWindowContextSource popupWindowContextSource) => host.NotifyCodeVisionClicked(hint.Target);
    }
}
