using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Code actions for the compiler's missing-name errors (<c>ide_list_code_actions</c>, <c>ide_apply_code_action</c>, unit 6.1-old and 6.2).
/// The IDE's own fix providers are internal to Visual Studio (unit 0.0), so this engine offers only the fix it can compute exactly:
/// adding a using directive for a type that exists in a namespace the file does not import (CS0246, CS0103). Each candidate has an id,
/// and the apply plan refuses any candidate that no longer matches. Pure: the caller applies <see cref="MethodEditPlan.Result"/>.
/// </summary>
internal static class CodeActionEngine
{
    private static readonly string[] MissingNameCodes = { "CS0246", "CS0103" };

    /// <summary>The candidate fixes for the missing names on a line: each has an id, a title and the namespace it would import.</summary>
    public static async Task<List<object>> ListAsync(Solution solution, string file, int line)
    {
        var found = new List<object>();
        foreach (var candidate in await CandidatesAsync(solution, file, line).ConfigureAwait(false))
        {
            found.Add(new { id = candidate.Id, title = candidate.Title, @namespace = candidate.Namespace, name = candidate.Name });
        }

        return found;
    }

    public static async Task<MethodEditPlan> ApplyAsync(Solution solution, string file, int line, string id)
    {
        var plan = new MethodEditPlan();
        var candidates = await CandidatesAsync(solution, file, line).ConfigureAwait(false);
        var chosen = candidates.FirstOrDefault(c => c.Id == id);
        if (chosen is null)
        {
            plan.Reasons.Add(Strings.Get("CodeActionStale"));
            return plan;
        }

        var documentId = solution.GetDocumentIdsWithFilePath(file).First();
        var root = await solution.GetDocument(documentId)!.GetSyntaxRootAsync().ConfigureAwait(false);
        var text = await solution.GetDocument(documentId)!.GetTextAsync().ConfigureAwait(false);
        var eol = text.ToString().Contains("\r\n") ? "\r\n" : "\n";
        var compilation = root!.DescendantNodes().OfType<UsingDirectiveSyntax>().ToList();
        var insertAt = compilation.Count > 0 ? compilation[compilation.Count - 1].Span.End : 0;
        var prefix = compilation.Count > 0 ? eol : "";
        var change = new TextChange(new TextSpan(insertAt, 0), prefix + "using " + chosen.Namespace + ";" + (compilation.Count == 0 ? eol : ""));
        var changes = new Dictionary<DocumentId, List<TextChange>> { [documentId] = new List<TextChange> { change } };
        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        edited = await EditChecks.FormatFileAsync(edited, documentId, plan).ConfigureAwait(false);
        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, new[] { documentId }, plan).ConfigureAwait(false);
        plan.Files.Add(solution.GetDocument(documentId)!.FilePath ?? "");
        plan.Warnings.Add(Strings.Format("CodeActionAdded", chosen.Namespace, chosen.Name));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    private static async Task<List<Candidate>> CandidatesAsync(Solution solution, string file, int line)
    {
        var result = new List<Candidate>();
        var documentId = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault();
        var document = documentId is null ? null : solution.GetDocument(documentId);
        if (document is null)
        {
            return result;
        }

        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var text = await document.GetTextAsync().ConfigureAwait(false);
        if (model is null || root is null || line < 1 || line > text.Lines.Count)
        {
            return result;
        }

        var span = text.Lines[line - 1].Span;
        var missing = model.GetDiagnostics(span).Where(d => MissingNameCodes.Contains(d.Id)).ToList();
        var names = missing.Select(d => text.GetSubText(d.Location.SourceSpan).ToString()).Where(n => n.Length > 0 && SyntaxFacts.IsValidIdentifier(n)).Distinct();
        var compilation = model.Compilation;
        var usings = new HashSet<string>(root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => u.Name!.ToString()), System.StringComparer.Ordinal);
        foreach (var name in names)
        {
            foreach (var type in compilation.GetSymbolsWithName(name, SymbolFilter.Type).OfType<INamedTypeSymbol>())
            {
                if (type.ContainingNamespace is not { IsGlobalNamespace: false } ns)
                {
                    continue;
                }

                var nsName = ns.ToDisplayString();
                if (usings.Contains(nsName) || (!type.Locations.Any(l => l.IsInSource) && type.DeclaredAccessibility != Accessibility.Public))
                {
                    continue;
                }

                result.Add(new Candidate("using:" + nsName + ":" + name, Strings.Format("CodeActionTitle", nsName, name), nsName, name));
            }
        }

        return result.GroupBy(c => c.Id, System.StringComparer.Ordinal).Select(g => g.First()).ToList();
    }

    private sealed class Candidate
    {
        public Candidate(string id, string title, string @namespace, string name)
        {
            Id = id;
            Title = title;
            Namespace = @namespace;
            Name = name;
        }

        public string Id { get; }

        public string Title { get; }

        public string Namespace { get; }

        public string Name { get; }
    }
}
