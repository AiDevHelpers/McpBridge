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
    public void ReportsMissingEditorConfig()
    {
        var findings = Service().Analyze(_root);

        Assert.Contains(findings, f => f.Area == "editorconfig" && f.Severity == "error");
    }

    [Fact]
    public void ReportsMissingRootTrue()
    {
        Write(".editorconfig", "[*.cs]\nindent_size = 4\n");

        var findings = Service().Analyze(_root);

        Assert.Contains(findings, f => f.Area == "editorconfig" && f.Message.Contains("root = true"));
    }

    [Fact]
    public void ReportsNoWarnThatSuppressesAnErrorSeverityRule()
    {
        Write(".editorconfig", "root = true\n[*.cs]\ndotnet_diagnostic.CA1822.severity = error\n");
        Write("src/App/App.csproj", "<Project><PropertyGroup><NoWarn>$(NoWarn);CA1822;CS1591</NoWarn></PropertyGroup></Project>");

        var findings = Service().Analyze(_root);

        var mismatch = Assert.Single(findings, f => f.Message.Contains("CA1822"));
        Assert.Equal("error", mismatch.Severity);
    }

    [Fact]
    public void NoWarnForWarningSeverityIsNotAMismatch()
    {
        Write(".editorconfig", "root = true\n[*.cs]\ndotnet_diagnostic.CA1822.severity = warning\n");
        Write("src/App/App.csproj", "<Project><PropertyGroup><NoWarn>CA1822</NoWarn></PropertyGroup></Project>");

        var findings = Service().Analyze(_root);

        Assert.DoesNotContain(findings, f => f.Message.Contains("CA1822"));
    }

    [Fact]
    public void ReportsTsConfigWithoutStrict()
    {
        Write(".editorconfig", "root = true\n");
        Write("web/tsconfig.json", "{ \"compilerOptions\": { \"target\": \"es2022\" } }");

        var findings = Service().Analyze(_root);

        Assert.Contains(findings, f => f.Area == "typescript" && f.Message.Contains("strict"));
    }

    [Fact]
    public void ReportsTypeScriptProjectWithoutEslintConfig()
    {
        Write(".editorconfig", "root = true\n");
        Write("web/package.json", "{ \"devDependencies\": { \"typescript\": \"5.6.0\" } }");

        var findings = Service().Analyze(_root);

        Assert.Contains(findings, f => f.Message.Contains("ESLint flat config") && f.Severity == "error");
    }

    [Fact]
    public void FlatConfigSatisfiesEslintCheck()
    {
        Write(".editorconfig", "root = true\n");
        Write("web/package.json", "{ \"devDependencies\": { \"typescript\": \"5.6.0\" } }");
        Write("web/eslint.config.mjs", "export default [];");

        var findings = Service().Analyze(_root);

        Assert.DoesNotContain(findings, f => f.Message.Contains("ESLint flat config"));
    }

    [Fact]
    public void ReportsTslintAsDeprecatedWhenTslintJsonExists()
    {
        Write(".editorconfig", "root = true\n");
        Write("web/tslint.json", "{}");

        var findings = Service().Analyze(_root);

        var finding = Assert.Single(findings, f => f.Message.Contains("TSLint is deprecated"));
        Assert.Equal("warning", finding.Severity);
    }

    [Fact]
    public void DotnetFormatFailureIsReported()
    {
        Write(".editorconfig", "root = true\n");
        Write("App.sln", "");

        var findings = Service(formatExit: 2, formatOutput: "Formatting changes needed\nsrc/A.cs").Analyze(_root, runDotnetFormat: true);

        Assert.Contains(findings, f => f.Area == "dotnet" && f.Message.Contains("exit 2"));
    }

    [Fact]
    public void DotnetFormatIsNotRunUnlessAsked()
    {
        Write(".editorconfig", "root = true\n");
        Write("App.sln", "");

        var findings = Service(formatExit: 2).Analyze(_root);

        Assert.DoesNotContain(findings, f => f.Message.Contains("dotnet format"));
    }

    [Fact]
    public void MissingFolderThrows()
    {
        Assert.Throws<DirectoryNotFoundException>(() => Service().Analyze(Path.Combine(_root, "nope")));
    }

    private sealed class FakeRunner(int exitCode, string output) : ICommandRunner
    {
        public (int ExitCode, string Output) Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory) =>
            (exitCode, output);
    }
}
