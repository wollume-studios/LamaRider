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
/// classes; parameters get an inlay hint of their own.
/// </summary>
[DaemonStage(HighlightingTypes = new[] { typeof(CodeInsightsHighlighting), typeof(AspectParameterHint) })]
public sealed class AspectCodeVisionStage(AspectModelHost host, AspectCodeInsightsProvider provider) : IDaemonStage
{
    public IEnumerable<IDaemonStageProcess> CreateProcess(IDaemonProcess process, IContextBoundSettingsStore settings, DaemonProcessKind processKind)
    {
        if (processKind != DaemonProcessKind.VISIBLE_DOCUMENT || host.Model is not { } model) return Array.Empty<IDaemonStageProcess>();
        if (process.SourceFile.GetDominantPsiFile<CSharpLanguage>() is not ICSharpFile file) return Array.Empty<IDaemonStageProcess>();
        return new[] { new Process(process, file, model, provider) };
    }

    sealed class Process(IDaemonProcess daemonProcess, ICSharpFile file, AspectModel model, AspectCodeInsightsProvider provider) : IDaemonStageProcess
    {
        public IDaemonProcess DaemonProcess => daemonProcess;

        public void Execute(Action<DaemonStageResult> committer)
        {
            var path = daemonProcess.SourceFile.GetLocation().FullPath;
            var result = new List<HighlightingInfo>();
            foreach (var declaration in file.Descendants<IDeclaration>())
            {
                if (daemonProcess.InterruptFlag) throw new OperationCanceledException();
                if (declaration.DeclaredElement is not { } element) continue;
                var nameRange = declaration.GetNameDocumentRange();
                if (!nameRange.IsValid()) continue;
                if (declaration is IParameterDeclaration)
                {
                    if (ParameterHint(element, nameRange) is { } hint) result.Add(new HighlightingInfo(hint.CalculateRange(), hint));
                    continue;
                }

                var docId = DocId(element);
                var all = model.InstancesFor(docId, path, nameRange.StartOffset.Offset, declaration.GetDocumentRange().EndOffset.Offset);
                var instances = docId != null && model.InstancesOf(docId).Count > 0 ? AspectHints.ForMember(all, docId) : all;
                var targets = docId != null && model.IsAspectClass(docId) ? model.TargetCountOf(docId) : 0;
                if (instances.Count == 0 && targets == 0) continue;

                var parts = new List<string>();
                if (instances.Count > 0) parts.Add(AspectHints.Text(instances.ToList(), model.AspectName));
                if (targets > 0) parts.Add(targets == 1 ? "applied to 1 target" : $"applied to {targets} targets");
                var text = string.Join(" · ", parts);
                var tooltip = instances.Count > 0 ? Tooltip(instances) : "Metalama aspect class";

                var highlighting = new CodeInsightsHighlighting(nameRange, text, tooltip, text, provider, element, null);
                provider.Remember(highlighting, AspectModel.ClickTarget(docId ?? "", instances));
                result.Add(new HighlightingInfo(nameRange, highlighting));
            }
            committer(new DaemonStageResult(result));
        }

        string Tooltip(IEnumerable<AspectInstanceInfo> instances) =>
            AspectHints.Tooltip(instances, model.AspectName, id => model.AspectClasses.FirstOrDefault(c => c.Id == id)?.Summary);

        AspectParameterHint? ParameterHint(IDeclaredElement element, DocumentRange nameRange)
        {
            if (element is not IParameter { ContainingParametersOwner: ITypeMember owner } parameter) return null;
            if (DocId(owner) is not { } ownerId) return null;
            var instances = AspectHints.ForParameter(model.InstancesOf(ownerId), ownerId, owner is IParametersOwner p ? p.Parameters.IndexOf(parameter) : -1);
            if (instances.Count == 0) return null;
            return new AspectParameterHint(new DocumentRange(nameRange.EndOffset), AspectHints.Text(instances.ToList(), model.AspectName),
                Tooltip(instances), instances[0].Target);
        }

        static string? DocId(IDeclaredElement element) => element switch
        {
            ITypeElement type => XMLDocUtil.GetTypeElementXmlDocId(type),
            ITypeMember member => XMLDocUtil.GetTypeMemberXmlDocId(member, member.ShortName),
            _ => null,
        };
    }
}
