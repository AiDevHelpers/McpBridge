using Xunit;

namespace AiDevHelpers.Helpers.LocalizationHelper.Tests;

public sealed class CultureCallCheckerTests : IDisposable
{
    private readonly ResxFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private IReadOnlyList<CultureCallFinding> Check(string body, string members = "")
    {
        var path = _fx.Cs("P", "Calls.cs",
            "using System;\nusing System.Globalization;\nclass C\n{\n" + members + "    void M(string s, int i, double d, DateTime t)\n    {\n        " + body + "\n    }\n}\n");
        return CultureCallChecker.Check(path);
    }

    [Fact]
    public void FlagsStringFormatWithoutProvider() =>
        Assert.Equal("culture-call", Assert.Single(Check("var a = string.Format(\"{0}\", i);")).Kind);

    [Fact]
    public void DoesNotFlagStringFormatWithInvariantCulture() =>
        Assert.Empty(Check("var a = string.Format(CultureInfo.InvariantCulture, \"{0}\", i);"));

    [Fact]
    public void FlagsIntParseAndTryParse() =>
        Assert.Equal(2, Check("var a = int.Parse(s); int.TryParse(s, out var b);").Count);

    [Fact]
    public void DoesNotFlagParseWithCulture() =>
        Assert.Empty(Check("var a = double.Parse(s, CultureInfo.InvariantCulture); DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out var b);"));

    [Fact]
    public void FlagsToUpperAndToLower() =>
        Assert.Equal(2, Check("var a = s.ToUpper(); var b = s.ToLower();").Count);

    [Fact]
    public void DoesNotFlagToUpperInvariant() =>
        Assert.Empty(Check("var a = s.ToUpperInvariant(); var b = s.ToLower(CultureInfo.InvariantCulture);"));

    [Fact]
    public void FlagsStringCompareAndStartsWithString() =>
        Assert.Equal(3, Check("var a = string.Compare(s, \"x\"); var b = s.StartsWith(\"x\"); var c = s.IndexOf(\"x\");").Count);

    [Fact]
    public void DoesNotFlagStringComparisonOrdinal() =>
        Assert.Empty(Check("var a = string.Compare(s, \"x\", StringComparison.Ordinal); var b = s.StartsWith(\"x\", StringComparison.Ordinal); var c = s.IndexOf('x');"));

    [Fact]
    public void FlagsToStringOnIntDoubleDateTime() =>
        Assert.Equal(3, Check("var a = i.ToString(); var b = d.ToString(); var c = t.ToString();").Count);

    [Fact]
    public void DoesNotFlagToStringOnStringOrUserType() =>
        Assert.Empty(Check("var a = s.ToString(); var b = new Widget().ToString();", "    sealed class Widget { }\n"));

    [Fact]
    public void DoesNotFlagContainsString() =>
        Assert.Empty(Check("var a = s.Contains(\"x\");"));

    [Fact]
    public void CultureOkCommentSuppresses() =>
        Assert.Empty(Check("var a = i.ToString(); // culture-ok"));

    [Fact]
    public void UnresolvedTypeIsSkipped() =>
        Assert.Empty(Check("var a = Missing.Parse(s);"));

    [Fact]
    public void FindingNamesTheCallAndALine()
    {
        var finding = Assert.Single(Check("var a = i.ToString();"));

        Assert.Equal("i.ToString()", finding.Call);
        Assert.Equal(7, finding.Line);
        Assert.False(string.IsNullOrWhiteSpace(finding.Suggestion));
    }

    [Fact]
    public void FolderScanSkipsObj()
    {
        _fx.Cs(Path.Combine("P", "obj"), "Gen.cs", "class G { string M(int i) => i.ToString(); }\n");
        _fx.Cs("P", "Ok.cs", "class O { }\n");

        Assert.Empty(CultureCallChecker.Check(_fx.Root));
    }

    [Fact]
    public void MissingPathThrows() =>
        Assert.Throws<DirectoryNotFoundException>(() => CultureCallChecker.Check(Path.Combine(_fx.Root, "nope")));
}
