using System.Text;
using Xunit;

namespace AiDevHelpers.Helpers.FileEditHelper.Tests;

public sealed class FileEditEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fe-eng-" + Guid.NewGuid().ToString("N"));
    private readonly FileEditEngine _engine = new();

    public FileEditEngineTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private string Write(string name, string content, Encoding? enc = null)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [.. (enc ?? new UTF8Encoding(false)).GetPreamble(), .. (enc ?? new UTF8Encoding(false)).GetBytes(content)]);
        return path;
    }

    private static string Read(string path) => File.ReadAllText(path);

    // ── apply ──

    [Fact]
    public async Task Apply_replaces_all_by_default()
    {
        var p = Write("a.txt", "x y x");
        var r = await _engine.ApplyAsync(new ReplaceOperation("a.txt", "x", "z"), _root);
        Assert.True(r.Ok);
        Assert.Equal("z y z", Read(p));
        Assert.NotNull(r.BackupPath);
        Assert.True(File.Exists(r.BackupPath));
    }

    [Fact]
    public async Task Apply_nth_occurrence_only()
    {
        var p = Write("a.txt", "x y x y x");
        var r = await _engine.ApplyAsync(new ReplaceOperation("a.txt", "x", "z", Occurrence: 2), _root);
        Assert.True(r.Ok);
        Assert.Equal("x y z y x", Read(p));
    }

    [Fact]
    public async Task Apply_nth_occurrence_missing_errors_without_writing()
    {
        var p = Write("a.txt", "x");
        var r = await _engine.ApplyAsync(new ReplaceOperation("a.txt", "x", "z", Occurrence: 3), _root);
        Assert.False(r.Ok);
        Assert.Contains("Occurrence 3", r.Error);
        Assert.Equal("x", Read(p));
    }

    [Fact]
    public async Task Apply_anchor_not_found_errors()
    {
        Write("a.txt", "abc");
        var r = await _engine.ApplyAsync(new ReplaceOperation("a.txt", "nope", "z"), _root);
        Assert.False(r.Ok);
        Assert.Contains("Anchor not found", r.Error);
    }

    [Fact]
    public async Task Apply_empty_old_text_errors()
    {
        Write("a.txt", "abc");
        var r = await _engine.ApplyAsync(new ReplaceOperation("a.txt", "", "z"), _root);
        Assert.False(r.Ok);
        Assert.Contains("OldText must not be empty", r.Error);
    }

    [Fact]
    public async Task Apply_missing_file_errors()
    {
        var r = await _engine.ApplyAsync(new ReplaceOperation("nope.txt", "a", "b"), _root);
        Assert.False(r.Ok);
        Assert.Contains("File not found", r.Error);
    }

    [Fact]
    public async Task Apply_outside_root_is_refused()
    {
        var r = await _engine.ApplyAsync(new ReplaceOperation("../x.txt", "a", "b"), _root);
        Assert.False(r.Ok);
        Assert.Contains("outside the workspace root", r.Error);
    }

    // ── dry run, backups ──

    [Fact]
    public async Task DryRun_writes_nothing_and_makes_no_backup()
    {
        var p = Write("a.txt", "x");
        var r = await _engine.ApplyAsync(new ReplaceOperation("a.txt", "x", "z", DryRun: true), _root);
        Assert.True(r.Ok);
        Assert.True(r.DryRun);
        Assert.Null(r.BackupPath);
        Assert.Equal("x", Read(p));
        Assert.False(Directory.Exists(Path.Combine(_root, FileEditEngine.BackupFolder)));
    }

    [Fact]
    public async Task Two_edits_in_a_row_keep_distinct_byte_exact_backups()
    {
        Write("a.txt", "one");
        var r1 = await _engine.ApplyAsync(new ReplaceOperation("a.txt", "one", "two"), _root);
        var r2 = await _engine.ApplyAsync(new ReplaceOperation("a.txt", "two", "three"), _root);
        Assert.NotEqual(r1.BackupPath, r2.BackupPath);
        Assert.Equal("one", Read(r1.BackupPath!));
        Assert.Equal("two", Read(r2.BackupPath!));
    }

    [Fact]
    public async Task Backups_live_under_the_backup_folder()
    {
        Write("a.txt", "x");
        var r = await _engine.ApplyAsync(new ReplaceOperation("a.txt", "x", "z"), _root);
        Assert.StartsWith(Path.GetFullPath(Path.Combine(_root, FileEditEngine.BackupFolder)), Path.GetFullPath(r.BackupPath!));
    }

    // ── encoding, atomic write ──

    [Fact]
    public async Task Utf8_bom_is_preserved()
    {
        var p = Write("a.txt", "héllo x", new UTF8Encoding(true));
        await _engine.ApplyAsync(new ReplaceOperation("a.txt", "x", "z"), _root);
        var bytes = File.ReadAllBytes(p);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("héllo z", new UTF8Encoding(false).GetString(bytes[3..]));
    }

    [Fact]
    public async Task No_bom_stays_without_bom()
    {
        var p = Write("a.txt", "x");
        await _engine.ApplyAsync(new ReplaceOperation("a.txt", "x", "z"), _root);
        Assert.Equal([(byte)'z'], File.ReadAllBytes(p));
    }

    [Fact]
    public async Task Utf16_le_and_be_are_preserved()
    {
        var le = Write("le.txt", "ab x", new UnicodeEncoding(false, true));
        var be = Write("be.txt", "ab x", new UnicodeEncoding(true, true));
        await _engine.ApplyAsync(new ReplaceOperation("le.txt", "x", "z"), _root);
        await _engine.ApplyAsync(new ReplaceOperation("be.txt", "x", "z"), _root);
        Assert.Equal([0xFF, 0xFE], File.ReadAllBytes(le)[..2]);
        Assert.Equal([0xFE, 0xFF], File.ReadAllBytes(be)[..2]);
        Assert.Equal("ab z", Read(le));
        Assert.Equal("ab z", Read(be));
    }

    [Fact]
    public async Task No_temp_file_is_left_behind()
    {
        Write("a.txt", "x");
        await _engine.ApplyAsync(new ReplaceOperation("a.txt", "x", "z"), _root);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    // ── insert ──

    [Theory]
    [InlineData(true, "ab-X|cd")]
    [InlineData(false, "abX-|cd")]
    public async Task Insert_before_or_after_anchor(bool after, string _)
    {
        var p = Write("a.txt", "ab|cd");
        var r = await _engine.ApplyAsync(new InsertOperation("a.txt", "|", "X", After: after), _root);
        Assert.True(r.Ok);
        Assert.Equal(after ? "ab|Xcd" : "abX|cd", Read(p));
    }

    [Fact]
    public async Task Insert_anchor_missing_errors_when_required()
    {
        Write("a.txt", "abc");
        var r = await _engine.ApplyAsync(new InsertOperation("a.txt", "zzz", "X"), _root);
        Assert.False(r.Ok);
        Assert.Contains("Anchor not found", r.Error);
    }

    [Fact]
    public async Task Insert_anchor_missing_appends_when_not_required()
    {
        var p = Write("a.txt", "abc");
        var r = await new FileEditEngine(requireAnchorMatch: false).ApplyAsync(new InsertOperation("a.txt", "zzz", "X"), _root);
        Assert.True(r.Ok);
        Assert.Equal("abcX", Read(p));
    }

    [Fact]
    public async Task Insert_empty_anchor_errors()
    {
        Write("a.txt", "abc");
        var r = await _engine.ApplyAsync(new InsertOperation("a.txt", "", "X"), _root);
        Assert.False(r.Ok);
        Assert.Contains("Anchor must not be empty", r.Error);
    }

    // ── delete ──

    [Fact]
    public async Task Delete_literal()
    {
        var p = Write("a.txt", "a-b-c");
        var r = await _engine.ApplyAsync(new DeleteOperation("a.txt", "-"), _root);
        Assert.True(r.Ok);
        Assert.Equal("abc", Read(p));
    }

    [Fact]
    public async Task Delete_literal_not_found_errors()
    {
        Write("a.txt", "abc");
        var r = await _engine.ApplyAsync(new DeleteOperation("a.txt", "zzz"), _root);
        Assert.False(r.Ok);
        Assert.Contains("Pattern not found", r.Error);
    }

    [Fact]
    public async Task Delete_regex_is_multiline()
    {
        var p = Write("a.txt", "keep\n// drop\nkeep");
        var r = await _engine.ApplyAsync(new DeleteOperation("a.txt", @"^// .*$\n", IsRegex: true), _root);
        Assert.True(r.Ok);
        Assert.Equal("keep\nkeep", Read(p));
    }

    [Fact]
    public async Task Delete_invalid_regex_errors()
    {
        Write("a.txt", "abc");
        var r = await _engine.ApplyAsync(new DeleteOperation("a.txt", "(", IsRegex: true), _root);
        Assert.False(r.Ok);
        Assert.Contains("Invalid regex", r.Error);
    }

    // ── rename ──

    [Fact]
    public async Task Rename_whole_word_only()
    {
        var p = Write("a.cs", "var foo = foobar + foo;");
        var r = await _engine.ApplyAsync(new RenameOperation("a.cs", "foo", "bar"), _root);
        Assert.True(r.Ok);
        Assert.Equal("var bar = foobar + bar;", Read(p));
        Assert.Equal(2, r.LinesChanged);
    }

    [Fact]
    public async Task Rename_substring_when_not_whole_word()
    {
        var p = Write("a.cs", "foo foobar");
        await _engine.ApplyAsync(new RenameOperation("a.cs", "foo", "bar", WholeWordOnly: false), _root);
        Assert.Equal("bar barbar", Read(p));
    }

    [Fact]
    public async Task Rename_skips_comment_lines_by_default()
    {
        var p = Write("a.cs", "foo\n// foo\n  /* foo\n * foo\nfoo");
        await _engine.ApplyAsync(new RenameOperation("a.cs", "foo", "bar"), _root);
        Assert.Equal("bar\n// foo\n  /* foo\n * foo\nbar", Read(p));
    }

    [Fact]
    public async Task Rename_in_comments_when_asked()
    {
        var p = Write("a.cs", "foo\n// foo");
        await _engine.ApplyAsync(new RenameOperation("a.cs", "foo", "bar", ReplaceInComments: true), _root);
        Assert.Equal("bar\n// bar", Read(p));
    }

    [Fact]
    public async Task Rename_preserves_crlf()
    {
        var p = Write("a.cs", "foo\r\nfoo\r\n");
        await _engine.ApplyAsync(new RenameOperation("a.cs", "foo", "bar"), _root);
        Assert.Equal("bar\r\nbar\r\n", Read(p));
    }

    [Fact]
    public async Task Rename_no_match_errors()
    {
        Write("a.cs", "abc");
        var r = await _engine.ApplyAsync(new RenameOperation("a.cs", "foo", "bar"), _root);
        Assert.False(r.Ok);
        Assert.Contains("not found", r.Error);
    }

    // ── rollback ──

    [Fact]
    public async Task Rollback_restores_the_original()
    {
        var p = Write("a.txt", "orig");
        var edit = await _engine.ApplyAsync(new ReplaceOperation("a.txt", "orig", "changed"), _root);
        var r = await _engine.RollbackAsync(edit.BackupPath!, "a.txt", _root);
        Assert.True(r.Ok);
        Assert.Equal("orig", Read(p));
    }

    [Fact]
    public async Task Rollback_refuses_a_source_that_is_not_a_backup()
    {
        Write("a.txt", "orig");
        var other = Write("other.txt", "secret");
        var r = await _engine.RollbackAsync(other, "a.txt", _root);
        Assert.False(r.Ok);
        Assert.Equal("orig", Read(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task Rollback_refuses_a_target_outside_the_workspace()
    {
        Write("a.txt", "orig");
        var edit = await _engine.ApplyAsync(new ReplaceOperation("a.txt", "orig", "changed"), _root);
        var outside = Path.Combine(Path.GetTempPath(), "fe-out-" + Guid.NewGuid().ToString("N") + ".txt");
        var r = await _engine.RollbackAsync(edit.BackupPath!, outside, _root);
        Assert.False(r.Ok);
        Assert.False(File.Exists(outside));
    }

    // ── batch ──

    [Fact]
    public async Task Batch_stops_at_the_first_failure()
    {
        var p = Write("a.txt", "a b");
        var results = await _engine.ApplyBatchAsync(
            [new ReplaceOperation("a.txt", "a", "1"), new ReplaceOperation("a.txt", "zzz", "2"), new ReplaceOperation("a.txt", "b", "3")],
            _root);
        Assert.Equal(2, results.Count);
        Assert.True(results[0].Ok);
        Assert.False(results[1].Ok);
        Assert.Equal("1 b", Read(p));
    }
}
