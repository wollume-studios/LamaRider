using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Application.Settings;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Daemon.CodeInsights;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Psi.Util;
using LamaRider.Model;

namespace LamaRider.CodeVision;

/// <summary>
/// Code Vision on aspect targets (aspect names, see <see cref="AspectHints"/>) and "applied to N targets" on aspect
/// classes; parameters and return values get an inlay hint of their own.
/// </summary>
[DaemonStage(HighlightingTypes = new[] { typeof(CodeInsightsHighlighting), typeof(AspectParameterHint) })]
public sealed class AspectCodeVisionStage(AspectModelHost host, AspectCodeInsightsProvider first, SecondAspectCodeInsightsProvider second,
    ThirdAspectCodeInsightsProvider third, AspectTargetsCodeInsightsProvider targets) : IDaemonStage
{
    public IEnumerable<IDaemonStageProcess> CreateProcess(IDaemonProcess process, IContextBoundSettingsStore settings, DaemonProcessKind processKind)
    {
        if (processKind != DaemonProcessKind.VISIBLE_DOCUMENT || host.Model is not { } model) return Array.Empty<IDaemonStageProcess>();
        if (process.SourceFile.GetDominantPsiFile<CSharpLanguage>() is not ICSharpFile file) return Array.Empty<IDaemonStageProcess>();
        return new[] { new Process(process, file, model, host.HintOptions, [first, second, third], targets) };
    }

    sealed class Process(IDaemonProcess daemonProcess, ICSharpFile file, AspectModel model, AspectHints.Options options, AspectCodeInsightsProviderBase[] aspectSlots,
        AspectCodeInsightsProviderBase targetsProvider) : IDaemonStageProcess
    {
        readonly List<HighlightingInfo> result = new();

        public IDaemonProcess DaemonProcess => daemonProcess;

        public void Execute(Action<DaemonStageResult> committer)
        {
            var path = daemonProcess.SourceFile.GetLocation().FullPath;
            foreach (var declaration in file.Descendants<IDeclaration>())
            {
                if (daemonProcess.InterruptFlag) throw new OperationCanceledException();
                if (declaration.DeclaredElement is not { } element) continue;
                var nameRange = declaration.GetNameDocumentRange();
                if (!nameRange.IsValid()) continue;
                var named = options.Style == AspectHints.Style.Named;
                if (declaration is IParameterDeclaration)
                {
                    if (named) foreach (var hint in ParameterHints(element, nameRange)) result.Add(new HighlightingInfo(hint.CalculateRange(), hint));
                    continue;
                }

                var docId = DocId(element);
                // Return value aspects: after the return type, like parameter hints after the name.
                if (named && docId != null && declaration is IMethodDeclaration { TypeUsage: { } returnType })
                    foreach (var hint in InlayHints(AspectHints.ForReturn(model.InstancesOf(docId), docId), returnType.GetDocumentRange()))
                        result.Add(new HighlightingInfo(hint.CalculateRange(), hint));
                // The declaration, its parameters and return value.
                var all = model.InstancesFor(docId, path, nameRange.StartOffset.Offset, declaration.GetDocumentRange().EndOffset.Offset);
                if (all.Count > 0)
                {
                    var member = AspectModel.ClickTarget(docId ?? "", all);
                    var hints = AspectHints.ForDeclaration(all, member, options, model.AspectName, Summary, OffersCodeFixes);
                    for (var i = 0; i < hints.Count && i < aspectSlots.Length; i++)
                        Add(aspectSlots[i], nameRange, element, hints[i].Text, hints[i].ToolTip, hints[i].TransformedCode ? TransformedCodeClick + member : member);
                }
                if (docId != null && model.IsAspectClass(docId) && model.TargetCountOf(docId) is var count and > 0)
                    Add(targetsProvider, nameRange, element, count == 1 ? "applied to 1 target" : $"applied to {count} targets", "Metalama aspect class", docId);
            }
            committer(new DaemonStageResult(result));
        }

        /// <summary>Click target prefix: open the transformed code right away (frontend, LamaRiderService).</summary>
        public const string TransformedCodeClick = "transformed|";

        string? Summary(string aspectClassId) => model.AspectClasses.FirstOrDefault(c => c.Id == aspectClassId)?.Summary;
        bool OffersCodeFixes(string aspectClassId) => model.AspectClasses.FirstOrDefault(c => c.Id == aspectClassId)?.OffersCodeFixes == true;

        void Add(AspectCodeInsightsProviderBase provider, DocumentRange nameRange, IDeclaredElement element, string text, string tooltip, string clickTarget)
        {
            var highlighting = new CodeInsightsHighlighting(nameRange, text, tooltip, text, provider, element, null);
            provider.Remember(highlighting, clickTarget);
            result.Add(new HighlightingInfo(nameRange, highlighting));
        }

        /// <summary>One inlay hint per aspect on the parameter (not attribute ones: the attribute is right there).</summary>
        IEnumerable<AspectParameterHint> ParameterHints(IDeclaredElement element, DocumentRange nameRange)
        {
            if (element is not IParameter { ContainingParametersOwner: ITypeMember owner } parameter) return [];
            if (DocId(owner) is not { } ownerId) return [];
            return InlayHints(AspectHints.ForParameter(model.InstancesOf(ownerId), ownerId, owner is IParametersOwner p ? p.Parameters.IndexOf(parameter) : -1), nameRange);
        }

        /// <summary>Inlay hints at the end of <paramref name="after"/>, one per aspect.</summary>
        IEnumerable<AspectParameterHint> InlayHints(IReadOnlyList<AspectInstanceInfo> instances, DocumentRange after) =>
            instances.Count == 0 ? [] : AspectHints.Hints(instances.ToList(), model.AspectName, Summary, OffersCodeFixes)
                .Select(hint => new AspectParameterHint(new DocumentRange(after.EndOffset), hint.Text, hint.ToolTip, instances[0].Target))
                .ToList();

        static string? DocId(IDeclaredElement element) => element switch
        {
            ITypeElement type => XMLDocUtil.GetTypeElementXmlDocId(type),
            ITypeMember member => XMLDocUtil.GetTypeMemberXmlDocId(member, member.ShortName),
            _ => null,
        };
    }
}
