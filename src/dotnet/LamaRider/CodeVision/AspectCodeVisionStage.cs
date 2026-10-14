using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Application.Settings;
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

/// <summary>"N aspects" on aspect targets and "applied to N targets" on aspect classes.</summary>
[DaemonStage(HighlightingTypes = new[] { typeof(CodeInsightsHighlighting) })]
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
                if (declaration is IParameterDeclaration || declaration.DeclaredElement is not { } element) continue; // parameters: on their method
                var nameRange = declaration.GetNameDocumentRange();
                if (!nameRange.IsValid()) continue;

                var docId = DocId(element);
                var instances = model.InstancesFor(docId, path, nameRange.StartOffset.Offset, declaration.GetDocumentRange().EndOffset.Offset);
                var targets = docId != null && model.IsAspectClass(docId) ? model.TargetCountOf(docId) : 0;
                if (instances.Count == 0 && targets == 0) continue;

                var parts = new List<string>();
                if (instances.Count > 0) parts.Add(instances.Count == 1 ? "1 aspect" : $"{instances.Count} aspects");
                if (targets > 0) parts.Add(targets == 1 ? "applied to 1 target" : $"applied to {targets} targets");
                var text = string.Join(" · ", parts);
                var tooltip = instances.Count > 0
                    ? string.Join(", ", instances.Select(i => $"{Short(i.AspectClass)} ({i.AppliedVia})"))
                    : "Metalama aspect class";

                var highlighting = new CodeInsightsHighlighting(nameRange, text, tooltip, text, provider, element, null);
                provider.Remember(highlighting, AspectModel.ClickTarget(docId ?? "", instances));
                result.Add(new HighlightingInfo(nameRange, highlighting));
            }
            committer(new DaemonStageResult(result));
        }

        static string Short(string classId) => classId.Substring(classId.LastIndexOf('.') + 1);

        static string? DocId(IDeclaredElement element) => element switch
        {
            ITypeElement type => XMLDocUtil.GetTypeElementXmlDocId(type),
            ITypeMember member => XMLDocUtil.GetTypeMemberXmlDocId(member, member.ShortName),
            _ => null,
        };
    }
}
