using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Application.Progress;
using JetBrains.Application.Settings;
using JetBrains.Application.StdApplicationUI;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.Features.SolutionBuilders;
using JetBrains.ReSharper.Feature.Services.ContextActions;
using JetBrains.ReSharper.Feature.Services.CSharp.ContextActions;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.QuickFixes;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Psi.Util;
using JetBrains.TextControl;
using JetBrains.Util;
using LamaRider.Model;

namespace LamaRider.Errors;

/// <summary>Project-wide Metalama errors (no location, e.g. LAMA0042) underlined on the user's aspect classes of that project.</summary>
[DaemonStage(HighlightingTypes = new[] { typeof(MetalamaProjectErrorHighlighting) })]
public sealed class MetalamaErrorStage(AspectModelHost host) : IDaemonStage
{
    public IEnumerable<IDaemonStageProcess> CreateProcess(IDaemonProcess process, IContextBoundSettingsStore settings, DaemonProcessKind processKind)
    {
        if (processKind != DaemonProcessKind.VISIBLE_DOCUMENT || host.Model is not { } model || !model.Diagnostics.Any()) return Array.Empty<IDaemonStageProcess>();
        if (process.SourceFile.GetDominantPsiFile<CSharpLanguage>() is not ICSharpFile file) return Array.Empty<IDaemonStageProcess>();
        return new[] { new Process(process, file, model) };
    }

    sealed class Process(IDaemonProcess daemonProcess, ICSharpFile file, AspectModel model) : IDaemonStageProcess
    {
        public IDaemonProcess DaemonProcess => daemonProcess;

        public void Execute(Action<DaemonStageResult> committer)
        {
            var result = new List<HighlightingInfo>();
            foreach (var declaration in file.Descendants<IClassDeclaration>())
            {
                if (declaration.DeclaredElement is not { } type) continue;
                var nameRange = declaration.GetNameDocumentRange();
                if (!nameRange.IsValid()) continue;
                foreach (var diagnostic in model.ProjectErrorsFor(XMLDocUtil.GetTypeElementXmlDocId(type)))
                    result.Add(new HighlightingInfo(nameRange, new MetalamaProjectErrorHighlighting(diagnostic, nameRange)));
            }
            committer(new DaemonStageResult(result));
        }
    }
}

[StaticSeverityHighlighting(Severity.ERROR, typeof(HighlightingGroupIds.CodeSmellStatic), OverlapResolve = OverlapResolveKind.ERROR, ShowToolTipInStatusBar = true)]
public sealed class MetalamaProjectErrorHighlighting(DiagnosticInfo diagnostic, DocumentRange range) : IHighlighting
{
    public DiagnosticInfo Diagnostic => diagnostic;
    public string ToolTip => $"Metalama {diagnostic.Id} in project '{diagnostic.Project}': {diagnostic.Message}";
    public string ErrorStripeToolTip => ToolTip;
    public bool IsValid() => true;
    public DocumentRange CalculateRange() => range;
}

[QuickFix]
public sealed class OpenMetalamaErrorDocumentation(MetalamaProjectErrorHighlighting highlighting) : QuickFixBase
{
    public override string Text => $"Open {highlighting.Diagnostic.Id} documentation";
    public override bool IsAvailable(IUserDataHolder cache) => highlighting.Diagnostic.Id.StartsWith("LAMA", StringComparison.Ordinal);

    protected override Action<ITextControl>? ExecutePsiTransaction(ISolution solution, IProgressIndicator progress) =>
        _ => solution.GetComponent<OpensUri>().OpenUri(new Uri($"https://doc.metalama.net/errors/{highlighting.Diagnostic.Id.ToLowerInvariant()}"));
}

/// <summary>
/// Metalama pauses design-time analysis (LAMA0118) after compile-time code changes until the project is built.
/// Rider reports that from its Roslyn host, out of ReSharper's reach, so this is offered in every compile-time file.
/// </summary>
[ContextAction(GroupType = typeof(CSharpContextActions), Name = "Build project to resume Metalama analysis",
    Description = "Builds the project of an aspect or template file, so Metalama's design-time analysis resumes.")]
public sealed class BuildToResumeMetalamaAction(ICSharpContextActionDataProvider provider) : ContextActionBase
{
    public override string Text => $"Build '{provider.Project?.Name}' to resume Metalama analysis";

    public override bool IsAvailable(IUserDataHolder cache) =>
        provider.Project != null
        && provider.Solution.GetComponent<AspectModelHost>().Model is { } model
        && model.IsCompileTimeFile(provider.SourceFile.GetLocation().FullPath);

    protected override Action<ITextControl>? ExecutePsiTransaction(ISolution solution, IProgressIndicator progress)
    {
        var project = provider.Project!;
        return _ => MetalamaBuild.Run(solution, project);
    }
}

public static class MetalamaBuild
{
    public static void Run(ISolution solution, params IProject[] projects) => Run(solution, (IReadOnlyCollection<IProject>) projects);

    public static void Run(ISolution solution, IReadOnlyCollection<IProject> projects)
    {
        var builder = solution.GetComponent<ISolutionBuilder>();
        var request = builder.CreateBuildRequest(BuildSessionTarget.Build, projects, SolutionBuilderRequestSilentMode.Default, new SolutionBuilderRequestAdvancedSettings());
        builder.ExecuteBuildRequest(request);
    }
}
