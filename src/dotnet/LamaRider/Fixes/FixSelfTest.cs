using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.Resources.Shell;
using LamaRider.Model;
using Newtonsoft.Json;

namespace LamaRider.Fixes;

/// <summary>
/// Applies every specific quick-fix of each sample of samples/MetalamaFixSamples (one project per diagnostic id) and
/// compares the code with the sample's .expected files, then restores it. Runs on the main thread with the read lock.
/// </summary>
public static class FixSelfTest
{
    public sealed record FixResult(string Text, bool Matches, string? Error);

    /// <param name="Reported">Diagnostic ids the worker reported in the sample.</param>
    public sealed record SampleResult(string Id, List<string> Reported, List<FixResult> Fixes, string? Error);

    /// <param name="progress">Called with a line of progress (the handler writes them to a file next to the report).</param>
    public static List<SampleResult> Run(ISolution solution, AspectModel model, Action<string>? progress = null)
    {
        var results = new List<SampleResult>();
        progress?.Invoke($"start: {solution.GetAllProjects().Count()} projects, {model.Diagnostics.Count} diagnostics");
        foreach (var project in solution.GetAllProjects().Where(p => Regex.IsMatch(p.Name, @"^LAMA\d{4}$")).OrderBy(p => p.Name))
        {
            try
            {
                var sample = RunSample(solution, model, project);
                results.Add(sample);
                progress?.Invoke($"{project.Name}: {sample.Fixes.Count} fixes, {sample.Fixes.Count(f => f.Matches)} match");
            }
            catch (Exception e) when (e is not OperationCanceledException) { results.Add(new SampleResult(project.Name, [], [], e.ToString())); }
        }
        return results;
    }

    static SampleResult RunSample(ISolution solution, AspectModel model, IProject project)
    {
        var id = project.Name;
        // Only the sample's own files, not the generated AssemblyInfo.cs and the like.
        var files = project.GetAllProjectFiles(f => f.Location.ExtensionNoDot == "cs" && f.Name.StartsWith(id + "_Error", StringComparison.Ordinal))
            .Select(f => f.ToSourceFile()).OfType<IPsiSourceFile>().ToList();
        var dir = project.Location.FullPath;
        var expected = FixSamples.Normalize(string.Concat(files.OrderBy(f => f.Name).Select(f =>
            File.ReadAllText(Path.Combine(dir, FixSamples.ExpectedName(f.Name))))));
        var diagnostics = model.Diagnostics.Where(d => d.File != null && files.Any(f => SamePath(f.GetLocation().FullPath, d.File))).ToList();
        var reported = diagnostics.Select(d => d.Id).Distinct().OrderBy(x => x).ToList();
        var fixResults = new List<FixResult>();
        var csproj = project.ProjectFileLocation.FullPath;

        foreach (var diagnostic in diagnostics.Where(d => d.Id == id))
        {
            int count;
            using (ReadLockCookie.Create()) count = Contexts(solution, model, diagnostic, files) is { } first ? Specific(first).Count : 0;
            for (var k = 0; k < count; k++)
            {
                var snapshot = files.ToDictionary(f => f.Document, f => f.Document.GetText());
                var csprojText = File.ReadAllText(csproj);
                string? text = null;
                FixContext? applied = null;
                try
                {
                    // PSI is read under the read lock and the fix records its edits; they are applied (write lock) after it.
                    // A fresh context per fix: the previous one's edits were undone, so PSI changed.
                    FixContext context;
                    Fix fix;
                    using (ReadLockCookie.Create())
                    {
                        solution.GetPsiServices().Files.CommitAllDocuments();
                        context = applied = Contexts(solution, model, diagnostic, files)!;
                        fix = Specific(context)[k];
                        text = fix.Text;
                        fix.Apply(context);
                    }
                    context.Commit(notify: false);
                    using (ReadLockCookie.Create()) solution.GetPsiServices().Files.CommitAllDocuments();
                    var actual = FixSamples.Normalize(string.Concat(files.OrderBy(f => f.Name).Select(f => f.Document.GetText())));
                    fixResults.Add(new FixResult(fix.Text, actual == expected, null));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    fixResults.Add(new FixResult(text ?? $"#{k}", false, e.GetType().Name + ": " + e.Message));
                }
                finally
                {
                    using (WriteLockCookie.Create())
                        foreach (var entry in snapshot)
                            if (entry.Key.GetText() != entry.Value) entry.Key.ReplaceText(new JetBrains.Util.TextRange(0, entry.Key.GetTextLength()), entry.Value);
                    if (File.ReadAllText(csproj) != csprojText) File.WriteAllText(csproj, csprojText);
                    foreach (var created in applied?.CreatedFiles ?? []) File.Delete(created);
                    using (ReadLockCookie.Create()) solution.GetPsiServices().Files.CommitAllDocuments();
                }
            }
        }
        return new SampleResult(id, reported, fixResults, null);
    }

    /// <summary>
    /// Fixes from providers that edit code: not the ones every diagnostic has (look up, suppress), nor actions with side
    /// effects outside the code (open a crash report, build).
    /// </summary>
    static List<Fix> Specific(FixContext context) =>
        FixRegistry.For(context).Where(f => !new[] { "Look up ", "Suppress ", "Open ", "Rebuild ", "Build " }.Any(f.Text.StartsWith)).ToList();

    static FixContext? Contexts(ISolution solution, AspectModel model, DiagnosticInfo diagnostic, List<IPsiSourceFile> files)
    {
        var source = files.FirstOrDefault(f => SamePath(f.GetLocation().FullPath, diagnostic.File!));
        if (source?.GetDominantPsiFile<CSharpLanguage>() is not ICSharpFile file) return null;
        var range = DiagnosticRanges.Of(diagnostic, source.Document, DiagnosticRanges.Names(file));
        return range is { } r ? new FixContext(solution, diagnostic, file, r, model) : null;
    }

    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    public static string ToJson(List<SampleResult> results) => JsonConvert.SerializeObject(results, Formatting.Indented);
}
