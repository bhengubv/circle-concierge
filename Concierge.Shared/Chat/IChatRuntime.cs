namespace Concierge.Shared.Chat;

/// <summary>
/// Host-neutral chat surface that the Razor UI calls. Implementations sit in adapter
/// packages — <c>Concierge.Ai</c> wraps the CircleAI on-device generator; future
/// packages can add BYO-API-key routers (OpenAI, Anthropic, Gemini) without the UI
/// having to know which engine answered.
/// </summary>
public interface IChatRuntime
{
    /// <summary>
    /// Short stable identifier used by the UI for the provider-selector dropdown and
    /// by callers that want to route to a specific runtime (e.g. <c>"circleai"</c>,
    /// <c>"openai"</c>, <c>"anthropic"</c>, <c>"gemini"</c>). Must be unique across
    /// every registered runtime in a host.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Display label for the active engine (e.g. <c>"Qwen3-30B-A3B-Q4 (CircleAI)"</c>).
    /// Reflects the model the runtime resolved at startup. Persisted alongside assistant
    /// messages so the UI can label past turns even after the runtime swaps out.
    /// </summary>
    string EngineLabel { get; }

    /// <summary>
    /// <c>true</c> once the runtime has finished loading. While <c>false</c>, the UI
    /// keeps the composer disabled and shows whatever <see cref="StatusMessage"/> says.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Human-readable status line — "loading model…", "engine offline: file not found",
    /// "ready", etc. Surfaced verbatim in the UI status pill, so avoid jargon.
    /// </summary>
    string StatusMessage { get; }

    /// <summary>
    /// Streams the assistant reply chunk-by-chunk. Each yielded string is the next
    /// fragment to append. Callers concatenate in order and re-render between yields.
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Host-neutral chat turn. Mirrors CircleAI.Inference.ChatMessage so the adapter can
/// translate without leaking the upstream type into UI code.
/// </summary>
/// <param name="Role">"system" / "user" / "assistant".</param>
/// <param name="Content">Text content.</param>
public sealed record ChatTurn(string Role, string Content);

/// <summary>
/// Null implementation used until an adapter is wired. Surfaces an honest "engine
/// offline" message instead of pretending to stream — UI behaviour stays consistent
/// either way.
/// </summary>
public sealed class NullChatRuntime : IChatRuntime
{
    public string Id => "null";

    public string EngineLabel => "No engine wired";

    public bool IsReady => false;

    public string StatusMessage => "No chat engine is wired. Add Concierge.Ai (or another IChatRuntime adapter) to enable conversations.";

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield return StatusMessage;
    }
}
