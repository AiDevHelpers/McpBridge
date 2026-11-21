using AiDevHelpers.Helpers.TestingHelper;

namespace AiDevHelpers.Helpers.TestingHelper.Tests;

/// <summary>Records each command as "file args" and returns a scripted result.</summary>
internal sealed class FakeTestRunner : ITestCommandRunner
{
    public List<string> Calls { get; } = [];

    public Func<string, IReadOnlyList<string>, TestCommandResult> Handler { get; set; } =
        (_, _) => new TestCommandResult(0, string.Empty, string.Empty);

    public Task<TestCommandResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        Calls.Add(file + " " + string.Join(" ", args));
        return Task.FromResult(Handler(file, args));
    }
}
