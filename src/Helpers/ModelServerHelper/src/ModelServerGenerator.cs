using System.Net.Http.Json;
using System.Text.Json;

namespace AiDevHelpers.Helpers.ModelServerHelper;

/// <summary>One chat message. <c>Role</c> is system, user or assistant.</summary>
public sealed record ChatTurn(string Role, string Content);

/// <summary>
/// The result of a generate or chat call. <c>Text</c> is the reply text (capped); <c>Status</c> is the HTTP code for a
/// non-success reply; <c>Error</c> is set when <c>Ok</c> is false.
/// </summary>
public sealed record ModelReply(bool Ok, string? Text, bool Capped, bool TimedOut, int? Status, string? Error);

/// <summary>The clean_and_chat result: the reply and how many older history entries were left out.</summary>
public sealed record CleanChatResult(ModelReply Reply, int HistoryTrimmed);

/// <summary>
/// Generate and chat against the SimpleModelServer router (<c>POST /api/generate</c>, <c>POST /v1/chat/completions</c>).
/// Behavior follows docs/Host-MCP/model-generate-contract.md: warm-up retries, a 20,000-character output cap, a per-call timeout,
/// and refusals made before any request is sent.
/// </summary>
public sealed class ModelServerGenerator(HttpClient http, Uri baseUrl, Func<TimeSpan, CancellationToken, Task> delay)
{
    public const int MaxChars = 20000;
    public const int MaxAttempts = 10;
    public const int MaxMessages = 200;
    public const int MaxPromptChars = 20000;
    public const int MaxHistory = 20;
    private const int DefaultRetryAfterSeconds = 3;
    private const int MaxErrorBodyChars = 500;

    private static readonly HashSet<string> Roles = new(StringComparer.Ordinal) { "system", "user", "assistant" };

    public static ModelServerGenerator CreateDefault(Uri? url = null) =>
        new(new HttpClient(), url ?? ModelServerAddress.Resolve(), (t, ct) => Task.Delay(t, ct));

    public Task<ModelReply> GenerateAsync(string model, string prompt, string? system, int timeoutSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model))
            return Task.FromResult(Refused(Strings.Get("GenerateModelRequired")));
        if (string.IsNullOrEmpty(prompt))
            return Task.FromResult(Refused(Strings.Get("GeneratePromptRequired")));
        if (Refusal(timeoutSeconds) is { } timeoutRefusal)
            return Task.FromResult(timeoutRefusal);

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["stream"] = false,
        };
        if (system is not null)
            body["system"] = system;

        return RunAsync("/api/generate", body, timeoutSeconds, ct, doc =>
            doc.RootElement.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? ""
                : null, "response");
    }

    public Task<ModelReply> ChatAsync(string model, IReadOnlyList<ChatTurn> messages, int timeoutSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model))
            return Task.FromResult(Refused(Strings.Get("GenerateModelRequired")));
        if (messages.Count == 0)
            return Task.FromResult(Refused(Strings.Get("ChatNeedsMessages")));
        if (messages.Count > MaxMessages)
            return Task.FromResult(Refused(Strings.Get("ChatTooManyMessages")));
        foreach (var turn in messages)
        {
            if (!Roles.Contains(turn.Role))
                return Task.FromResult(Refused(Strings.Format("ChatRoleInvalid", turn.Role)));
        }
        if (Refusal(timeoutSeconds) is { } timeoutRefusal)
            return Task.FromResult(timeoutRefusal);

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages.Select(m => new Dictionary<string, object?> { ["role"] = m.Role, ["content"] = m.Content }).ToList(),
            ["stream"] = false,
        };

        return RunAsync("/v1/chat/completions", body, timeoutSeconds, ct, doc =>
            doc.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                ? content.GetString() ?? ""
                : null, "choices[0].message.content");
    }

    /// <summary>
    /// clean_and_chat: cleans the prompt, validates the history, keeps the most recent <see cref="MaxHistory" /> history entries,
    /// and sends them with the cleaned prompt as one chat call. <c>HistoryTrimmed</c> counts the entries left out.
    /// </summary>
    public async Task<CleanChatResult> CleanAndChatAsync(
        string model, string prompt, IReadOnlyList<ChatTurn> history, int timeoutSeconds, CancellationToken ct)
    {
        var cleaned = PromptCleaner.Clean(prompt);
        if (cleaned.Length == 0)
            return new CleanChatResult(Refused(Strings.Get("GeneratePromptRequired")), 0);
        if (cleaned.Length > MaxPromptChars)
            return new CleanChatResult(Refused(Strings.Format("PromptTooLong", MaxPromptChars)), 0);

        for (var i = 0; i < history.Count; i++)
        {
            if (!Roles.Contains(history[i].Role))
                return new CleanChatResult(Refused(Strings.Format("ChatRoleInvalid", history[i].Role)), 0);
            if (string.IsNullOrWhiteSpace(history[i].Content))
                return new CleanChatResult(Refused(Strings.Format("HistoryEntryEmpty", i + 1)), 0);
        }

        var trimmed = Math.Max(0, history.Count - MaxHistory);
        var messages = history.Skip(trimmed).ToList();
        messages.Add(new ChatTurn("user", cleaned));

        var reply = await ChatAsync(model, messages, timeoutSeconds, ct);
        return new CleanChatResult(reply, trimmed);
    }

    private static ModelReply? Refusal(int timeoutSeconds) =>
        timeoutSeconds is < 1 or > 600 ? Refused(Strings.Format("TimeoutRange", timeoutSeconds)) : null;

    private static ModelReply Refused(string error) => new(false, null, false, false, null, error);

    private async Task<ModelReply> RunAsync(
        string path, object body, int timeoutSeconds, CancellationToken ct,
        Func<JsonDocument, string?> extract, string field)
    {
        for (var attempt = 1; ; attempt++)
        {
            var outcome = await PostOnceAsync(path, body, timeoutSeconds, ct);
            if (outcome.Failure is { } failure)
                return failure;

            using var doc = outcome.Document!;
            if (IsWarmingUp(doc, out var retryAfter))
            {
                if (attempt >= MaxAttempts)
                    return Refused(Strings.Format("WarmUpGaveUp", MaxAttempts));
                await delay(TimeSpan.FromSeconds(Math.Max(1, retryAfter ?? DefaultRetryAfterSeconds)), ct);
                continue;
            }

            var text = extract(doc);
            if (text is null)
                return Refused(Strings.Format("ReplyMissingField", field));

            var capped = text.Length > MaxChars;
            return new ModelReply(true, capped ? text[..MaxChars] : text, capped, false, null, null);
        }
    }

    private sealed record Post(ModelReply? Failure, JsonDocument? Document);

    private async Task<Post> PostOnceAsync(string path, object body, int timeoutSeconds, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var response = await http.PostAsJsonAsync(new Uri(baseUrl, path), body, cts.Token);
            var raw = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var snippet = raw.Length > MaxErrorBodyChars ? raw[..MaxErrorBodyChars] : raw;
                return new Post(new ModelReply(false, null, false, false, (int)response.StatusCode,
                    Strings.Format("NonSuccessStatus", (int)response.StatusCode, snippet)), null);
            }

            try
            {
                return new Post(null, JsonDocument.Parse(raw));
            }
            catch (JsonException)
            {
                return new Post(Refused(Strings.Get("ReplyNotJson")), null);
            }
        }
        catch (HttpRequestException)
        {
            return new Post(Refused(Strings.Format("RouterUnreachable", baseUrl)), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Post(new ModelReply(false, null, false, true, null,
                Strings.Format("CallTimedOut", timeoutSeconds)), null);
        }
    }

    private static bool IsWarmingUp(JsonDocument doc, out int? retryAfter)
    {
        retryAfter = null;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
            status.GetString() != "warming_up")
            return false;

        if (root.TryGetProperty("retry_after", out var after) && after.ValueKind == JsonValueKind.Number && after.TryGetInt32(out var n))
            retryAfter = n;
        return true;
    }
}
