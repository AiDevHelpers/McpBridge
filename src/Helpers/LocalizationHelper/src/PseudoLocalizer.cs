using System.Text;
using System.Text.RegularExpressions;

namespace AiDevHelpers.Helpers.LocalizationHelper;

/// <summary>Turns text into an accented, padded look-alike so unlocalized text and layout limits show up. Placeholders and escaped braces are kept.</summary>
public static partial class PseudoLocalizer
{
    private const double Growth = 0.3;

    private static readonly Dictionary<char, char> Accents = new()
    {
        ['a'] = 'á', ['A'] = 'Á', ['c'] = 'ç', ['C'] = 'Ç', ['e'] = 'é', ['E'] = 'É', ['i'] = 'í', ['I'] = 'Í',
        ['n'] = 'ñ', ['N'] = 'Ñ', ['o'] = 'ó', ['O'] = 'Ó', ['s'] = 'š', ['S'] = 'Š', ['u'] = 'ú', ['U'] = 'Ú',
        ['y'] = 'ý', ['Y'] = 'Ý', ['z'] = 'ž', ['Z'] = 'Ž',
    };

    public static string Transform(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var body = new StringBuilder();
        var last = 0;
        foreach (Match m in Protected().Matches(text))
        {
            Accent(text[last..m.Index], body);
            body.Append(m.Value);
            last = m.Index + m.Length;
        }

        Accent(text[last..], body);
        var pad = Math.Max(1, (int)Math.Ceiling(body.Length * Growth));
        return "[" + body + new string('~', pad) + "]";
    }

    private static void Accent(string segment, StringBuilder into)
    {
        foreach (var c in segment)
        {
            into.Append(Accents.TryGetValue(c, out var accented) ? accented : c);
        }
    }

    [GeneratedRegex(@"\{\{|\}\}|(?<!\{)\{[A-Za-z0-9_]+(?:[:,][^{}]*)?\}(?!\})")]
    private static partial Regex Protected();
}
