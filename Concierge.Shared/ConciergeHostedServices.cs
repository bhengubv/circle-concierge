using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Concierge.Shared;

/// <summary>
/// Starts <see cref="IHostedService"/> registrations in a host that has no host.
/// </summary>
/// <remarks>
/// <para>
/// ASP.NET Core runs a host, so <c>AddHostedService</c> works there without anybody thinking
/// about it. MAUI builds a service provider and stops — nothing ever calls <c>StartAsync</c>.
/// The registration still compiles and still resolves, so the failure is silent: the on-device
/// model loader is registered that way, and the desktop app sat at "Engine queued for load…"
/// while the identical code reached the model on the web.
/// </para>
/// <para>
/// Deliberately not a host. A host owns application lifetime and would take the app down when
/// a service fails to start; on a phone that is a crash on launch over something optional.
/// This starts what is registered, keeps going when one of them throws, and gets out of the way.
/// </para>
/// </remarks>
public static class ConciergeHostedServices
{
    /// <summary>
    /// Starts every registered background service and waits for each to return from
    /// <see cref="IHostedService.StartAsync"/>. One that throws is logged and skipped.
    /// </summary>
    public static async Task StartAllAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(ConciergeHostedServices));

        foreach (var service in services.GetServices<IHostedService>())
        {
            try
            {
                await service.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger?.LogError(exception,
                    "Background service {Service} failed to start; the rest are unaffected.",
                    service.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Same, from app startup. Returns to the caller straight away — the work it kicks off
    /// includes loading a model, which takes minutes, and blocking here is a black screen on
    /// desktop and an ANR on Android.
    /// </summary>
    public static void StartInBackground(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Nothing awaits this task, so it must not be able to fault: an unobserved exception on
        // a thread pool thread ends the process. StartAllAsync already swallows per-service
        // failures; this covers the provider itself going wrong.
        _ = Task.Run(async () =>
        {
            try
            {
                await StartAllAsync(services).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                services.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(ConciergeHostedServices))
                    .LogError(exception, "Could not start background services.");
            }
        });
    }
}
