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
    private readonly SolutionLoader _loader;
    private readonly ActionDispatcher _dispatcher = new();
    private Microsoft.CodeAnalysis.Solution? _solution;

    public RoslynRequestHandler(Func<string?> solutionPath, SolutionLoader? loader = null)
    {
        _solutionPath = solutionPath ?? throw new ArgumentNullException(nameof(solutionPath));
        _loader = loader ?? new SolutionLoader();
        _dispatcher.Register("health", _ => Task.FromResult<object>(new
        {
            ok = true,
            pid = Environment.ProcessId,
            solutionPath = _solutionPath(),
        }));
        _dispatcher.Register("loadSolution", LoadSolutionAsync);
        PlanActions.Register(_dispatcher, () => _solution, s => _solution = s);
        ListUnusedActions.Register(_dispatcher, () => _solution);
        RenameSymbolActions.Register(_dispatcher, () => _solution, s => _solution = s);
    }

    /// <summary>Runs one action. Unknown actions throw, and the pipe server turns the message into an error reply.</summary>
    public Task<object> InvokeAsync(string action, JsonElement args) => _dispatcher.InvokeAsync(action, args);

    private async Task<object> LoadSolutionAsync(JsonElement args)
    {
        var solutionPath = args.GetProperty("solutionPath").GetString()
            ?? throw new InvalidOperationException("loadSolution needs a solutionPath.");
        var loaded = await _loader.LoadAsync(solutionPath).ConfigureAwait(false);
        _solution = loaded.Solution;
        return new
        {
            ok = true,
            result = new { projects = loaded.Projects },
        };
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
