using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// A field or property the caller wants: its name and type, its privacy, how it is backed, and its attributes. Rendered to C#
/// by <see cref="MemberRenderer"/>.
/// </summary>
internal sealed class MemberSpec
{
    /// <summary>The names <see cref="Backing"/> accepts.</summary>
    public static readonly string[] BackingKinds = { "auto", "field", "readonly-field", "field-only", "expression" };

    public string Name { get; set; } = "";

    public string Type { get; set; } = "";

    /// <summary>public, internal, protected, private, "protected internal" or "private protected". Always given by the caller.</summary>
    public string Accessibility { get; set; } = "";

    /// <summary>
    /// auto: <c>{ get; set; }</c>. field: a private backing field and a property over it. readonly-field: a readonly field.
    /// field-only: a plain field. expression: a get-only property computed from <see cref="Initializer"/>.
    /// </summary>
    public string Backing { get; set; } = "auto";

    /// <summary>For a property: set, init, none, or an accessibility and set/init such as "private set". Default set.</summary>
    public string Setter { get; set; } = "set";

    public bool Required { get; set; }

    /// <summary>The value the member starts with, or the expression of an expression-bodied property.</summary>
    public string? Initializer { get; set; }

    public List<string> Attributes { get; } = new();

    public string? Summary { get; set; }

    /// <summary>The name of the private backing field made for the "field" backing.</summary>
    public string BackingFieldName => "_" + (Name.Length > 0 ? char.ToLowerInvariant(Name[0]) + Name.Substring(1) : Name);
}

internal static class MemberRenderer
{
    private static readonly string[] Accessibilities = { "public", "internal", "protected", "private", "protected internal", "private protected" };

    /// <summary>Why a spec cannot be rendered, or null when it can.</summary>
    public static string? Validate(MemberSpec spec)
    {
        if (!SyntaxFacts.IsValidIdentifier(spec.Name))
        {
            return Strings.Format("MemberBadName", spec.Name);
        }

        if (!Accessibilities.Contains(spec.Accessibility))
        {
            return Strings.Format("MemberBadAccessibility", spec.Accessibility);
        }

        if (!MemberSpec.BackingKinds.Contains(spec.Backing))
        {
            return Strings.Format("MemberBadBacking", spec.Backing, string.Join(", ", MemberSpec.BackingKinds));
        }

        var type = SyntaxFactory.ParseTypeName(spec.Type);
        if (spec.Type.Trim().Length == 0 || type.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return Strings.Format("MemberBadType", spec.Type);
        }

        if (spec.Backing == "expression" && string.IsNullOrWhiteSpace(spec.Initializer))
        {
            return Strings.Get("MemberExpressionNeedsInitializer");
        }

        return null;
    }

    /// <summary>The C# lines of the member (a backing field first when the backing needs one), without a doc comment.</summary>
    public static List<string> Render(MemberSpec spec)
    {
        var lines = new List<string>();
        var required = spec.Required ? "required " : "";
        var init = string.IsNullOrWhiteSpace(spec.Initializer) ? "" : " = " + spec.Initializer!.Trim();
        switch (spec.Backing)
        {
            case "readonly-field":
                lines.AddRange(AttributeLines(spec));
                lines.Add($"{spec.Accessibility} {required}readonly {spec.Type} {spec.Name}{init};");
                break;
            case "field-only":
                lines.AddRange(AttributeLines(spec));
                lines.Add($"{spec.Accessibility} {required}{spec.Type} {spec.Name}{init};");
                break;
            case "expression":
                lines.AddRange(AttributeLines(spec));
                lines.Add($"{spec.Accessibility} {spec.Type} {spec.Name} => {spec.Initializer!.Trim()};");
                break;
            case "field":
                lines.Add($"private {spec.Type} {spec.BackingFieldName}{init};");
                lines.Add("");
                lines.AddRange(AttributeLines(spec));
                lines.Add($"{spec.Accessibility} {required}{spec.Type} {spec.Name}");
                lines.Add("{");
                lines.Add($"    get => {spec.BackingFieldName};");
                if (SetterText(spec) is { } fieldSetter)
                {
                    lines.Add($"    {fieldSetter.Replace(";", "")} => {spec.BackingFieldName} = value;");
                }

                lines.Add("}");
                break;
            default:
                lines.AddRange(AttributeLines(spec));
                var setter = SetterText(spec);
                var accessors = "{ get;" + (setter is null ? "" : " " + setter) + " }";
                lines.Add($"{spec.Accessibility} {required}{spec.Type} {spec.Name} {accessors}" + (init.Length > 0 ? init + ";" : ""));
                break;
        }

        return lines;
    }

    // "set;", "init;", "private set;", or null when the property has no setter.
    private static string? SetterText(MemberSpec spec)
    {
        var setter = (spec.Setter ?? "set").Trim();
        if (setter.Length == 0 || setter == "none")
        {
            return null;
        }

        return setter.EndsWith(";", StringComparison.Ordinal) ? setter : setter + ";";
    }

    private static IEnumerable<string> AttributeLines(MemberSpec spec) => spec.Attributes.Select(NormalizeAttribute);

    /// <summary>"Required" and "[Required]" both become "[Required]".</summary>
    public static string NormalizeAttribute(string attribute)
    {
        var text = attribute.Trim();
        return text.StartsWith("[", StringComparison.Ordinal) ? text : "[" + text + "]";
    }

    /// <summary>Parses the rendered lines as members. Null (with the error) when they are not valid C#.</summary>
    public static string? ValidateRendered(IEnumerable<string> lines)
    {
        var text = string.Join("\n", lines);
        var wrapper = SyntaxFactory.ParseCompilationUnit("class __W {\n" + text + "\n}");
        return wrapper.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error) is { } error
            ? Strings.Format("MemberRenderedInvalid", error.GetMessage())
            : null;
    }
}
