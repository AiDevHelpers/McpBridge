using System.Globalization;
using System.Resources;

namespace AiDevHelpers.MCP.Http;

/// <summary>User-facing text, read from Resources/Strings.resx so it can be localized.</summary>
internal static class Strings
{
    private static readonly ResourceManager Manager = new("AiDevHelpers.MCP.Http.Resources.Strings", typeof(Strings).Assembly);

    public static string Get(string name) => Manager.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    public static string Format(string name, params object?[] args) =>
        string.Format(CultureInfo.CurrentUICulture, Get(name), args);
}
