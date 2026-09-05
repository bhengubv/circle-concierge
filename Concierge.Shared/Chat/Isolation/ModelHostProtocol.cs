using System.Text.Json;
using System.Text.Json.Serialization;

namespace Concierge.Shared.Chat.Isolation;

/// <summary>
/// The line protocol between Concierge and the process that owns the model.
///
/// One JSON object per line, in both directions, over the child's standard
/// input and output. Not a socket and not a named pipe: a pipe that closes
/// when the process dies is exactly the signal this needs, and stdio gives
/// that for free on every platform without a port, a permission, or a
/// listener something else could connect to.
///
/// The messages are deliberately dull. Everything interesting about this
/// design is that the child is allowed to die.
/// </summary>
public static class ModelHostProtocol
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>Sent by the child once, before anything else, so the parent
    /// knows what it is talking to and can show a label.</summary>
    public const string Hello = "hello";

    /// <summary>Parent asks for a reply.</summary>
    public const string Stream = "stream";

    /// <summary>Parent asks the child to stop the current reply.</summary>
    public const string Cancel = "cancel";
}

/// <param name="Op">One of the constants on <see cref="ModelHostProtocol"/>.</param>
/// <param name="Id">Correlates chunks with the request that asked for them.</param>
/// <param name="Turns">The conversation, oldest first.</param>
public sealed record ModelHostRequest(
    string Op,
    int Id = 0,
    IReadOnlyList<ModelHostTurn>? Turns = null);

/// <summary>
/// A turn on the wire.
///
/// Images are base64 here rather than raw bytes because the transport is a
/// line of text. It is the one place in the codebase that encodes them, and
/// the cost is paid only when a picture is actually attached.
/// </summary>
public sealed record ModelHostTurn(
    string Role,
    string Content,
    IReadOnlyList<ModelHostImage>? Images = null);

public sealed record ModelHostImage(string FileName, string MediaType, string Base64);

/// <param name="Id">Which request this belongs to.</param>
/// <param name="Chunk">The next fragment of the reply, if any.</param>
/// <param name="Done">Set when the reply is complete.</param>
/// <param name="Error">Set when the child could not answer. Not a crash — a
/// crash arrives as the pipe closing, which is the case this exists for.</param>
/// <param name="Ready">On a hello: whether the model loaded.</param>
/// <param name="EngineLabel">On a hello: what to call it.</param>
/// <param name="Status">On a hello: the human-readable state.</param>
public sealed record ModelHostResponse(
    int Id = 0,
    string? Chunk = null,
    bool Done = false,
    string? Error = null,
    bool Ready = false,
    string? EngineLabel = null,
    string? Status = null);
