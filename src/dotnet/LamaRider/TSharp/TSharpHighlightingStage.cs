using System;
using System.Collections.Generic;
using JetBrains.Application.Settings;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.Util;
using LamaRider.Model;

namespace LamaRider.TSharp;

/// <summary>Colors compile-time template code using the spans Metalama's classifier produced in the worker.</summary>
[DaemonStage(HighlightingTypes = new[] { typeof(TSharpHighlighting) })]
public sealed class TSharpHighlightingStage(AspectModelHost host) : IDaemonStage
{
    public IEnumerable<IDaemonStageProcess> CreateProcess(IDaemonProcess process, IContextBoundSettingsStore settings, DaemonProcessKind processKind)
    {
        if (processKind != DaemonProcessKind.VISIBLE_DOCUMENT) return Array.Empty<IDaemonStageProcess>();
        var classification = host.Model?.ClassificationFor(process.SourceFile.GetLocation().FullPath, process.Document.GetTextLength());
        return classification == null ? Array.Empty<IDaemonStageProcess>() : new[] { new Process(process, classification) };
    }

    sealed class Process(IDaemonProcess daemonProcess, FileClassification classification) : IDaemonStageProcess
    {
        public IDaemonProcess DaemonProcess => daemonProcess;

        public void Execute(Action<DaemonStageResult> committer)
        {
            var document = daemonProcess.Document;
            var length = document.GetTextLength();
            var result = new List<HighlightingInfo>();
            foreach (var span in classification.Spans)
            {
                if (TSharpHighlighterIds.ForKind(span.Kind) is not { } id || span.End > length) continue;
                var range = new DocumentRange(document, new TextRange(span.Start, span.End));
                result.Add(new HighlightingInfo(range, new TSharpHighlighting(id, range)));
            }
            committer(new DaemonStageResult(result));
        }
    }
}
