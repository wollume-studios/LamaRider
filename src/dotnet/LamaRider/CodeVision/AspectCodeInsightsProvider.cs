using System.Collections.Generic;
using System.Runtime.CompilerServices;
using JetBrains.Application.Parts;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Daemon.CodeInsights;
using JetBrains.Rider.Model;

namespace LamaRider.CodeVision;

/// <summary>
/// Rider shows one Code Vision entry per provider and declaration, so each aspect hint has a provider of its own:
/// <see cref="AspectCodeInsightsProvider"/>, <see cref="SecondAspectCodeInsightsProvider"/> and <see cref="ThirdAspectCodeInsightsProvider"/> (one hint each, see
/// AspectHints.MaxNamed, plus "transformed code"), <see cref="AspectTargetsCodeInsightsProvider"/> for "applied to N targets" on aspect classes.
/// </summary>
public abstract class AspectCodeInsightsProviderBase(AspectModelHost host) : ICodeInsightsProvider
{
    // Highlighting -> worker target id, for the click handler (the span fallback has no element doc id).
    readonly ConditionalWeakTable<CodeInsightsHighlighting, string> _targets = new();

    public abstract string ProviderId { get; }
    public abstract string DisplayName { get; }
    public CodeVisionAnchorKind DefaultAnchor => CodeVisionAnchorKind.Top;
    public abstract ICollection<CodeVisionRelativeOrdering> RelativeOrderings { get; }
    public bool IsAvailableIn(ISolution solution) => true;

    public void Remember(CodeInsightsHighlighting highlighting, string target) => _targets.Add(highlighting, target);

    public void OnClick(CodeInsightHighlightInfo highlightInfo, ISolution solution, CodeInsightsClickInfo? clickInfo)
    {
        if (_targets.TryGetValue(highlightInfo.CodeInsightsHighlighting, out var target)) host.NotifyCodeVisionClicked(target);
    }

    public void OnExtraActionClick(CodeInsightHighlightInfo highlightInfo, string actionId, ISolution solution) { }
}

[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public sealed class AspectCodeInsightsProvider(AspectModelHost host) : AspectCodeInsightsProviderBase(host)
{
    public const string Id = "LamaRider.Aspects";
    public override string ProviderId => Id;
    public override string DisplayName => "Metalama aspects";
    public override ICollection<CodeVisionRelativeOrdering> RelativeOrderings => new CodeVisionRelativeOrdering[] { new CodeVisionRelativeOrderingLast() };
}

[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public sealed class SecondAspectCodeInsightsProvider(AspectModelHost host) : AspectCodeInsightsProviderBase(host)
{
    public const string Id = "LamaRider.Aspects.Second";
    public override string ProviderId => Id;
    public override string DisplayName => "Metalama aspects (second aspect)";
    public override ICollection<CodeVisionRelativeOrdering> RelativeOrderings => new CodeVisionRelativeOrdering[] { new CodeVisionRelativeOrderingAfter(AspectCodeInsightsProvider.Id) };
}

[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public sealed class ThirdAspectCodeInsightsProvider(AspectModelHost host) : AspectCodeInsightsProviderBase(host)
{
    public const string Id = "LamaRider.Aspects.Third";
    public override string ProviderId => Id;
    public override string DisplayName => "Metalama aspects (third hint, e.g. transformed code)";
    public override ICollection<CodeVisionRelativeOrdering> RelativeOrderings => new CodeVisionRelativeOrdering[] { new CodeVisionRelativeOrderingAfter(SecondAspectCodeInsightsProvider.Id) };
}

[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public sealed class AspectTargetsCodeInsightsProvider(AspectModelHost host) : AspectCodeInsightsProviderBase(host)
{
    public const string Id = "LamaRider.AspectTargets";
    public override string ProviderId => Id;
    public override string DisplayName => "Metalama aspect targets";
    public override ICollection<CodeVisionRelativeOrdering> RelativeOrderings => new CodeVisionRelativeOrdering[] { new CodeVisionRelativeOrderingAfter(ThirdAspectCodeInsightsProvider.Id) };
}
