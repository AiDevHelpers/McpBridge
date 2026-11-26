using Xunit;

namespace AiDevHelpers.Helpers.LocalizationHelper.Tests;

public sealed class StringExtractorTests : IDisposable
{
    private readonly ResxFixture _fx = new();
    private readonly PlanStore<ExtractPlan> _store = new();

    public void Dispose() => _fx.Dispose();

    private StringExtractor Extractor() => new(_store);

    private string Project(params (string Name, string Value, string? Comment)[] entries)
    {
        _fx.Cs("P", "Strings.cs", "namespace P;\ninternal static class Strings\n{\n}\n");
        _fx.Resx("P", null, "\n", false, entries);
        return _fx.Root;
    }

    private string Source(string body, string file = "A.cs", string ns = "P") =>
        _fx.Cs("P", file, $"namespace {ns};\nclass A\n{{\n    void M()\n    {{\n{body}\n    }}\n}}\n");


    [Fact]
    public void PlainLiteralBecomesStringsGet()
    {
        var root = Project();
        var cs = Source("        var a = \"Could not read the file.\";");
        var x = Extractor();

        var plan = x.Plan(root, null);
        x.Apply(plan.Token);

        Assert.Equal("CouldNotReadTheFile", Assert.Single(plan.Items).Key);
        Assert.Contains("var a = Strings.Get(\"CouldNotReadTheFile\");", File.ReadAllText(cs));
        Assert.Equal("Could not read the file.", ResxFile.Load(Path.Combine(root, "P", "Resources", "Strings.resx")).Entries.Single().Value);
    }

    [Fact]
    public void InterpolatedLiteralBecomesFormat()
    {
        var root = Project();
        var cs = Source("        var a = $\"Could not read {name} at {path.Full}.\";");
        var x = Extractor();

        var plan = x.Plan(root, null);
        x.Apply(plan.Token);

        Assert.Contains("Strings.Format(\"CouldNotReadAt\", name, path.Full)", File.ReadAllText(cs));
        Assert.Equal("Could not read {0} at {1}.", ResxFile.Load(Path.Combine(root, "P", "Resources", "Strings.resx")).Entries.Single().Value);
    }

    [Fact]
    public void RepeatedHoleGetsTwoPlaceholders()
    {
        var root = Project();
        Source("        var a = $\"Copy the file {name} over {name} now.\";");

        var item = Assert.Single(Extractor().Plan(root, null).Items);

        Assert.Equal("Copy the file {0} over {1} now.", item.Value);
        Assert.Equal("Strings.Format(\"" + item.Key + "\", name, name)", item.Replacement);
    }

    [Fact]
    public void DuplicateTextSharesOneKey()
    {
        var root = Project();
        Source("        var a = \"Could not read it.\";\n        var b = \"Could not read it.\";");

        var plan = Extractor().Plan(root, null);

        Assert.Equal(2, plan.Items.Count);
        Assert.Single(plan.Items.Select(i => i.Key).Distinct());
    }

    [Fact]
    public void ExistingResxTextReusesItsKey()
    {
        var root = Project(("Already", "Could not read it.", null));
        Source("        var a = \"Could not read it.\";");

        Assert.Equal("Already", Assert.Single(Extractor().Plan(root, null).Items).Key);
    }

    [Fact]
    public void KeyCollisionGetsSuffix()
    {
        var root = Project(("CouldNotReadIt", "Something else entirely.", null));
        Source("        var a = \"Could not read it.\";");

        Assert.Equal("CouldNotReadIt2", Assert.Single(Extractor().Plan(root, null).Items).Key);
    }

    [Fact]
    public void CallerKeyOverridesProposal()
    {
        var root = Project();
        Source("        var a = \"Could not read it.\";");

        var plan = Extractor().Plan(root, new Dictionary<string, string> { ["Could not read it."] = "ReadFailed" });

        Assert.Equal("ReadFailed", Assert.Single(plan.Items).Key);
    }

    [Fact]
    public void CallerKeyWithDifferentExistingTextRefused()
    {
        var root = Project(("ReadFailed", "Different text.", null));
        Source("        var a = \"Could not read it.\";");

        Assert.Throws<ArgumentException>(() =>
            Extractor().Plan(root, new Dictionary<string, string> { ["Could not read it."] = "ReadFailed" }));
    }

    [Fact]
    public void FileWithoutStringsClassIsSkipped()
    {
        _fx.Cs("Q", "A.cs", "namespace Q;\nclass A { void M() { var a = \"Could not read it.\"; } }\n");

        var plan = Extractor().Plan(_fx.Root, null);

        Assert.Empty(plan.Items);
        Assert.Equal("no-strings-class", Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void NamespaceOutsideProjectIsSkipped()
    {
        var root = Project();
        Source("        var a = \"Could not read it.\";", ns: "Other");

        Assert.Equal("namespace-mismatch", Assert.Single(Extractor().Plan(root, null).Skipped).Reason);
    }

    [Fact]
    public void ConstLineIsSkipped()
    {
        var root = Project();
        Source("        const string A = \"Could not read it.\";");

        Assert.Equal("const-context", Assert.Single(Extractor().Plan(root, null).Skipped).Reason);
    }

    [Fact]
    public void CaseLabelIsSkipped()
    {
        var root = Project();
        Source("        switch (x) { case \"Could not read it.\": break; }");

        Assert.Equal("case-label", Assert.Single(Extractor().Plan(root, null).Skipped).Reason);
    }

    [Fact]
    public void FormatSpecifierHoleIsSkipped()
    {
        var root = Project();
        Source("        var a = $\"Total is {count:N2} items.\";");

        Assert.Equal("interpolation-format", Assert.Single(Extractor().Plan(root, null).Skipped).Reason);
    }

    [Fact]
    public void VerbatimIsSkipped()
    {
        var root = Project();
        Source("        var a = @\"Could not read it.\";");

        Assert.Equal("verbatim-or-raw", Assert.Single(Extractor().Plan(root, null).Skipped).Reason);
    }

    [Fact]
    public void TwoLiteralsOnOneLineRewriteCorrectly()
    {
        var root = Project();
        var cs = Source("        var a = Pick(\"First message here.\", \"Second message here.\");");
        var x = Extractor();

        x.Apply(x.Plan(root, null).Token);

        var text = File.ReadAllText(cs);
        Assert.Contains("Pick(Strings.Get(\"FirstMessageHere\"), Strings.Get(\"SecondMessageHere\"))", text);
    }

    [Fact]
    public void ApplyRefusesWhenFileChanged()
    {
        var root = Project();
        var cs = Source("        var a = \"Could not read it.\";");
        var x = Extractor();
        var plan = x.Plan(root, null);

        File.AppendAllText(cs, "// edited\n");

        Assert.Throws<InvalidOperationException>(() => x.Apply(plan.Token));
    }

    [Fact]
    public void CrlfSourceStaysCrlf()
    {
        var root = Project();
        var cs = Source("        var a = \"Could not read it.\";");
        File.WriteAllText(cs, File.ReadAllText(cs).Replace("\n", "\r\n"));
        var x = Extractor();

        x.Apply(x.Plan(root, null).Token);

        var text = File.ReadAllText(cs);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty));
        Assert.Contains("Strings.Get(", text);
    }

    [Fact]
    public void PlanWritesNothing()
    {
        var root = Project();
        var cs = Source("        var a = \"Could not read it.\";");
        var before = File.ReadAllBytes(cs);
        var resx = Path.Combine(root, "P", "Resources", "Strings.resx");
        var resxBefore = File.ReadAllBytes(resx);

        Extractor().Plan(root, null);

        Assert.Equal(before, File.ReadAllBytes(cs));
        Assert.Equal(resxBefore, File.ReadAllBytes(resx));
    }

    [Fact]
    public void AfterApplyScannerFindsNothingNew()
    {
        var root = Project();
        Source("        var a = \"Could not read it.\";");
        var x = Extractor();

        x.Apply(x.Plan(root, null).Token);

        Assert.Empty(StringScanner.Scan(Path.Combine(root, "P", "A.cs")));
    }

    [Fact]
    public void AfterApplyResxCheckReportsNoMissing()
    {
        var root = Project();
        Source("        var a = \"Could not read it.\";");
        var x = Extractor();

        x.Apply(x.Plan(root, null).Token);

        Assert.DoesNotContain(ResxChecker.Check(root), f => f.Kind == "missing");
    }

    [Fact]
    public void ScannerFindingCarriesColumnAndInterpolatedFlag()
    {
        var root = Project();
        Source("        var a = $\"Could not read {name} now.\";");

        var finding = Assert.Single(StringScanner.Scan(Path.Combine(root, "P", "A.cs")));

        Assert.True(finding.Interpolated);
        Assert.Equal("        var a = ".Length, finding.Column);
    }
}
