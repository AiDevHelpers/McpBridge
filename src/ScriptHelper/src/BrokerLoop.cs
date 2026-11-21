namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// Answers pending requests. The decision step is a delegate, so the loop is tested on its own;
/// the broker passes RequestHandler.Handle. A decision that throws is answered as a failure, so
/// one bad request cannot stop the others or leave its requester waiting.
/// </summary>
public sealed class BrokerLoop
{
    private const string InternalErrorReason = "internal error while handling the request";

    private readonly RequestQueue _queue;
    private readonly Func<BrokerRequest, BrokerResponse> _decide;

    public BrokerLoop(RequestQueue queue, Func<BrokerRequest, BrokerResponse> decide)
    {
        _queue = queue;
        _decide = decide;
    }

    /// <summary>One pass over the pending requests. Returns how many were answered.</summary>
    public int RunOnce()
    {
        var answered = 0;
        foreach (var item in _queue.Pending())
        {
            var request = new BrokerRequest(item.Request.ProjectRoot, item.Request.Script, item.Request.Args, item.RequesterSid);

            BrokerResponse response;
            try
            {
                response = _decide(request);
            }
            catch (Exception)
            {
                response = new BrokerResponse(false, InternalErrorReason, null, string.Empty, string.Empty);
            }

            _queue.WriteResult(item.RequesterSid, item.Id, response);
            answered++;
        }

        return answered;
    }
}
