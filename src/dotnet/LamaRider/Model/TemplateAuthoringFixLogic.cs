using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LamaRider.Model;

/// <summary>Pure helpers of the template authoring quick-fixes: reading diagnostic messages, re-indenting moved code.</summary>
public static class TemplateAuthoringFixLogic
{
    /// <summary>LAMA0033: the aspect class and the member used as a template without [Template].</summary>
    public static (string Class, string Member)? TemplateMember(string message) =>
        Regex.Match(message, @"class '([^']+)' defines a member named '([^']+)'") is { Success: true } m ? (m.Groups[1].Value, m.Groups[2].Value) : null;

    /// <summary>LAMA0104: true when the expression must become compile-time, false when it must become run-time.</summary>
    public static bool? ExpectsCompileTime(string message) =>
        Regex.Match(message, @"is (run-time|compile-time),? but it is expected to be (run-time|compile-time)") is { Success: true } m
            ? m.Groups[2].Value == "compile-time" : null;

    /// <summary>LAMA0242: the sub-expression whose scope doesn't match (`x` or 'x' in the message).</summary>
    public static string? SubExpression(string message) =>
        Regex.Match(message, @"sub-expression (?:`(.+?)`|'(.+?)') is") is { Success: true } m
            ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;

    /// <summary>The first quoted attribute name of a message, without "Attribute" ('TemplateAttribute' → Template).</summary>
    public static string? AttributeName(string message) =>
        Regex.Match(message, @"'(\w+?)Attribute'") is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>LAMA0253: the compile-time type named by the message ('IMethod').</summary>
    public static string? CompileTimeType(string message) =>
        Regex.Match(message, @"type '([^']+)'") is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>LAMA0253: members that turn a code model object into something run-time code can use.</summary>
    public static IReadOnlyList<string> RunTimeMembers(string? compileTimeType) => compileTimeType switch
    {
        "IMethod" => ["Name", "ToMethodInfo()"],
        "IConstructor" => ["Name", "ToConstructorInfo()"],
        "IParameter" or "IField" or "IProperty" or "IFieldOrProperty" or "IFieldOrPropertyOrIndexer" => ["Value", "Name"],
        "IExpression" => ["Value"],
        "INamedType" => ["ToType()", "Name"],
        "IType" => ["ToType()"],
        _ => ["Name", "Value", "ToMethodInfo()", "ToType()"],
    };

    /// <summary>Replaces the `dynamic` type in a type or signature: by `object` (keeping `?`), or always by `object?`.</summary>
    public static string ReplaceDynamic(string text, bool alwaysNullable) =>
        Regex.Replace(text, @"\bdynamic\b(\?)?", m => alwaysNullable ? "object?" : "object" + m.Groups[1].Value);

    public static bool ContainsDynamic(string text) => Regex.IsMatch(text, @"\bdynamic\b");

    /// <summary>Whether <paramref name="code"/> uses the identifier <paramref name="name"/>.</summary>
    public static bool Uses(string code, string name) => Regex.IsMatch(code, @"(?<![\w@.])@?" + Regex.Escape(name) + @"\b");

    /// <summary>Removes up to <paramref name="columns"/> leading spaces or tabs from each line but the first.</summary>
    public static string Dedent(string text, int columns)
    {
        if (columns <= 0) return text;
        var lines = text.Split('\n');
        for (var i = 1; i < lines.Length; i++)
        {
            var n = 0;
            while (n < columns && n < lines[i].Length && lines[i][n] is ' ' or '\t') n++;
            lines[i] = lines[i].Substring(n);
        }
        return string.Join("\n", lines);
    }

    /// <summary>Adds <paramref name="extra"/> at the start of each non-blank line but the first.</summary>
    public static string Indent(string text, string extra)
    {
        if (extra.Length == 0) return text;
        var lines = text.Split('\n');
        for (var i = 1; i < lines.Length; i++)
            if (lines[i].Trim().Length > 0) lines[i] = extra + lines[i];
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The parenthesized arguments of the first base type in an extends list (": Base( x ), IFoo" → the offsets of
    /// "(" and ")"), the arguments a primary constructor passes to the base constructor.
    /// </summary>
    public static (int Open, int Close)? BaseArguments(string extendsList)
    {
        int angle = 0, open = -1, depth = 0;
        for (var i = 0; i < extendsList.Length; i++)
        {
            var ch = extendsList[i];
            if (open < 0)
            {
                if (ch == '<') angle++;
                else if (ch == '>') angle--;
                else if (ch == ',' && angle == 0) return null;
                else if (ch == '(' && angle == 0) { open = i; depth = 1; }
                continue;
            }
            if (ch == '(') depth++;
            else if (ch == ')' && --depth == 0) return (open, i);
        }
        return null;
    }

    /// <summary>LAMA0105: one declaration statement per declarator ("int " + "a = 1", "b = 2" → "int a = 1;\n    int b = 2;").</summary>
    public static string SplitDeclaration(string prefix, IEnumerable<string> declarators, string indent)
    {
        var result = new StringBuilder();
        foreach (var declarator in declarators)
        {
            if (result.Length > 0) result.Append('\n').Append(indent);
            result.Append(prefix).Append(declarator.Trim()).Append(';');
        }
        return result.ToString();
    }

    /// <summary>A lambda that invokes a method group ("items.First", 0 → "() => items.First()").</summary>
    public static string LambdaFor(string methodGroup, int parameters)
    {
        var names = parameters == 1 ? ["arg"] : Enumerable.Range(1, parameters).Select(i => "arg" + i).ToArray();
        var list = string.Join(", ", names);
        return (parameters == 1 ? list : $"({list})") + $" => {methodGroup}({list})";
    }

    /// <summary>The type of a parameter as written before its name ("params int[] " → "int[]").</summary>
    public static string ParameterType(string textBeforeName) =>
        Regex.Replace(textBeforeName.Trim(), @"^(?:(?:params|scoped|ref|in|out|readonly)\s+)+", "");
}
