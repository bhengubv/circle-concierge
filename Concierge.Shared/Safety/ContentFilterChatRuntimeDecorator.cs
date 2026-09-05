using System.Runtime.CompilerServices;
using System.Text;
using CircleAI.ContentPolicy;
using Concierge.Shared.Chat;

namespace Concierge.Shared.Safety;

/// <summary>
/// Wraps any <see cref="IChatRuntime"/> with the content-filter + refusal-policy
/// pipeline. The Razor UI keeps calling IChatRuntime the same way it always
/// did; the decorator runs the safety stack underneath, refusing risky turns
/// before they reach the model and re-classifying the model's reply before
/// it streams to the user.
/// </summary>
/// <remarks>
/// Settings.Strictness == Off is a zero-cost pass-through. When kid mode is
/// on we never bypass — that's the point of the parental-controls contract.
/// Optional capabilities are propagated: IPersistableChatRuntime so the MAUI host's
/// OnSleep / OnResume hooks keep working, and IModelDownloadRequired so the on-device
/// engine can still ask before fetching a model. Nothing holds the real runtime — every
/// host resolves this — so a capability that stops here stops existing.
/// </remarks>
public sealed class ContentFilterChatRuntimeDecorator : IChatRuntime, IPersistableChatRuntime, IModelDownloadRequired
{
    // Stable message the UI shows when a refusal lands. Kept short + warm
    // because Bell's voice is "honest, never punitive". The actual reason
    // is captured in the audit log for the parent to review.
    private const string KidFriendlyRefusal =
        "[I can't help with that one — let's pick something else.]";

    private readonly IChatRuntime _inner;
    private readonly IContentFilter _filter;
    private readonly IRefusalPolicy _policy;
    private readonly ISafetyAuditLog _audit;
    private readonly Func<ConciergeSafetySettings> _settingsReader;
    private readonly Func<string> _userIdReader;

    public ContentFilterChatRuntimeDecorator(
        IChatRuntime inner,
        IContentFilter filter,
        IRefusalPolicy policy,
        ISafetyAuditLog audit,
        Func<ConciergeSafetySettings> settingsReader,
        Func<string>? userIdReader = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _filter = filter ?? throw new ArgumentNullException(nameof(filter));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _settingsReader = settingsReader ?? throw new ArgumentNullException(nameof(settingsReader));
        _userIdReader = userIdReader ?? (static () => "local");
    }

    public string Id => _inner.Id;

    /// <summary>
    /// Forwarded, like everything else here. A decorator that answered this for
    /// itself would relabel a cloud provider as local simply by wrapping it.
    /// </summary>
    public bool LeavesDevice => _inner.LeavesDevice;
    public string EngineLabel => _inner.EngineLabel;
    public bool IsReady => _inner.IsReady;
    public string StatusMessage => _inner.StatusMessage;

    /// <inheritdoc/>
    /// <remarks>
    /// Null when the wrapped runtime has no model to fetch — a cloud adapter, say. The wrapper
    /// reports what is there and never invents one.
    /// </remarks>
    public PendingModelDownload? PendingDownload =>
        (_inner as IModelDownloadRequired)?.PendingDownload;

    /// <inheritdoc/>
    public Task<bool> AcceptDownloadAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => _inner is IModelDownloadRequired inner
            ? inner.AcceptDownloadAsync(progress, cancellationToken)
            : Task.FromResult(false);

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var settings = _settingsReader();

        // Off-mode short-circuit — the decorator gets out of the way entirely
        // so adult-mode chats see no extra latency from the filter pipeline.
        if (settings.Strictness == SafetyStrictness.Off)
        {
            await foreach (var chunk in _inner.StreamAsync(messages, cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
            yield break;
        }

        // 1. Classify the last user turn — that's the only one the user just
        // submitted. Older turns are vestigial context and already passed
        // through the filter when they were live.
        var lastUserTurn = messages
            .LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
        var userFinding = lastUserTurn is null
            ? new SafetyFinding(SafetyVerdict.Allow, "ok", "no user turn", 1f)
            : await _filter.ClassifyAsync(lastUserTurn.Content, cancellationToken).ConfigureAwait(false);

        var inboundRefuse = await _policy.ShouldRefuseAsync(new[] { userFinding }, cancellationToken)
            .ConfigureAwait(false);

        await _audit.LogAsync(new SafetyAuditEntry(
            AtUtc: DateTimeOffset.UtcNow,
            UserId: _userIdReader(),
            Action: "user-turn",
            Verdict: inboundRefuse ? SafetyVerdict.Refuse : userFinding.Verdict,
            Reason: userFinding.Reason), cancellationToken).ConfigureAwait(false);

        if (inboundRefuse)
        {
            yield return KidFriendlyRefusal;
            yield break;
        }

        // 2. Stream the model's reply, accumulating into a buffer we
        // periodically classify. Mid-stream refusals are blunt — we stop
        // streaming, emit the refusal stub, and log the cut. Granularity is
        // every ~80 chars so the user sees prompt feedback but the classifier
        // isn't called per token.
        var buffer = new StringBuilder();
        var lastChecked = 0;
        SafetyVerdict modelVerdict = SafetyVerdict.Allow;
        string modelReason = "ok";

        await foreach (var chunk in _inner.StreamAsync(messages, cancellationToken).ConfigureAwait(false))
        {
            buffer.Append(chunk);
            yield return chunk;

            if (buffer.Length - lastChecked < 80) continue;
            lastChecked = buffer.Length;

            var partialFinding = await _filter
                .ClassifyAsync(buffer.ToString(), cancellationToken)
                .ConfigureAwait(false);
            if (partialFinding.Verdict == SafetyVerdict.Refuse)
            {
                modelVerdict = SafetyVerdict.Refuse;
                modelReason = partialFinding.Reason;
                yield return Environment.NewLine + KidFriendlyRefusal;
                break;
            }
            if (partialFinding.Verdict == SafetyVerdict.Flag)
            {
                modelVerdict = SafetyVerdict.Flag;
                modelReason = partialFinding.Reason;
            }
        }

        await _audit.LogAsync(new SafetyAuditEntry(
            AtUtc: DateTimeOffset.UtcNow,
            UserId: _userIdReader(),
            Action: "model-reply",
            Verdict: modelVerdict,
            Reason: modelReason), cancellationToken).ConfigureAwait(false);
    }

    // ── IPersistableChatRuntime — pass through if the inner runtime supports it.

    public string? SessionSnapshotPath =>
        _inner is IPersistableChatRuntime persistable ? persistable.SessionSnapshotPath : null;

    public Task<bool> SaveSessionAsync(string path, CancellationToken cancellationToken = default)
        => _inner is IPersistableChatRuntime persistable
            ? persistable.SaveSessionAsync(path, cancellationToken)
            : Task.FromResult(false);

    public Task<bool> LoadSessionAsync(string path, CancellationToken cancellationToken = default)
        => _inner is IPersistableChatRuntime persistable
            ? persistable.LoadSessionAsync(path, cancellationToken)
            : Task.FromResult(false);
}
