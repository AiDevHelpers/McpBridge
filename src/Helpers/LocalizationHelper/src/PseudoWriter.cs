using System.Security.Cryptography;

namespace AiDevHelpers.Helpers.LocalizationHelper;

/// <summary>One key of the pseudo file: the neutral text and what it becomes.</summary>
public sealed record PseudoChange(string Key, string Source, string Pseudo);

public sealed record PseudoPlan(string Root, string NeutralPath, string OutputPath, IReadOnlyList<PseudoChange> Changes, IReadOnlyDictionary<string, string> Hashes)
{
    public string Token { get; init; } = string.Empty;

    public DateTime ExpiresUtc { get; init; }
}

/// <summary>Plans and writes the pseudo-localized resx (Strings.qps-ploc.resx) from a neutral one. Nothing is written until <see cref="Apply"/>.</summary>
public sealed class PseudoWriter(PlanStore<PseudoPlan> store)
{
    public const string Comment = "pseudo-localized";

    /// <param name="neutralResx">The neutral Strings.resx, absolute or relative to <paramref name="root"/>.</param>
    /// <param name="output">Where to write; default is Strings.qps-ploc.resx beside the neutral file. Must end in .resx and be inside <paramref name="root"/>.</param>
    public PseudoPlan Plan(string root, string neutralResx, string? output)
    {
        var fullRoot = Path.GetFullPath(root);
        var neutral = Path.GetFullPath(Path.IsPathRooted(neutralResx) ? neutralResx : Path.Combine(fullRoot, neutralResx));
        if (!neutral.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(Strings.Format("NotAResx", neutralResx), nameof(neutralResx));
        }

        if (!File.Exists(neutral))
        {
            throw new FileNotFoundException(Strings.Format("FolderNotFound", neutral));
        }

        var target = output is null
            ? Path.Combine(Path.GetDirectoryName(neutral)!, $"Strings.{ResourceSet.PseudoCulture}.resx")
            : Path.GetFullPath(Path.IsPathRooted(output) ? output : Path.Combine(fullRoot, output));
        if (!target.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(Strings.Format("NotAResx", output ?? target), nameof(output));
        }

        if (!target.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(Strings.Format("OutputOutsideRoot", target), nameof(output));
        }

        var changes = ResxFile.Load(neutral).Entries.Select(e => new PseudoChange(e.Name, e.Value, PseudoLocalizer.Transform(e.Value))).ToList();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [neutral] = Hash(neutral), [target] = Hash(target) };
        var plan = new PseudoPlan(fullRoot, neutral, target, changes, hashes);
        var token = store.Add(plan);
        return plan with { Token = token, ExpiresUtc = store.ExpiresUtc(token) };
    }

    /// <summary>Writes the planned file once. Returns its path relative to the root.</summary>
    public string Apply(string token)
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

        var pseudo = ResxFile.CreateFrom(ResxFile.Load(plan.NeutralPath), plan.OutputPath);
        foreach (var change in plan.Changes)
        {
            pseudo.Add(change.Key, change.Pseudo, Comment);
        }

        pseudo.Save();
        return Path.GetRelativePath(plan.Root, plan.OutputPath);
    }

    private static string Hash(string path) =>
        File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : string.Empty;
}
