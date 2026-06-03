namespace Concierge.Shared.Media;

/// <summary>
/// Host-neutral image-generation surface — mirrors <see cref="Chat.IChatRuntime"/> for image
/// providers. Implementations wrap OpenAI DALL-E, Stability AI, local ONNX/DirectML SDXL,
/// etc. The host registers as many as it wants; the UI walks <c>IEnumerable&lt;IImageRuntime&gt;</c>
/// to build a provider selector.
/// </summary>
public interface IImageRuntime
{
    /// <summary>Short stable id — <c>"openai-images"</c>, <c>"stability"</c>, <c>"sdxl-local"</c>, etc.</summary>
    string Id { get; }

    /// <summary>Display label for the UI selector.</summary>
    string EngineLabel { get; }

    bool IsReady { get; }

    string StatusMessage { get; }

    /// <summary>
    /// Generates one or more images for the prompt. Returns artifact descriptors carrying
    /// either a remote URL (cloud adapters) or an inline byte stream (local generators).
    /// </summary>
    Task<IReadOnlyList<ImageArtifact>> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default);
}

/// <param name="Prompt">Text prompt.</param>
/// <param name="NegativePrompt">Optional negative prompt (Stability supports it; OpenAI ignores).</param>
/// <param name="Size">Square size in pixels — typical values 512, 768, 1024, 1536.</param>
/// <param name="Count">Number of images to produce (1..n).</param>
/// <param name="Style">Optional style preset id (provider-specific).</param>
public sealed record ImageGenerationRequest(
    string Prompt,
    string? NegativePrompt = null,
    int Size = 1024,
    int Count = 1,
    string? Style = null);

/// <param name="RuntimeId">Id of the runtime that produced this image.</param>
/// <param name="Prompt">Verbatim prompt used.</param>
/// <param name="MimeType">Content type — <c>image/png</c>, <c>image/jpeg</c>, etc.</param>
/// <param name="Url">Remote URL if the provider hosts the result; null when <see cref="Bytes"/> is set.</param>
/// <param name="Bytes">Inline payload if the provider returns base64 / binary; null when <see cref="Url"/> is set.</param>
/// <param name="GeneratedAtUtc">Timestamp the image was returned.</param>
public sealed record ImageArtifact(
    string RuntimeId,
    string Prompt,
    string MimeType,
    string? Url,
    byte[]? Bytes,
    DateTimeOffset GeneratedAtUtc);

public sealed class NullImageRuntime : IImageRuntime
{
    public string Id => "null";
    public string EngineLabel => "No image runtime";
    public bool IsReady => false;
    public string StatusMessage => "No image runtime is wired. Configure OpenAI:ApiKey or Stability:ApiKey to enable.";

    public Task<IReadOnlyList<ImageArtifact>> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ImageArtifact>>(Array.Empty<ImageArtifact>());
}
