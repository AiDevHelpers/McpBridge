using System.Collections.Generic;
using System.Text.Json;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>The bridge request a file_edit call becomes on an IDE path. Refusal is set when the call cannot be expressed on the bridge.</summary>
public sealed record FileEditMapping(string? Refusal, string Action, Dictionary<string, object?> Args);

/// <summary>
/// Maps a file_edit call onto the bridge's replaceInFile action. The bridge matches literal text in one file with the same rules as
/// FileEditHelper. Rename and rollback have no bridge equivalent, and a regex delete has none either; those return a refusal.
/// </summary>
public static class ToolsetFileEditMapper
{
    public static FileEditMapping Map(IDictionary<string, JsonElement> args)
    {
        var action = Text(args, "action") ?? "";
        var file = Text(args, "filePath") ?? "";
        var dryRun = Flag(args, "dryRun");
        var edit = new Dictionary<string, object?>();

        switch (action)
        {
            case "apply":
                edit["oldText"] = Text(args, "oldText") ?? "";
                edit["newText"] = Text(args, "newText") ?? "";
                edit["occurrence"] = Number(args, "occurrence");
                break;
            case "insert":
                var anchor = Text(args, "anchor") ?? "";
                var content = Text(args, "content") ?? "";
                var before = string.Equals(Text(args, "position"), "before", System.StringComparison.OrdinalIgnoreCase);
                edit["oldText"] = anchor;
                edit["newText"] = before ? content + anchor : anchor + content;
                edit["occurrence"] = 1;
                break;
            case "delete":
                if (Flag(args, "isRegex"))
                {
                    return Refused(action, Strings.Get("FileEditReasonRegex"));
                }

                edit["oldText"] = Text(args, "pattern") ?? "";
                edit["newText"] = "";
                edit["occurrence"] = 0;
                break;
            case "rename":
            case "rollback":
                return Refused(action, Strings.Get("FileEditReasonIde"));
            default:
                return Refused(action, Strings.Get("FileEditReasonUnknown"));
        }

        var mapped = new Dictionary<string, object?>
        {
            ["file"] = file,
            ["edits"] = new List<IDictionary<string, object?>> { edit },
            ["confirm"] = !dryRun,
        };
        return new FileEditMapping(null, "replaceInFile", mapped);
    }

    private static FileEditMapping Refused(string action, string why) =>
        new(Strings.Format("FileEditIdeRefused", action, why), "", new Dictionary<string, object?>());

    private static string? Text(IDictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(IDictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int Number(IDictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
}
