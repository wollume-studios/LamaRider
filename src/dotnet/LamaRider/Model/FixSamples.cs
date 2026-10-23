using System.Text;

namespace LamaRider.Model;

/// <summary>
/// The fix self-test's samples (samples/MetalamaFixSamples, from Metalama's documentation): LAMA####_Error*.cs report the
/// diagnostic, LAMA####_Fixed*.cs.expected is the code after the fix.
/// </summary>
public static class FixSamples
{
    public static string ExpectedName(string errorFile) => errorFile.Replace("_Error", "_Fixed") + ".expected";

    /// <summary>Code without comments and whitespace, the sample's ".Fixed" namespace read as ".Error": what a fix must produce.</summary>
    public static string Normalize(string code)
    {
        var result = new StringBuilder(code.Length);
        for (var i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            if (ch == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                while (i < code.Length && code[i] != '\n') i++;
                continue;
            }
            if (ch == '/' && i + 1 < code.Length && code[i + 1] == '*')
            {
                var end = code.IndexOf("*/", i + 2, System.StringComparison.Ordinal);
                i = end < 0 ? code.Length : end + 1;
                continue;
            }
            if (ch == '"' || ch == '\'')
            {
                var start = i++;
                while (i < code.Length && code[i] != ch) i += code[i] == '\\' ? 2 : 1;
                result.Append(code, start, System.Math.Min(i + 1, code.Length) - start);
                continue;
            }
            if (!char.IsWhiteSpace(ch)) result.Append(ch);
        }
        return result.ToString().Replace(".Fixed;", ".Error;").Replace(".Fixed{", ".Error{").Replace(".Fixed.", ".Error.");
    }
}
