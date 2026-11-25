using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Plans the two TDD steps. <see cref="StartAsync"/> adds a method (as a stub that throws when it has no body) and a test for it, so
/// the test can be run red. <see cref="UpdateTestAsync"/> replaces the body of a test. Both only plan; the MCP server runs the test.
/// </summary>
internal static class TddEngine
{
    public static async Task<MethodEditPlan> StartAsync(
        Solution solution, string testFile, int testLine, string testMethodText, string methodFile, int methodLine, string methodText, DocUpdates docs)
    {
        if (string.Equals(System.IO.Path.GetFullPath(testFile), System.IO.Path.GetFullPath(methodFile), StringComparison.OrdinalIgnoreCase))
        {
            var refused = new MethodEditPlan();
            refused.Reasons.Add(Strings.Get("TddSameFile"));
            return refused;
        }

        var method = await MethodAddEngine.PlanAsync(solution, methodFile, methodLine, WithStubBody(methodText), null, false, docs).ConfigureAwait(false);
        if (method.Refused)
        {
            return method;
        }

        // The test is planned on the solution that already has the method, so it can call it.
        var test = await MethodAddEngine.PlanAsync(method.Result!, testFile, testLine, testMethodText, null, false, new DocUpdates()).ConfigureAwait(false);
        var combined = new MethodEditPlan { Result = test.Result, Formatted = method.Formatted || test.Formatted, InsertedAtLine = test.InsertedAtLine };
        combined.Reasons.AddRange(method.Reasons.Concat(test.Reasons));
        combined.Warnings.AddRange(method.Warnings.Concat(test.Warnings));
        combined.NewErrors.AddRange(method.NewErrors.Concat(test.NewErrors));
        combined.NewWarnings.AddRange(method.NewWarnings.Concat(test.NewWarnings));
        combined.NewNotices.AddRange(method.NewNotices.Concat(test.NewNotices));
        combined.Files.AddRange(method.Files.Concat(test.Files));
        combined.NewSignature = method.NewSignature;
        combined.DocsUpdated = method.DocsUpdated;
        combined.Method = method.Method;
        if (!test.Refused && test.Added is { } added)
        {
            if (!test.AddedIsTest)
            {
                combined.Reasons.Add(Strings.Get("TddNotATest"));
            }
            else
            {
                combined.Tests.Add(added);
            }
        }

        if (combined.Refused)
        {
            combined.Result = null;
        }

        return combined;
    }

    // A method given without a body gets one that throws, so it compiles and its test fails.
    private static string WithStubBody(string methodText)
    {
        var parsed = SyntaxFactory.ParseMemberDeclaration(methodText) as MethodDeclarationSyntax;
        if (parsed is null || parsed.Body is not null || parsed.ExpressionBody is not null)
        {
            return methodText;
        }

        var body = SyntaxFactory.Block(SyntaxFactory.ParseStatement("throw new System.NotImplementedException();"));
        return parsed.WithSemicolonToken(default).WithBody(body).NormalizeWhitespace().ToString();
    }

    public static async Task<MethodEditPlan> UpdateTestAsync(
        Solution solution, string file, int line, string newBody, string? targetFile, int? targetLine)
    {
        var plan = new MethodEditPlan();
        var documentId = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault();
        var document = documentId is null ? null : solution.GetDocument(documentId);
        var text = document is null ? null : await document.GetTextAsync().ConfigureAwait(false);
        if (document is null || text is null || line < 1 || line > text.Lines.Count)
        {
            plan.Reasons.Add(Strings.Format("NoRoslynDocumentFor", file));
            return plan;
        }

        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        var lineText = text.Lines[line - 1].ToString();
        var position = text.Lines[line - 1].Start + (lineText.Length - lineText.TrimStart().Length);
        var node = root!.FindToken(Math.Min(position, root.FullSpan.End)).Parent?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        var symbol = node is null ? null : model!.GetDeclaredSymbol(node);
        if (node is null || symbol is null || !TestDiscovery.IsTestMethod(symbol))
        {
            plan.Reasons.Add(Strings.Get("TddNotATest"));
            return plan;
        }

        var trimmed = newBody.Trim();
        var block = SyntaxFactory.ParseStatement(trimmed.StartsWith("{", StringComparison.Ordinal) ? trimmed : "{" + trimmed + "}") as BlockSyntax;
        if (block is null || block.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            plan.Reasons.Add(Strings.Get("TddBodyNotABlock"));
            return plan;
        }

        var newText = node.WithBody(block).WithExpressionBody(null).WithSemicolonToken(default).WithoutTrivia().ToString();
        var edited = await MethodEditEngine.PlanAsync(solution, file, line, 1, newText, Array.Empty<CallerEdit>(), null).ConfigureAwait(false);

        // Run this test, and report coverage of the method under test when the caller names it.
        edited.Tests.Clear();
        edited.Tests.Add(TestDiscovery.TargetFor(symbol, document, line));
        edited.Method = null;
        if (!edited.Refused && targetFile is not null && targetLine is { } tl && edited.Result is { } result)
        {
            edited.Method = await TestDiscovery.LocateAtAsync(result, targetFile, tl).ConfigureAwait(false);
        }

        return edited;
    }
}
