using Microsoft.Extensions.Hosting;

namespace Concierge.Platforms.Android;

/// <summary>Refreshes non-invasive service capability and permission state for voice selection.</summary>
public sealed class AndroidCircleAiServiceVoiceProbe(AndroidCircleAiSharedService service) : IHostedService
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _run = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        await service.InspectAsync(_stop.Token);
                        await service.RefreshVoiceAccessAsync(_stop.Token);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                    catch { }
                    await Task.Delay(TimeSpan.FromSeconds(45), _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        });
        return Task.CompletedTask;
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.Cancel();
        if (_run is not null) try { await _run.WaitAsync(cancellationToken); } catch (OperationCanceledException) { }
    }
}
