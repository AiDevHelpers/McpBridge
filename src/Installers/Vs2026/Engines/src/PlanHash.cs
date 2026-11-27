using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// A fingerprint of what a plan would change: each changed document's path and its full new text. The user approves a plan by its
/// text, so confirm re-plans and applies only when the fingerprint is the one that was shown.
/// </summary>
internal static class PlanHash
{
    public static async Task<string?> ComputeAsync(Solution? before, Solution? after)
    {
        if (after is null || before is null)
        {
            return null;
        }

        var lines = new List<string>();
        foreach (var changed in after.GetChanges(before).GetProjectChanges().SelectMany(p => p.GetChangedDocuments()).OrderBy(d => d.Id.ToString(), StringComparer.Ordinal))
        {
            var document = after.GetDocument(changed);
            var text = document is null ? "<removed>" : (await document.GetTextAsync().ConfigureAwait(false)).ToString();
            lines.Add((document?.FilePath ?? changed.ToString()) + "\n" + text);
        }

        return lines.Count == 0 ? null : Hash(string.Join("\u0001", lines));
    }

    /// <summary>True when the fingerprint of a re-plan matches the one the user was shown.</summary>
    public static bool Matches(string? shown, string? replanned) => shown is not null && string.Equals(shown, replanned, StringComparison.Ordinal);

    internal static string Hash(string text)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }
}
