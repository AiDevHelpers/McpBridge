using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiDevHelpers.Common.Terminals;

/// <summary>A terminal type the person registered for the helper.</summary>
public sealed record RegisteredTerminal(string Id, string Name, string? Path, string? Version);

/// <summary>The registered types and the ones the person declined.</summary>
public sealed record TerminalChoices(IReadOnlyList<RegisteredTerminal> Registered, IReadOnlyList<string> Declined);

/// <summary>
/// This user's terminal choices on this machine, kept in <c>%LOCALAPPDATA%\AiDevHelpers\terminals.json</c>. They are per machine
/// and per user (paths differ between machines), so they are not kept in a repo. A missing or malformed file means no choices.
/// </summary>
public sealed class TerminalRegistry(string path)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiDevHelpers", "terminals.json");

    public static TerminalRegistry ForCurrentUser() => new(DefaultPath());

    public TerminalChoices Read()
    {
        var root = ReadRoot();
        var registered = root?["registered"] is JsonArray list
            ? list.OfType<JsonObject>()
                .Select(o => new RegisteredTerminal(
                    Text(o, "id") ?? string.Empty,
                    Text(o, "name") ?? string.Empty,
                    Text(o, "path"),
                    Text(o, "version")))
                .Where(r => r.Id.Length > 0)
                .ToList()
            : [];
        var declined = root?["declined"] is JsonArray d
            ? d.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList()
            : [];
        return new TerminalChoices(registered, declined);
    }

    public void Register(RegisteredTerminal terminal) => Update(root =>
    {
        var list = root["registered"] as JsonArray ?? new JsonArray();
        root["registered"] = list;
        foreach (var existing in list.OfType<JsonObject>().Where(o => string.Equals(Text(o, "id"), terminal.Id, StringComparison.OrdinalIgnoreCase)).ToList())
            list.Remove(existing);

        list.Add(new JsonObject { ["id"] = terminal.Id, ["name"] = terminal.Name, ["path"] = terminal.Path, ["version"] = terminal.Version });
        RemoveFrom(root, "declined", terminal.Id);
    });

    public void Decline(string id) => Update(root =>
    {
        var list = root["declined"] as JsonArray ?? new JsonArray();
        root["declined"] = list;
        if (!list.OfType<JsonValue>().Any(v => string.Equals(v.TryGetValue<string>(out var s) ? s : null, id, StringComparison.OrdinalIgnoreCase)))
            list.Add(id);
    });

    /// <summary>Forgets every declined type, so the next setup asks about them again.</summary>
    public void ClearDeclined() => Update(root => root.Remove("declined"));

    private JsonObject? ReadRoot()
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private void Update(Action<JsonObject> edit)
    {
        JsonObject root;
        try
        {
            root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new IOException($"{path} is not valid JSON; fix or remove it first.", ex);
        }

        edit(root);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(Indented) + "\n");
        File.Move(temp, path, overwrite: true);
    }

    private static void RemoveFrom(JsonObject root, string key, string id)
    {
        if (root[key] is not JsonArray list)
            return;

        foreach (var item in list.OfType<JsonValue>().Where(v => string.Equals(v.TryGetValue<string>(out var s) ? s : null, id, StringComparison.OrdinalIgnoreCase)).ToList())
            list.Remove(item);
    }

    private static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
