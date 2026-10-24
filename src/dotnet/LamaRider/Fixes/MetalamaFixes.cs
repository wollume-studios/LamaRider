using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JetBrains.Application.Progress;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Bulbs;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.Intentions;
using JetBrains.ReSharper.Feature.Services.Protocol;
using JetBrains.ReSharper.Feature.Services.QuickFixes;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.TextControl;
using JetBrains.Util;
using LamaRider.Actions;
using LamaRider.Model;
using static JetBrains.Rider.Model.SolutionLamaRiderModelEx;

namespace LamaRider.Fixes;

/// <summary>A Metalama diagnostic in the editor (error, warning or info), what quick-fixes attach to.</summary>
public interface IMetalamaDiagnosticHighlighting : IHighlighting
{
    DiagnosticInfo Diagnostic { get; }
    ISolution Solution { get; }
}

/// <summary>A quick-fix for a Metalama diagnostic: <see cref="Apply"/> records edits on the context, which applies them.</summary>
public sealed record Fix(string Text, Action<FixContext> Apply);

/// <summary>
/// Quick-fixes for some diagnostic ids. Subclasses are found by reflection (one per group of related diagnostics), so a
/// group lives in a file of its own.
/// </summary>
public abstract class FixProvider
{
    public abstract IEnumerable<string> Ids { get; }

    /// <summary>The fixes that apply here; empty when the context doesn't fit (e.g. the code is not where expected).</summary>
    public abstract IEnumerable<Fix> Fixes(FixContext context);
}

/// <summary>
/// Where a diagnostic is and what can be edited: PSI to find nodes, text edits to change them (applied together, as one
/// undoable command, after the fix has run), project file edits, and a message for the user.
/// </summary>
public sealed class FixContext
{
    readonly List<(DocumentRange Range, string Text)> _edits = new();
    readonly List<(IProject Project, Func<string, string> Edit)> _projectEdits = new();
    readonly List<(string Path, string Text)> _newFiles = new();

    public FixContext(ISolution solution, DiagnosticInfo diagnostic, ICSharpFile? file, DocumentRange range, AspectModel? model)
    {
        Solution = solution;
        Diagnostic = diagnostic;
        File = file;
        Range = range;
        Model = model;
    }

    public ISolution Solution { get; }
    public DiagnosticInfo Diagnostic { get; }
    public ICSharpFile? File { get; }
    /// <summary>The reported span (the whole line when Metalama gave only a line).</summary>
    public DocumentRange Range { get; }
    public AspectModel? Model { get; }
    public IDocument? Document => Range.IsValid() ? Range.Document : null;
    public IProject? Project => File?.GetSourceFile()?.GetProject();
    public string? Message { get; private set; }

    /// <summary>The smallest node covering the reported span.</summary>
    public ITreeNode? Node =>
        File != null && Range.IsValid() && File.Translate(Range) is var tree && tree.IsValid() ? File.FindNodeAt(tree) : null;

    /// <summary>The closest node of type <typeparamref name="T"/> at or around the reported span.</summary>
    public T? At<T>() where T : class, ITreeNode => Node?.GetContainingNode<T>(returnThis: true);

    /// <summary>The nodes of type <typeparamref name="T"/> inside the reported span (e.g. an expression on the line).</summary>
    public IEnumerable<T> Within<T>() where T : class, ITreeNode =>
        File?.Descendants<T>().ToEnumerable().Where(n => Range.Contains(n.GetDocumentRange())) ?? Enumerable.Empty<T>();

    /// <summary>The declaration the diagnostic names (its doc id), in this file.</summary>
    public IDeclaration? Target =>
        Diagnostic.Target is not { } id || File == null ? null
            : File.Descendants<IDeclaration>().ToEnumerable().FirstOrDefault(d => d.DeclaredElement is { } e && AspectTargets.DocId(e) == AspectModel.Owner(id));

    /// <summary>Text from the document of <paramref name="node"/>.</summary>
    public static string TextOf(ITreeNode node) => node.GetText();

    public void Replace(DocumentRange range, string text) => _edits.Add((range, text));
    public void Replace(ITreeNode node, string text) => Replace(node.GetDocumentRange(), text);
    public void Insert(DocumentOffset offset, string text) => Replace(new DocumentRange(offset), text);
    public void Delete(DocumentRange range) => Replace(range, "");
    public void Delete(ITreeNode node) => Replace(node, "");
    public void EditProjectFile(IProject project, Func<string, string> edit) => _projectEdits.Add((project, edit));

    /// <summary>Creates a file (e.g. a fabric); SDK-style projects pick it up from disk.</summary>
    public void CreateFile(string path, string text) => _newFiles.Add((path, text));

    /// <summary>The files <see cref="Commit"/> created (the self-test deletes them again).</summary>
    public List<string> CreatedFiles { get; } = new();
    public void Tell(string message) => Message = Message == null ? message : Message + "\n" + message;

    internal bool HasEdits => _edits.Count > 0 || _projectEdits.Count > 0 || _newFiles.Count > 0;

    /// <summary>Document edits from the end backwards, so earlier offsets stay valid; then project files. Outside PSI transactions.</summary>
    internal void Commit(bool notify = true)
    {
        using (WriteLockCookie.Create())
            foreach (var (range, text) in _edits.Distinct().OrderByDescending(e => e.Range.StartOffset.Offset).ThenByDescending(e => e.Range.EndOffset.Offset))
                range.Document.ReplaceText(range.TextRange, text);
        foreach (var (project, edit) in _projectEdits)
        {
            var path = project.ProjectFileLocation.FullPath;
            try { System.IO.File.WriteAllText(path, edit(System.IO.File.ReadAllText(path))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Tell($"Cannot change {Path.GetFileName(path)}: {e.Message}"); }
        }
        foreach (var (path, text) in _newFiles)
        {
            try
            {
                System.IO.File.WriteAllText(path, text);
                CreatedFiles.Add(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Tell($"Cannot create {Path.GetFileName(path)}: {e.Message}"); }
        }
        if (notify && Message != null) Solution.GetProtocolSolution().GetLamaRiderModel().Info.Fire(Message);
    }
}

/// <summary>The fix providers, by diagnostic id.</summary>
public static class FixRegistry
{
    static readonly Lazy<Dictionary<string, List<FixProvider>>> Providers = new(() =>
        typeof(FixProvider).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(FixProvider).IsAssignableFrom(t))
            .Select(t => (FixProvider)Activator.CreateInstance(t)!)
            .SelectMany(p => p.Ids.Select(id => (id, p)))
            .GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.Select(x => x.p).ToList()));

    public static IReadOnlyCollection<string> Ids => Providers.Value.Keys;

    /// <summary>The specific fixes of the diagnostic, then the ones every Metalama diagnostic has.</summary>
    public static List<Fix> For(FixContext context)
    {
        var fixes = new List<Fix>();
        if (Providers.Value.TryGetValue(context.Diagnostic.Id, out var providers))
            foreach (var provider in providers)
            {
                try { fixes.AddRange(provider.Fixes(context)); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    JetBrains.Util.Logging.Logger.GetLogger(typeof(FixRegistry)).Warn($"LamaRider: fixes for {context.Diagnostic.Id} failed: {e}");
                }
            }
        fixes.AddRange(GenericFixes.For(context));
        return fixes;
    }

    /// <summary>Runs <paramref name="fix"/> and applies its edits; false when it made none.</summary>
    public static bool Apply(Fix fix, FixContext context)
    {
        fix.Apply(context);
        if (!context.HasEdits && context.Message == null) return false;
        context.Commit();
        return true;
    }
}

/// <summary>The fix context of a highlighting in the editor.</summary>
static class Contexts
{
    public static FixContext? Of(IMetalamaDiagnosticHighlighting highlighting)
    {
        var solution = highlighting.Solution;
        var range = highlighting.CalculateRange();
        if (!range.IsValid()) return null;
        var file = range.Document.GetPsiSourceFile(solution)?.GetDominantPsiFile<CSharpLanguage>() as ICSharpFile;
        return new FixContext(solution, highlighting.Diagnostic, file, range, solution.GetComponent<AspectModelHost>().Model);
    }
}

/// <summary>Alt+Enter on a Metalama diagnostic: its fixes from <see cref="FixRegistry"/>.</summary>
public abstract class MetalamaFixesBase(IMetalamaDiagnosticHighlighting highlighting) : IQuickFix
{
    public IEnumerable<IntentionAction> CreateBulbItems()
    {
        if (Contexts.Of(highlighting) is not { } context) return [];
        return FixRegistry.For(context).Select(f => (BulbActionBase)new Item(highlighting, f.Text, f)).ToQuickFixIntentions();
    }

    public bool IsAvailable(IUserDataHolder cache) => highlighting.IsValid();

    sealed class Item(IMetalamaDiagnosticHighlighting highlighting, string text, Fix fix) : BulbActionBase
    {
        public override string Text => text;

        protected override Action<ITextControl>? ExecutePsiTransaction(ISolution solution, IProgressIndicator progress)
        {
            if (Contexts.Of(highlighting) is not { } context) return null;
            // PSI is read here; the edits are text edits, applied after the transaction.
            fix.Apply(context);
            return _ => context.Commit();
        }
    }
}
