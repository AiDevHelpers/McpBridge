using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>A request read from the queue. RequesterSid comes from the folder it was written in.</summary>
public sealed record QueuedRequest(string Id, string RequesterSid, BrokerRequestFile Request);

/// <summary>The on-disk request body. The requester is never taken from this file.</summary>
public sealed record BrokerRequestFile(string ProjectRoot, string Script, IReadOnlyList<string> Args);

/// <summary>
/// File-based transport between the requesting user and the broker. Requests live in one folder
/// per requester SID; results in a matching folder. Files are created new (never overwritten),
/// and a request with no result is what the broker still has to answer.
/// </summary>
public sealed class RequestQueue
{
    private const string RequestSuffix = ".request.json";
    private const string MalformedReason = "malformed request";
    private const string StaleReason = "stale: no broker picked the request up in time";
    private static readonly Regex SidShape = new(@"^S-1-\d+(-\d+)+$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _queueRoot;
    private readonly string _resultsRoot;

    public RequestQueue(string queueRoot, string resultsRoot)
    {
        _queueRoot = queueRoot;
        _resultsRoot = resultsRoot;
    }

    public string Enqueue(string requesterSid, BrokerRequestFile request)
    {
        var id = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(_queueRoot, requesterSid);
        Directory.CreateDirectory(dir);
        WriteNew(Path.Combine(dir, id + RequestSuffix), JsonSerializer.Serialize(request, Options));
        return id;
    }

    /// <summary>Requests that have no result yet. A request that cannot be read is answered as malformed.</summary>
    public IReadOnlyList<QueuedRequest> Pending()
    {
        var list = new List<QueuedRequest>();
        foreach (var sid in SidFolders(_queueRoot))
        {
            foreach (var file in Directory.GetFiles(Path.Combine(_queueRoot, sid), "*" + RequestSuffix))
            {
                var id = Path.GetFileName(file)[..^RequestSuffix.Length];
                if (File.Exists(ResultPath(sid, id)))
                    continue;

                BrokerRequestFile? body = null;
                try
                {
                    body = JsonSerializer.Deserialize<BrokerRequestFile>(File.ReadAllText(file));
                }
                catch (JsonException)
                {
                    body = null;
                }
                catch (IOException)
                {
                    body = null;
                }

                if (body is null || body.ProjectRoot is null || body.Script is null || body.Args is null)
                {
                    WriteResult(sid, id, new BrokerResponse(false, MalformedReason, null, string.Empty, string.Empty));
                    continue;
                }

                list.Add(new QueuedRequest(id, sid, body));
            }
        }

        return list;
    }

    public void WriteResult(string requesterSid, string id, BrokerResponse response)
    {
        var dir = Path.Combine(_resultsRoot, requesterSid);
        Directory.CreateDirectory(dir);
        WriteNew(Path.Combine(dir, id + ".json"), JsonSerializer.Serialize(response, Options));
    }

    public BrokerResponse? TryReadResult(string requesterSid, string id)
    {
        var path = ResultPath(requesterSid, id);
        if (!File.Exists(path))
            return null;

        return JsonSerializer.Deserialize<BrokerResponse>(File.ReadAllText(path));
    }

    /// <summary>Answers requests older than maxAge that still have no result. Returns how many it answered.</summary>
    public int FailStale(DateTime now, TimeSpan maxAge)
    {
        var answered = 0;
        foreach (var sid in SidFolders(_queueRoot))
        {
            foreach (var file in Directory.GetFiles(Path.Combine(_queueRoot, sid), "*" + RequestSuffix))
            {
                var id = Path.GetFileName(file)[..^RequestSuffix.Length];
                if (File.Exists(ResultPath(sid, id)))
                    continue;

                if (File.GetLastWriteTimeUtc(file) < now.ToUniversalTime() - maxAge)
                {
                    WriteResult(sid, id, new BrokerResponse(false, StaleReason, null, string.Empty, string.Empty));
                    answered++;
                }
            }
        }

        return answered;
    }

    private string ResultPath(string sid, string id) => Path.Combine(_resultsRoot, sid, id + ".json");

    private static IEnumerable<string> SidFolders(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        foreach (var dir in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (SidShape.IsMatch(name))
                yield return name;
        }
    }

    /// <summary>Creates the file only if it does not exist, so nothing already answered is overwritten.</summary>
    private static void WriteNew(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(text);
    }
}
