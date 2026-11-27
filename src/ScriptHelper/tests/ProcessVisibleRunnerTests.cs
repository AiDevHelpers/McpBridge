using AiDevHelpers.Common.Processes;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class ProcessVisibleRunnerTests
{
    [Fact]
    public async Task TheWrapper_IsValidPowerShell_AndHoldsNoCallerText()
    {
        var file = Path.Combine(Path.GetTempPath(), "wrapper-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, ProcessVisibleRunner.Wrapper);
        try
        {
            var result = await CommandRunner.RunAsync(new CommandRequest
            {
                FileName = "powershell.exe",
                Arguments =
                [
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "$e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile($env:SCRIPT_UNDER_TEST, [ref]$null, [ref]$e); if ($e.Count) { $e | ForEach-Object { $_.Message }; exit 1 }",
                ],
                Environment = new Dictionary<string, string?> { ["SCRIPT_UNDER_TEST"] = file },
            });

            Assert.True(result.Success, result.StdOut + result.StdErr);
        }
        finally
        {
            File.Delete(file);
        }

        Assert.DoesNotContain("Invoke-Expression", ProcessVisibleRunner.Wrapper);
        Assert.Contains("ConvertFrom-Json", ProcessVisibleRunner.Wrapper);
        Assert.Contains("Start-Transcript", ProcessVisibleRunner.Wrapper);
        Assert.Contains("-File $Script @scriptArgs | Out-Host", ProcessVisibleRunner.Wrapper);
        Assert.DoesNotContain("& $Script", ProcessVisibleRunner.Wrapper);
    }

    [Fact]
    public void TheArguments_NameOnlyBrokerMadePaths_AndNeverTheScriptArguments()
    {
        var line = ProcessVisibleRunner.WrapperArguments(@"C:\s\w\wrapper.ps1", @"C:\s\a.ps1", @"C:\s\w\args.json", @"C:\s\w\t.txt");

        Assert.Equal("-NoProfile -ExecutionPolicy Bypass -File \"C:\\s\\w\\wrapper.ps1\" -Script \"C:\\s\\a.ps1\" -ArgsFile \"C:\\s\\w\\args.json\" -Log \"C:\\s\\w\\t.txt\"", line);
    }

    [Fact]
    public async Task TheWrapper_BindsNamedParametersAndPassesTheExitCodeThrough()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wrapper-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var script = Path.Combine(dir, "s.ps1");
            File.WriteAllText(script, "param([int]$ExitCode = 0)\nWrite-Output \"got $ExitCode\"\nexit $ExitCode\n");
            var wrapper = Path.Combine(dir, "wrapper.ps1");
            File.WriteAllText(wrapper, ProcessVisibleRunner.Wrapper.Replace("[void](Read-Host)", "$null"));
            var argsFile = Path.Combine(dir, "args.json");
            File.WriteAllText(argsFile, System.Text.Json.JsonSerializer.Serialize(new[] { "-ExitCode", "3" }));
            var log = Path.Combine(dir, "t.txt");

            var result = await CommandRunner.RunAsync(new CommandRequest
            {
                FileName = "pwsh.exe",
                Arguments = ["-NoProfile", "-File", wrapper, "-Script", script, "-ArgsFile", argsFile, "-Log", log],
            });

            Assert.Equal(3, result.ExitCode);
            Assert.Contains("got 3", File.ReadAllText(log));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
