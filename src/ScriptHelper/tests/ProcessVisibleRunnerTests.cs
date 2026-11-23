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
    }

    [Fact]
    public void TheArguments_NameOnlyBrokerMadePaths_AndNeverTheScriptArguments()
    {
        var line = ProcessVisibleRunner.WrapperArguments(@"C:\s\w\wrapper.ps1", @"C:\s\a.ps1", @"C:\s\w\args.json", @"C:\s\w\t.txt");

        Assert.Equal("-NoProfile -ExecutionPolicy Bypass -File \"C:\\s\\w\\wrapper.ps1\" -Script \"C:\\s\\a.ps1\" -ArgsFile \"C:\\s\\w\\args.json\" -Log \"C:\\s\\w\\t.txt\"", line);
    }
}