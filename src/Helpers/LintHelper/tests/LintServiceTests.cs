using AiDevHelpers.Helpers.LintHelper;
using Xunit;

namespace AiDevHelpers.Helpers.LintHelper.Tests;

public sealed class LintServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lint-tests", Guid.NewGuid().ToString("N"));

    public LintServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static LintService Service(int formatExit = 0, string formatOutput = "") =>
        new(new FakeRunner(formatExit, formatOutput));

    [Fact]
    public async Task ReportsMissingEditorConfig()
    {
        var findings = await Service().AnalyzeAsync(_root);

        Assert.Contains(findings, f => f.Area == "editorconfig" && f.Severity == "error");
    }

    [Fact]
    public async Task ReportsMissingRootTrue()
    {
        Write(".editorconfig", "[*.cs]\nindent_size = 4\n");

        var findings = await Service().AnalyzeAsync(_root);

        Assert.Contains(findings, f => f.Area == "editorconfig" && f.Message.Contains("root = true"));
    }

    [Fact]
    public async Task ReportsNoWarnThatSuppressesAnErrorSeverityRule()
    {
        Write(".editorconfig", "root = true\n[*.cs]\ndotnet_diagnostic.CA1822.severity = error\n");
        Write("src/App/App.csproj", "<Project><PropertyGroup><NoWarn>$(NoWarn);CA1822;CS1591</NoWarn></PropertyGroup></Project>");

        var findings = await Service().AnalyzeAsync(_root);

        var mismatch = Assert.Single(findings, f => f.Message.Contains("CA1822"));
        Assert.Equal("error", mismatch.Severity);
    }

    [Fact]
    public async Task NoWarnForWarningSeverityIsNotAMismatch()
    {
        Write(".editorconfig", "root = true\n[*.cs]\ndotnet_diagnostic.CA1822.severity = warning\n");
        Write("src/App/App.csproj", "<Project><PropertyGroup><NoWarn>CA1822</NoWarn></PropertyGroup></Project>");

        var findings = await Service().AnalyzeAsync(_root);

        Assert.DoesNotContain(findings, f => f.Message.Contains("CA1822"));
    }

    [Fact]
    public async Task ReportsTsConfigWithoutStrict()
    {
        Write(".editorconfig", "root = true\n");
        Write("web/tsconfig.json", "{ \"compilerOptions\": { \"target\": \"es2022\" } }");

        var findings = await Service().AnalyzeAsync(_root);

        Assert.Contains(findings, f => f.Area == "typescript" && f.Message.Contains("strict"));
    }

    [Fact]
    public async Task ReportsTypeScriptProjectWithoutEslintConfig()
    {
        Write(".editorconfig", "root = true\n");
        Write("web/package.json", "{ \"devDependencies\": { \"typescript\": \"5.6.0\" } }");

        var findings = await Service().AnalyzeAsync(_root);

        Assert.Contains(findings, f => f.Message.Contains("ESLint flat config") && f.Severity == "error");
    }

    [Fact]
    public async Task FlatConfigSatisfiesEslintCheck()
    {
        Write(".editorconfig", "root = true\n");
        Write("web/package.json", "{ \"devDependencies\": { \"typescript\": \"5.6.0\" } }");
        Write("web/eslint.config.mjs", "export default [];");

        var findings = await Service().AnalyzeAsync(_root);

        Assert.DoesNotContain(findings, f => f.Message.Contains("ESLint flat config"));
    }

    [Fact]
    public async Task ReportsTslintAsDeprecatedWhenTslintJsonExists()
    {
        Write(".editorconfig", "root = true\n");
        Write("web/tslint.json", "{}");

        var findings = await Service().AnalyzeAsync(_root);

        var finding = Assert.Single(findings, f => f.Message.Contains("TSLint is deprecated"));
        Assert.Equal("warning", finding.Severity);
    }

    [Fact]
    public async Task DotnetFormatFailureIsReported()
    {
        Write(".editorconfig", "root = true\n");
        Write("App.sln", "");

        var findings = await Service(formatExit: 2, formatOutput: "Formatting changes needed\nsrc/A.cs").AnalyzeAsync(_root, runDotnetFormat: true);

        Assert.Contains(findings, f => f.Area == "dotnet" && f.Message.Contains("exit 2"));
    }

    [Fact]
    public async Task DotnetFormatIsNotRunUnlessAsked()
    {
        Write(".editorconfig", "root = true\n");
        Write("App.sln", "");

        var findings = await Service(formatExit: 2).AnalyzeAsync(_root);

        Assert.DoesNotContain(findings, f => f.Message.Contains("dotnet format"));
    }

    [Fact]
    public async Task MissingFolderThrows()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Service().AnalyzeAsync(Path.Combine(_root, "nope")));
    }

    private sealed class FakeRunner(int exitCode, string output) : ICommandRunner
    {
        public Task<(int ExitCode, string Output)> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken ct = default) =>
            Task.FromResult((exitCode, output));
    }
}
