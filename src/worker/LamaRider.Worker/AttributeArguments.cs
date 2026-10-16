using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis.CSharp;

namespace LamaRider.Worker;

/// <summary>C# source for an attribute's constructor arguments and named arguments (fully qualified, global::).</summary>
public sealed record AttributeSetup(List<string> Arguments, List<NamedArgument> Named)
{
    public bool Equals(AttributeSetup? other) =>
        other != null && Arguments.SequenceEqual(other.Arguments) && Named.SequenceEqual(other.Named);

    public override int GetHashCode() => Arguments.Count ^ Named.Count;
}

public sealed record NamedArgument(string Name, string Value);

/// <summary>
/// How to write a live aspect object (added by a fabric, inheritance etc.) as an attribute with the same settings:
/// constructor arguments read back from the members named like the constructor's parameters, then every public
/// settable property or field that differs from an instance made with those arguments.
/// </summary>
public static class AttributeArguments
{
    /// <summary>Null when it can't be an attribute or a value can't be written (not an attribute argument type, lost constructor argument).</summary>
    public static AttributeSetup? Of(object aspect)
    {
        var type = aspect.GetType();
        if (!typeof(Attribute).IsAssignableFrom(type) || !type.IsPublic && !type.IsNestedPublic) return null;
        try
        {
            foreach (var constructor in type.GetConstructors().OrderBy(c => c.GetParameters().Length))
            {
                var values = new List<object?>();
                foreach (var parameter in constructor.GetParameters())
                {
                    if (Member(type, parameter.Name!) is not { } member) break;
                    values.Add(member.Value(aspect));
                }
                if (values.Count != constructor.GetParameters().Length) continue;

                var arguments = values.Select(Literal).ToList();
                if (arguments.Any(a => a == null)) return null;
                var reference = constructor.Invoke(values.ToArray());
                var named = new List<NamedArgument>();
                foreach (var member in Settable(type))
                {
                    var value = member.Value(aspect);
                    if (Equals(value, member.Value(reference)) || value is Array a && member.Value(reference) is Array b && Same(a, b)) continue;
                    if (Literal(value) is not { } literal) return null;
                    named.Add(new NamedArgument(member.Name, literal));
                }
                return new AttributeSetup(arguments!, named);
            }
            return null;
        }
        catch (Exception e) when (e is TargetInvocationException or MemberAccessException or ArgumentException)
        {
            return null;
        }
    }

    sealed record Accessor(string Name, Func<object, object?> Value);

    static Accessor? Member(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
        if (type.GetProperty(name, flags) is { CanRead: true } property && property.GetIndexParameters().Length == 0)
            return new Accessor(property.Name, property.GetValue);
        // Primary constructor parameters and "_name" backing fields.
        foreach (var candidate in new[] { name, "_" + name, "<" + name + ">P" })
            if (type.GetField(candidate, flags) is { } field) return new Accessor(field.Name, field.GetValue);
        return null;
    }

    static IEnumerable<Accessor> Settable(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0 && p.DeclaringType != typeof(Attribute))
            .Select(p => new Accessor(p.Name, p.GetValue))
            .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.Public).Where(f => !f.IsInitOnly).Select(f => new Accessor(f.Name, f.GetValue)));

    static bool Same(Array a, Array b) => a.Length == b.Length && a.Cast<object?>().SequenceEqual(b.Cast<object?>());

    /// <summary>C# for an attribute argument value, or null when the type isn't allowed in attributes.</summary>
    public static string? Literal(object? value) => value switch
    {
        null => "null",
        string s => SymbolDisplay.FormatLiteral(s, quote: true),
        char c => SymbolDisplay.FormatLiteral(c, quote: true),
        bool b => b ? "true" : "false",
        Enum e => EnumLiteral(e),
        Type t => $"typeof({TypeName(t)})",
        float f => f.ToString("R", CultureInfo.InvariantCulture) + "F",
        double d => d.ToString("R", CultureInfo.InvariantCulture) + "D",
        long l => l.ToString(CultureInfo.InvariantCulture) + "L",
        ulong ul => ul.ToString(CultureInfo.InvariantCulture) + "UL",
        uint ui => ui.ToString(CultureInfo.InvariantCulture) + "U",
        int or short or byte or sbyte or ushort => Convert.ToString(value, CultureInfo.InvariantCulture),
        Array array when array.Rank == 1 => ArrayLiteral(array),
        _ => null,
    };

    static string? ArrayLiteral(Array array)
    {
        var items = array.Cast<object?>().Select(Literal).ToList();
        return items.Any(i => i == null) ? null : $"new {TypeName(array.GetType().GetElementType()!)}[] {{ {string.Join(", ", items)} }}";
    }

    static string EnumLiteral(Enum value)
    {
        var type = TypeName(value.GetType());
        if (Enum.IsDefined(value.GetType(), value)) return $"{type}.{value}";
        var parts = value.ToString().Split(", ");
        return parts.All(p => Enum.IsDefined(value.GetType(), p))
            ? string.Join(" | ", parts.Select(p => $"{type}.{p}"))
            : $"({type}){Convert.ToString(Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType())), CultureInfo.InvariantCulture)}";
    }

    /// <summary>C# name of a type: keywords for predefined types, global:: and '.' for nested types, generic arguments.</summary>
    public static string TypeName(Type type)
    {
        if (type.IsArray) return TypeName(type.GetElementType()!) + "[]";
        var keyword = Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => "bool", TypeCode.Byte => "byte", TypeCode.Char => "char", TypeCode.Decimal => "decimal",
            TypeCode.Double => "double", TypeCode.Int16 => "short", TypeCode.Int32 => "int", TypeCode.Int64 => "long",
            TypeCode.SByte => "sbyte", TypeCode.Single => "float", TypeCode.String => "string", TypeCode.UInt16 => "ushort",
            TypeCode.UInt32 => "uint", TypeCode.UInt64 => "ulong",
            _ => type == typeof(object) ? "object" : null,
        };
        if (keyword != null && !type.IsEnum) return keyword;
        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0) name = name.Substring(0, tick);
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
            name += "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">";
        return type.DeclaringType != null && !type.IsGenericParameter
            ? TypeName(type.DeclaringType) + "." + name
            : "global::" + (string.IsNullOrEmpty(type.Namespace) ? "" : type.Namespace + ".") + name;
    }
}
