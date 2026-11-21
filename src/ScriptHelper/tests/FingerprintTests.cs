using System.Text;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public class FingerprintTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Sha256_of_abc_matches_known_value()
    {
        var hex = Fingerprint.Sha256Hex(Bytes("abc"));

        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", hex);
    }

    [Fact]
    public void Same_parts_give_same_fingerprint()
    {
        var parts = new[] { new FingerprintPart("reset.ps1", Bytes("body")) };

        Assert.Equal(Fingerprint.Compute(parts), Fingerprint.Compute(parts));
    }

    [Fact]
    public void Changing_script_content_changes_fingerprint()
    {
        var before = Fingerprint.Compute(new[] { new FingerprintPart("reset.ps1", Bytes("v1")) });
        var after = Fingerprint.Compute(new[] { new FingerprintPart("reset.ps1", Bytes("v2")) });

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Changing_a_dot_sourced_dependency_changes_fingerprint()
    {
        var before = Fingerprint.Compute(new[]
        {
            new FingerprintPart("reset.ps1", Bytes("main")),
            new FingerprintPart("helpers.ps1", Bytes("helper v1")),
        });
        var after = Fingerprint.Compute(new[]
        {
            new FingerprintPart("reset.ps1", Bytes("main")),
            new FingerprintPart("helpers.ps1", Bytes("helper v2")),
        });

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Changing_the_interpreter_changes_fingerprint()
    {
        var before = Fingerprint.Compute(new[]
        {
            new FingerprintPart("reset.ps1", Bytes("main")),
            new FingerprintPart("interpreter", Bytes("pwsh 7.4")),
        });
        var after = Fingerprint.Compute(new[]
        {
            new FingerprintPart("reset.ps1", Bytes("main")),
            new FingerprintPart("interpreter", Bytes("pwsh 7.5")),
        });

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Part_order_does_not_change_fingerprint()
    {
        var a = new FingerprintPart("a.ps1", Bytes("1"));
        var b = new FingerprintPart("b.ps1", Bytes("2"));

        Assert.Equal(Fingerprint.Compute(new[] { a, b }), Fingerprint.Compute(new[] { b, a }));
    }

    [Fact]
    public void Renaming_a_part_changes_fingerprint()
    {
        var before = Fingerprint.Compute(new[] { new FingerprintPart("a.ps1", Bytes("same")) });
        var after = Fingerprint.Compute(new[] { new FingerprintPart("b.ps1", Bytes("same")) });

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Newline_in_a_name_cannot_forge_a_two_part_fingerprint()
    {
        var one = Fingerprint.Compute(new[] { new FingerprintPart("a\nb", Bytes("c")) });
        var two = Fingerprint.Compute(new[]
        {
            new FingerprintPart("a", Bytes("c")),
            new FingerprintPart("b", Bytes("c")),
        });

        Assert.NotEqual(one, two);
    }

    [Fact]
    public void Duplicate_part_names_are_rejected()
    {
        var parts = new[]
        {
            new FingerprintPart("reset.ps1", Bytes("v1")),
            new FingerprintPart("reset.ps1", Bytes("v2")),
        };

        Assert.Throws<ArgumentException>(() => Fingerprint.Compute(parts));
    }
}
