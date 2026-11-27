using System.Security.Cryptography;
using System.Text;

namespace AiDevHelpers.MCP.Http;

/// <summary>What the tray shows the user, and what the answer must echo back.</summary>
/// <param name="Nonce">A fresh value per call; the answer must return it.</param>
/// <param name="Tool">The tool name.</param>
/// <param name="ArgumentsHash">SHA-256 (lower-case hex) of the arguments text; the answer must return it.</param>
/// <param name="Summary">The arguments, shortened, for the dialog.</param>
public sealed record ApprovalRequest(string Nonce, string Tool, string ArgumentsHash, string Summary);

/// <summary>The user's answer, with the nonce and hash it was given for.</summary>
public sealed record ApprovalResponse(string Nonce, string ArgumentsHash, bool Approved);

/// <summary>Asks the user. The real one is a named pipe to the tray; tests use a fake.</summary>
public interface IApprovalChannel
{
    Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>Whether a tool call may run, and if not, why.</summary>
public sealed record GateResult(bool Allowed, string? Reason = null);

/// <summary>
/// Decides whether a tool call may run. Safe tools run. Every other tool, including one the catalog does not know,
/// needs the user's approval for that exact call: the answer must return the call's nonce and the hash of its
/// arguments, so an approval cannot be reused for other arguments. Any error, a missing tray or no answer in time
/// refuses the call (fail closed).
/// </summary>
public sealed class ApprovalGate(IApprovalChannel channel, TimeSpan timeout, Func<string>? nonceFactory = null)
{
    private const int SummaryLength = 500;

    private readonly Func<string> _nonce = nonceFactory ?? (() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());

    public async Task<GateResult> CheckAsync(string? tool, string argumentsJson, CancellationToken cancellationToken)
    {
        if (ToolRiskCatalog.Classify(tool) == ToolRisk.Safe)
        {
            return new GateResult(true);
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(argumentsJson))).ToLowerInvariant();
        var summary = argumentsJson.Length <= SummaryLength ? argumentsJson : argumentsJson[..SummaryLength];
        var request = new ApprovalRequest(_nonce(), tool ?? string.Empty, hash, summary);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var response = await channel.RequestAsync(request, timeoutSource.Token).WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            if (response is null || response.Nonce != request.Nonce || response.ArgumentsHash != request.ArgumentsHash)
            {
                return new GateResult(false, Strings.Get("ApprovalMismatch"));
            }

            return response.Approved ? new GateResult(true) : new GateResult(false, Strings.Get("ApprovalDenied"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GateResult(false, Strings.Get("ApprovalTimedOut"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new GateResult(false, Strings.Get("ApprovalUnavailable"));
        }
    }
}
