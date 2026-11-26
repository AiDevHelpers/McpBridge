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

        public SetupResult AllowAllResult { get; set; } = new(true, "allow-all approved");

        public SetupResult AllowAll(string root)
        {
            Calls.Add($"allow-all {root}");
            return AllowAllResult;
        }

        public SetupResult PruneStale(string root) => throw new NotSupportedException();

        public SetupResult AllowAllApproved(string root, string approvedHash)
        {
            Calls.Add($"allow-all-approved {root} {approvedHash}");
            return AllowAllResult;
        }

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

    [Fact]
    public void AllowAll_CallsTheSetupAndExitsZero()
    {
        var (code, output, setup) = Run(null, "allow-all", @"C:\repo");

        Assert.Equal(0, code);
        Assert.Equal([@"allow-all C:\repo"], setup.Calls);
        Assert.Contains("allow-all approved", output);
    }

    [Fact]
    public void AllowAll_Refused_ExitsOne()
    {
        var setup = new RecordingSetup { AllowAllResult = new(false, "allow-all declined") };

        var (code, output, _) = Run(setup, "allow-all", @"C:\repo");

        Assert.Equal(1, code);
        Assert.Contains("refused: allow-all declined", output);
    }

    private sealed class FakeVerify(VerifyReport report) : IVerifyCommands
    {
        public List<string> Approved { get; } = [];

        public VerifyReport Verify(string root) => report;

        public ScanReport ScanResult { get; set; } = new(true, "scanned", []);

        public List<string> Added { get; } = [];

        public ScanReport Scan(string root) => ScanResult;

        public IReadOnlyList<SetupResult> AddToAllowlist(string root, Func<string, bool> confirm)
        {
            foreach (var s in ScanResult.Scripts.Where(s => !s.Listed && confirm(s.Path)))
            {
                Added.Add(s.Path);
            }

            return Added.Select(p => new SetupResult(true, "added " + p)).ToList();
        }

        public IReadOnlyList<SetupResult> ApproveUnsigned(string root, Func<string, bool> confirm)
        {
            foreach (var s in report.Scripts.Where(s => s.Status != ApprovalStatus.Approved && confirm(s.Path)))
            {
                Approved.Add(s.Path);
            }

            return Approved.Select(p => new SetupResult(true, "approved " + p)).ToList();
        }
    }

    private sealed class YesNo(bool answer) : AiDevHelpers.Common.Contracts.IConfirmer
    {
        public List<string> Prompts { get; } = [];

        public bool Confirm(string prompt)
        {
            Prompts.Add(prompt);
            return answer;
        }
    }

    private static VerifyReport Report(params (string Path, ApprovalStatus Status)[] scripts) =>
        new(true, "verified", false, scripts.Select(s => new ScriptStatus(s.Path, s.Status, "")).ToList());

    [Fact]
    public void Scan_ListsScriptsAndAddsTheConfirmedOnes()
    {
        var output = new StringWriter();
        var verify = new FakeVerify(Report())
        {
            ScanResult = new(true, "scanned", [new FoundScript("a.ps1", ".vscode/launch.json", false), new FoundScript("b.ps1", ".vscode/tasks.json", true)]),
        };

        var code = SetupCli.Run(["scan", @"C:\repo"], new RecordingSetup(), output, verify, new YesNo(true));

        Assert.Equal(0, code);
        Assert.Equal(["a.ps1"], verify.Added);
        Assert.Contains("setup verify", output.ToString());
        Assert.Contains("listed", output.ToString());
    }

    [Fact]
    public void Scan_Declined_AddsNothing()
    {
        var output = new StringWriter();
        var verify = new FakeVerify(Report()) { ScanResult = new(true, "scanned", [new FoundScript("a.ps1", "x", false)]) };

        var code = SetupCli.Run(["scan", @"C:\repo"], new RecordingSetup(), output, verify, new YesNo(false));

        Assert.Equal(0, code);
        Assert.Empty(verify.Added);
        Assert.DoesNotContain("setup verify", output.ToString());
    }

    [Fact]
    public void Scan_NothingFound_SaysSo()
    {
        var output = new StringWriter();

        var code = SetupCli.Run(["scan", @"C:\repo"], new RecordingSetup(), output, new FakeVerify(Report()), new YesNo(true));

        Assert.Equal(0, code);
        Assert.Contains("No launch or debug scripts", output.ToString());
    }

    [Fact]
    public void Scan_Refused_ExitsOne()
    {
        var output = new StringWriter();
        var verify = new FakeVerify(Report()) { ScanResult = ScanReport.Failed("project is not registered") };

        var code = SetupCli.Run(["scan", @"C:\repo"], new RecordingSetup(), output, verify, new YesNo(true));

        Assert.Equal(1, code);
        Assert.Contains("refused: project is not registered", output.ToString());
    }

    [Fact]
    public void Verify_AllApproved_ExitsZeroAndAsksNothing()
    {
        var output = new StringWriter();
        var confirmer = new YesNo(true);

        var code = SetupCli.Run(["verify", @"C:\repo"], new RecordingSetup(), output, new FakeVerify(Report(("a.ps1", ApprovalStatus.Approved))), confirmer);

        Assert.Equal(0, code);
        Assert.Empty(confirmer.Prompts);
    }

    [Fact]
    public void Verify_Unsigned_AsksPerScriptAndApprovesTheConfirmedOnes()
    {
        var output = new StringWriter();
        var verify = new FakeVerify(Report(("a.ps1", ApprovalStatus.Unsigned), ("b.ps1", ApprovalStatus.Changed)));
        var confirmer = new YesNo(true);

        SetupCli.Run(["verify", @"C:\repo"], new RecordingSetup(), output, verify, confirmer);

        Assert.Equal(["a.ps1", "b.ps1"], verify.Approved);
        Assert.Equal(2, confirmer.Prompts.Count);
        Assert.Contains("a.ps1", output.ToString());
        Assert.Contains("Unsigned", output.ToString());
    }

    [Fact]
    public void Verify_Declined_ApprovesNothingAndExitsOne()
    {
        var output = new StringWriter();
        var verify = new FakeVerify(Report(("a.ps1", ApprovalStatus.Unsigned)));

        var code = SetupCli.Run(["verify", @"C:\repo"], new RecordingSetup(), output, verify, new YesNo(false));

        Assert.Equal(1, code);
        Assert.Empty(verify.Approved);
    }

    [Fact]
    public void Verify_Refused_ExitsOne()
    {
        var output = new StringWriter();

        var code = SetupCli.Run(["verify", @"C:\repo"], new RecordingSetup(), output, new FakeVerify(VerifyReport.Failed("project is not registered")), new YesNo(true));

        Assert.Equal(1, code);
        Assert.Contains("refused: project is not registered", output.ToString());
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

    [Fact]
    public void SetupToolsIsRefusedWithoutTheYesInChat()
    {
        var refusal = SetupApproval.ToolsRefusal(approvedInChat: false);

        Assert.NotNull(refusal);
        Assert.Contains("--approved-in-chat", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupToolsRunsWhenTheYesWasGivenInChat()
    {
        Assert.Null(SetupApproval.ToolsRefusal(approvedInChat: true));
    }
}
