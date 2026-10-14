using Microsoft.CodeAnalysis.Text;

namespace LamaRider.Worker;

/// <summary>LF-normalized offsets, because Rider/IntelliJ documents contain LF line breaks only.</summary>
public sealed class Offsets
{
    readonly Dictionary<string, (SourceText Text, int[] LfLineStarts)?> _files = new(StringComparer.OrdinalIgnoreCase);

    public int? FromPosition(string path, int position)
    {
        if (Get(path) is not { } f) return null;
        var lp = f.Text.Lines.GetLinePosition(position);
        return f.LfLineStarts[lp.Line] + lp.Character;
    }

    /// <summary>Length of the file with every line break counted as one character.</summary>
    public int? LfLength(string path)
    {
        if (Get(path) is not { } f) return null;
        var last = f.Text.Lines[f.Text.Lines.Count - 1];
        return f.LfLineStarts[f.Text.Lines.Count - 1] + last.Span.Length;
    }

    (SourceText Text, int[] LfLineStarts)? Get(string path)
    {
        if (_files.TryGetValue(path, out var cached)) return cached;
        (SourceText, int[])? result = null;
        if (File.Exists(path))
        {
            var text = SourceText.From(File.ReadAllText(path));
            var starts = new int[text.Lines.Count];
            for (var i = 1; i < starts.Length; i++)
                starts[i] = starts[i - 1] + text.Lines[i - 1].Span.Length + 1;
            result = (text, starts);
        }
        _files[path] = result;
        return result;
    }
}
