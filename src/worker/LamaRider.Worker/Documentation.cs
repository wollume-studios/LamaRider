using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace LamaRider.Worker;

public static class Documentation
{
    /// <summary>Plain text of the &lt;summary&gt; of an XML documentation comment; cref/langword references become short names.</summary>
    public static string? Summary(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var summary = XElement.Parse(xml).Element("summary");
            if (summary == null) return null;
            foreach (var reference in summary.Descendants().Where(e => e.Name.LocalName is "see" or "seealso" or "paramref" or "typeparamref").ToList())
                reference.ReplaceWith(new XText(ShortName(reference)));
            var text = Regex.Replace(summary.Value, @"\s+", " ").Trim();
            return text.Length == 0 ? null : text;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    static string ShortName(XElement reference)
    {
        var value = (string?) reference.Attribute("cref") ?? (string?) reference.Attribute("langword") ?? (string?) reference.Attribute("name") ?? reference.Value;
        value = Regex.Replace(value, @"^[A-Z]:", "");          // T:, M:, P: …
        value = Regex.Replace(value, @"\(.*\)$", "");           // method parameters
        var dot = value.LastIndexOf('.');
        return dot >= 0 ? value[(dot + 1)..] : value;
    }
}
