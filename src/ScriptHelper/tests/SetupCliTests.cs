using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class SetupCliTests
{
    private sealed class RecordingSetup : ISetupCommands
    {
        public SetupResult RegisterResult { get; set; } = new(true, "registered", "id-1");
        public SetupResult ApproveResult { get; set; } = new(true, "approved");
        public List<string> Calls { get; } = new();

        public SetupResult Register(string root)
        {
            Calls.Add($"register {root}");
            return RegisterResult;
        }

        public SetupResult Approve(string root, string relativeScript)
        {
            Calls.Add($"approve {root} {relativeScript}");
            return ApproveResult;
        }
    }

    private static (int Code, string Output, RecordingSetup Setup) Run(RecordingSetup? setup, params string[] args)
    {
        setup ??= new RecordingSetup();
        var output = new StringWriter();
        var code = SetupCli.Run(args, setup, output);
        return (code, output.ToString(), setup);
    }

    [Fact]
    public void Register_calls_the_command_and_prints_the_result()
    {
        var (code, output, setup) = Run(null, "register", @"C:\repo");

        Assert.Equal(0, code);
        Assert.Equal(new[] { @"register C:\repo" }, setup.Calls);
        Assert.Contains("registered", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Approve_calls_the_command_with_root_and_script()
    {
        var (code, _, setup) = Run(null, "approve", @"C:\repo", "scripts/a.ps1");

        Assert.Equal(0, code);
        Assert.Equal(new[] { @"approve C:\repo scripts/a.ps1" }, setup.Calls);
    }

    [Fact]
    public void A_refused_command_exits_one_and_prints_the_reason()
    {
        var setup = new RecordingSetup { ApproveResult = new SetupResult(false, "approval declined") };

        var (code, output, _) = Run(setup, "approve", @"C:\repo", "scripts/a.ps1");

        Assert.Equal(1, code);
        Assert.Contains("approval declined", output, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> BadArguments() => new[]
    {
        new object[] { Array.Empty<string>() },
        new object[] { new[] { "register" } },
        new object[] { new[] { "approve", "only-root" } },
        new object[] { new[] { "register", "a", "b" } },
        new object[] { new[] { "frobnicate", "x" } },
    };

    [Theory]
    [MemberData(nameof(BadArguments))]
    public void Bad_arguments_print_usage_and_exit_two_without_running_anything(string[] args)
    {
        var (code, output, setup) = Run(null, args);

        Assert.Equal(2, code);
        Assert.Contains("usage", output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(setup.Calls);
    }
}
