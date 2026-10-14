using Android.Content;
using Android.Content.PM;
using Android.OS;
using CircleAI.Linking;
using Concierge.Shared.Devices;

namespace Concierge.Platforms.Android;

/// <summary>
/// Thin transport to the standalone Circle AI process, resolved using the
/// installed SDK's shared wire constants rather than the local sample source.
/// One outstanding Binder transaction maximum; timing out a caller must not
/// create another blocked native transaction on every subsequent click.
/// </summary>
public sealed class AndroidCircleAiSharedService : ICircleAiSharedService, ICircleAiSharedServiceLink, ICircleAiServiceChatLink
{
    private const string Package = LinkIpc.HostPackage;
    private readonly object _gate = new();
    private Task<CircleAiSharedServiceSnapshot>? _probe;

    public Task<bool> RequestConnectionAsync(CancellationToken cancellationToken = default)
        => MainThread.InvokeOnMainThreadAsync(() =>
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as MainActivity
                ?? throw new InvalidOperationException("Open Concierge before connecting Circle AI.");
            return activity.RequestCircleAiLinkAsync(cancellationToken);
        });

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
            return Result("Authorized catalogue received. Model readiness is not reported by this link.",
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
    private readonly SemaphoreSlim _requests = new(1, 1);
    internal async Task<LinkRowsReply> ExecuteVerbAsync(LinkVerbRequest request, CancellationToken cancellationToken)
    {
        var fields = await RoundTripAsync(LinkIpc.TransactVerb, LinkVerbCodec.Encode(request), TimeSpan.FromSeconds(12), cancellationToken);
        if (fields.TryGetValue(LinkVerbCodec.KeyOk, out var ok) && ok == "1"
            && (!fields.TryGetValue(LinkVerbCodec.KeyRowCount, out var text)
                || !int.TryParse(text, out var rows) || rows < 0 || rows > fields.Count || rows > 2048))
            throw new InvalidOperationException("The service returned an invalid row count.");
        return LinkVerbCodec.DecodeReply(fields);
    }

    public async Task<CircleAiServiceChatStatus> ReadChatStatusAsync(CancellationToken cancellationToken)
    {
        var setup = await ReadSetupStatusAsync(cancellationToken);
        if (setup.Ready || setup.Stage is "AuthorizationRequired" or "Unavailable") return setup;
        var abilities = await ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.Abilities), cancellationToken);
        if (abilities.Ok && CircleAiServiceReadiness.TextAnsweringIsOn(abilities.Rows))
            return new(true, "TextReady", "Circle AI can answer here. Voice setup is still incomplete.", setup.Version);
        return setup;
    }

    internal async Task<CircleAiServiceChatStatus> ReadSetupStatusAsync(CancellationToken cancellationToken)
    {
        var response = await ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.SetupReadiness), cancellationToken);
        var info = global::Android.App.Application.Context.PackageManager!.GetPackageInfo(Package, (PackageInfoFlags)0)!;
        var version = $"{info.VersionName} build {info.LongVersionCode}";
        if (!response.Ok) return new(false, response.Error?.Contains("not linked", StringComparison.OrdinalIgnoreCase) == true
            ? "AuthorizationRequired" : "Unavailable", response.Error ?? "Circle AI did not report readiness.", version);
        var row = response.Rows.FirstOrDefault();
        if (row is null || row.Count < 4 || row[3] is not ("0" or "1"))
            throw new InvalidOperationException("Circle AI returned an invalid readiness report.");
        return new(row[3] == "1", row[0], string.Join(" ", row.Skip(1).Take(2)), version);
    }

    public async Task<CircleAiServiceChatReply> SendChatAsync(string sessionId, string message, bool agentic,
        CancellationToken cancellationToken)
    {
        var fields = await RoundTripAsync(LinkIpc.TransactAsk,
            LinkTurnCodec.Encode(new LinkTurnRequest(sessionId, message, agentic)), TimeSpan.FromMinutes(2), cancellationToken);
        var reply = LinkTurnCodec.DecodeReply(fields);
        return new(reply.Ok, reply.Reply, reply.Error);
    }

    private async Task<IReadOnlyDictionary<string, string>> RoundTripAsync(int code,
        IReadOnlyDictionary<string, string> fields, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _requests.WaitAsync(timeout, cancellationToken))
            throw new TimeoutException("Circle AI is still handling another request.");
        try
        {
            // Settings progress and readiness polls share this connection. Queue
            // them rather than misreporting a healthy busy service as unavailable.
            if (_transaction is { IsCompleted: false })
            {
                try { await _transaction.WaitAsync(timeout, cancellationToken); }
                catch when (_transaction.IsCompleted && !cancellationToken.IsCancellationRequested) { }
            }
            _transaction = Task.Run(() => SendCoreAsync(code, fields));
            // Timeout/cancellation stops waiting, not native execution. Never multiply blocked transactions.
            return await _transaction.WaitAsync(timeout, cancellationToken);
        }
        finally { _requests.Release(); }
    }

    private static async Task<IReadOnlyDictionary<string, string>> SendCoreAsync(int code, IReadOnlyDictionary<string, string> fieldsToSend)
    {
        var context = global::Android.App.Application.Context;
            using var intent = new Intent(LinkIpc.BindAction);
            intent.SetPackage(Package);
            var service = context.PackageManager.ResolveService(intent, (PackageInfoFlags)0)?.ServiceInfo;
            if (service is null || !service.Exported)
                throw new InvalidOperationException("Installed, but no exported Circle AI link was found.");
            using var connection = new Connection(context);
            var binder = await connection.BindAsync(intent).ConfigureAwait(false);
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
            if (!binder.Transact(code, data, reply, (TransactionFlags)0))
                throw new InvalidOperationException("The installed service does not support catalogue discovery.");
            reply.ReadException();
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

    private sealed class Connection(Context context) : Java.Lang.Object, IServiceConnection
    {
        private readonly TaskCompletionSource<IBinder?> _bound = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _registered;
        public async Task<IBinder?> BindAsync(Intent intent)
        {
            _registered = context.BindService(intent, this, Bind.AutoCreate);
            return _registered
                ? await _bound.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)
                : null;
        }
        public void OnServiceConnected(ComponentName? name, IBinder? service) => _bound.TrySetResult(service);
        public void OnServiceDisconnected(ComponentName? name) => _bound.TrySetResult(null);
        public void OnNullBinding(ComponentName? name) => _bound.TrySetResult(null);
        public void OnBindingDied(ComponentName? name) => _bound.TrySetResult(null);
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
