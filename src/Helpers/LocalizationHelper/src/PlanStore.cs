using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AiDevHelpers.Helpers.LocalizationHelper;

/// <summary>Holds plans between a plan call and its confirm. A token is single use and expires after 15 minutes.</summary>
public sealed class PlanStore<T>(Func<DateTime>? clock = null) where T : class
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private readonly ConcurrentDictionary<string, (T Plan, DateTime Expires)> _plans = new();

    public string Add(T plan)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _plans[token] = (plan, _clock() + Lifetime);
        return token;
    }

    public bool TryTake(string token, out T plan)
    {
        plan = null!;
        if (!_plans.TryRemove(token, out var entry) || entry.Expires < _clock())
        {
            return false;
        }

        plan = entry.Plan;
        return true;
    }

    public DateTime ExpiresUtc(string token) => _plans.TryGetValue(token, out var entry) ? entry.Expires : default;
}
