using System;
using System.IO;
using System.Linq;
using System.Text;
using AiDevHelpers.Installers.Vs2026.Core;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class FileEditEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fileedit-" + Guid.NewGuid().ToString("N"));

    public FileEditEngineTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void DetectBomReadsEachByteOrderMark()
    {
        Assert.Equal(FileEditEngine.Encodings.Utf8, FileEditEngine.DetectBom(new byte[] { 0xEF, 0xBB, 0xBF, 0x61 }).Encoding);
        Assert.Equal(FileEditEngine.Encodings.Utf16Le, FileEditEngine.DetectBom(new byte[] { 0xFF, 0xFE, 0x61, 0x00 }).Encoding);
        Assert.Equal(FileEditEngine.Encodings.Utf16Be, FileEditEngine.DetectBom(new byte[] { 0xFE, 0xFF, 0x00, 0x61 }).Encoding);
        Assert.Equal(FileEditEngine.Encodings.Utf8, FileEditEngine.DetectBom(Encoding.UTF8.GetBytes("abc")).Encoding);
        Assert.Equal(0, FileEditEngine.DetectBom(Encoding.UTF8.GetBytes("abc")).BomLength);
    }

    [Fact]
    public void ApplyReplacesEveryMatchByDefault()
    {
        var r = FileEditEngine.ApplyEdits("a b a", new[] { new FileEditEngine.Edit("a", "x", 0) });

        Assert.Equal("x b x", r.Text);
        Assert.Equal(2, r.Matches);
    }

    [Fact]
    public void ApplyReplacesOnlyTheNthMatch()
    {
        var r = FileEditEngine.ApplyEdits("a b a", new[] { new FileEditEngine.Edit("a", "x", 2) });

        Assert.Equal("a b x", r.Text);
    }

    [Fact]
    public void ApplyRefusesAnOldTextWithNoMatch()
    {
        Assert.Throws<InvalidOperationException>(() => FileEditEngine.ApplyEdits("abc", new[] { new FileEditEngine.Edit("zzz", "x", 0) }));
    }

    [Fact]
    public void ApplyStopsAtTheFirstFailingEdit()
    {
        Assert.Throws<InvalidOperationException>(() => FileEditEngine.ApplyEdits("abc", new[]
        {
            new FileEditEngine.Edit("a", "x", 0),
            new FileEditEngine.Edit("zzz", "y", 0),
        }));
    }

    [Fact]
    public void PlanWritesNothing()
    {
        var file = Path.Combine(_root, "a.txt");
        File.WriteAllText(file, "one two", new UTF8Encoding(false));

        var result = FileEditEngine.Replace(_root, "a.txt", new[] { new FileEditEngine.Edit("two", "three", 0) }, confirm: false);

        Assert.Equal("one two", File.ReadAllText(file));
        Assert.Equal(1, result.Matches);
        Assert.False(result.Applied);
    }

    [Fact]
    public void ApplyKeepsTheBomAndWritesAByteExactBackup()
    {
        var file = Path.Combine(_root, "b.txt");
        var original = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("hi there")).ToArray();
        File.WriteAllBytes(file, original);

        var result = FileEditEngine.Replace(_root, "b.txt", new[] { new FileEditEngine.Edit("there", "you", 0) }, confirm: true);

        Assert.True(result.Applied);
        var after = File.ReadAllBytes(file);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, after.Take(2).ToArray());
        Assert.Equal("hi you", Encoding.Unicode.GetString(after, 2, after.Length - 2));

        var backups = Directory.GetFiles(Path.Combine(_root, ".aidevhelpers", "backups"));
        Assert.Single(backups);
        Assert.Equal(original, File.ReadAllBytes(backups[0]));
        Assert.Equal(backups[0], result.BackupPath);
    }

    [Fact]
    public void ApplyKeepsBigEndianUtf16()
    {
        var file = Path.Combine(_root, "c.txt");
        File.WriteAllBytes(file, new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes("hi there")).ToArray());

        FileEditEngine.Replace(_root, "c.txt", new[] { new FileEditEngine.Edit("there", "you", 0) }, confirm: true);

        var after = File.ReadAllBytes(file);
        Assert.Equal(new byte[] { 0xFE, 0xFF }, after.Take(2).ToArray());
        Assert.Equal("hi you", Encoding.BigEndianUnicode.GetString(after, 2, after.Length - 2));
    }

    [Fact]
    public void RefusesAFileOutsideTheRoot()
    {
        Assert.Throws<InvalidOperationException>(() => FileEditEngine.Replace(
            _root, Path.Combine("..", "escape.txt"), new[] { new FileEditEngine.Edit("a", "b", 0) }, confirm: true));
    }

    [Fact]
    public void RefusesAMissingFile()
    {
        Assert.Throws<InvalidOperationException>(() => FileEditEngine.Replace(
            _root, "missing.txt", new[] { new FileEditEngine.Edit("a", "b", 0) }, confirm: true));
    }
}
