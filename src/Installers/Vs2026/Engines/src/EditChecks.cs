using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>Checks run after a method edit, add or overload: format the file, then report the diagnostics the edit introduced.</summary>
internal static class EditChecks
{
    private const int MaxReported = 50;

    private sealed class Diag
    {
        public string Key { get; set; } = "";

        public string Id { get; set; } = "";

        public string Message { get; set; } = "";

        public int Line { get; set; }

        public DiagnosticSeverity Severity { get; set; }
    }

    /// <summary>Formats the whole document with the project's formatting options and editorconfig. Sets <c>plan.Formatted</c> when the text changed.</summary>
    public static async Task<Solution> FormatFileAsync(Solution solution, DocumentId id, MethodEditPlan plan)
    {
        var document = solution.GetDocument(id)!;
        var formatted = await Formatter.FormatAsync(document).ConfigureAwait(false);
        var before = await document.GetTextAsync().ConfigureAwait(false);
        var after = await formatted.GetTextAsync().ConfigureAwait(false);

        // The formatter writes the machine's line ending for every break it makes. Keep the file's own.
        var eol = before.ToString().Contains("\r\n") ? "\r\n" : "\n";
        var normalized = after.ToString().Replace("\r\n", "\n");
        if (eol == "\r\n")
        {
            normalized = normalized.Replace("\n", "\r\n");
        }

        var finalText = SourceText.From(normalized, before.Encoding);
        if (!before.ContentEquals(finalText))
        {
            plan.Formatted = true;
        }

        return solution.WithDocumentText(id, finalText);
    }

    /// <summary>
    /// Diagnostics (compiler and analyzers) of the given documents after the edit that were not there before. New errors refuse the
    /// edit; new warnings and notices are reported and never refuse.
    /// </summary>
    public static async Task CheckNewDiagnosticsAsync(Solution before, Solution after, IEnumerable<DocumentId> ids, MethodEditPlan plan)
    {
        foreach (var id in ids.Distinct())
        {
            var oldDocument = before.GetDocument(id);
            var newDocument = after.GetDocument(id);
            if (oldDocument is null || newDocument is null)
            {
                continue;
            }

            var remaining = (await DiagnosticsAsync(oldDocument, plan).ConfigureAwait(false)).GroupBy(d => d.Key).ToDictionary(g => g.Key, g => g.Count());
            foreach (var diagnostic in await DiagnosticsAsync(newDocument, plan).ConfigureAwait(false))
            {
                if (remaining.TryGetValue(diagnostic.Key, out var count) && count > 0)
                {
                    remaining[diagnostic.Key] = count - 1;
                    continue;
                }

                var entry = new { file = newDocument.FilePath, line = diagnostic.Line, id = diagnostic.Id, message = diagnostic.Message };
                var target = diagnostic.Severity switch
                {
                    DiagnosticSeverity.Error => plan.NewErrors,
                    DiagnosticSeverity.Warning => plan.NewWarnings,
                    _ => plan.NewNotices,
                };
                if (target.Count < MaxReported)
                {
                    target.Add(entry);
                }
            }
        }

        if (plan.NewErrors.Count > 0)
        {
            plan.Reasons.Add(Strings.Format("MethodEditNewErrors", plan.NewErrors.Count));
        }
    }

    private static async Task<List<Diag>> DiagnosticsAsync(Document document, MethodEditPlan plan)
    {
        var list = new List<Diag>();
        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        var tree = await document.GetSyntaxTreeAsync().ConfigureAwait(false);
        if (model is null || tree is null)
        {
            return list;
        }

        var all = new List<Diagnostic>(model.GetDiagnostics());
        var project = document.Project;
        var analyzers = project.AnalyzerReferences.SelectMany(r => r.GetAnalyzers(project.Language)).ToImmutableArray();
        if (analyzers.Length > 0)
        {
            try
            {
                var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
                var withAnalyzers = compilation!.WithAnalyzers(analyzers, project.AnalyzerOptions);
                all.AddRange(await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(tree, default).ConfigureAwait(false));
                all.AddRange(await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, null, default).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                var note = Strings.Format("MethodEditAnalysisUnavailable", ex.Message);
                if (!plan.Warnings.Contains(note))
                {
                    plan.Warnings.Add(note);
                }
            }
        }

        foreach (var diagnostic in all.Where(d => d.Severity != DiagnosticSeverity.Hidden))
        {
            var message = diagnostic.GetMessage();
            list.Add(new Diag
            {
                Key = diagnostic.Id + "|" + message,
                Id = diagnostic.Id,
                Message = message,
                Line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1,
                Severity = diagnostic.Severity,
            });
        }

        return list;
    }
}
