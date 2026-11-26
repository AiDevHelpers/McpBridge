using System.Diagnostics;
using System.Text.Json;

namespace AiDevHelpers.Helpers.RoslynHelper;

/// <summary>
/// Actions the Roslyn helper answers. <see cref="InvokeAsync"/> is the action dispatcher the pipe server calls;
/// <see cref="HandleLineAsync"/> is the same protocol over one raw request line, with the reply shape from the plan.
/// </summary>
public sealed class RoslynRequestHandler
{
    private readonly Func<string?> _solutionPath;

    public RoslynRequestHandler(Func<string?> solutionPath)
    {
        _solutionPath = solutionPath ?? throw new ArgumentNullException(nameof(solutionPath));
    }

    /// <summary>Runs one action. Unknown actions throw, and the pipe server turns the message into an error reply.</summary>
    public Task<object> InvokeAsync(string action, JsonElement args)
    {
        if (action == "health")
        {
            return Task.FromResult<object>(new
            {
                ok = true,
                pid = Environment.ProcessId,
                solutionPath = _solutionPath(),
            });
        }

        throw new InvalidOperationException($"Unknown action: {action}.");
    }

    /// <summary>Handles one request line and returns one reply line. A malformed line gets an error reply.</summary>
    public async Task<string> HandleLineAsync(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var action = root.GetProperty("action").GetString() ?? string.Empty;
            var args = root.TryGetProperty("args", out var a) ? a.Clone() : default;
            var result = await InvokeAsync(action, args).ConfigureAwait(false);
            return JsonSerializer.Serialize(result);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or InvalidCastException)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }
    }
}
