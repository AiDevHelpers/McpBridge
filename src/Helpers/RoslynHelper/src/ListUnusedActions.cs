using System.Text.Json;
using AiDevHelpers.Installers.Vs2026.Core;
using Microsoft.CodeAnalysis;

namespace AiDevHelpers.Helpers.RoslynHelper;

/// <summary>
/// listUnused: lists private members nothing calls (public ones only with includePublic), optionally within one project. It reads
/// the helper's solution and changes nothing, so it does not go through the plan flow.
/// </summary>
public static class ListUnusedActions
{
    public static void Register(ActionDispatcher dispatcher, Func<Solution?> current) =>
        dispatcher.Register("listUnused", args => ListAsync(args, current));

    private static async Task<object> ListAsync(JsonElement args, Func<Solution?> current)
    {
        var solution = current() ?? throw new InvalidOperationException(Strings.Get("NoSolutionLoaded"));
        var project = ArgumentParsers.OptionalString(args, "project");
        var includePublic = ArgumentParsers.OptionalBool(args, "includePublic") == true;
        var unused = await UnusedFinder.FindAsync(solution, project, includePublic).ConfigureAwait(false);
        return new { count = unused.Count, unused };
    }
}
