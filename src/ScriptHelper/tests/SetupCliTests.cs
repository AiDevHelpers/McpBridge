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

        public ApprovalPreview PreviewResult { get; set; } =
            new(true, "prepared", "\nApprove this script to run elevated?\n\nProject\n    C:\\repo\n", "FP1", "AL1");

        public SetupResult ConfirmResult { get; set; } = new(true, "approved");

        public ApprovalPreview PrepareApproval(string root, string relativeScript)
        {
            Calls.Add($"prepare {root} {relativeScript}");
            return PreviewResult;
        }

        public SetupResult ConfirmApproval(string root, string relativeScript, string fingerprint, string allowlistHash)
        {
            Calls.Add($"confirm {root} {relativeScript} {fingerprint} {allowlistHash}");
            return ConfirmResult;
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

    [Fact]
    public void Prepare_flag_prints_the_prompt_and_the_confirm_command_without_approving()
    {
        var (code, output, setup) = Run(null, "approve", @"C:\repo", "scripts/a.ps1", "--prepare");

        Assert.Equal(0, code);
        Assert.Equal(new[] { @"prepare C:\repo scripts/a.ps1" }, setup.Calls);
        Assert.Contains("Approve this script to run elevated?", output, StringComparison.Ordinal);
        Assert.Contains("--confirm FP1 AL1", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_that_is_refused_exits_one_and_prints_no_confirm_command()
    {
        var setup = new RecordingSetup { PreviewResult = new ApprovalPreview(false, "script is not listed", "", "", "") };

        var (code, output, _) = Run(setup, "approve", @"C:\repo", "scripts/a.ps1", "--prepare");

        Assert.Equal(1, code);
        Assert.Contains("refused: script is not listed", output, StringComparison.Ordinal);
        Assert.DoesNotContain("--confirm", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Confirm_flag_passes_both_hashes_to_the_command()
    {
        var (code, output, setup) = Run(null, "approve", @"C:\repo", "scripts/a.ps1", "--confirm", "FP1", "AL1");

        Assert.Equal(0, code);
        Assert.Equal(new[] { @"confirm C:\repo scripts/a.ps1 FP1 AL1" }, setup.Calls);
        Assert.Contains("approved", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Refused_confirm_exits_one()
    {
        var setup = new RecordingSetup { ConfirmResult = new SetupResult(false, "script changed since it was shown") };

        var (code, output, _) = Run(setup, "approve", @"C:\repo", "scripts/a.ps1", "--confirm", "FP1", "AL1");

        Assert.Equal(1, code);
        Assert.Contains("refused: script changed since it was shown", output, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> BadFlagArguments() => new[]
    {
        new object[] { new[] { "approve", "r", "s", "--confirm" } },
        new object[] { new[] { "approve", "r", "s", "--confirm", "FP1" } },
        new object[] { new[] { "approve", "r", "s", "--confirm", "FP1", "AL1", "extra" } },
        new object[] { new[] { "approve", "r", "s", "--prepare", "extra" } },
        new object[] { new[] { "approve", "r", "s", "--yes" } },
    };

    [Theory]
    [MemberData(nameof(BadFlagArguments))]
    public void Bad_approve_flags_print_usage_and_run_nothing(string[] args)
    {
        var (code, output, setup) = Run(null, args);

        Assert.Equal(2, code);
        Assert.Contains("usage", output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(setup.Calls);
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
