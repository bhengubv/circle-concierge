using Android.Content;
using Android.Content.PM;
using Android.OS;
using CircleAI.Linking;
using CircleAI.Assistant.Lifecycle;
using Concierge.Shared.Devices;
using Concierge.Shared.Caching;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Concierge.Shared.Tools;

namespace Concierge.Platforms.Android;

/// <summary>
/// Thin transport to the standalone Circle AI process, resolved using the
/// installed SDK's shared wire constants rather than the local sample source.
/// One outstanding Binder transaction maximum; timing out a caller must not
/// create another blocked native transaction on every subsequent click.
/// </summary>
public sealed class AndroidCircleAiSharedService : ICircleAiSharedService, ICircleAiSharedServiceLink, ICircleAiServiceChatLink, ICircleAiServiceRequestProgress, IDisposable
{
    public event Action<string>? RequestProgress;
    private const string Package = LinkIpc.HostPackage;
    private readonly object _gate = new();
    private Task<CircleAiSharedServiceSnapshot>? _probe;
    private readonly AsyncSnapshotCache<LinkVerb, LinkRowsReply> _reads = new(12, 65_536,
        reply => reply.Rows.Sum(row => row.Sum(value => (long)value.Length + 16)), reply => reply.Ok);
    public void InvalidateReads() => _reads.Invalidate();

    public async Task<bool> RequestConnectionAsync(CancellationToken cancellationToken = default)
    {
        InvalidateReads();
        try { return await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as MainActivity
                ?? throw new InvalidOperationException("Open Concierge before connecting Circle AI.");
            return activity.RequestCircleAiLinkAsync(cancellationToken);
        }); }
        finally { InvalidateReads(); }
    }

    public Task<CircleAiSharedServiceSnapshot> InspectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<CircleAiSharedServiceSnapshot> probe;
        lock (_gate)
        {
            if (_probe is null || _probe.IsCompleted)
                _probe = Task.Run(InspectCoreAsync);
            probe = _probe;
        }
        return probe.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);
    }

    private async Task<CircleAiSharedServiceSnapshot> InspectCoreAsync()
    {
        var context = global::Android.App.Application.Context;
        string? version = null;
        long? build = null;
        bool installed = false;
        CircleAiSharedServiceSnapshot Result(string status, bool authorized = false,
            IReadOnlyList<CircleAiServiceCapability>? capabilities = null)
            => new(Package, version, build, installed, authorized, status, capabilities ?? []);
        try
        {
            var info = context.PackageManager!.GetPackageInfo(Package, (PackageInfoFlags)0)!;
            installed = true;
            version = info.VersionName;
            build = Build.VERSION.SdkInt >= BuildVersionCodes.P ? info.LongVersionCode : info.VersionCode;

            var response = await ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.Capabilities), default).ConfigureAwait(false);
            if (!response.Ok)
                return Result("Shared service: " + response.Error);
            var capabilities = response.Rows
                .Where(row => row.Count >= 3)
                .Select(row => new CircleAiServiceCapability(row[0], row[1], row[2])).ToArray();
            return Result("Authorized catalogue received. Live readiness is checked separately.",
                authorized: true, capabilities: capabilities);
        }
        catch (PackageManager.NameNotFoundException)
        {
            return Result("Circle AI shared service is not installed on this device.");
        }
        catch (TimeoutException)
        {
            return Result("The shared service did not connect within ten seconds.");
        }
        catch (Exception exception)
        {
            return Result("Shared service connection failed: " + exception.Message);
        }
    }

    private Task<IReadOnlyDictionary<string, string>>? _transaction;
    // Accessed only by the single owned transaction task. Reuse the binding, not answers.
    private Connection? _connection;
    private int _disposed;
    private readonly SemaphoreSlim _requests = new(1, 1);
    internal async Task<LinkRowsReply> ExecuteVerbAsync(LinkVerbRequest request, CancellationToken cancellationToken)
    {
        var lifetime = CircleAiReadCachePolicy.LifetimeFor(request.Verb.ToString());
        if (lifetime > TimeSpan.Zero && request.Query is null && request.Text is null && request.Subject is null && request.Args is null)
        {
            var result = await _reads.GetAsync(request.Verb, lifetime,
                () => ExecuteUncachedVerbAsync(request, CancellationToken.None), cancellationToken);
#if DEBUG
            var metrics = _reads.Metrics;
            global::Android.Util.Log.Info("Concierge.CircleAI.Cache", $"hits={metrics.Hits} loads={metrics.Loads} joined={metrics.Joined} entries={metrics.Entries} contentWeight={metrics.ContentWeight}");
#endif
            return result;
        }
        var invalidates = CircleAiReadCachePolicy.InvalidatesReads(request.Verb.ToString());
        if (invalidates) InvalidateReads();
        try { return await ExecuteUncachedVerbAsync(request, cancellationToken); }
        finally { if (invalidates) InvalidateReads(); }
    }

    private async Task<LinkRowsReply> ExecuteUncachedVerbAsync(LinkVerbRequest request, CancellationToken cancellationToken)
    {
        try
        {
        var fields = await RoundTripAsync(LinkIpc.TransactVerb, LinkVerbCodec.Encode(request), TimeSpan.FromSeconds(12), cancellationToken);
        if (fields.TryGetValue(LinkVerbCodec.KeyOk, out var ok) && ok == "1"
            && (!fields.TryGetValue(LinkVerbCodec.KeyRowCount, out var text)
                || !int.TryParse(text, out var rows) || rows < 0 || rows > fields.Count || rows > 2048))
            throw new InvalidOperationException("The service returned an invalid row count.");
        var result = LinkVerbCodec.DecodeReply(fields);
        if (!result.Ok) InvalidateReads();
        return result;
        }
        catch { InvalidateReads(); throw; }
    }

    public async Task<CircleAiServiceChatStatus> ReadChatStatusAsync(CancellationToken cancellationToken)
    {
        var info = global::Android.App.Application.Context.PackageManager!.GetPackageInfo(Package, (PackageInfoFlags)0)!;
        var version = $"{info.VersionName} build {info.LongVersionCode}";
        var response = await ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.Status), cancellationToken);
        if (!response.Ok) return new(false, response.Error?.Contains("not linked", StringComparison.OrdinalIgnoreCase) == true
            ? "AuthorizationRequired" : "Unavailable", response.Error ?? "Circle AI did not report live readiness.", version);
        var status = CircleAiLiveReadiness.Chat(CircleAiLiveReadiness.Read(response.Rows), version);
        var hello = await ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.Hello), cancellationToken);
        var accepts = CircleAiQueuedChat.IsSupported(hello);
        return status with
        {
            AcceptsQueuedRequests = accepts,
            Explanation = accepts && !status.Ready
                ? status.Explanation + " You can ask now; Circle AI will keep your question and report its progress."
                : status.Explanation
        };
    }

    internal async Task<ReadinessSnapshot> ReadLiveReadinessAsync(CancellationToken cancellationToken)
    {
        var response = await ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.Status), cancellationToken);
        if (!response.Ok) throw new InvalidOperationException(response.Error ?? "Circle AI did not report live readiness.");
        return CircleAiLiveReadiness.Read(response.Rows);
    }

    public async Task<CircleAiServiceChatReply> SendChatAsync(string sessionId, string message, bool agentic,
        CancellationToken cancellationToken)
    {
        try
        {
        var hello = await ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.Hello), cancellationToken);
        if (CircleAiQueuedChat.IsSupported(hello))
        {
            var run = ToolApprovalRunContext.Current
                ?? throw new InvalidOperationException("A durable run is required for Circle AI service.");
            var requestId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{run.RunId:D}\n{sessionId}\n{agentic}\n{message}")));
            async Task<LinkRowsReply> ObserveAskAsync(LinkVerbRequest request, CancellationToken token)
            {
                var result = await ExecuteVerbAsync(request, token);
#if DEBUG
                foreach (var row in result.Rows)
                    if (LinkAskRows.ReadAsk(row) is { } ask)
                        global::Android.Util.Log.Info("Concierge.CircleAI.Ask",
                            $"verb={request.Verb} id={ask.Id} state={ask.StateName} model={ask.ModelId} duplicate={ask.Duplicate} timing={System.Text.Json.JsonSerializer.Serialize(ask.Timing)}");
#endif
                return result;
            }
            return await CircleAiQueuedChat.SendAsync(ObserveAskAsync, requestId, sessionId, message,
                agentic, sentence => RequestProgress?.Invoke(sentence), cancellationToken);
        }
        var fields = await RoundTripAsync(LinkIpc.TransactAsk,
            LinkTurnCodec.Encode(new LinkTurnRequest(sessionId, message, agentic)), TimeSpan.FromMinutes(2), cancellationToken);
        var reply = LinkTurnCodec.DecodeReply(fields);
        if (!reply.Ok) InvalidateReads();
        return new(reply.Ok, reply.Reply, reply.Error);
        }
        catch { InvalidateReads(); throw; }
    }

    private async Task<IReadOnlyDictionary<string, string>> RoundTripAsync(int code,
        IReadOnlyDictionary<string, string> fields, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var roundTrip = Stopwatch.StartNew();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _requests.WaitAsync(timeout, cancellationToken))
            throw new TimeoutException("Circle AI is still handling another request.");
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            // Settings progress and readiness polls share this connection. Queue
            // them rather than misreporting a healthy busy service as unavailable.
            if (_transaction is { IsCompleted: false })
            {
                try { await _transaction.WaitAsync(timeout, cancellationToken); }
                catch when (_transaction.IsCompleted && !cancellationToken.IsCancellationRequested) { }
            }
            var queueMs = roundTrip.Elapsed.TotalMilliseconds;
            _transaction = Task.Run(() => SendCoreAsync(code, fields));
            // Timeout/cancellation stops waiting, not native execution. Never multiply blocked transactions.
            var result = await _transaction.WaitAsync(timeout, cancellationToken);
#if DEBUG
            global::Android.Util.Log.Info("Concierge.CircleAI.IPC", $"kind={(code == LinkIpc.TransactAsk ? "chat" : "metadata")} queueMs={queueMs:F2} totalMs={roundTrip.Elapsed.TotalMilliseconds:F2}");
#endif
            return result;
        }
        finally { _requests.Release(); }
    }

    private async Task<IReadOnlyDictionary<string, string>> SendCoreAsync(int code, IReadOnlyDictionary<string, string> fieldsToSend)
    {
        try
        {
        var context = global::Android.App.Application.Context;
            var bind = Stopwatch.StartNew();
            var reused = _connection?.Binder is { IsBinderAlive: true };
            if (!reused)
            {
            _connection?.Dispose();
            _connection = null;
            using var intent = new Intent(LinkIpc.BindAction);
            intent.SetPackage(Package);
            var service = context.PackageManager.ResolveService(intent, (PackageInfoFlags)0)?.ServiceInfo;
            if (service is null || !service.Exported)
                throw new InvalidOperationException("Installed, but no exported Circle AI link was found.");
            _connection = new Connection(context, InvalidateReads);
            await _connection.BindAsync(intent).ConfigureAwait(false);
            }
            var binder = _connection?.Binder;
            var bindMs = bind.Elapsed.TotalMilliseconds;
            if (binder is null)
                throw new InvalidOperationException("Installed, but the shared service did not connect.");

            using var data = Parcel.Obtain()!;
            using var reply = Parcel.Obtain()!;
            data.WriteInterfaceToken(LinkIpc.Descriptor);
            data.WriteInt(fieldsToSend.Count);
            foreach (var field in fieldsToSend)
            {
                data.WriteString(field.Key);
                data.WriteString(field.Value);
            }
            // Runs on the owned background task, never the Android/UI thread.
            var serviceTime = Stopwatch.StartNew();
            if (!binder.Transact(code, data, reply, (TransactionFlags)0))
                throw new InvalidOperationException("The installed service does not support catalogue discovery.");
            reply.ReadException();
#if DEBUG
            global::Android.Util.Log.Info("Concierge.CircleAI.Transport", $"kind={(code == LinkIpc.TransactAsk ? "chat" : "metadata")} reused={reused} bindMs={bindMs:F2} serviceMs={serviceTime.Elapsed.TotalMilliseconds:F2}");
#endif
            var count = reply.ReadInt();
            if (count is < 0 or > 2048)
                throw new InvalidOperationException("The service returned an invalid catalogue response.");
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < count; index++)
            {
                var key = reply.ReadString();
                var value = reply.ReadString();
                if (key is null || value is null || !fields.TryAdd(key, value))
                    throw new InvalidOperationException("The service returned an invalid catalogue response.");
            }
            return fields;
        }
        catch
        {
            _connection?.Dispose();
            _connection = null;
            InvalidateReads();
            throw; // Never retry a potentially accepted write or question here.
        }
        finally
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                _connection?.Dispose();
                _connection = null;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // The transaction owns its Binder until native execution finishes, even if
        // its caller has stopped waiting. Dispose only after that owned task.
        _ = ReleaseConnectionAsync();
    }

    private async Task ReleaseConnectionAsync()
    {
        await _requests.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_transaction is not null)
                try { await _transaction.ConfigureAwait(false); } catch { }
            _connection?.Dispose();
            _connection = null;
        }
        finally { _requests.Release(); }
    }

    private sealed class Connection(Context context, Action disconnected) : Java.Lang.Object, IServiceConnection
    {
        private IBinder? _binder;
        public IBinder? Binder => Volatile.Read(ref _binder);
        private readonly TaskCompletionSource<IBinder?> _bound = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _registered;
        public async Task<IBinder?> BindAsync(Intent intent)
        {
            _registered = context.BindService(intent, this, Bind.AutoCreate);
            return _registered
                ? await _bound.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)
                : null;
        }
        public void OnServiceConnected(ComponentName? name, IBinder? service)
        { Volatile.Write(ref _binder, service); _bound.TrySetResult(service); }
        private void Disconnected()
        { Volatile.Write(ref _binder, null); _bound.TrySetResult(null); disconnected(); }
        public void OnServiceDisconnected(ComponentName? name) => Disconnected();
        public void OnNullBinding(ComponentName? name) => Disconnected();
        public void OnBindingDied(ComponentName? name) => Disconnected();
        protected override void Dispose(bool disposing)
        {
            if (disposing && _registered)
            {
                _registered = false;
                try { context.UnbindService(this); }
                catch (Java.Lang.IllegalArgumentException) { /* The OS already released the binding. */ }
            }
            base.Dispose(disposing);
        }
    }
}
