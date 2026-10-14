using System.Collections.Generic;
using System.Runtime.CompilerServices;
using JetBrains.Application.Parts;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Daemon.CodeInsights;
using JetBrains.Rider.Model;

namespace LamaRider.CodeVision;

[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public sealed class AspectCodeInsightsProvider(AspectModelHost host) : ICodeInsightsProvider
{
    public const string Id = "LamaRider.Aspects";

    // Highlighting -> worker target id, for the click handler (the span fallback has no element doc id).
    readonly ConditionalWeakTable<CodeInsightsHighlighting, string> _targets = new();

    public string ProviderId => Id;
    public string DisplayName => "Metalama aspects";
    public CodeVisionAnchorKind DefaultAnchor => CodeVisionAnchorKind.Top;
    public ICollection<CodeVisionRelativeOrdering> RelativeOrderings => new CodeVisionRelativeOrdering[] { new CodeVisionRelativeOrderingLast() };
    public bool IsAvailableIn(ISolution solution) => true;

    public void Remember(CodeInsightsHighlighting highlighting, string target) => _targets.Add(highlighting, target);

    public void OnClick(CodeInsightHighlightInfo highlightInfo, ISolution solution, CodeInsightsClickInfo? clickInfo)
    {
        if (_targets.TryGetValue(highlightInfo.CodeInsightsHighlighting, out var target)) host.NotifyCodeVisionClicked(target);
    }

    public void OnExtraActionClick(CodeInsightHighlightInfo highlightInfo, string actionId, ISolution solution) { }
}
