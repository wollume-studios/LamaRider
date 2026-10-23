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
using JetBrains.ReSharper.Feature.Services.Protocol;
using static JetBrains.Rider.Model.SolutionLamaRiderModelEx;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Psi.Util;
using JetBrains.TextControl;
using JetBrains.Util;
using JetBrains.Util.dataStructures.TypedIntrinsics;
using LamaRider.Model;

namespace LamaRider.Errors;

/// <summary>
/// Metalama diagnostics from the worker model, which has them seconds after a save (errors as errors; warnings and infos
/// as hints that carry quick-fixes): project-wide ones (no location, e.g. LAMA0042)
/// on the user's aspect classes of that project; located ones (e.g. LAMA0037) on their target declaration or line. Rider's
/// own Metalama squiggles wait for a build after compile-time code changed (LAMA0118).
/// </summary>
[DaemonStage(HighlightingTypes = new[] { typeof(MetalamaProjectErrorHighlighting), typeof(MetalamaDiagnosticHint) })]
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
                foreach (var diagnostic in model.ProjectDiagnosticsFor(XMLDocUtil.GetTypeElementXmlDocId(type)))
                    result.Add(new HighlightingInfo(nameRange, diagnostic.Severity == "Error"
                        ? new MetalamaProjectErrorHighlighting(diagnostic, nameRange, daemonProcess.Solution)
                        : new MetalamaDiagnosticHint(diagnostic, nameRange, daemonProcess.Solution)));
            }

            var located = model.LocatedDiagnosticsIn(daemonProcess.SourceFile.GetLocation().FullPath).ToList();
            if (located.Count > 0)
            {
                var names = Fixes.DiagnosticRanges.Names(file);
                var document = daemonProcess.Document;
                foreach (var diagnostic in located)
                {
                    if (Fixes.DiagnosticRanges.Of(diagnostic, document, names) is not { } range) continue;
                    IHighlighting highlighting = diagnostic.Severity == "Error"
                        ? new MetalamaProjectErrorHighlighting(diagnostic, range, daemonProcess.Solution)
                        : new MetalamaDiagnosticHint(diagnostic, range, daemonProcess.Solution);
                    result.Add(new HighlightingInfo(range, highlighting));
                }
            }
            committer(new DaemonStageResult(result));
        }

    }
}

[StaticSeverityHighlighting(Severity.ERROR, typeof(HighlightingGroupIds.CodeSmellStatic), OverlapResolve = OverlapResolveKind.ERROR, ShowToolTipInStatusBar = true)]
public sealed class MetalamaProjectErrorHighlighting(DiagnosticInfo diagnostic, DocumentRange range, ISolution solution) : Fixes.IMetalamaDiagnosticHighlighting
{
    public DiagnosticInfo Diagnostic => diagnostic;
    public ISolution Solution => solution;
    public string ToolTip => diagnostic.File == null
        ? $"Metalama {diagnostic.Id} in project '{diagnostic.Project}': {diagnostic.Message}"
        : $"Metalama {diagnostic.Id}: {diagnostic.Message}";
    public string ErrorStripeToolTip => ToolTip;
    public bool IsValid() => true;
    public DocumentRange CalculateRange() => range;
}

/// <summary>
/// A Metalama warning or info. Rider shows its own squiggle for it; this one is barely visible and carries the quick-fixes.
/// </summary>
[StaticSeverityHighlighting(Severity.HINT, typeof(HighlightingGroupIds.CodeSmellStatic), OverlapResolve = OverlapResolveKind.NONE)]
public sealed class MetalamaDiagnosticHint(DiagnosticInfo diagnostic, DocumentRange range, ISolution solution) : Fixes.IMetalamaDiagnosticHighlighting
{
    public DiagnosticInfo Diagnostic => diagnostic;
    public ISolution Solution => solution;
    public string ToolTip => $"Metalama {diagnostic.Id}: {diagnostic.Message}";
    public string ErrorStripeToolTip => ToolTip;
    public bool IsValid() => true;
    public DocumentRange CalculateRange() => range;
}

[QuickFix]
public sealed class MetalamaErrorFixes(MetalamaProjectErrorHighlighting highlighting) : Fixes.MetalamaFixesBase(highlighting);

[QuickFix]
public sealed class MetalamaHintFixes(MetalamaDiagnosticHint highlighting) : Fixes.MetalamaFixesBase(highlighting);

/// <summary>
/// Metalama pauses design-time analysis (LAMA0118) after compile-time code changes until the project is built.
/// Rider reports that from its Roslyn host, out of ReSharper's reach, so this is offered in every compile-time file.
/// </summary>
[ContextAction(GroupType = typeof(CSharpContextActions), Name = "Build project to resume Metalama analysis",
    Description = "Builds the project of an aspect or template file changed since its last build, so Metalama's design-time analysis resumes.")]
public sealed class BuildToResumeMetalamaAction(ICSharpContextActionDataProvider provider) : ContextActionBase
{
    public override string Text => $"Build '{provider.Project?.Name}' to resume Metalama analysis";

    public override bool IsAvailable(IUserDataHolder cache) =>
        provider.Project is { } project
        && provider.Solution.GetComponent<AspectModelHost>().Model is { } model
        && model.IsCompileTimeFile(provider.SourceFile.GetLocation().FullPath)
        && AnalysisPause.Likely(model.CompileTimeFilesIn(project.Location.FullPath), LastBuild(project), System.IO.File.GetLastWriteTimeUtc);

    /// <summary>When the project's output assembly was written, if it was.</summary>
    static DateTime? LastBuild(IProject project)
    {
        var output = project.GetOutputFilePath(project.GetCurrentTargetFrameworkId());
        return output.ExistsFile ? System.IO.File.GetLastWriteTimeUtc(output.FullPath) : null;
    }

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
