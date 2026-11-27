using System.Text.Json;

namespace AiDevHelpers.Helpers.RoslynHelper;

/// <summary>
/// Maps each action name to its handler. Every edit unit registers its actions here, so the pipe server has one place
/// to route requests. An unregistered action is refused by name, and an action can be registered only once.
/// </summary>
public sealed class ActionDispatcher
{
    private readonly Dictionary<string, Func<JsonElement, Task<object>>> _handlers = new(StringComparer.Ordinal);

    public void Register(string action, Func<JsonElement, Task<object>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryAdd(action, handler))
        {
            throw new InvalidOperationException(Strings.Format("ActionAlreadyRegistered", action));
        }
    }

    public bool Has(string action) => _handlers.ContainsKey(action);

    /// <summary>Every registered action name, in no particular order.</summary>
    public IReadOnlyList<string> Names => _handlers.Keys.ToList();

    /// <summary>Runs one registered action. An unknown action throws, and the pipe server turns the message into an error reply.</summary>
    public Task<object> InvokeAsync(string action, JsonElement args)
    {
        if (!_handlers.TryGetValue(action, out var handler))
        {
            throw new InvalidOperationException($"Unknown action: {action}.");
        }

        return handler(args);
    }
}
