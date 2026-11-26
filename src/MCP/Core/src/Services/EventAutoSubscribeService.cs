using System;
using System.Threading;
using System.Threading.Tasks;
using AiDevHelpers.Bridge;
using Microsoft.Extensions.Hosting;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// Keeps one events subscription open to every running Visual Studio instance, so events are buffered from the moment the
/// instance starts. Every few seconds it subscribes to instances it does not yet hold (including ones that reconnected) and
/// drops nothing itself: closed subscriptions are retried on the next pass.
/// </summary>
public sealed class EventAutoSubscribeService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var instance in VsInstanceRegistry.ListRunning())
            {
                try
                {
                    await EventSubscriptions.EnsureSubscribedAsync(instance.Pid, stoppingToken, instance.Ide).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The instance is busy or still starting. The next pass tries again.
                }
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
