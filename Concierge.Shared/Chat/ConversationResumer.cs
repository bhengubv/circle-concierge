namespace Concierge.Shared.Chat;

/// <summary>A conversation brought back after the process ended.</summary>
/// <param name="ConversationId">Which conversation.</param>
/// <param name="History">The turns to continue from.</param>
/// <param name="EngineStateRestored">
/// Whether the engine's cached state came back. False means the next reply pays the prefill
/// cost again — slower, not broken.
/// </param>
/// <param name="Problems">Anything wrong with the log, in plain words. Empty when it is sound.</param>
public sealed record ResumedConversation(
    Guid ConversationId,
    IReadOnlyList<ChatTurn> History,
    bool EngineStateRestored,
    IReadOnlyList<string> Problems);

/// <summary>
/// Puts a conversation back together after a restart: the history from the log, and the
/// engine's cached state where the engine can restore it.
/// </summary>
/// <remarks>
/// <para>
/// Android kills apps. <see cref="IPersistableChatRuntime"/> already snapshots the model's
/// state on sleep; this is the other half, and without it reopening gives you the text beside
/// a cold engine.
/// </para>
/// <para>
/// Two things are deliberately not fatal: an engine that cannot snapshot, and a snapshot that
/// will not load. Both cost the prefill and nothing else. What is not swept aside is a
/// damaged log — that is reported, because a conversation that arrived incomplete must not
/// look whole.
/// </para>
/// </remarks>
public sealed class ConversationResumer
{
    private readonly IConversationStore _store;
    private readonly IChatRuntime _runtime;

    public ConversationResumer(IConversationStore store, IChatRuntime runtime)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <summary>Bring one conversation back.</summary>
    /// <exception cref="InvalidOperationException">No such conversation.</exception>
    public async Task<ResumedConversation> ResumeAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var conversation = await _store.GetAsync(conversationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Conversation {conversationId} not found.");

        var events = await _store.ReadEventsAsync(conversationId, cancellationToken).ConfigureAwait(false);
        var problems = ConversationLogInvariants.Check(events).Problems;
        var history = await _store.DeriveMessagesAsync(conversationId, cancellationToken).ConfigureAwait(false);

        var restored = await TryRestoreEngineStateAsync(cancellationToken).ConfigureAwait(false);

        return new ResumedConversation(conversation.Id, history, restored, problems);
    }

    private async Task<bool> TryRestoreEngineStateAsync(CancellationToken cancellationToken)
    {
        if (_runtime is not IPersistableChatRuntime persistable || persistable.SessionSnapshotPath is not { } path)
        {
            // A cloud adapter holds no in-process state, so there is nothing to bring back.
            return false;
        }

        try
        {
            return await persistable.LoadSessionAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The snapshot is an optimisation. A corrupt or missing one costs the prefill;
            // it must never cost the conversation.
            return false;
        }
    }
}
