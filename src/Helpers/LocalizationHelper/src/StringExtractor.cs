using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AiDevHelpers.Helpers.LocalizationHelper;

/// <summary>
/// A literal the extractor will replace. <see cref="Literal"/> is the text as written in source; <see cref="Value"/> is the resx value
/// (escapes resolved, interpolation holes numbered). <see cref="Column"/> is where the source token starts on its line.
/// </summary>
public sealed record ExtractItem(string File, int Line, int Column, string Literal, string Key, string Replacement, string Value, string FilePath, string ResxPath, int Length);

/// <summary>A literal left alone. <see cref="Reason"/> is a stable code, for example "const-context".</summary>
public sealed record ExtractSkip(string File, int Line, string Literal, string Reason);

public sealed record ExtractPlan(string Root, IReadOnlyList<ExtractItem> Items, IReadOnlyList<ExtractSkip> Skipped, IReadOnlyDictionary<string, string> Hashes)
{
    public string Token { get; init; } = string.Empty;

    public DateTime ExpiresUtc { get; init; }
}

/// <summary>Moves hardcoded literals into the project's Strings.resx and rewrites them as Strings.Get or Strings.Format. Nothing is written until <see cref="Apply"/>.</summary>
public sealed partial class StringExtractor(PlanStore<ExtractPlan> store)
{
    private const int MaxResxSearchDepth = 8;
    private const int MaxKeyLength = 40;
    private const int MaxKeyWords = 5;

    /// <param name="keys">Caller-chosen keys by literal text; they win over the proposed ones.</param>
    public ExtractPlan Plan(string path, IReadOnlyDictionary<string, string>? keys)
    {
        var baseDir = File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path))! : Path.GetFullPath(path);
        var findings = StringScanner.Scan(path);
        var items = new List<ExtractItem>();
        var skipped = new List<ExtractSkip>();
        var resxState = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var lineCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var finding in findings)
        {
            var full = Path.Combine(baseDir, finding.File);
            if (!lineCache.TryGetValue(full, out var lines))
            {
                lines = File.ReadAllLines(full);
                lineCache[full] = lines;
            }

            var line = lines[finding.Line - 1];
            var skip = (Func<string, ExtractSkip>)(reason => new ExtractSkip(finding.File, finding.Line, finding.Literal, reason));

            var resxPath = NearestResx(full);
            if (resxPath is null || !File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(resxPath))!, "Strings.cs")))
            {
                skipped.Add(skip("no-strings-class"));
                continue;
            }

            var stringsNs = NamespaceOf(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(resxPath))!, "Strings.cs")));
            var fileNs = NamespaceOf(string.Join('\n', lines));
            if (stringsNs is null || fileNs is null || !(fileNs == stringsNs || fileNs.StartsWith(stringsNs + ".", StringComparison.Ordinal)))
            {
                skipped.Add(skip("namespace-mismatch"));
                continue;
            }

            var token = SourceToken().Match(line, finding.Column);
            if (!token.Success || (finding.Column > 0 && line[finding.Column - 1] == '@') || line[finding.Column..].StartsWith("\"\"\"", StringComparison.Ordinal)
                || line[finding.Column..].StartsWith("$\"\"\"", StringComparison.Ordinal))
            {
                skipped.Add(skip("verbatim-or-raw"));
                continue;
            }

            if (line.Contains("const ", StringComparison.Ordinal))
            {
                skipped.Add(skip("const-context"));
                continue;
            }

            if (line.TrimStart().StartsWith("case ", StringComparison.Ordinal) || line[..finding.Column].Contains("case ", StringComparison.Ordinal))
            {
                skipped.Add(skip("case-label"));
                continue;
            }

            if (UnsupportedEscape().IsMatch(finding.Literal))
            {
                skipped.Add(skip("unsupported-escape"));
                continue;
            }

            var holes = new List<string>();
            string value;
            if (finding.Interpolated)
            {
                var bad = false;
                value = Hole().Replace(Unescape(finding.Literal), m =>
                {
                    var expr = m.Groups["e"].Value.Trim();
                    if (expr.Length == 0 || expr.Contains(':') || expr.Contains(',') || expr.Contains('"'))
                    {
                        bad = true;
                        return m.Value;
                    }

                    holes.Add(expr);
                    return "{" + (holes.Count - 1) + "}";
                });
                if (bad)
                {
                    skipped.Add(skip("interpolation-format"));
                    continue;
                }
            }
            else
            {
                value = Unescape(finding.Literal);
            }

            if (!resxState.TryGetValue(resxPath, out var state))
            {
                state = ResxFile.Load(resxPath).Entries.GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
                resxState[resxPath] = state;
            }

            var key = ChooseKey(finding, value, keys, state);
            state[key] = value;
            var replacement = holes.Count == 0
                ? $"Strings.Get(\"{key}\")"
                : $"Strings.Format(\"{key}\", {string.Join(", ", holes)})";
            items.Add(new ExtractItem(finding.File, finding.Line, finding.Column, finding.Literal, key, replacement, value, full, resxPath, token.Length));
        }

        var hashes = items.Select(i => i.FilePath).Concat(items.Select(i => i.ResxPath)).Distinct()
            .ToDictionary(p => p, p => Hash(p), StringComparer.OrdinalIgnoreCase);
        var plan = new ExtractPlan(baseDir, items, skipped, hashes);
        var tokenId = store.Add(plan);
        return plan with { Token = tokenId, ExpiresUtc = store.ExpiresUtc(tokenId) };
    }

    /// <summary>Applies a plan once. Returns the files written, relative to the planned folder.</summary>
    public IReadOnlyList<string> Apply(string token)
    {
        if (!store.TryTake(token, out var plan))
        {
            throw new InvalidOperationException(Strings.Get("TokenInvalid"));
        }

        foreach (var (file, hash) in plan.Hashes)
        {
            if (Hash(file) != hash)
            {
                throw new InvalidOperationException(Strings.Format("FileChangedSincePlan", Path.GetRelativePath(plan.Root, file)));
            }
        }

        var written = new List<string>();
        foreach (var group in plan.Items.GroupBy(i => i.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var bytes = File.ReadAllBytes(group.Key);
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            var starts = LineStarts(text);
            foreach (var item in group.OrderByDescending(i => i.Line).ThenByDescending(i => i.Column))
            {
                var offset = starts[item.Line - 1] + item.Column;
                text = text.Remove(offset, item.Length).Insert(offset, item.Replacement);
            }

            var body = new UTF8Encoding(false).GetBytes(text);
            File.WriteAllBytes(group.Key, bom ? [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. body] : body);
            written.Add(Path.GetRelativePath(plan.Root, group.Key));
        }

        foreach (var resxGroup in plan.Items.GroupBy(i => i.ResxPath, StringComparer.OrdinalIgnoreCase))
        {
            var resx = ResxFile.Load(resxGroup.Key);
            var present = resx.Entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var item in resxGroup.DistinctBy(i => i.Key))
            {
                if (present.Add(item.Key))
                {
                    resx.Add(item.Key, item.Value, null);
                }
            }

            resx.Save();
            written.Add(Path.GetRelativePath(plan.Root, resxGroup.Key));
        }

        return written;
    }

    private static string ChooseKey(StringFinding finding, string value, IReadOnlyDictionary<string, string>? keys, Dictionary<string, string> state)
    {
        if (keys is not null && keys.TryGetValue(finding.Literal, out var chosen))
        {
            if (state.TryGetValue(chosen, out var existingValue) && existingValue != value)
            {
                throw new ArgumentException(Strings.Format("KeyExists", chosen), nameof(keys));
            }

            return chosen;
        }

        if (!finding.Interpolated && finding.ResxKey is not null)
        {
            return finding.ResxKey;
        }

        var existing = state.FirstOrDefault(kv => kv.Value == value).Key;
        if (existing is not null)
        {
            return existing;
        }

        var proposed = Propose(value);
        var key = proposed;
        for (var n = 2; state.ContainsKey(key); n++)
        {
            key = proposed + n;
        }

        return key;
    }

    private static string Propose(string value)
    {
        var words = Regex.Matches(Hole().Replace(value, " "), "[A-Za-z0-9]+").Select(m => m.Value).Take(MaxKeyWords);
        var key = string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
        if (key.Length > MaxKeyLength)
        {
            key = key[..MaxKeyLength];
        }

        if (key.Length == 0)
        {
            return "Text";
        }

        return char.IsDigit(key[0]) ? "T" + key : key;
    }

    private static string Unescape(string raw) =>
        raw.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");

    private static string? NearestResx(string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        for (var depth = 0; dir is not null && depth < MaxResxSearchDepth; depth++, dir = Path.GetDirectoryName(dir))
        {
            var resx = Path.Combine(dir, "Resources", "Strings.resx");
            if (File.Exists(resx))
            {
                return resx;
            }
        }

        return null;
    }

    private static string? NamespaceOf(string source)
    {
        var m = NamespaceDecl().Match(source);
        return m.Success ? m.Groups["ns"].Value : null;
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [GeneratedRegex(@"\G\$?""(?:[^""\\]|\\.)*""")]
    private static partial Regex SourceToken();

    [GeneratedRegex(@"\\[^""\\nt]")]
    private static partial Regex UnsupportedEscape();

    [GeneratedRegex(@"(?<!\{)\{(?<e>[^{}]*)\}(?!\})")]
    private static partial Regex Hole();

    [GeneratedRegex(@"^\s*namespace\s+(?<ns>[\w.]+)", RegexOptions.Multiline)]
    private static partial Regex NamespaceDecl();
}
