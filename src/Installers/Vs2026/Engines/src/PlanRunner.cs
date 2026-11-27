using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Runs an edit plan off the UI thread, against a snapshot of the solution, with a time limit. The engines' Roslyn calls can take
/// seconds on a large solution; running them on the UI thread would freeze Visual Studio. A plan that does not finish in time is
/// reported as incomplete and refused, never applied.
/// </summary>
internal static class PlanRunner
{
    /// <summary>The default time limit for a plan.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public sealed class Outcome
    {
        public Outcome(MethodEditPlan? plan, bool incomplete)
        {
            Plan = plan;
            Incomplete = incomplete;
        }

        /// <summary>The plan, or null when it did not finish in time.</summary>
        public MethodEditPlan? Plan { get; }

        public bool Incomplete { get; }
    }

    /// <summary>
    /// Runs <paramref name="plan"/> on <paramref name="snapshot"/> on a thread-pool thread. The caller must take the snapshot on the UI
    /// thread; this method does not touch the UI thread. The plan keeps running in the background after a timeout, since the engines
    /// take no cancellation token yet; its result is dropped.
    /// </summary>
    public static async Task<Outcome> RunAsync(Solution snapshot, Func<Solution, Task<MethodEditPlan>> plan, TimeSpan timeout)
    {
        var planning = Task.Run(() => plan(snapshot));
        var finished = await Task.WhenAny(planning, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished != planning)
        {
            return new Outcome(null, incomplete: true);
        }

        return new Outcome(await planning.ConfigureAwait(false), incomplete: false);
    }

    /// <summary>The plan reply for a run that did not finish: refused, with the reason.</summary>
    public static MethodEditPlan Incomplete(TimeSpan timeout)
    {
        var plan = new MethodEditPlan();
        plan.Reasons.Add(Strings.Format("PlanIncomplete", (int)timeout.TotalSeconds));
        return plan;
    }
}
