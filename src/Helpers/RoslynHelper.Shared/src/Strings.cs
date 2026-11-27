using System.Globalization;
using System.Resources;

namespace AiDevHelpers.Helpers.RoslynHelper;

/// <summary>Text the helper returns to its caller, read from Resources/Strings.resx so it can be localized.</summary>
public static class Strings
{
    private static readonly ResourceManager Manager = new("AiDevHelpers.Helpers.RoslynHelper.Resources.Strings", typeof(Strings).Assembly);

    public static string Get(string name) => Manager.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    public static string Format(string name, params object?[] args) =>
        string.Format(CultureInfo.CurrentUICulture, Get(name), args);
}
