using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace LamaRider.Model;

// Mirrors the worker's model.json (schema 1). No JetBrains dependencies: unit-tested outside the Rider SDK.

public sealed class SourceLocation
{
    public string File = "";
    public int Start;
    public int End;
}

public sealed class AspectClassInfo
{
    public string Id = "";
    public string Name = "";
    public string Project = "";
    public bool IsLibrary;
    public SourceLocation? Location;
    public string Package = "";
    public string? Summary;
    public string? DocUrl;
}

public sealed class AspectInstanceInfo
{
    public string AspectClass = "";
    public string Target = "";
    public string TargetName = "";
    public string TargetType = "";
    public string TargetKind = "";
    public string Project = "";
    public string AppliedVia = "";
    public SourceLocation? Location;

    internal string Key => $"{AspectClass}|{Target}|{AppliedVia}";
}

public sealed class TransformationInfo
{
    public string AspectClass = "";
    public string Target = "";
    public string Kind = "";
    public string Description = "";
    public SourceLocation? Location;
}

public sealed class DiagnosticInfo
{
    public string Id = "";
    public string Severity = "";
    public string Message = "";
    public string? Target;
    public string? File;
    public int? Line;
    public string? Project;
}

public sealed class ClassifiedSpan
{
    public int Start;
    public int End;
    public string Kind = "";
}

public sealed class FileClassification
{
    public string File = "";
    public int Length; // LF-normalized length of the file when classified
    public List<ClassifiedSpan> Spans = new();
}

public sealed class TransformedFileInfo
{
    public string File = "";
    public string TransformedPath = "";
}

public sealed class AspectModel
{
    public const int SupportedSchema = 1;

    public int Schema;
    public string? MetalamaVersion;
    public List<AspectClassInfo> AspectClasses = new();
    public List<AspectInstanceInfo> Instances = new();
    public List<TransformationInfo> Transformations = new();
    public List<DiagnosticInfo> Diagnostics = new();
    public List<FileClassification> TemplateClassifications = new();
    public List<TransformedFileInfo> TransformedFiles = new();

    Dictionary<string, List<AspectInstanceInfo>> _byTarget = new();
    Dictionary<string, List<AspectInstanceInfo>> _byFile = new();
    Dictionary<string, FileClassification> _classifications = new();
    HashSet<string> _aspectClassIds = new();

    // Newtonsoft matches the camelCase JSON to these fields case-insensitively.
    public static AspectModel Parse(string json)
    {
        AspectModel? model;
        try { model = JsonConvert.DeserializeObject<AspectModel>(json); }
        catch (JsonException e) { throw new InvalidDataException("Malformed worker output: " + e.Message, e); }
        if (model == null) throw new InvalidDataException("Empty worker output");
        if (model.Schema != SupportedSchema) throw new InvalidDataException($"Unsupported worker schema {model.Schema}");
        model.Instances = model.Instances.GroupBy(i => i.Key).Select(g => g.First()).ToList();
        // Parameter targets ("M:...;Parameter=0") count for their method: Code Vision shows one hint per line.
        model._byTarget = model.Instances.GroupBy(i => Owner(i.Target)).ToDictionary(g => g.Key, g => g.ToList());
        model._byFile = model.Instances.Where(i => i.Location != null)
            .GroupBy(i => Normalize(i.Location!.File)).ToDictionary(g => g.Key, g => g.ToList());
        model._classifications = model.TemplateClassifications
            .GroupBy(c => Normalize(c.File)).ToDictionary(g => g.Key, g => g.First());
        model._aspectClassIds = new HashSet<string>(model.AspectClasses.Select(c => c.Id));
        return model;
    }

    /// <summary>
    /// Doc-ID match first; otherwise a declaration in the same file whose span ends where this declaration
    /// ends and contains its name (Roslyn spans exclude leading doc comments, so starts differ but ends agree;
    /// a containing type never ends where one of its members ends).
    /// </summary>
    public IReadOnlyList<AspectInstanceInfo> InstancesFor(string? docId, string file, int nameOffset, int declarationEnd)
    {
        if (docId != null && _byTarget.TryGetValue(docId, out var byId)) return byId;
        if (!_byFile.TryGetValue(Normalize(file), out var inFile)) return Array.Empty<AspectInstanceInfo>();
        return inFile.Where(i => i.Location!.End == declarationEnd && i.Location.Start <= nameOffset).ToList();
    }

    /// <summary>"" or " · N Metalama errors" for the status line; the frontend shows the details in a notification.</summary>
    public string ErrorSummary
    {
        get
        {
            var errors = Diagnostics.Where(d => d.Severity == "Error").ToList();
            if (errors.Count == 0) return "";
            return errors.Count == 1 ? " · 1 Metalama error" : $" · {errors.Count} Metalama errors";
        }
    }

    public int TargetCountOf(string aspectClassId) => Instances.Count(i => i.AspectClass == aspectClassId);

    public bool IsAspectClass(string docId) => _aspectClassIds.Contains(docId);

    /// <summary>
    /// Metalama errors located in <paramref name="path"/>. Rider shows them too, but only after a build once compile-time
    /// code changed (LAMA0118); the worker has them seconds after a save.
    /// </summary>
    public IEnumerable<DiagnosticInfo> LocatedErrorsIn(string path)
    {
        var normalized = Normalize(path);
        return Diagnostics.Where(d => d.Severity == "Error" && d.File != null && Normalize(d.File) == normalized);
    }

    /// <summary>Files with compile-time code (aspect classes, templates): editing them pauses Metalama until a build.</summary>
    public bool IsCompileTimeFile(string path)
    {
        var normalized = Normalize(path);
        return AspectClasses.Any(c => c.Location != null && Normalize(c.Location.File) == normalized)
            || TemplateClassifications.Any(c => Normalize(c.File) == normalized);
    }

    /// <summary>
    /// Errors without a location (e.g. LAMA0042 aspect ordering) in the project of one of the user's own aspect classes:
    /// shown on the class name, since there is no better place in the editor.
    /// </summary>
    public IEnumerable<DiagnosticInfo> ProjectErrorsFor(string aspectClassId)
    {
        var aspect = AspectClasses.FirstOrDefault(c => c.Id == aspectClassId);
        if (aspect == null || aspect.IsLibrary) return [];
        return Diagnostics.Where(d => d.Severity == "Error" && d.File == null && d.Project == aspect.Project);
    }

    /// <summary>Null when the document no longer has the length it had at classification time: spans would land on the wrong text.</summary>
    public FileClassification? ClassificationFor(string file, int documentLength) =>
        _classifications.TryGetValue(Normalize(file), out var c) && c.Length == documentLength ? c : null;

    /// <summary>Id the frontend can resolve: the worker's target when instances matched (also via span fallback), else the doc id.</summary>
    public static string ClickTarget(string docId, IReadOnlyList<AspectInstanceInfo> instances) =>
        instances.Count > 0 ? Owner(instances[0].Target) : docId;

    /// <summary>The member a parameter/return-value target belongs to; other targets unchanged.</summary>
    public static string Owner(string target)
    {
        var separator = target.IndexOf(';');
        return separator < 0 ? target : target.Substring(0, separator);
    }

    // ponytail: case-insensitive paths everywhere; per-OS comparison if Linux users hit collisions.
    static string Normalize(string path) => Path.GetFullPath(path).ToUpperInvariant();
}
