using System.Text.Json.Serialization;

namespace Concierge.Shared.Session;

/// <summary>
/// What you had, so closing the app does not cost it.
///
/// Deliberately not what was *happening*. A half-finished streaming reply is
/// already checkpointed and recovered into the thread, and that is as far as
/// this goes: an interrupted agentic run is not picked back up. A run that
/// stopped in the middle of asking permission to write a file must not resume
/// itself on next launch, and one that stopped for any other reason has lost
/// the context that made its next step sensible.
///
/// So this restores three things, each because losing it is felt:
///
///   The thread you were in, so launching lands where you left rather than on
///   an empty workspace.
///
///   Text typed and not sent. The most common loss and the cheapest to fix.
///
///   Which skills were on. This one matters most and is the least visible: a
///   skill composes into the system prompt before every turn, so losing it
///   silently changes how the assistant answers, with nothing on screen to say
///   why.
/// </summary>
public interface ISessionState
{
    /// <summary>Reads the last saved state. Never throws — a missing or
    /// unreadable file is an empty session, not a failure to start.</summary>
    Task<SessionSnapshot> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the state. Never throws, for the same reason a draft
    /// checkpoint never throws: losing durability beats losing the app.</summary>
    Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>Forgets everything. Used when a person clears their history.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <param name="LastConversationId">The thread that was open, if any.</param>
/// <param name="ActiveSkillIds">Skills switched on. Order is not meaningful.</param>
/// <param name="UnsentText">
/// Composer text that was typed and never sent, keyed by conversation id so
/// two threads do not overwrite each other's half-written questions.
/// </param>
public sealed record SessionSnapshot(
    Guid? LastConversationId = null,
    IReadOnlyCollection<string>? ActiveSkillIds = null,
    IReadOnlyDictionary<string, string>? UnsentText = null)
{
    public static SessionSnapshot Empty { get; } = new();

    // Ignored, or the serializer writes each of these a second time under its
    // own name: the file came back holding activeSkillIds and skills, and
    // unsentText and unsent, saying the same thing twice.

    [JsonIgnore]
    public IReadOnlyCollection<string> Skills => ActiveSkillIds ?? Array.Empty<string>();

    [JsonIgnore]
    public IReadOnlyDictionary<string, string> Unsent =>
        UnsentText ?? new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The unsent text for one thread, or empty.</summary>
    public string UnsentFor(Guid conversationId)
        => Unsent.TryGetValue(conversationId.ToString("N"), out var text) ? text : string.Empty;
}
