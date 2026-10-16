using System.Text.Json;

namespace LamaRider.Worker;

public sealed record WorkerOutput(
    int Schema,
    string? MetalamaVersion,
    List<AspectClassInfo> AspectClasses,
    List<AspectInstanceInfo> Instances,
    List<TransformationInfo> Transformations,
    List<DiagnosticInfo> Diagnostics,
    List<FileClassification> TemplateClassifications,
    List<TransformedFileInfo> TransformedFiles);

public sealed record SourceLocation(string File, int Start, int End);

/// <param name="Package">Project name for aspects in the solution, assembly (package) name for library aspects.</param>
/// <param name="Summary">XML-doc summary, when available.</param>
/// <param name="DocUrl">Online API documentation (Metalama packages only).</param>
/// <param name="OffersCodeFixes">Its assembly references Metalama.Extensions.CodeFixes (a Premium component): it may offer code fixes.</param>
public sealed record AspectClassInfo(
    string Id, string Name, string Project, bool IsLibrary, SourceLocation? Location, string Package, string? Summary, string? DocUrl,
    bool OffersCodeFixes = false);

/// <param name="AppliedBy">What added it: the fabric's full name, or "Aspect on target" for child/required aspects; null for attributes.</param>
/// <param name="Attribute">For aspects not added by an attribute: how to write it as one with the same settings (null: it can't be).</param>
public sealed record AspectInstanceInfo(
    string AspectClass, string Target, string TargetName, string TargetType, string TargetKind,
    string Project, string AppliedVia, SourceLocation? Location, string? AppliedBy = null, AttributeSetup? Attribute = null);

/// <param name="InstanceTarget">Target of the aspect instance that made it: a parameter contract transforms its method.</param>
public sealed record TransformationInfo(string AspectClass, string Target, string Kind, string Description, SourceLocation? Location, string? InstanceTarget = null);

public sealed record DiagnosticInfo(string Id, string Severity, string Message, string? Target, string? File, int? Line, string? Project = null);

public sealed record ClassifiedSpan(int Start, int End, string Kind);

/// <summary>Length is the LF-normalized length of the file the spans refer to (stale-model guard).</summary>
public sealed record FileClassification(string File, int Length, List<ClassifiedSpan> Spans);

public sealed record TransformedFileInfo(string File, string TransformedPath, List<TransformedDeclaration> Declarations);

/// <summary>A type or member in a transformed file: XML doc id, LF offsets there and in the source file (null when introduced).</summary>
/// <param name="Standalone">
/// Its transformed code to replace the source with (single-declaration divorce): aspect attributes removed and aspects that
/// fabrics, inheritance or other aspects would add again excluded. Null when no aspect touches it or it was introduced.
/// </param>
public sealed record TransformedDeclaration(string Target, int Start, int End, int? SourceStart = null, int? SourceEnd = null, string? Standalone = null);

public static class WorkerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
