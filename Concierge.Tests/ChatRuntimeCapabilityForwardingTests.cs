using Concierge.Shared.Chat;
using Concierge.Shared.Safety;
using CircleAI.ContentPolicy;

namespace Concierge.Tests;

/// <summary>
/// That wrapping a runtime for safety does not hide what it can do.
/// </summary>
/// <remarks>
/// Every host wraps the chat runtime in the content filter, so the UI never holds the real
/// runtime — it holds the decorator. A capability the decorator does not forward is a capability
/// that does not exist in the shipped app, however well it is tested on its own. That is exactly
/// what happened to the model-download offer: the runtime reported one, the decorator swallowed
/// it, and the desktop app sat at "needs a 21 GB download" with no way to say yes.
/// </remarks>
public sealed class ChatRuntimeCapabilityForwardingTests
{
    [Fact]
    public void A_wrapped_runtime_that_needs_a_download_still_says_so()
    {
        var decorated = Wrap(new NeedsDownloadRuntime());

        Assert.IsAssignableFrom<IModelDownloadRequired>(decorated);
        Assert.NotNull(((IModelDownloadRequired)decorated).PendingDownload);
    }

    [Fact]
    public void The_pending_download_is_the_inner_runtimes_own()
    {
        var inner = new NeedsDownloadRuntime();

        var pending = ((IModelDownloadRequired)Wrap(inner)).PendingDownload;

        Assert.Equal(inner.Pending, pending);
    }

    [Fact]
    public void Accepting_through_the_wrapper_reaches_the_runtime()
    {
        var inner = new NeedsDownloadRuntime();

        Assert.True(((IModelDownloadRequired)Wrap(inner)).AcceptDownloadAsync().Result);
        Assert.Equal(1, inner.Accepted);
    }

    [Fact]
    public void Wrapping_a_runtime_with_nothing_to_download_offers_nothing()
    {
        // A cloud runtime has no model to fetch. The wrapper must not invent one.
        Assert.Null(((IModelDownloadRequired)Wrap(new NullChatRuntime())).PendingDownload);
    }

    [Fact]
    public async Task Accepting_a_download_that_does_not_exist_is_refused_not_thrown()
    {
        Assert.False(await ((IModelDownloadRequired)Wrap(new NullChatRuntime())).AcceptDownloadAsync());
    }

    private static IChatRuntime Wrap(IChatRuntime inner) => new ContentFilterChatRuntimeDecorator(
        inner,
        new ConciergeContentFilter(() => SafetyStrictness.Off),
        new ConciergeRefusalPolicy(),
        new DiscardingAuditLog(),
        () => new ConciergeSafetySettings());

    private sealed class DiscardingAuditLog : ISafetyAuditLog
    {
        public string BackendId => "discard";

        public ValueTask LogAsync(SafetyAuditEntry entry, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<SafetyAuditEntry>> ReadAsync(
            string? userId, int limit = 100, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<SafetyAuditEntry>>([]);
    }

    private sealed class NeedsDownloadRuntime : IChatRuntime, IModelDownloadRequired
    {
        public PendingModelDownload Pending { get; } = new("Some-Model", 21_000_000_000, true);
        public int Accepted { get; private set; }

        public string Id => "needs-download";
        public string EngineLabel => "Some-Model";
        public bool IsReady => false;
        public string StatusMessage => "Some-Model needs a 19.6 GB download before it can run.";

        PendingModelDownload? IModelDownloadRequired.PendingDownload => Pending;

        public Task<bool> AcceptDownloadAsync(
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Accepted++;
            return Task.FromResult(true);
        }

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return StatusMessage;
        }
    }
}
