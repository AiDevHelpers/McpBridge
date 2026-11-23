namespace AiDevHelpers.ScriptHelper.Core;

public enum BrokerState
{
    Answering,
    Silent,
    UntrustedInstall,
}

/// <summary>Asks whether a broker is answering and whether it is installed where it may be trusted.</summary>
public interface IBrokerPing
{
    BrokerState Check(TimeSpan timeout);
}

public sealed record ClientResult(bool Ok, string Reason, int? ExitCode, string Stdout, string Stderr);

/// <summary>
/// The `elev` client: ping the broker, submit one request under the caller's SID, wait for the
/// answer. A script is never sent to a broker that did not answer the ping.
/// </summary>
public sealed class ScriptClient
{
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly RequestQueue _queue;
    private readonly IBrokerPing _ping;
    private readonly string _requesterSid;

    public ScriptClient(RequestQueue queue, IBrokerPing ping, string requesterSid)
    {
        _queue = queue;
        _ping = ping;
        _requesterSid = requesterSid;
    }

    public Task<ClientResult> RunAsync(string projectRoot, string script, IReadOnlyList<string> args, TimeSpan timeout) =>
        SubmitAsync(new BrokerRequestFile(projectRoot, script, args), timeout);

    /// <summary>
    /// Asks the broker for the approval status of every allowlisted file in the project. Read only, and it never starts a broker:
    /// a silent broker is reported, so opening a repo does not raise a UAC prompt.
    /// </summary>
    public Task<ClientResult> VerifyAsync(string projectRoot, TimeSpan timeout) =>
        SubmitAsync(new BrokerRequestFile(projectRoot, string.Empty, Array.Empty<string>(), BrokerRequest.VerifyKind), timeout);

    private async Task<ClientResult> SubmitAsync(BrokerRequestFile request, TimeSpan timeout)
    {
        switch (_ping.Check(PingTimeout))
        {
            case BrokerState.Silent:
                return new ClientResult(false, "broker is not responding; start it before running scripts", null, string.Empty, string.Empty);
            case BrokerState.UntrustedInstall:
                return new ClientResult(false, "refused: the broker is not in the admin-only install folder", null, string.Empty, string.Empty);
        }

        var id = _queue.Enqueue(_requesterSid, request);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var answer = _queue.TryReadResult(_requesterSid, id);
            if (answer is not null)
            {
                return answer.Ok
                    ? new ClientResult(true, answer.Reason, answer.ExitCode, answer.Stdout, answer.Stderr)
                    : new ClientResult(false, answer.Reason, null, string.Empty, string.Empty);
            }

            await Task.Delay(PollInterval);
        }

        return new ClientResult(false, "timed out waiting for the broker's answer", null, string.Empty, string.Empty);
    }
}
