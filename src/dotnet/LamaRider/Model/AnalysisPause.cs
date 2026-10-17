using System;
using System.Collections.Generic;
using System.Linq;

namespace LamaRider.Model;

/// <summary>
/// Metalama pauses design-time analysis after compile-time code changes until the project is built (LAMA0118). Rider
/// reports that from its Roslyn host, out of ReSharper's reach: compile-time files newer than the last build tell.
/// </summary>
public static class AnalysisPause
{
    public static bool Likely(IEnumerable<string> compileTimeFiles, DateTime? lastBuildUtc, Func<string, DateTime> modifiedUtc) =>
        lastBuildUtc is not { } built || compileTimeFiles.Any(f => modifiedUtc(f) > built);
}
