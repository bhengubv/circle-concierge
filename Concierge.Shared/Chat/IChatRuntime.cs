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
/// <param name="Images">
/// Pictures attached to this turn, or null. Optional so every existing caller
/// and every text-only runtime is unaffected: a runtime that cannot see simply
/// never reads it.
/// </param>
public sealed record ChatTurn(string Role, string Content, IReadOnlyList<ChatImage>? Images = null);

/// <summary>
/// A picture attached to a turn.
///
/// Carried as bytes rather than a path or a data URI: the runtimes encode it
/// differently — one wants base64 in JSON, another wants a byte array — and a
/// data URI would mean every runtime that does not want one has to parse it
/// back out.
/// </summary>
/// <param name="FileName">What it was called, for the transcript.</param>
/// <param name="MediaType">e.g. <c>image/jpeg</c>. Sent to providers verbatim.</param>
/// <param name="Bytes">The image itself.</param>
public sealed record ChatImage(string FileName, string MediaType, byte[] Bytes);

/// <summary>
/// Optional capability for runtimes that can look at a picture.
///
/// Optional, and checked rather than assumed, because most cannot: the model
/// that ships with Concierge runs on the device and is text-only. A UI that
/// attaches an image regardless produces a prompt full of decoded JPEG bytes,
/// which is what this codebase did — every attachment was folded into the
/// prompt with Encoding.UTF8.GetString, pictures included.
///
/// A runtime that does not implement this is not asked to look at anything,
/// and the person is told plainly instead.
/// </summary>
public interface IVisionCapableRuntime
{
    /// <summary>Media types it can accept, e.g. image/jpeg and image/png.</summary>
    IReadOnlyCollection<string> SupportedImageMediaTypes { get; }
}

/// <summary>
/// Optional capability for chat runtimes whose backend supports snapshotting
/// the in-memory conversation state (KV cache + history) to disk. The MAUI
/// host calls <see cref="SaveSessionAsync"/> in its <c>OnSleep</c> hook so the
/// active conversation survives an Android OOM kill, and <see cref="LoadSessionAsync"/>
/// in <c>OnResume</c> to skip the prefill cost on wake-up.
/// </summary>
/// <remarks>
/// Cloud adapters (OpenAI / Anthropic / Gemini) have no in-process state to
/// snapshot, so they will not implement this interface. The host detects
/// support via a pattern-match on the active <see cref="IChatRuntime"/>; runtimes
/// that don't implement <see cref="IPersistableChatRuntime"/> are skipped and
/// conversation lives entirely in the SQLite store (the existing behaviour).
/// </remarks>
public interface IPersistableChatRuntime
{
    /// <summary>
    /// Default snapshot path the host should pass to
    /// <see cref="SaveSessionAsync"/> / <see cref="LoadSessionAsync"/> when it
    /// has no opinion of its own. The implementation owns this so the host
    /// doesn't need to know about per-adapter folder conventions. Null when
    /// snapshotting is disabled by configuration.
    /// </summary>
    string? SessionSnapshotPath { get; }

    /// <summary>
    /// Serialise the runtime's in-memory session (KV cache + token history) to
    /// <paramref name="path"/>. Returns <c>true</c> on success. Implementations
    /// must be safe to call from a lifecycle thread and should never throw —
    /// surface the failure as <c>false</c> instead.
    /// </summary>
    Task<bool> SaveSessionAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hydrate the runtime from a previously-saved snapshot. Returns <c>true</c>
    /// on success. Same no-throw contract as <see cref="SaveSessionAsync"/> —
    /// a corrupt or missing snapshot falls back to a cold session.
    /// </summary>
    Task<bool> LoadSessionAsync(string path, CancellationToken cancellationToken = default);
}

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

/// <summary>
/// Optional capability for chat runtimes that pick their own model and may find it is not
/// on the device yet. Lets a surface offer the download instead of showing a dead status line.
/// </summary>
/// <remarks>
/// <para>
/// Concierge selects a model from the device, not from a config file, because it has to run on
/// whatever handset or server somebody already owns. The consequence is that the runtime, not
/// the person, is the first to know a multi-gigabyte file is missing — and it must not act on
/// that alone. The desktop-tier bundle is 21 GB; on a mid-range phone over mobile data that is
/// somebody's month.
/// </para>
/// <para>
/// A runtime that ships with its model, or reaches one over the network, has nothing to
/// implement here. The UI pattern-matches and shows nothing when the match fails.
/// </para>
/// </remarks>
public interface IModelDownloadRequired
{
    /// <summary>
    /// The download standing between the runtime and being ready, or <c>null</c> when there
    /// isn't one — either because the model is already present or because loading has not run.
    /// </summary>
    PendingModelDownload? PendingDownload { get; }

    /// <summary>
    /// Fetch <see cref="PendingDownload"/> and finish loading. Call only once somebody has
    /// agreed to it. Returns <c>false</c> rather than throwing when it does not work out;
    /// <see cref="IChatRuntime.StatusMessage"/> carries the reason.
    /// </summary>
    /// <param name="progress">Where it is up to. Reported often enough to show movement.</param>
    /// <param name="cancellationToken">
    /// Stops the transfer. Honoured mid-download, not only between steps — a person who has
    /// changed their mind about 21 GB needs it to stop now, not at the end.
    /// </param>
    Task<bool> AcceptDownloadAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Where a model download has got to.
/// </summary>
/// <param name="Ratio">Completion, 0 to 1. Zero when the total size is not yet known.</param>
/// <param name="Description">
/// A line fit to put straight on screen — what is being fetched, how much of it, how fast, how
/// long is left. A bare fraction is indistinguishable from a hang at the slow end of a
/// connection, and the slow end is where the people this is built for are.
/// </param>
public sealed record ModelDownloadProgress(double Ratio, string Description);

/// <summary>
/// A model the runtime wants but does not have.
/// </summary>
/// <param name="ModelId">What was selected, e.g. <c>"Qwen3.6-35B-A3B-MNN"</c>.</param>
/// <param name="Bytes">How big it is. The whole decision, so it is not optional.</param>
/// <param name="FitsThisDevice">
/// <c>false</c> when nothing in the catalogue fits and this is the least-bad option — worth
/// saying out loud before somebody spends an hour downloading something that will run badly.
/// </param>
public sealed record PendingModelDownload(string ModelId, long Bytes, bool FitsThisDevice)
{
    /// <summary>Size in GB, for showing to a person.</summary>
    public double Gigabytes => Bytes / 1024d / 1024d / 1024d;
}
