using System.Security.AccessControl;
using System.Security.Principal;

namespace AiDevHelpers.Host;

/// <summary>
/// The folder that holds the HTTP MCP's token and endpoint record: <c>%ProgramData%\AiDevHelpers\mcp-http</c>.
/// Only the logged-on user (who runs the server and creates the token there), SYSTEM (the supervisor's health
/// probe reads the token) and Administrators can use it; other users get nothing.
/// </summary>
public static class McpDataFolder
{
    /// <summary>The default folder.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "AiDevHelpers", "mcp-http");

    public static string TokenPath(string folder) => Path.Combine(folder, "token");

    public static string EndpointPath(string folder) => Path.Combine(folder, "endpoint.json");

    /// <summary>Creates the folder if needed and sets its access to <paramref name="user"/>, SYSTEM and Administrators.</summary>
    public static void Ensure(string path, SecurityIdentifier user)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(user);
        Directory.CreateDirectory(path);

        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
