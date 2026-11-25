using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiDevHelpers.Common.Terminals;

/// <summary>
/// Reads and edits a Windows Terminal <c>settings.json</c>. The file allows comments and trailing commas, so a profile is added by
/// inserting text into <c>profiles.list</c>, never by parsing and rewriting the file (that would drop the person's comments and layout).
/// </summary>
public static class WindowsTerminalSettings
{
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The profile GUID for a terminal type. It is derived from the id, so adding the same type twice is a no-op.</summary>
    public static Guid ProfileGuid(string id)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes("AiDevHelpers.terminal:" + id.ToLowerInvariant()));
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    /// <summary>
    /// True when Windows Terminal already offers the type: a profile that runs it (by command line, name or the helper's GUID), or a
    /// generator that makes it on its own and has not been switched off in <c>disabledProfileSources</c>. Types with no generator
    /// (cmd, Windows PowerShell) are always offered. A file that cannot be read counts as not covering anything.
    /// </summary>
    public static bool Covers(string settingsText, string id, string name, string? path, string generator)
    {
        try
        {
            using var doc = JsonDocument.Parse(settingsText, Lenient);
            var root = doc.RootElement;
            if (generator.Length == 0)
                return true;

            var guid = "{" + ProfileGuid(id) + "}";
            var disabled = root.TryGetProperty("disabledProfileSources", out var sources) && sources.ValueKind == JsonValueKind.Array
                && sources.EnumerateArray().Any(s => s.ValueKind == JsonValueKind.String && string.Equals(s.GetString(), generator, StringComparison.OrdinalIgnoreCase));

            foreach (var profile in Profiles(root))
            {
                if (Text(profile, "guid") is { } g && string.Equals(g, guid, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (Text(profile, "name") is { } n && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    return true;

                var commandLine = Text(profile, "commandline");
                if (commandLine is not null && path is not null && commandLine.Contains(path, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (commandLine is not null && id.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase)
                    && commandLine.Contains("-d " + id[4..], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return !disabled;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The settings text with one more profile at the end of <c>profiles.list</c>, or null when it cannot be done safely (the text is not
    /// valid settings JSON, has no list, or the result would not parse). Returns the text unchanged when the profile is already there.
    /// Everything outside the inserted lines, comments included, is kept byte for byte.
    /// </summary>
    public static string? InsertProfile(string text, string id, string name, string commandLine)
    {
        try
        {
            using var _ = JsonDocument.Parse(text, Lenient);
        }
        catch (JsonException)
        {
            return null;
        }

        var guid = "{" + ProfileGuid(id) + "}";
        if (text.Contains(guid, StringComparison.OrdinalIgnoreCase))
            return text;

        var open = FindProfileList(text);
        var close = open < 0 ? -1 : FindMatching(text, open);
        if (close < 0)
            return null;

        // The last real character of the list (a comment or whitespace after it stays where it is, after the new profile).
        var last = LastCodeIndex(text, open, close);
        var lineBreak = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var indent = "        ";
        var profile = string.Join(
            lineBreak,
            indent + "{",
            indent + "    \"guid\": " + JsonSerializer.Serialize(guid) + ",",
            indent + "    \"name\": " + JsonSerializer.Serialize(name) + ",",
            indent + "    \"commandline\": " + JsonSerializer.Serialize(commandLine) + ",",
            indent + "    \"hidden\": false",
            indent + "}");

        var comma = text[last] is '[' or ',' ? string.Empty : ",";
        var updated = text[..(last + 1)] + comma + lineBreak + profile + text[(last + 1)..];

        // A bad insert is never written: the result has to parse and has to contain the new profile.
        try
        {
            using var check = JsonDocument.Parse(updated, Lenient);
            return Profiles(check.RootElement).Any(p => Text(p, "guid") == guid) ? updated : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds the profile to a settings file. Writes <c>settings.json.bak</c> first and replaces the file atomically. Returns false when the
    /// profile was already there or the file could not be edited safely; the file is then left as it was.
    /// </summary>
    public static bool AddProfile(string settingsPath, string id, string name, string commandLine)
    {
        var text = File.ReadAllText(settingsPath);
        var updated = InsertProfile(text, id, name, commandLine);
        if (updated is null || updated == text)
            return false;

        File.Copy(settingsPath, settingsPath + ".bak", overwrite: true);
        var temp = settingsPath + ".tmp";
        File.WriteAllText(temp, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: HasBom(settingsPath)));
        File.Move(temp, settingsPath, overwrite: true);
        return true;
    }

    private static bool HasBom(string path)
    {
        using var stream = File.OpenRead(path);
        var bom = new byte[3];
        return stream.Read(bom, 0, 3) == 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF;
    }

    private static IEnumerable<JsonElement> Profiles(JsonElement root)
    {
        if (!root.TryGetProperty("profiles", out var profiles))
            yield break;

        var list = profiles.ValueKind == JsonValueKind.Array ? profiles : profiles.ValueKind == JsonValueKind.Object && profiles.TryGetProperty("list", out var l) ? l : default;
        if (list.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
            yield return item;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // The index of the '[' that opens profiles.list (or profiles when it is itself an array), or -1.
    private static int FindProfileList(string text)
    {
        var root = NextCode(text, 0);
        if (root >= text.Length || text[root] != '{')
            return -1;

        var profiles = ValueOf(text, root, "profiles");
        if (profiles < 0)
            return -1;

        if (text[profiles] == '[')
            return profiles;

        if (text[profiles] != '{')
            return -1;

        var list = ValueOf(text, profiles, "list");
        return list >= 0 && text[list] == '[' ? list : -1;
    }

    // The index where the value of 'key' starts, for a key directly inside the object that opens at objectOpen; -1 when absent.
    private static int ValueOf(string text, int objectOpen, string key)
    {
        var depth = 0;
        for (var i = objectOpen; i < text.Length; i = NextCode(text, i + 1))
        {
            switch (text[i])
            {
                case '{' or '[':
                    depth++;
                    break;
                case '}' or ']':
                    depth--;
                    if (depth == 0)
                        return -1;

                    break;
                case '"':
                    var end = StringEnd(text, i);
                    if (depth == 1 && text[(i + 1)..end] == key)
                    {
                        var colon = NextCode(text, end + 1);
                        if (colon < text.Length && text[colon] == ':')
                        {
                            var value = NextCode(text, colon + 1);
                            return value < text.Length ? value : -1;
                        }
                    }

                    i = end;
                    break;
            }
        }

        return -1;
    }

    // The index of the bracket that closes the one at 'open', skipping strings and comments; -1 when unbalanced.
    private static int FindMatching(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i = NextCode(text, i + 1))
        {
            switch (text[i])
            {
                case '{' or '[':
                    depth++;
                    break;
                case '}' or ']':
                    depth--;
                    if (depth == 0)
                        return i;

                    break;
                case '"':
                    i = StringEnd(text, i);
                    break;
            }
        }

        return -1;
    }

    // The index of the last character between the brackets that is code: not whitespace, not inside a comment.
    private static int LastCodeIndex(string text, int open, int close)
    {
        var last = open;
        for (var i = NextCode(text, open + 1); i < close; i = NextCode(text, i + 1))
        {
            if (text[i] == '"')
                i = StringEnd(text, i);

            last = i;
        }

        return last;
    }

    // The first index at or after 'from' that is not whitespace and not inside a comment; text.Length when there is none.
    private static int NextCode(string text, int from)
    {
        var i = from;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var eol = text.IndexOf('\n', i);
                i = eol < 0 ? text.Length : eol + 1;
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
            }
            else
            {
                return i;
            }
        }

        return text.Length;
    }

    // The index of the closing quote of the string that opens at i.
    private static int StringEnd(string text, int i)
    {
        for (var j = i + 1; j < text.Length; j++)
        {
            if (text[j] == '\\')
                j++;
            else if (text[j] == '"')
                return j;
        }

        return text.Length - 1;
    }
}
