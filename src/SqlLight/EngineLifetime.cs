using Microsoft.Extensions.Hosting;

namespace SqlLight;

public sealed class EngineLifetime(PanelServer panel, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);
        await panel.ResumeEnvironments(stoppingToken);
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await panel.StopEnvironments(cancellationToken);
    }
}
