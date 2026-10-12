using Android.App;
using Android.App.Job;
using Android.Content;
using Android.OS;
using Concierge.Ai;
using Concierge.Shared.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Concierge.Platforms.Android;

/// <summary>Schedules person-approved model preparation as an Android 14+ UIDT job.</summary>
internal sealed class AndroidModelDownloadCoordinator : IModelDownloadCoordinator
{
    internal const int JobId = 51981;
    internal const string LogTag = "Concierge.ModelDownload";

    public async Task<bool> StartAcceptedDownloadAsync(
        IModelDownloadRequired runtime,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            if (runtime is not IModelDownloadApprovalPreparer legacyPreparer
                || runtime is not IResumableModelDownload
                || !await legacyPreparer.PrepareDownloadApprovalAsync(cancellationToken).ConfigureAwait(false))
                return false;

            var appContext = global::Android.App.Application.Context;
            var intent = new Intent(appContext, typeof(AndroidModelDownloadForegroundService));
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                    appContext.StartForegroundService(intent);
                else
                    appContext.StartService(intent);
                return true;
            }
            catch
            {
                await runtime.CancelCurrentDownloadAsync().ConfigureAwait(false);
                throw;
            }
        }

        if (runtime is not IModelDownloadApprovalPreparer preparer
            || runtime is not IResumableModelDownload)
            return false;

        global::Android.Util.Log.Info(LogTag,
            $"Preparing approved download for {runtime.GetType().FullName}; resumable runtime is available.");

        if (!await preparer.PrepareDownloadApprovalAsync(cancellationToken).ConfigureAwait(false))
            return false;

        var offer = runtime.PendingDownload;
        if (offer is null)
        {
            await runtime.CancelCurrentDownloadAsync().ConfigureAwait(false);
            return false;
        }

        var app = global::Android.App.Application.Context;
        var scheduler = (JobScheduler?)app.GetSystemService(Context.JobSchedulerService);
        if (scheduler is null)
        {
            await runtime.CancelCurrentDownloadAsync().ConfigureAwait(false);
            return false;
        }

        var scheduled = ScheduleUidtJob(app, scheduler, offer);
        global::Android.Util.Log.Info(LogTag,
            $"Scheduled user-initiated model job {JobId}: {scheduled}; staged bytes={offer.StagingBytes}; selected bytes={offer.Bytes}.");
        if (!scheduled)
            await runtime.CancelCurrentDownloadAsync().ConfigureAwait(false);
        return scheduled;
    }

    [SupportedOSPlatform("android34.0")]
    private static bool ScheduleUidtJob(Context app, JobScheduler scheduler, PendingModelDownload offer)
    {
        var serviceType = Java.Lang.Class.FromType(typeof(AndroidModelDownloadJobService))
            ?? throw new InvalidOperationException("Android did not expose the model download job class.");
        var service = new ComponentName(app, serviceType);
        var builder = new JobInfo.Builder(JobId, service);
        builder.SetRequiredNetworkType(NetworkType.Any);
        builder.SetEstimatedNetworkBytes(Math.Max(offer.StagingBytes, offer.Bytes), 0);
        builder.SetUserInitiated(true);
        var job = builder.Build() ?? throw new InvalidOperationException("Android could not build the model download job.");

        return scheduler.Schedule(job) == JobScheduler.ResultSuccess;
    }

    public async Task StopAsync(IModelDownloadRequired runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        await runtime.CancelCurrentDownloadAsync().ConfigureAwait(false);
        var app = global::Android.App.Application.Context;
        (app.GetSystemService(Context.JobSchedulerService) as JobScheduler)?.Cancel(JobId);
    }
}

[Service(
    Name = "com.companyname.concierge.AndroidModelDownloadForegroundService",
    Exported = false)]
internal sealed class AndroidModelDownloadForegroundService : Service
{
    private const string ChannelId = "circle-ai-model-download";
    private const int NotificationId = 51982;
    private CancellationTokenSource? _stopping;
    private readonly ModelDownloadProgressThrottle _notificationThrottle = new(TimeSpan.FromSeconds(1));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        EnsureNotificationChannel();
        var initial = BuildNotification("Starting model download…", null);
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, initial, global::Android.Content.PM.ForegroundService.TypeDataSync);
        else
            StartForeground(NotificationId, initial);

        _stopping?.Cancel();
        _stopping?.Dispose();
        _stopping = new CancellationTokenSource();
        var stopping = _stopping.Token;
        _ = Task.Run(() => RunAcceptedDownloadAsync(startId, stopping), CancellationToken.None);
        return StartCommandResult.RedeliverIntent;
    }

    public override void OnDestroy()
    {
        try { _stopping?.Cancel(); }
        catch (ObjectDisposedException) { }
        if (AndroidCircleAiRuntime.Resolve(IPlatformApplication.Current?.Services) is IResumableModelDownload resumable)
            _ = resumable.PauseCurrentDownloadAsync();
        _stopping?.Dispose();
        _stopping = null;
        base.OnDestroy();
    }

    private async Task RunAcceptedDownloadAsync(int startId, CancellationToken stopping)
    {
        try
        {
            var runtime = AndroidCircleAiRuntime.Resolve(IPlatformApplication.Current?.Services)
                ?? throw new InvalidOperationException("Concierge services are not available to the Android service.");
            if (runtime is IInitializableChatRuntime initializable)
                await initializable.InitializeAsync(stopping).ConfigureAwait(false);
            if (runtime is not IResumableModelDownload resumable)
                return;

            var progress = new InlineProgress<ModelDownloadProgress>(report =>
            {
                if (_notificationThrottle.ShouldReport(report))
                    UpdateNotification(report.Description, report.Ratio);
            });
            await resumable.ResumeAcceptedDownloadAsync(progress, stopping).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The service is being stopped or the OS is reclaiming it; the accepted plan stays.
        }
        catch (Exception)
        {
            // The runtime publishes the actionable failure to Concierge.
        }
        finally
        {
            StopForeground(StopForegroundFlags.Remove);
            StopSelfResult(startId);
        }
    }

    private Notification BuildNotification(string detail, double? ratio)
    {
        var stopIntent = new Intent(this, typeof(AndroidModelDownloadCancelReceiver));
        stopIntent.SetAction("com.companyname.concierge.STOP_MODEL_DOWNLOAD");
        var stop = PendingIntent.GetBroadcast(this, AndroidModelDownloadCoordinator.JobId, stopIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);
        builder.SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)
            .SetContentTitle("Preparing Circle AI")
            .SetContentText(string.IsNullOrWhiteSpace(detail) ? "Downloading model files" : detail)
            .SetOnlyAlertOnce(true)
            .SetOngoing(true)
            .AddAction(new Notification.Action.Builder(null, "Stop", stop).Build());
        if (ratio is { } value)
            builder.SetProgress(100, (int)Math.Round(Math.Clamp(value, 0, 1) * 100), false);
        else
            builder.SetProgress(100, 0, true);
        return builder.Build()!;
    }

    private void UpdateNotification(string detail, double ratio)
    {
        var notification = BuildNotification(detail, ratio);
        (GetSystemService(NotificationService) as NotificationManager)?.Notify(NotificationId, notification);
    }

    private void EnsureNotificationChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            return;
        (GetSystemService(NotificationService) as NotificationManager)?.CreateNotificationChannel(
            new NotificationChannel(ChannelId, "Circle AI model downloads", NotificationImportance.Low));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

[Service(
    Name = "com.companyname.concierge.AndroidModelDownloadJobService",
    Permission = "android.permission.BIND_JOB_SERVICE",
    Exported = false)]
internal sealed class AndroidModelDownloadJobService : JobService
{
    private const string ChannelId = "circle-ai-model-download";
    private const int NotificationId = 51982;
    private readonly object _gate = new();
    private readonly ModelDownloadProgressThrottle _notificationThrottle = new(TimeSpan.FromSeconds(1));
    private CancellationTokenSource? _stopping;
    private JobParameters? _activeParameters;
    private IResumableModelDownload? _activeRuntime;

    public override bool OnStartJob(JobParameters? parameters)
    {
        if (parameters is null)
            return false;

        global::Android.Util.Log.Info(AndroidModelDownloadCoordinator.LogTag,
            $"JobService started job {parameters.JobId}.");

        lock (_gate)
        {
            _activeParameters = parameters;
            _stopping = new CancellationTokenSource();
        }

        EnsureNotificationChannel();
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            SetJobNotification(parameters, BuildNotification("Starting model download…", null));
        var stopping = _stopping.Token;
        _ = Task.Run(() => RunAcceptedDownloadAsync(parameters, stopping), CancellationToken.None);
        return true;
    }

    public override bool OnStopJob(JobParameters? parameters)
    {
        CancellationTokenSource? stopping;
        IResumableModelDownload? runtime;
        lock (_gate)
        {
            stopping = _stopping;
            runtime = _activeRuntime;
            _stopping = null;
            _activeRuntime = null;
            _activeParameters = null;
        }

        try { stopping?.Cancel(); }
        catch (ObjectDisposedException) { }
        stopping?.Dispose();

        if (OperatingSystem.IsAndroidVersionAtLeast(31)
            && parameters?.StopReason == (int)StopReason.User)
        {
            if (AndroidCircleAiRuntime.Resolve(IPlatformApplication.Current?.Services) is IModelDownloadRequired accepted)
                _ = accepted.CancelCurrentDownloadAsync();
            return false;
        }

        if (runtime is not null)
            _ = runtime.PauseCurrentDownloadAsync();

        // JobScheduler can relaunch the job after a system stop. Its accepted plan and the
        // bundle's partial bytes are both durable, so returning true resumes the same work.
        return true;
    }

    private async Task RunAcceptedDownloadAsync(JobParameters parameters, CancellationToken stopping)
    {
        var shouldFinish = false;
        CancellationTokenSource? ownedTokenSource = null;
        IChatRuntime? runtime = null;
        try
        {
            var platform = IPlatformApplication.Current
                ?? throw new InvalidOperationException("The MAUI service provider is not available to the Android model-download job.");
            runtime = AndroidCircleAiRuntime.Resolve(platform.Services)
                ?? throw new InvalidOperationException("Circle AI runtime is not registered for the Android model-download job.");
            global::Android.Util.Log.Info(AndroidModelDownloadCoordinator.LogTag,
                $"Job resolved runtime {runtime.GetType().FullName}; resumable={runtime is IResumableModelDownload}; initializable={runtime is IInitializableChatRuntime}.");

            if (runtime is IInitializableChatRuntime initializable)
                await initializable.InitializeAsync(stopping).ConfigureAwait(false);

            if (runtime is not IResumableModelDownload resumable)
                throw new InvalidOperationException($"Runtime {runtime.GetType().FullName} cannot resume an accepted model download.");

            lock (_gate)
                _activeRuntime = resumable;

            global::Android.Util.Log.Info(AndroidModelDownloadCoordinator.LogTag,
                "Resuming the accepted Circle AI model plan.");

            var progress = new InlineProgress<ModelDownloadProgress>(report =>
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(34) && _notificationThrottle.ShouldReport(report))
                    UpdateNotification(parameters, report.Description, report.Ratio);
            });
            var resumed = await resumable.ResumeAcceptedDownloadAsync(progress, stopping).ConfigureAwait(false);
            global::Android.Util.Log.Info(AndroidModelDownloadCoordinator.LogTag,
                $"Accepted model plan returned {resumed}; runtime status: {runtime.StatusMessage}");
            if (!resumed && runtime is IModelDownloadRequired downloadRuntime)
                await downloadRuntime.CancelCurrentDownloadAsync().ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // OnStopJob owns retry and consent preservation.
        }
        catch (Exception ex)
        {
            // Android jobs must not crash the system service, but do record the
            // cause so a failed start cannot masquerade as an endless 0% wait.
            global::Android.Util.Log.Error(AndroidModelDownloadCoordinator.LogTag,
                $"Could not resume the accepted Circle AI model download: {ex}");
            if (!stopping.IsCancellationRequested && runtime is IModelDownloadRequired downloadRuntime)
            {
                try { await downloadRuntime.CancelCurrentDownloadAsync().ConfigureAwait(false); }
                catch (Exception cleanupError)
                {
                    global::Android.Util.Log.Error(AndroidModelDownloadCoordinator.LogTag,
                        $"Could not clear the failed download state: {cleanupError.Message}");
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeParameters, parameters))
                {
                    _activeParameters = null;
                    _activeRuntime = null;
                    ownedTokenSource = _stopping;
                    _stopping = null;
                    shouldFinish = true;
                }
            }
            ownedTokenSource?.Dispose();
            if (shouldFinish)
                new Handler(Looper.MainLooper!).Post(() => JobFinished(parameters, false));
        }
    }

    [SupportedOSPlatform("android34.0")]
    private void UpdateNotification(JobParameters parameters, string detail, double ratio)
    {
        var notification = BuildNotification(detail, ratio);
        new Handler(Looper.MainLooper!).Post(() =>
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_activeParameters, parameters))
                    return;
            }
            if (OperatingSystem.IsAndroidVersionAtLeast(34))
                SetJobNotification(parameters, notification);
        });
    }

    [SupportedOSPlatform("android34.0")]
    private void SetJobNotification(JobParameters parameters, Notification notification)
        => SetNotification(parameters, NotificationId, notification, JobEndNotificationPolicy.Remove);

    [SupportedOSPlatform("android26.0")]
    private Notification BuildNotification(string detail, double? ratio)
    {
        var stopIntent = new Intent(this, typeof(AndroidModelDownloadCancelReceiver));
        stopIntent.SetAction("com.companyname.concierge.STOP_MODEL_DOWNLOAD");
        var stop = PendingIntent.GetBroadcast(
            this,
            AndroidModelDownloadCoordinator.JobId,
            stopIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var notification = new Notification.Builder(this, ChannelId);
        notification
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)
            .SetContentTitle("Preparing Circle AI")
            .SetContentText(string.IsNullOrWhiteSpace(detail) ? "Downloading model files" : detail)
            .SetOnlyAlertOnce(true)
            .SetOngoing(true)
            .AddAction(new Notification.Action.Builder(null, "Stop", stop).Build());
        if (ratio is { } value)
        {
            var percent = (int)Math.Round(Math.Clamp(value, 0, 1) * 100);
            notification.SetProgress(100, percent, false);
        }
        else
        {
            notification.SetProgress(100, 0, true);
        }
        return notification.Build()!;
    }

    private void EnsureNotificationChannel()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            CreateNotificationChannel();
    }

    [SupportedOSPlatform("android26.0")]
    private void CreateNotificationChannel()
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(new NotificationChannel(ChannelId, "Circle AI model downloads", NotificationImportance.Low));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

[BroadcastReceiver(Enabled = true, Exported = false)]
internal sealed class AndroidModelDownloadCancelReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        var appContext = global::Android.App.Application.Context;
        if (AndroidCircleAiRuntime.Resolve(IPlatformApplication.Current?.Services) is IModelDownloadRequired runtime)
            _ = runtime.CancelCurrentDownloadAsync();
        if (context is not null)
            context.StopService(new Intent(context, typeof(AndroidModelDownloadForegroundService)));
        (appContext.GetSystemService(Context.JobSchedulerService) as JobScheduler)?
            .Cancel(AndroidModelDownloadCoordinator.JobId);
    }
}

/// <summary>
/// Resolves the local Circle AI engine specifically. The general IChatRuntime registration is
/// intentionally multi-provider and its last registration may be a cloud adapter; background
/// model jobs must resume the same local engine whose approval was persisted by the UI.
/// </summary>
internal static class AndroidCircleAiRuntime
{
    public static IChatRuntime? Resolve(IServiceProvider? services)
    {
        if (services is null)
            return null;

        return services.GetService<CircleAiChatRuntime>()
            ?? services.GetServices<IChatRuntime>()
                .FirstOrDefault(runtime => string.Equals(runtime.Id, "circleai", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Bounds progress notifications so parallel download chunks cannot flood Android's main looper.</summary>
internal sealed class ModelDownloadProgressThrottle(TimeSpan minimumInterval)
{
    private readonly object _gate = new();
    private long _lastReportTimestamp;

    public bool ShouldReport(ModelDownloadProgress report)
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (report.Ratio >= 1
                || _lastReportTimestamp == 0
                || Stopwatch.GetElapsedTime(_lastReportTimestamp, now) >= minimumInterval)
            {
                _lastReportTimestamp = now;
                return true;
            }

            return false;
        }
    }
}
