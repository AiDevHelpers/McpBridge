using System.Text.RegularExpressions;

namespace AiDevHelpers.Helpers.ConfigHelper;

/// <summary>Replaces values of secret-looking keys in JSON and XML text with "***".</summary>
public static partial class ConfigRedactor
{
    private const string SecretKeys = "password|passwd|pwd|secret|token|apikey|api_key|connectionstring|credential";
    private const string NameChars = @"[A-Za-z0-9_:.\-]";

    private const string JsonPattern =
        @"(?<prefix>""[^""]*(?:" + SecretKeys + @")[^""]*""\s*:\s*)""(?:[^""\x5C]|\x5C.)*""";

    private const string XmlElementPattern =
        @"(?<open><(?<n>" + NameChars + @"*(?:" + SecretKeys + @")" + NameChars + @"*)>)[^<]*(?<close></\k<n>>)";

    private const string XmlAttributePattern =
        @"(?<attr>\b" + NameChars + @"*(?:" + SecretKeys + @")" + NameChars + @"*\s*=\s*"")[^""]*(?<end>"")";

    /// <summary>Redacts JSON string values, XML element text and XML attribute values for secret-looking keys.</summary>
    public static string Redact(string text)
    {
        text = JsonSecret().Replace(text, "${prefix}\"***\"");
        text = XmlElementSecret().Replace(text, "${open}***${close}");
        text = XmlAttributeSecret().Replace(text, "${attr}***${end}");
        return text;
    }

    [GeneratedRegex(JsonPattern, RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecret();

    [GeneratedRegex(XmlElementPattern, RegexOptions.IgnoreCase)]
    private static partial Regex XmlElementSecret();

    [GeneratedRegex(XmlAttributePattern, RegexOptions.IgnoreCase)]
    private static partial Regex XmlAttributeSecret();
}
