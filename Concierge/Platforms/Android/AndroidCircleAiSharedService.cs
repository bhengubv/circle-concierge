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
public sealed class AndroidCircleAiSharedService : ICircleAiSharedService, ICircleAiSharedServiceLink, ICircleAiServiceChatLink, ICircleAiServiceStreamingChatLink, ICircleAiServiceKnowledgeLink, ICircleAiServiceControls, ICircleAiServiceAudioLink, ICircleAiServiceRequestProgress, IDisposable
{
    public event Action<string>? RequestProgress;
    private const string Package = LinkIpc.HostPackage;
    private readonly object _gate = new();
    private Task<CircleAiSharedServiceSnapshot>? _probe;
    private readonly AsyncSnapshotCache<LinkVerb, LinkRowsReply> _reads = new(12, 65_536,
        reply => reply.Rows.Sum(row => row.Sum(value => (long)value.Length + 16)), reply => reply.Ok);
    private volatile bool _voiceAuthorized;
    public bool VoiceAuthorized => _voiceAuthorized;
    public void InvalidateReads() => _reads.Invalidate();

    public Task<bool> RequestConnectionAsync(CancellationToken cancellationToken = default)
        => RequestFullAccessAsync(cancellationToken);

    private static readonly LinkScope ConsumerScopes = LinkScope.Chat | LinkScope.Memory | LinkScope.Skills
        | LinkScope.Voice | LinkScope.Setup | LinkScope.Background;

    public async Task<bool> RequestFullAccessAsync(CancellationToken cancellationToken)
    {
        var approved = await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as MainActivity
                ?? throw new InvalidOperationException("Open Concierge before connecting Circle AI.");
            return await activity.RequestCircleAiLinkAsync(cancellationToken, ConsumerScopes);
        });
        _voiceAuthorized = approved;
        InvalidateReads();
        return approved;
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
    private Task<CircleAiServiceAudioResult>? _audioTransaction;
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

    public Task<CircleAiServiceChatReply> SendChatAsync(string sessionId, string message, bool agentic,
        CancellationToken cancellationToken) => SendChatCoreAsync(sessionId, message, agentic, null, cancellationToken);

    public Task<CircleAiServiceChatReply> SendChatStreamingAsync(string sessionId, string message, bool agentic,
        Action<string> answerChunk, CancellationToken cancellationToken) =>
        SendChatCoreAsync(sessionId, message, agentic, answerChunk, cancellationToken);

    private async Task<CircleAiServiceChatReply> SendChatCoreAsync(string sessionId, string message, bool agentic,
        Action<string>? answerChunk, CancellationToken cancellationToken)
    {
        try
        {
        var hello = await ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.Hello), cancellationToken);
        if (CircleAiQueuedChat.IsSupported(hello))
        {
            var run = ToolApprovalRunContext.Current
                ?? throw new InvalidOperationException("A durable run is required for Circle AI service.");
            // Project and workspace runs should keep going if their foreground
            // surface is left. Plain chat keeps the interactive priority lane.
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
                agentic, sentence => RequestProgress?.Invoke(sentence), cancellationToken, answerChunk,
                background: run.Background);
        }
        var fields = await RoundTripAsync(LinkIpc.TransactAsk,
            LinkTurnCodec.Encode(new LinkTurnRequest(sessionId, message, agentic)), TimeSpan.FromMinutes(2), cancellationToken);
        var reply = LinkTurnCodec.DecodeReply(fields);
        if (!reply.Ok) InvalidateReads();
        return new(reply.Ok, reply.Reply, reply.Error);
        }
        catch { InvalidateReads(); throw; }
    }

    public async Task<IReadOnlyList<CircleAiServiceSkill>> SearchSkillsAsync(string? query,
        CancellationToken cancellationToken)
    {
        var response = await ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.Skills, Query: query), cancellationToken);
        if (!response.Ok) throw new InvalidOperationException(response.Error ?? "Circle AI skill search failed.");
        return response.Rows.Where(row => row.Count >= 2 && !string.IsNullOrWhiteSpace(row[0]))
            .Select(row => new CircleAiServiceSkill(row[0], row[1]))
            .ToArray();
    }

    public Task<bool> RequestAccessAsync(CancellationToken cancellationToken)
        => RequestFullAccessAsync(cancellationToken);

    public async Task<IReadOnlyList<CircleAiWakePhrase>> ReadWakePhrasesAsync(string language, CancellationToken cancellationToken)
    {
        var reply = await ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.WakePhrasesFor,
            Args: new Dictionary<string, string> { ["language"] = language }), cancellationToken);
        EnsureOk(reply, "Circle AI wake phrases could not be read.");
        _voiceAuthorized = true;
        return reply.Rows.Where(row => row.Count >= 5).Select(row => new CircleAiWakePhrase(
            row[0], IsTrue(row[1]), IsTrue(row[2]), row[3], row[4])).ToArray();
    }

    public async Task RefreshVoiceAccessAsync(CancellationToken cancellationToken)
    {
        try { await ReadWakePhrasesAsync(System.Globalization.CultureInfo.CurrentUICulture.Name, cancellationToken); }
        catch (global::System.OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { _voiceAuthorized = false; }
    }

    public async Task<CircleAiWakePhraseCheck> CheckWakePhraseAsync(string phrase, string language, CancellationToken cancellationToken)
    {
        var reply = await ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.WakePhraseCheck, Text: phrase,
            Args: new Dictionary<string, string> { ["language"] = language, ["phrase"] = phrase }), cancellationToken);
        EnsureOk(reply, "Circle AI could not check that wake phrase.");
        var row = reply.Rows.FirstOrDefault() ?? throw new InvalidOperationException("Circle AI returned no wake phrase verdict.");
        if (row.Count < 3) throw new InvalidOperationException("Circle AI returned an incomplete wake phrase verdict.");
        return new(IsTrue(row[0]), row[1], row[2]);
    }

    public Task AddWakePhraseAsync(string phrase, string language, CancellationToken cancellationToken) =>
        RunWakePhraseVerbAsync(LinkVerb.WakePhraseAdd, phrase, language, cancellationToken);

    public Task ChooseWakePhraseAsync(string phrase, string language, CancellationToken cancellationToken) =>
        RunWakePhraseVerbAsync(LinkVerb.WakePhraseChoose, phrase, language, cancellationToken);

    public Task RemoveWakePhraseAsync(string phrase, string language, CancellationToken cancellationToken) =>
        RunWakePhraseVerbAsync(LinkVerb.WakePhraseRemove, phrase, language, cancellationToken);

    private async Task RunWakePhraseVerbAsync(LinkVerb verb, string phrase, string language, CancellationToken cancellationToken)
    {
        var reply = await ExecuteVerbAsync(new LinkVerbRequest(verb, Text: phrase,
            Args: new Dictionary<string, string> { ["language"] = language, ["phrase"] = phrase }), cancellationToken);
        EnsureOk(reply, $"Circle AI could not {verb.ToString().Replace("WakePhrase", "wake phrase ", StringComparison.Ordinal).ToLowerInvariant()}.");
    }

    public async Task<IReadOnlyList<string>> SetResidentListeningAsync(bool enabled, CancellationToken cancellationToken)
    {
        var reply = await ExecuteVerbAsync(new LinkVerbRequest(enabled ? LinkVerb.ResidentStart : LinkVerb.ResidentStop), cancellationToken);
        EnsureOk(reply, enabled ? "Circle AI could not start listening." : "Circle AI could not stop listening.");
        return reply.Rows.FirstOrDefault() ?? [];
    }

    public async Task CancelSetupAsync(CancellationToken cancellationToken)
    {
        var reply = await ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.SetupCancel), cancellationToken);
        EnsureOk(reply, "Circle AI could not cancel setup.");
    }

    public async Task<IReadOnlyList<CircleAiServiceFact>> ReadHealthAsync(CancellationToken cancellationToken)
    {
        var reply = await ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.Health), cancellationToken);
        EnsureOk(reply, "Circle AI health could not be read.");
        return reply.Rows.Where(row => row.Count >= 3)
            .Select(row => new CircleAiServiceFact(row[1], row[2])).ToArray();
    }

    public async Task<CircleAiServiceDecision?> ReadLastDecisionAsync(CancellationToken cancellationToken)
    {
        var reply = await ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.Verdict), cancellationToken);
        EnsureOk(reply, "Circle AI’s last decision could not be read.");
        var row = reply.Rows.FirstOrDefault();
        return row is { Count: >= 2 } && long.TryParse(row[0], out var sequence)
            ? new(sequence, row[1]) : null;
    }

    public async Task<IReadOnlyList<CircleAiServiceAsk>> ReadRecentAsksAsync(bool openOnly, CancellationToken cancellationToken)
    {
        var reply = await ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.AskList,
            Args: new Dictionary<string, string> { [LinkAskRows.ArgOpenOnly] = openOnly ? "1" : "0" }), cancellationToken);
        EnsureOk(reply, "Circle AI request history could not be read.");
        return reply.Rows.Select(ReadAsk).Where(ask => ask is not null).Cast<CircleAiServiceAsk>().ToArray();
    }

    public async Task<CircleAiServiceAsk?> ReadAskAsync(string id, CancellationToken cancellationToken)
    {
        var reply = await ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.AskStatus, Query: id,
            Args: new Dictionary<string, string>
            {
                [LinkAskRows.ArgSince] = "0",
                [LinkAskRows.ArgAfterSeq] = "0",
                [LinkAskRows.ArgWaitMs] = "0"
            }), cancellationToken);
        EnsureOk(reply, "Circle AI request status could not be read.");
        return reply.Rows.Select(ReadAsk).FirstOrDefault(ask => ask is not null);
    }

    public async Task CancelAskAsync(string id, CancellationToken cancellationToken)
    {
        var reply = await ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.AskCancel, Query: id), cancellationToken);
        EnsureOk(reply, "Circle AI request could not be cancelled.");
    }

    private static CircleAiServiceAsk? ReadAsk(IReadOnlyList<string> row)
    {
        var ask = LinkAskRows.ReadAsk(row);
        return ask is null ? null : new(ask.Id, "", "Circle AI request",
            ask.StateName ?? ask.State.ToString(), ask.Sentence ?? "", ask.Text, ask.IsFinal, ask.Seq, ask.ModelId);
    }

    public Task<CircleAiServiceAudioResult> TranscribePcmAsync(byte[] pcm, string? language, CancellationToken cancellationToken) =>
        ExecuteAudioAsync(new LinkAudioRequest(LinkAudioVerb.Transcribe, pcm, "", language), cancellationToken);

    public Task<CircleAiServiceAudioResult> SpeakAsync(string text, string? language, CancellationToken cancellationToken) =>
        ExecuteAudioAsync(new LinkAudioRequest(LinkAudioVerb.Speak, [], text, language), cancellationToken);

    private async Task<CircleAiServiceAudioResult> ExecuteAudioAsync(LinkAudioRequest request, CancellationToken cancellationToken)
    {
        var byteCount = request.Audio?.Length ?? 0;
        if (!LinkAudio.Fits(byteCount, out var why)) return new(false, null, [], why);
        if (!await _requests.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken))
            throw new TimeoutException("Circle AI is still handling another request.");
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_transaction is { IsCompleted: false })
                try { await _transaction.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
                catch when (_transaction.IsCompleted && !cancellationToken.IsCancellationRequested) { }
            if (_audioTransaction is { IsCompleted: false })
                try { await _audioTransaction.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
                catch when (_audioTransaction.IsCompleted && !cancellationToken.IsCancellationRequested) { }
            var context = global::Android.App.Application.Context;
            if (_connection?.Binder is not { IsBinderAlive: true })
            {
                _connection?.Dispose(); _connection = null;
                using var intent = new Intent(LinkIpc.BindAction); intent.SetPackage(Package);
                var service = context.PackageManager!.ResolveService(intent, (PackageInfoFlags)0)?.ServiceInfo;
                if (service is null || !service.Exported) throw new InvalidOperationException("Circle AI audio link is unavailable.");
                _connection = new Connection(context, InvalidateReads);
                await _connection.BindAsync(intent).ConfigureAwait(false);
            }
            var binder = _connection?.Binder ?? throw new InvalidOperationException("Circle AI audio link did not connect.");
            _audioTransaction = Task.Run(() =>
            {
                using var data = Parcel.Obtain()!; using var reply = Parcel.Obtain()!;
                var writer = new ParcelAudioWriter(data);
                if (!LinkAudioCodec.TryWriteRequest(writer, request, out var encodeError))
                    throw new InvalidOperationException(encodeError ?? "Circle AI refused the audio size.");
                if (!binder.Transact(LinkIpc.TransactAudio, data, reply, (TransactionFlags)0))
                    throw new InvalidOperationException("The installed Circle AI service does not support audio requests.");
                reply.ReadException();
                var result = LinkAudioCodec.ReadReply(new ParcelAudioReader(reply));
                return new CircleAiServiceAudioResult(result.Ok, result.Text, result.Audio ?? [], result.Error);
            }, cancellationToken);
            return await _audioTransaction.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
        }
        finally { _requests.Release(); }
    }

    private sealed class ParcelAudioWriter(Parcel parcel) : LinkAudioCodec.IWriter
    {
        public void WriteInt(int value) => parcel.WriteInt(value);
        public void WriteString(string? value) => parcel.WriteString(value);
        public void WriteBytes(byte[] value) => parcel.WriteByteArray(value);
    }
    private sealed class ParcelAudioReader(Parcel parcel) : LinkAudioCodec.IReader
    {
        public int ReadInt() => parcel.ReadInt();
        public string? ReadString() => parcel.ReadString();
        public byte[] ReadBytes() => parcel.CreateByteArray() ?? [];
    }

    private static bool IsTrue(string value) => value is "1" or "true" or "True" or "yes" or "Yes";
    private static void EnsureOk(LinkRowsReply reply, string fallback)
    { if (!reply.Ok) throw new InvalidOperationException(reply.Error ?? fallback); }

    public async Task<CircleAiKnowledgeRead> RecallAsync(string situation, int limit,
        CancellationToken cancellationToken)
    {
        var response = await ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.Recall,
            Query: situation, Limit: Math.Clamp(limit, 1, 8)), cancellationToken);
        return new(response.Ok,
            response.Rows.Where(row => row.Count > 0 && !string.IsNullOrWhiteSpace(row[0]))
                .Select(row => row[0]).ToArray(),
            response.Error);
    }

    public async Task<bool> RememberAsync(string fact, string? subject,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fact)) return false;
        var response = await ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.Remember,
            Text: fact.Trim(), Subject: string.IsNullOrWhiteSpace(subject) ? null : subject.Trim()), cancellationToken);
        return response.Ok;
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
            if (_audioTransaction is { IsCompleted: false })
            {
                try { await _audioTransaction.WaitAsync(timeout, cancellationToken); }
                catch when (_audioTransaction.IsCompleted && !cancellationToken.IsCancellationRequested) { }
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
            if (_audioTransaction is not null)
                try { await _audioTransaction.ConfigureAwait(false); } catch { }
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
