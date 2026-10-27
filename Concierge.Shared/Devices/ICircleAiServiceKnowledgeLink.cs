namespace Concierge.Shared.Devices;

/// <summary>One skill name returned by Circle AI's own skill database.</summary>
public sealed record CircleAiServiceSkill(string Id, string Name);

/// <summary>Read from Circle AI's SQLite-backed skill or long-term memory store.</summary>
public sealed record CircleAiKnowledgeRead(bool Ok, IReadOnlyList<string> Items, string? Error = null);

/// <summary>
/// Optional service-owned knowledge operations. The client never opens or copies
/// Circle AI's private SQLite files; these calls use the service's authorised link.
/// </summary>
public interface ICircleAiServiceKnowledgeLink
{
    Task<bool> RequestAccessAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CircleAiServiceSkill>> SearchSkillsAsync(string? query, CancellationToken cancellationToken);
    Task<CircleAiKnowledgeRead> RecallAsync(string situation, int limit, CancellationToken cancellationToken);
    Task<bool> RememberAsync(string fact, string? subject, CancellationToken cancellationToken);
}

public sealed record CircleAiWakePhrase(string Text, bool Chosen, bool BuiltIn,
    string Quality, string Advice);
public sealed record CircleAiWakePhraseCheck(bool Added, string Quality, string Advice);
public sealed record CircleAiServiceFact(string Key, string Value);
public sealed record CircleAiServiceDecision(long Sequence, string Text);
public sealed record CircleAiServiceAsk(string Id, string SessionId, string Question, string State,
    string Sentence, string? Answer, bool IsFinal, long Sequence, string? ModelId);

/// <summary>Consumer controls exposed by the installed Circle AI link contract.</summary>
public interface ICircleAiServiceControls
{
    Task<bool> RequestFullAccessAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CircleAiWakePhrase>> ReadWakePhrasesAsync(string language, CancellationToken cancellationToken);
    Task<CircleAiWakePhraseCheck> CheckWakePhraseAsync(string phrase, string language, CancellationToken cancellationToken);
    Task AddWakePhraseAsync(string phrase, string language, CancellationToken cancellationToken);
    Task ChooseWakePhraseAsync(string phrase, string language, CancellationToken cancellationToken);
    Task RemoveWakePhraseAsync(string phrase, string language, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> SetResidentListeningAsync(bool enabled, CancellationToken cancellationToken);
    Task CancelSetupAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CircleAiServiceFact>> ReadHealthAsync(CancellationToken cancellationToken);
    Task<CircleAiServiceDecision?> ReadLastDecisionAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CircleAiServiceAsk>> ReadRecentAsksAsync(bool openOnly, CancellationToken cancellationToken);
    Task<CircleAiServiceAsk?> ReadAskAsync(string id, CancellationToken cancellationToken);
    Task CancelAskAsync(string id, CancellationToken cancellationToken);
}

public sealed record CircleAiServiceAudioResult(bool Ok, string? Text, byte[] Audio, string? Error = null);
public interface ICircleAiServiceAudioLink
{
    Task<CircleAiServiceAudioResult> TranscribePcmAsync(byte[] pcm, string? language, CancellationToken cancellationToken);
    Task<CircleAiServiceAudioResult> SpeakAsync(string text, string? language, CancellationToken cancellationToken);
}
