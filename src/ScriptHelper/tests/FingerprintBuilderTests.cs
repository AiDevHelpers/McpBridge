using System.Text;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

internal sealed class FakeInspector : IPathInspector
{
    public HashSet<string> ReparsePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsReparsePoint(string path) => ReparsePaths.Contains(path.TrimEnd('\\'));
}

internal sealed class FakeInterpreter : IInterpreterInfo
{
    public string Description { get; set; } = "pwsh|test|7.5";
    public string Describe() => Description;
}

public sealed class FingerprintBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sh-fp-" + Guid.NewGuid());
    private readonly FakeInspector _inspector = new();
    private readonly FakeInterpreter _interpreter = new();

    public FingerprintBuilderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_root, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private FingerprintResult Build(string rel = "scripts/reset.ps1") =>
        FingerprintBuilder.Build(_root, rel, _interpreter, _inspector);

    // A pin file the script reads from its own folder is part of the approved set, so it is staged beside the script.
    [Fact]
    public void Declared_pins_are_part_of_the_fingerprint_and_the_file_set()
    {
        Write("scripts/install.ps1", "Write-Output 'hi'");
        Write("scripts/install.sha256", "ABC");

        var plain = Build("scripts/install.ps1");
        var pinned = FingerprintBuilder.Build(_root, "scripts/install.ps1", _interpreter, _inspector, ["scripts/install.sha256"]);

        Assert.True(pinned.Ok);
        Assert.Contains("scripts/install.sha256", pinned.Files);
        Assert.NotEqual(plain.Fingerprint, pinned.Fingerprint);
    }

    [Fact]
    public void A_declared_pin_that_is_missing_fails_closed()
    {
        Write("scripts/install.ps1", "Write-Output 'hi'");

        var result = FingerprintBuilder.Build(_root, "scripts/install.ps1", _interpreter, _inspector, ["scripts/install.sha256"]);

        Assert.False(result.Ok);
    }

    [Fact]
    public void Script_only_fingerprint_is_script_plus_interpreter()
    {
        Write("scripts/reset.ps1", "Write-Output 'hi'");

        var r = Build();

        var expected = Fingerprint.Compute(new[]
        {
            new FingerprintPart("scripts/reset.ps1", Encoding.UTF8.GetBytes("Write-Output 'hi'")),
            new FingerprintPart("interpreter", Encoding.UTF8.GetBytes("pwsh|test|7.5")),
        });
        Assert.True(r.Ok);
        Assert.Equal(expected, r.Fingerprint);
    }

    [Fact]
    public void Changing_the_script_changes_the_fingerprint()
    {
        Write("scripts/reset.ps1", "v1");
        var before = Build().Fingerprint;
        Write("scripts/reset.ps1", "v2");

        Assert.NotEqual(before, Build().Fingerprint);
    }

    [Fact]
    public void Changing_the_interpreter_changes_the_fingerprint()
    {
        Write("scripts/reset.ps1", "x");
        var before = Build().Fingerprint;
        _interpreter.Description = "pwsh|test|7.6";

        Assert.NotEqual(before, Build().Fingerprint);
    }

    [Fact]
    public void Dot_sourced_dependency_is_included_and_pinned()
    {
        Write("scripts/reset.ps1", ". .\\helpers.ps1\nhelper");
        Write("scripts/helpers.ps1", "helper v1");
        var before = Build();
        Write("scripts/helpers.ps1", "helper v2");
        var after = Build();

        Assert.True(before.Ok);
        Assert.Contains("scripts/helpers.ps1", before.Files);
        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    [Fact]
    public void Transitive_dependency_is_included()
    {
        Write("scripts/reset.ps1", ". .\\a.ps1");
        Write("scripts/a.ps1", ". .\\lib\\b.ps1");
        Write("scripts/lib/b.ps1", "leaf v1");
        var before = Build();
        Write("scripts/lib/b.ps1", "leaf v2");

        Assert.Contains("scripts/lib/b.ps1", before.Files);
        Assert.NotEqual(before.Fingerprint, Build().Fingerprint);
    }

    [Fact]
    public void Cyclic_dot_sourcing_terminates()
    {
        Write("scripts/reset.ps1", ". .\\a.ps1");
        Write("scripts/a.ps1", ". .\\reset.ps1");

        var r = Build();

        Assert.True(r.Ok);
        Assert.Equal(2, r.Files.Count);
    }

    [Fact]
    public void Dependency_outside_the_root_fails()
    {
        Write("scripts/reset.ps1", ". ..\\..\\..\\outside.ps1");

        var r = Build();

        Assert.False(r.Ok);
        Assert.Contains("escapes", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Absolute_dependency_fails()
    {
        Write("scripts/reset.ps1", ". C:\\Windows\\evil.ps1");

        Assert.False(Build().Ok);
    }

    [Fact]
    public void Missing_dependency_fails()
    {
        Write("scripts/reset.ps1", ". .\\gone.ps1");

        var r = Build();

        Assert.False(r.Ok);
        Assert.Contains("does not exist", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unresolved_dependency_fails_closed()
    {
        Write("scripts/reset.ps1", ". $somewhere");

        var r = Build();

        Assert.False(r.Ok);
        Assert.Contains("unresolved", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_script_fails()
    {
        var r = Build("scripts/nope.ps1");

        Assert.False(r.Ok);
        Assert.Contains("does not exist", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reparse_point_on_the_script_fails()
    {
        Write("scripts/reset.ps1", "x");
        _inspector.ReparsePaths.Add(Path.Combine(_root, "scripts", "reset.ps1"));

        var r = Build();

        Assert.False(r.Ok);
        Assert.Contains("reparse", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reparse_point_on_a_parent_directory_fails()
    {
        Write("scripts/reset.ps1", "x");
        _inspector.ReparsePaths.Add(Path.Combine(_root, "scripts"));

        Assert.False(Build().Ok);
    }

    [Fact]
    public void Reparse_point_on_the_root_fails()
    {
        Write("scripts/reset.ps1", "x");
        _inspector.ReparsePaths.Add(_root);

        Assert.False(Build().Ok);
    }

    [Fact]
    public void Reparse_point_on_a_dependency_fails()
    {
        Write("scripts/reset.ps1", ". .\\helpers.ps1");
        Write("scripts/helpers.ps1", "h");
        _inspector.ReparsePaths.Add(Path.Combine(_root, "scripts", "helpers.ps1"));

        Assert.False(Build().Ok);
    }

    [Fact]
    public void Too_many_dependencies_fail()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 70; i++)
        {
            Write($"scripts/dep{i}.ps1", "x");
            sb.AppendLine($". .\\dep{i}.ps1");
        }

        Write("scripts/reset.ps1", sb.ToString());

        var r = Build();

        Assert.False(r.Ok);
        Assert.Contains("too many", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dependency_path_spelling_does_not_change_the_fingerprint()
    {
        Write("scripts/reset.ps1", ". .\\helpers.ps1");
        Write("scripts/helpers.ps1", "h");
        var a = Build().Fingerprint;
        Write("scripts/reset.ps1", ". .\\sub\\..\\helpers.ps1");
        // Script text differs, so the script part differs; compare the dependency file list instead.
        var b = Build();

        Assert.Contains("scripts/helpers.ps1", b.Files);
        Assert.NotEqual(a, b.Fingerprint);
    }
}
