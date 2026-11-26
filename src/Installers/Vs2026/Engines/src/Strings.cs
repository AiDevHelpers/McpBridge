using System.Globalization;
using System.Resources;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>User-facing text of the Visual Studio bridge, read from Resources/Strings.resx so it can be localized.</summary>
public static class Strings
{
    private static readonly ResourceManager Manager = new("AiDevHelpers.Installers.Vs2026.Core.Resources.Strings", typeof(Strings).Assembly);

    public static string Get(string name) => Manager.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    public static string Format(string name, params object?[] args) =>
        string.Format(CultureInfo.CurrentUICulture, Get(name), args);
}
