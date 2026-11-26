using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Adds stubs to a class, struct or record (<c>ide_implement_members</c>). In "interface" mode it stubs every member of its interfaces
/// that nothing implements yet, implicitly or explicitly. In "override" mode it overrides the named virtual or abstract members of its
/// base classes. Every stub throws <c>System.NotImplementedException</c>. The stubs go at the end of the type, in its own layout.
/// </summary>
internal static class ImplementMembersEngine
{
    private const string Throw = "throw new System.NotImplementedException();";

    public static async Task<MethodEditPlan> PlanAsync(Solution solution, string file, int line, string mode, IReadOnlyList<string> names, bool explicitImplementation)
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
        if (line < 1 || line > text.Lines.Count)
        {
            plan.Reasons.Add(Strings.Format("LineOutOfRange", line, file));
            return plan;
        }

        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        var lineText = text.Lines[line - 1].ToString();
        var position = text.Lines[line - 1].Start + (lineText.Length - lineText.TrimStart().Length);
        var type = root!.FindToken(Math.Min(position, root.FullSpan.End)).Parent?.AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>().FirstOrDefault(t => t is not InterfaceDeclarationSyntax);
        if (type is null || model!.GetDeclaredSymbol(type) is not INamedTypeSymbol symbol)
        {
            plan.Reasons.Add(Strings.Get("ImplementNotAType"));
            return plan;
        }

        var existing = new HashSet<string>(type.Members.SelectMany(MemberAddEngine.NamesOf), StringComparer.Ordinal);
        var stubs = new List<string>();
        if (mode == "interface")
        {
            foreach (var iface in symbol.AllInterfaces)
            {
                foreach (var member in iface.GetMembers().Where(m => !m.IsStatic && !m.IsImplicitlyDeclared))
                {
                    // Implemented by this type or inherited from a base class: nothing to stub.
                    if (symbol.FindImplementationForInterfaceMember(member) is not null)
                    {
                        continue;
                    }

                    var stub = Stub(member, explicitImplementation ? iface : null, overriding: false);
                    if (stub is null)
                    {
                        plan.Reasons.Add(Strings.Format("ImplementUnsupported", member.ToDisplayString()));
                        return plan;
                    }

                    stubs.Add(stub);
                }
            }
        }
        else if (mode == "override")
        {
            foreach (var name in names)
            {
                var member = FindVirtual(symbol.BaseType, name);
                if (member is null)
                {
                    plan.Reasons.Add(Strings.Format("ImplementNoVirtual", name));
                    return plan;
                }

                var stub = Stub(member, null, overriding: true);
                if (stub is null)
                {
                    plan.Reasons.Add(Strings.Format("ImplementUnsupported", member.ToDisplayString()));
                    return plan;
                }

                stubs.Add(stub);
            }
        }
        else
        {
            plan.Reasons.Add(Strings.Format("ImplementBadMode", mode));
            return plan;
        }

        if (stubs.Count == 0)
        {
            plan.Reasons.Add(Strings.Get("ImplementNothingToDo"));
            return plan;
        }

        var duplicates = stubs.Where(s => existing.Contains(NameOfStub(s))).ToList();
        if (duplicates.Count > 0 && mode == "override")
        {
            plan.Reasons.Add(Strings.Format("MemberExists", string.Join(", ", duplicates.Select(NameOfStub))));
            return plan;
        }

        var eol = text.ToString().Contains("\r\n") ? "\r\n" : "\n";
        var indent = type.Members.Count > 0
            ? MethodAddEngine.LeadingWhitespace(text.Lines.GetLineFromPosition(type.Members[0].SpanStart).ToString())
            : MethodAddEngine.LeadingWhitespace(text.Lines.GetLineFromPosition(type.CloseBraceToken.SpanStart).ToString()) + "    ";
        var block = string.Join(eol + eol, stubs.Select(s => string.Join(eol, s.Split('\n').Select(l => indent + l))));
        var last = type.Members.LastOrDefault();
        var change = last is null
            ? new TextChange(new TextSpan(type.OpenBraceToken.Span.End, 0), eol + block)
            : new TextChange(new TextSpan(last.Span.End, 0), eol + eol + block);

        var changes = new Dictionary<DocumentId, List<TextChange>> { [documentId!] = new List<TextChange> { change } };
        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        edited = await EditChecks.FormatFileAsync(edited, documentId!, plan).ConfigureAwait(false);
        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, new[] { documentId! }, plan).ConfigureAwait(false);
        plan.Files.Add(document.FilePath ?? file);
        plan.Warnings.Add(Strings.Format("ImplementStubs", string.Join(", ", stubs.Select(NameOfStub))));
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    // The virtual or abstract member of a base class with this name, nearest first. Sealed or non-virtual members are not overridable.
    private static ISymbol? FindVirtual(INamedTypeSymbol? type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers(name))
            {
                if (member is IMethodSymbol { IsVirtual: true } or IPropertySymbol { IsVirtual: true } or IMethodSymbol { IsAbstract: true } or IPropertySymbol { IsAbstract: true })
                {
                    return member;
                }
            }
        }

        return null;
    }

    private static readonly SymbolDisplayFormat Shown = SymbolDisplayFormat.MinimallyQualifiedFormat;

    // One stub, or null when the member's shape is not one this tool writes (generic methods, for example).
    private static string? Stub(ISymbol member, INamedTypeSymbol? explicitInterface, bool overriding)
    {
        var prefix = explicitInterface is not null ? explicitInterface.ToDisplayString(Shown) + "." : "";
        var modifiers = overriding ? "public override " : explicitInterface is null ? "public " : "";
        switch (member)
        {
            case IMethodSymbol { MethodKind: MethodKind.Ordinary, TypeParameters.Length: 0 } method:
                var parameters = string.Join(", ", method.Parameters.Select(Parameter));
                return modifiers + method.ReturnType.ToDisplayString(Shown) + " " + prefix + method.Name + "(" + parameters + ")" + Body();

            case IPropertySymbol property when property.IsIndexer:
                var indexed = string.Join(", ", property.Parameters.Select(Parameter));
                return modifiers + property.Type.ToDisplayString(Shown) + " " + prefix + "this[" + indexed + "] " + Accessors(property.GetMethod is not null, property.SetMethod is not null);

            case IPropertySymbol property:
                return modifiers + property.Type.ToDisplayString(Shown) + " " + prefix + property.Name + " " + Accessors(property.GetMethod is not null, property.SetMethod is not null);

            case IEventSymbol evt:
                return modifiers + "event " + evt.Type.ToDisplayString(Shown) + " " + prefix + evt.Name + " { add { " + Throw + " } remove { " + Throw + " } }";

            default:
                return null;
        }
    }

    private static string Parameter(IParameterSymbol parameter)
    {
        var kind = parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => "",
        };
        return (parameter.IsParams ? "params " : "") + kind + parameter.Type.ToDisplayString(Shown) + " " + parameter.Name;
    }

    private static string Body() => " { " + Throw + " }";

    private static string Accessors(bool get, bool set)
    {
        var parts = new List<string>();
        if (get)
        {
            parts.Add("get => " + Throw.TrimEnd(';') + ";");
        }

        if (set)
        {
            parts.Add("set => " + Throw.TrimEnd(';') + ";");
        }

        return "{ " + string.Join(" ", parts) + " }";
    }

    // The member name a stub defines, for duplicate checks and the reply: the text between the last "." or ")" and the first "(".
    private static string NameOfStub(string stub)
    {
        var head = stub.Split('(')[0].Split('{')[0].Trim();
        var word = head.Split(' ').Last();
        return word.Split('.').Last();
    }
}
