using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Extracts an interface from a class or struct (<c>ide_extract_interface</c>, unit 6.8). The named public instance methods and properties
/// become the interface's members, written as signatures in <c>I{Name}.cs</c> beside the type's file, and the type adds the interface to
/// its base list. Refused for a member that is not public and non-static, a name already taken, or a type that already has the interface.
/// Pure: the caller applies <see cref="MethodEditPlan.Result"/> and <see cref="MethodEditPlan.Files"/>.
/// </summary>
internal static class ExtractInterfaceEngine
{
    public static async Task<MethodEditPlan> PlanAsync(Solution solution, string file, int line, string interfaceName, IReadOnlyList<string> memberNames)
    {
        var plan = new MethodEditPlan();
        var documentId = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault();
        var document = documentId is null ? null : solution.GetDocument(documentId);
        if (document is null)
        {
            plan.Reasons.Add(Strings.Format("NoRoslynDocumentFor", file));
            return plan;
        }

        var text = await document.GetTextAsync().ConfigureAwait(false);
        if (line < 1 || line > text.Lines.Count || !SyntaxFacts.IsValidIdentifier(interfaceName) || memberNames.Count == 0)
        {
            plan.Reasons.Add(Strings.Get("ExtractInterfaceBadInput"));
            return plan;
        }

        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        var lineText = text.Lines[line - 1].ToString();
        var position = text.Lines[line - 1].Start + (lineText.Length - lineText.TrimStart().Length);
        var type = root!.FindToken(Math.Min(position, root.FullSpan.End)).Parent?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (type is null || type is InterfaceDeclarationSyntax || model!.GetDeclaredSymbol(type) is not INamedTypeSymbol symbol)
        {
            plan.Reasons.Add(Strings.Get("ImplementNotAType"));
            return plan;
        }

        var path = Path.Combine(Path.GetDirectoryName(document.FilePath ?? "") ?? "", "I" + interfaceName + ".cs");
        if (solution.GetDocumentIdsWithFilePath(path).Length > 0 || symbol.AllInterfaces.Any(i => i.Name == interfaceName))
        {
            plan.Reasons.Add(Strings.Format("ExtractInterfaceExists", interfaceName));
            return plan;
        }

        var signatures = new List<string>();
        foreach (var name in memberNames)
        {
            var member = type.Members.FirstOrDefault(m => MemberAddEngine.NamesOf(m).Contains(name, StringComparer.Ordinal));
            if (member is null || !member.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)) || member.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            {
                plan.Reasons.Add(Strings.Format("ExtractInterfaceBadMember", name));
                return plan;
            }

            switch (member)
            {
                case MethodDeclarationSyntax method:
                    signatures.Add(method.ReturnType.WithoutTrivia() + " " + method.Identifier.ValueText + (method.TypeParameterList?.ToString() ?? "")
                        + method.ParameterList.WithoutTrivia() + string.Concat(method.ConstraintClauses.Select(c => " " + c.ToString())) + ";");
                    break;
                case PropertyDeclarationSyntax property:
                    var accessors = property.AccessorList is null ? "{ get; }"
                        : "{ get;" + (property.AccessorList.Accessors.Any(a => a.Keyword.IsKind(SyntaxKind.SetKeyword) || a.Keyword.IsKind(SyntaxKind.InitKeyword)) ? " set;" : "") + " }";
                    signatures.Add(property.Type.WithoutTrivia() + " " + property.Identifier.ValueText + " " + accessors);
                    break;
                default:
                    plan.Reasons.Add(Strings.Format("ExtractInterfaceBadMember", name));
                    return plan;
            }
        }

        var documentType = type;
        var eol = text.ToString().Contains("\r\n") ? "\r\n" : "\n";
        var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => u.WithoutTrivia().ToString()).Distinct().ToList();
        var ns = type.Parent is BaseNamespaceDeclarationSyntax nsDecl ? nsDecl.Name.ToString() : "";
        var body = new List<string> { "public interface I" + interfaceName, "{" };
        body.AddRange(signatures.Select(s => "    " + s));
        body.Add("}");
        var lines = new List<string>(usings);
        if (usings.Count > 0)
        {
            lines.Add("");
        }

        if (ns.Length > 0)
        {
            lines.Add("namespace " + ns);
            lines.Add("{");
            lines.AddRange(body.Select(l => "    " + l));
            lines.Add("}");
        }
        else
        {
            lines.AddRange(body);
        }

        // The type gains the interface in its base list: appended to an existing list, or added after the name and type parameters.
        var listAt = documentType.BaseList is { } baseList
            ? new TextChange(new TextSpan(baseList.Types.Last().Span.End, 0), ", I" + interfaceName)
            : new TextChange(new TextSpan(documentType.TypeParameterList?.Span.End ?? documentType.Identifier.Span.End, 0), " : I" + interfaceName);
        var changes = new Dictionary<DocumentId, List<TextChange>> { [documentId!] = new List<TextChange> { listAt } };
        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        edited = edited.AddDocument(DocumentId.CreateNewId(document.Project.Id), "I" + interfaceName + ".cs", SourceText.From(string.Join(eol, lines) + eol, System.Text.Encoding.UTF8), filePath: path);
        var newId = edited.GetDocumentIdsWithFilePath(path).Single();
        edited = await EditChecks.FormatFileAsync(edited, documentId!, plan).ConfigureAwait(false);
        edited = await EditChecks.FormatFileAsync(edited, newId, plan).ConfigureAwait(false);
        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, new[] { documentId!, newId }, plan).ConfigureAwait(false);
        plan.Files.Add(document.FilePath ?? file);
        plan.Files.Add(path);
        plan.Warnings.Add(Strings.Format("ExtractInterfaceDone", "I" + interfaceName, symbol.Name, string.Join(", ", memberNames)));
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }
}
