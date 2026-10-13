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
public sealed class AndroidCircleAiSharedService : ICircleAiSharedService, ICircleAiSharedServiceLink
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

    private Task<LinkRowsReply>? _transaction;
    internal async Task<LinkRowsReply> ExecuteVerbAsync(LinkVerbRequest request, CancellationToken cancellationToken)
    {
        Task<LinkRowsReply> transaction;
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_transaction is { IsCompleted: false })
                throw new InvalidOperationException("Circle AI is still answering the previous settings request.");
            _transaction = transaction = Task.Run(() => SendCoreAsync(request));
        }
        // A timed-out native transaction remains owned; subsequent calls cannot multiply it.
        return await transaction.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);
    }

    private static async Task<LinkRowsReply> SendCoreAsync(LinkVerbRequest request)
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
            var fieldsToSend = LinkVerbCodec.Encode(request);
            data.WriteInt(fieldsToSend.Count);
            foreach (var field in fieldsToSend)
            {
                data.WriteString(field.Key);
                data.WriteString(field.Value);
            }
            // Runs on the owned background task, never the Android/UI thread.
            if (!binder.Transact(LinkIpc.TransactVerb, data, reply, (TransactionFlags)0))
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
            if (fields.TryGetValue(LinkVerbCodec.KeyOk, out var ok) && ok == "1"
                && (!fields.TryGetValue(LinkVerbCodec.KeyRowCount, out var rowCountText)
                    || !int.TryParse(rowCountText, out var rowCount)
                    || rowCount < 0 || rowCount > count || rowCount > 2048))
                throw new InvalidOperationException("The service returned an invalid catalogue row count.");
            return LinkVerbCodec.DecodeReply(fields);
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
