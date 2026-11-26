using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AiDevHelpers.Helpers.LocalizationHelper;

/// <summary>A culture-sensitive call with no culture or comparison argument. <see cref="Kind"/> is always "culture-call".</summary>
public sealed record CultureCallFinding(string File, int Line, string Kind, string Call, string Suggestion);

/// <summary>
/// Finds calls whose result depends on the current culture and that name no culture: string.Format, Parse and TryParse on numbers and dates,
/// ToUpper and ToLower, string.Compare, StartsWith/EndsWith/IndexOf/LastIndexOf with a string, and ToString() on numbers and dates.
/// Uses the semantic model, so calls whose types do not resolve are skipped. A trailing "// culture-ok" comment suppresses a line.
/// </summary>
public static class CultureCallChecker
{
    private const string SuppressMarker = "culture-ok";
    private const int MaxCallLength = 80;

    private static readonly HashSet<SpecialType> NumberOrDate =
    [
        SpecialType.System_Int32, SpecialType.System_Int64, SpecialType.System_Int16, SpecialType.System_Byte, SpecialType.System_UInt32,
        SpecialType.System_UInt64, SpecialType.System_Single, SpecialType.System_Double, SpecialType.System_Decimal, SpecialType.System_DateTime,
    ];

    private static readonly HashSet<string> NumberOrDateNames = ["DateTimeOffset", "TimeSpan", "DateOnly", "TimeOnly"];

    public static IReadOnlyList<CultureCallFinding> Check(string path)
    {
        string baseDir;
        IEnumerable<string> files;
        if (File.Exists(path))
        {
            baseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            files = [path];
        }
        else if (Directory.Exists(path))
        {
            baseDir = Path.GetFullPath(path);
            files = Directory.EnumerateFiles(baseDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !StringScanner.InBuildFolder(Path.GetRelativePath(baseDir, f)));
        }
        else
        {
            throw new DirectoryNotFoundException(Strings.Format("FolderNotFound", path));
        }

        var trees = files.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: Path.GetFullPath(f))).ToList();
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("CultureCheck", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var findings = new List<CultureCallFinding>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            var text = tree.GetText();
            foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol method)
                {
                    continue;
                }

                var kind = Classify(method);
                if (kind is null)
                {
                    continue;
                }

                var line = text.Lines.GetLineFromPosition(call.SpanStart);
                if (line.ToString().Contains("// " + SuppressMarker, StringComparison.Ordinal))
                {
                    continue;
                }

                var oneLine = string.Join(' ', call.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                findings.Add(new CultureCallFinding(
                    Path.GetRelativePath(baseDir, tree.FilePath),
                    line.LineNumber + 1,
                    "culture-call",
                    oneLine.Length > MaxCallLength ? oneLine[..MaxCallLength] + "..." : oneLine,
                    Strings.Get(kind)));
            }
        }

        return findings;
    }

    /// <summary>The resx key of the suggestion when <paramref name="method"/> is a culture-sensitive call without a culture argument, else null.</summary>
    private static string? Classify(IMethodSymbol method)
    {
        var type = method.ContainingType;
        var isString = type.SpecialType == SpecialType.System_String;
        var hasCulture = method.Parameters.Any(p => IsCultureType(p.Type));
        var hasProvider = method.Parameters.Any(p => p.Type.Name == "IFormatProvider" || p.Type.Name == "CultureInfo");

        return method.Name switch
        {
            "Format" when isString && !hasProvider => "CultureSuggestInvariant",
            "Parse" or "TryParse" when IsNumberOrDate(type) && !hasProvider => "CultureSuggestInvariant",
            "ToUpper" or "ToLower" when isString && method.Parameters.Length == 0 => "CultureSuggestInvariant",
            "ToString" when IsNumberOrDate(type) && method.Parameters.Length == 0 => "CultureSuggestInvariant",
            "Compare" when isString && !hasCulture => "CultureSuggestOrdinal",
            "StartsWith" or "EndsWith" or "IndexOf" or "LastIndexOf"
                when isString && method.Parameters.Length > 0 && method.Parameters[0].Type.SpecialType == SpecialType.System_String && !hasCulture => "CultureSuggestOrdinal",
            _ => null,
        };
    }

    private static bool IsCultureType(ITypeSymbol type) =>
        type.Name is "CultureInfo" or "StringComparison" or "CompareOptions" or "IFormatProvider";

    private static bool IsNumberOrDate(INamedTypeSymbol type) =>
        NumberOrDate.Contains(type.SpecialType) || NumberOrDateNames.Contains(type.Name);
}
