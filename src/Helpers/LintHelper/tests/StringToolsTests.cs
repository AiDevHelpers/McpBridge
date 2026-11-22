using AiDevHelpers.Helpers.LintHelper;
using Xunit;

namespace AiDevHelpers.Helpers.LintHelper.Tests;

public sealed class StringScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "string-scan-tests", Guid.NewGuid().ToString("N"));

    public StringScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void FindsAMultiWordLiteralWithItsLine()
    {
        Write("A.cs", "class A\n{\n    string M() => \"Could not read the file.\";\n}\n");

        var finding = Assert.Single(StringScanner.Scan(_root));

        Assert.Equal("A.cs", finding.File);
        Assert.Equal(3, finding.Line);
        Assert.Equal("Could not read the file.", finding.Literal);
        Assert.Null(finding.ResxKey);
    }

    [Fact]
    public void SkipsCommentsAttributesSingleWordsAndLoggerCalls()
    {
        Write("B.cs",
            "// a comment with several words\n" +
            "[Description(\"A tool description with words\")]\n" +
            "var one = \"Word\";\n" +
            "logger.LogInformation(\"Started the server now\");\n");

        Assert.Empty(StringScanner.Scan(_root));
    }

    [Fact]
    public void SkipsStringsDotCsAndBuildFolders()
    {
        Write("Strings.cs", "var a = \"Two words here\";\n");
        Write("obj/C.cs", "var b = \"Two words here\";\n");
        Write("bin/D.cs", "var c = \"Two words here\";\n");

        Assert.Empty(StringScanner.Scan(_root));
    }

    [Fact]
    public void AllowedLiteralsAreLeftOut()
    {
        Write("E.cs", "var a = \"Keep this one\";\nvar b = \"Report this one\";\n");

        var findings = StringScanner.Scan(_root, literal => literal == "Keep this one");

        Assert.Equal("Report this one", Assert.Single(findings).Literal);
    }

    [Fact]
    public void NamesTheResxKeyWhenTheTextIsAlreadyAResource()
    {
        Write("Resources/Strings.resx",
            "<root><data name=\"NotFound\" xml:space=\"preserve\"><value>Could not read the file.</value></data></root>");
        Write("F.cs", "var a = \"Could not read the file.\";\n");

        var finding = Assert.Single(StringScanner.Scan(_root));

        Assert.Equal("NotFound", finding.ResxKey);
    }

    [Fact]
    public void ScansASingleFile()
    {
        var file = Write("G.cs", "var a = \"Only this file\";\n");

        var finding = Assert.Single(StringScanner.Scan(file));

        Assert.Equal("G.cs", finding.File);
    }

    [Fact]
    public void MissingPathThrows()
    {
        Assert.Throws<DirectoryNotFoundException>(() => StringScanner.Scan(Path.Combine(_root, "nope")));
    }
}

public sealed class ResxCheckerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resx-check-tests", Guid.NewGuid().ToString("N"));

    public ResxCheckerTests() => Directory.CreateDirectory(_root);

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

    private static string Resx(params string[] names) =>
        "<root>" + string.Concat(names.Select(n => $"<data name=\"{n}\" xml:space=\"preserve\"><value>text for {n}</value></data>")) + "</root>";

    [Fact]
    public void ACleanProjectHasNoFindings()
    {
        Write("H/src/Resources/Strings.resx", Resx("One"));
        Write("H/src/X.cs", "var a = Strings.Get(\"One\");\n");

        Assert.Empty(ResxChecker.Check(_root));
    }

    [Fact]
    public void ReportsADuplicateName()
    {
        Write("H/src/Resources/Strings.resx", Resx("One", "One"));
        Write("H/src/X.cs", "var a = Strings.Get(\"One\");\n");

        var finding = Assert.Single(ResxChecker.Check(_root));

        Assert.Equal("duplicate", finding.Kind);
        Assert.Equal("One", finding.Key);
    }

    [Fact]
    public void ReportsAKeyUsedInCodeButMissingFromTheResx()
    {
        Write("H/src/Resources/Strings.resx", Resx("One"));
        Write("H/src/X.cs", "var a = Strings.Get(\"One\");\nvar b = Strings.Format(\"Two\", 1);\n");

        var finding = Assert.Single(ResxChecker.Check(_root));

        Assert.Equal("missing", finding.Kind);
        Assert.Equal("Two", finding.Key);
    }

    [Fact]
    public void ReportsAKeyNeverUsedInCode()
    {
        Write("H/src/Resources/Strings.resx", Resx("One", "Spare"));
        Write("H/src/X.cs", "var a = Strings.Get(\"One\");\n");

        var finding = Assert.Single(ResxChecker.Check(_root));

        Assert.Equal("unused", finding.Kind);
        Assert.Equal("Spare", finding.Key);
    }

    [Fact]
    public void MissingFolderThrows()
    {
        Assert.Throws<DirectoryNotFoundException>(() => ResxChecker.Check(Path.Combine(_root, "nope")));
    }
}
