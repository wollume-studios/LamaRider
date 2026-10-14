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
public sealed record AspectClassInfo(
    string Id, string Name, string Project, bool IsLibrary, SourceLocation? Location, string Package, string? Summary, string? DocUrl);

public sealed record AspectInstanceInfo(
    string AspectClass, string Target, string TargetName, string TargetType, string TargetKind,
    string Project, string AppliedVia, SourceLocation? Location);

public sealed record TransformationInfo(string AspectClass, string Target, string Kind, string Description, SourceLocation? Location);

public sealed record DiagnosticInfo(string Id, string Severity, string Message, string? Target, string? File, int? Line, string? Project = null);

public sealed record ClassifiedSpan(int Start, int End, string Kind);

/// <summary>Length is the LF-normalized length of the file the spans refer to (stale-model guard).</summary>
public sealed record FileClassification(string File, int Length, List<ClassifiedSpan> Spans);

public sealed record TransformedFileInfo(string File, string TransformedPath);

public static class WorkerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
