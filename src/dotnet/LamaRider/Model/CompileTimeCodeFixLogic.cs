using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LamaRider.Model;

/// <summary>Pure text logic of the compile-time code quick-fixes (scopes, templates, fabrics, ordering, serialization).</summary>
public static class CompileTimeCodeFixLogic
{
    /// <summary>`from v in source where c select e` as `source.Where(v => c).Select(v => e)`; no Select for `select v` after a where.</summary>
    public static string QueryToMethods(string source, string variable, IReadOnlyList<string> wheres, string select)
    {
        var result = new StringBuilder(source);
        foreach (var where in wheres) result.Append($".Where({variable} => {where})");
        if (select.Trim() != variable || wheres.Count == 0) result.Append($".Select({variable} => {select})");
        return result.ToString();
    }

    /// <summary>
    /// A compile-time `if` choosing between two copies of <paramref name="code"/> where the ConfigureAwait argument at
    /// [<paramref name="argStart"/>, <paramref name="argEnd"/>) is the literal true, then false (LAMA0254).
    /// </summary>
    public static string ConfigureAwaitBranches(string code, int argStart, int argEnd, string condition, string indent)
    {
        string Branch(string literal) =>
            indent + "    " + (code.Substring(0, argStart) + literal + code.Substring(argEnd)).Replace("\n", "\n    ");
        return $"if ( {condition} )\n{indent}{{\n{Branch("true")}\n{indent}}}\n{indent}else\n{indent}{{\n{Branch("false")}\n{indent}}}";
    }

    /// <summary>A new name for a template that clashes with another one: Log(category) → LogWithCategory, else Log2.</summary>
    public static string TemplateName(string name, IReadOnlyList<string> parameters, ICollection<string> taken)
    {
        var candidate = parameters.Count > 0 ? name + "With" + string.Concat(parameters.Select((p, i) => (i > 0 ? "And" : "") + Pascal(p))) : name + "2";
        var unique = candidate;
        for (var i = 2; taken.Contains(unique); i++) unique = candidate + i;
        return unique;
    }

    static string Pascal(string name) => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name.Substring(1);

    /// <summary>The names quoted in a diagnostic message, in order ('LogAllMethods', 'Log').</summary>
    public static List<string> Quoted(string message) => Regex.Matches(message, "'([^']+)'").Cast<Match>().Select(m => m.Groups[1].Value).ToList();

    /// <summary>LAMA0022: (parent, child) from "The aspect 'P' cannot add a child aspect … of type 'C' …".</summary>
    public static (string Parent, string Child)? ParentAndChild(string message)
    {
        var match = Regex.Match(message, @"aspect '(?<parent>[^']+)' cannot add a child aspect.*?of type '(?<child>[^']+)'");
        return match.Success ? (match.Groups["parent"].Value, match.Groups["child"].Value) : null;
    }

    /// <summary>LAMA0042: (aspect with layers, low-level aspect) from the message.</summary>
    public static (string Aspect, string Weaver)? LayeredAndWeaver(string message)
    {
        var match = Regex.Match(message, @"aspect '(?<aspect>[^']+)' has several layers, but the low-level aspect '(?<weaver>[^']+)'");
        return match.Success ? (match.Groups["aspect"].Value, match.Groups["weaver"].Value) : null;
    }

    /// <summary>Whether a type as written (in typeof, "Ns.LogAttribute") is the aspect a message names ("Log" or "LogAttribute").</summary>
    public static bool IsAspectNamed(string typeText, string aspectName)
    {
        var name = Regex.Replace(typeText, @"<.*$", "").Trim();
        name = name.Substring(name.LastIndexOfAny(['.', ':']) + 1);
        return name == aspectName || name == aspectName + "Attribute" || StripAttribute(name) == StripAttribute(aspectName);
    }

    static string StripAttribute(string name) => name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > 9 ? name.Substring(0, name.Length - 9) : name;

    /// <summary>`[assembly: AspectOrder(AspectOrderDirection.CompileTime, typeof(First), typeof(Second))]`: First is processed first.</summary>
    public static string AspectOrder(string first, string second) =>
        $"[assembly: AspectOrder( AspectOrderDirection.CompileTime, typeof({first}), typeof({second}) )]";

    /// <summary>
    /// A nested serializer for a positional record (LAMA0210): ReferenceTypeSerializer&lt;T&gt; for a record class,
    /// ValueTypeSerializer&lt;T&gt; for a record struct; the parameters go through the constructor arguments.
    /// </summary>
    public static string RecordSerializer(string recordType, IReadOnlyList<(string Type, string Name)> parameters, bool isStruct, string indent)
    {
        var i1 = indent + "    ";
        var i2 = i1 + "    ";
        var create = $"new( {string.Join(", ", parameters.Select(p => $"constructorArguments.GetValue<{p.Type}>( \"{p.Name}\" )"))} )";
        var sets = parameters.Select(p => $"constructorArguments.SetValue( \"{p.Name}\", obj.{p.Name} );").ToList();
        string Body(string signature) => sets.Count == 1
            ? $"{i1}{signature}\n{i2}=> {sets[0]}"
            : $"{i1}{signature}\n{i1}{{\n{string.Concat(sets.Select(s => $"{i2}{s}\n"))}{i1}}}";
        return isStruct
            ? $"{indent}public class Serializer : ValueTypeSerializer<{recordType}>\n{indent}{{\n"
              + $"{i1}public override {recordType} DeserializeObject( IArgumentsReader constructorArguments )\n{i2}=> {create};\n\n"
              + Body($"public override void SerializeObject( {recordType} obj, IArgumentsWriter constructorArguments )") + $"\n{indent}}}"
            : $"{indent}public class Serializer : ReferenceTypeSerializer<{recordType}>\n{indent}{{\n"
              + $"{i1}public override {recordType} CreateInstance( IArgumentsReader constructorArguments )\n{i2}=> {create};\n\n"
              + Body($"public override void SerializeObject( {recordType} obj, IArgumentsWriter constructorArguments, IArgumentsWriter initializationArguments )") + $"\n{indent}}}";
    }
}
