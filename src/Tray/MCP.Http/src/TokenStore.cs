using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace AiDevHelpers.MCP.Http;

/// <summary>
/// The bearer token that gates the HTTP MCP. Created on first start, stored in a file only the current user and
/// SYSTEM can read. The token is not a boundary against code already running as the user; tray confirmation of
/// dangerous tools is the real control (see docs/plans/2026-11-26-tray-service.md).
/// </summary>
public static class TokenStore
{
    private const int MinimumLength = 32;
    private const int TokenBytes = 32;

    /// <summary>Returns the stored token, creating the file with a new random token when it does not exist.</summary>
    /// <exception cref="InvalidDataException">The file holds a token shorter than 32 characters.</exception>
    public static string LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length < MinimumLength)
            {
                throw new InvalidDataException(Strings.Get("TokenTooShort"));
            }

            return existing;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        using (File.Create(path))
        {
        }

        RestrictToCurrentUserAndSystem(path);
        File.WriteAllText(path, token);
        return token;
    }

    /// <summary>Compares a presented token with the stored one in constant time. False for null, empty or wrong tokens.</summary>
    public static bool Verify(string expected, string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(candidate));
    }

    private static void RestrictToCurrentUserAndSystem(string path)
    {
        var info = new FileInfo(path);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
