using System.Text.Json;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>Reads the JSON arguments of a bridge request: required and optional values, and the arrays each action takes. Shared by the Visual Studio bridge and the Roslyn helper.</summary>
internal static class ArgumentParsers
{
    internal static string? OptionalString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Reads an optional boolean argument. Returns null when absent or not a boolean, so callers choose the default.
    internal static bool? OptionalBool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : null;


    internal static List<(string File, int StartLine, int StartColumn, int EndLine, int EndColumn, string NewText)> ParseEdits(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("edits", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException(Strings.Format("MissingStringArgument", "edits"));
        }

        var edits = new List<(string, int, int, int, int, string)>();
        foreach (var e in array.EnumerateArray())
        {
            edits.Add((RequireString(e, "file"), RequireInt(e, "startLine"), RequireInt(e, "startColumn"), RequireInt(e, "endLine"), RequireInt(e, "endColumn"), RequireString(e, "newText")));
        }

        return edits;
    }

    // The items array of removeUnused: each {file, line, kind} where kind is "member" (the default) or "type".
    internal static List<(string File, int Line, string Kind)> ParseUnusedItems(JsonElement args)
    {
        var items = new List<(string File, int Line, string Kind)>();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in array.EnumerateArray())
            {
                items.Add((RequireString(e, "file"), RequireInt(e, "line"), OptionalString(e, "kind") ?? "member"));
            }
        }

        return items;
    }

    // The parameters array of changeSignature, in the new order: each item is a declaration, the old index it keeps (absent for a new
    // parameter) and the argument existing calls get for a new parameter.
    internal static List<NewParameter> ParseNewParameters(JsonElement args)
    {
        var parameters = new List<NewParameter>();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("parameters", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in array.EnumerateArray())
            {
                parameters.Add(new NewParameter(RequireString(e, "declaration"), OptionalInt(e, "fromOld"), OptionalString(e, "valueForCallers")));
            }
        }

        return parameters;
    }

    // The optional callerEdits array of editMethod: each item names a reference (file, line, column) and its replacement text.
    internal static List<CallerEdit> ParseCallerEdits(JsonElement args)
    {
        var edits = new List<CallerEdit>();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("callerEdits", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in array.EnumerateArray())
            {
                edits.Add(new CallerEdit(RequireString(e, "file"), RequireInt(e, "line"), RequireInt(e, "column"), RequireString(e, "newText")));
            }
        }

        return edits;
    }

    // The members array: each item names a field or property, its type, privacy and backing.
    internal static List<MemberSpec> ParseMemberSpecs(JsonElement args)
    {
        var specs = new List<MemberSpec>();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("members", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in array.EnumerateArray())
            {
                var declaration = OptionalString(e, "declaration");
                var spec = new MemberSpec
                {
                    Name = declaration is null ? RequireString(e, "name") : OptionalString(e, "name") ?? "",
                    Type = declaration is null ? RequireString(e, "type") : OptionalString(e, "type") ?? "",
                    Declaration = declaration,
                    Accessibility = OptionalString(e, "accessibility") ?? "",
                    Backing = OptionalString(e, "backing") ?? "auto",
                    Setter = OptionalString(e, "setter") ?? "set",
                    Required = OptionalBool(e, "required") == true,
                    Initializer = OptionalString(e, "initializer"),
                    Summary = OptionalString(e, "summary"),
                };
                spec.Attributes.AddRange(StringList(e, "attributes"));
                specs.Add(spec);
            }
        }

        return specs;
    }

    // The editMember arguments: only the fields that are present change.
    internal static MemberEdit ParseMemberEdit(JsonElement args)
    {
        var edit = new MemberEdit
        {
            NewDeclaration = OptionalString(args, "declaration"),
            NewName = OptionalString(args, "newName"),
            NewType = OptionalString(args, "newType"),
            Accessibility = OptionalString(args, "accessibility"),
            Backing = OptionalString(args, "backing"),
            Setter = OptionalString(args, "setter"),
            Required = OptionalBool(args, "required"),
            NewSummary = OptionalString(args, "newSummary"),
        };
        edit.AddAttributes.AddRange(StringList(args, "addAttributes"));
        edit.RemoveAttributes.AddRange(StringList(args, "removeAttributes"));
        return edit;
    }

    internal static IEnumerable<string> StringList(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(v => v.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : new List<string>();

    // The optional newSummary and docs of editMethod: docs is an array of {tag, name, text} for param, typeparam and returns tags.
    internal static DocUpdates ParseDocUpdates(JsonElement args)
    {
        var updates = new DocUpdates { NewSummary = OptionalString(args, "newSummary") };
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("docs", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in array.EnumerateArray())
            {
                updates.Items.Add(new DocItem(RequireString(e, "tag"), OptionalString(e, "name"), RequireString(e, "text")));
            }
        }

        return updates;
    }


    internal static int? OptionalInt(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    internal static int RequireInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new ArgumentException(Strings.Format("MissingIntegerArgument", name));
        }

        return value.GetInt32();
    }

    internal static string RequireString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(Strings.Format("MissingStringArgument", name));
        }

        return value.GetString()!;
    }
}
