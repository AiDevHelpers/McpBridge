namespace AiDevHelpers.MCP.Http;

/// <summary>Why a request was refused, or <see cref="Allowed"/>.</summary>
public enum GuardResult
{
    Allowed,
    BadHost,
    BadOrigin,
    Unauthorized,
}

/// <summary>
/// Request checks for the HTTP MCP, in order: Host (DNS-rebinding defense), Origin (any browser-sent origin is
/// refused; MCP clients are not browsers), then the bearer token.
/// </summary>
public static class RequestGuard
{
    private const string BearerPrefix = "Bearer ";

    /// <param name="host">The Host header.</param>
    /// <param name="origin">The Origin header, or null when absent.</param>
    /// <param name="authorization">The Authorization header.</param>
    /// <param name="port">The port the server listens on.</param>
    /// <param name="token">The expected bearer token.</param>
    public static GuardResult Check(string? host, string? origin, string? authorization, int port, string token)
    {
        if (!IsLoopbackHost(host, port))
        {
            return GuardResult.BadHost;
        }

        if (origin is not null)
        {
            return GuardResult.BadOrigin;
        }

        if (authorization is null || !authorization.StartsWith(BearerPrefix, StringComparison.Ordinal)
            || !TokenStore.Verify(token, authorization[BearerPrefix.Length..]))
        {
            return GuardResult.Unauthorized;
        }

        return GuardResult.Allowed;
    }

    private static bool IsLoopbackHost(string? host, int port)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        return string.Equals(host, $"127.0.0.1:{port}", StringComparison.Ordinal)
            || string.Equals(host, $"localhost:{port}", StringComparison.OrdinalIgnoreCase);
    }
}
