using System.Runtime.CompilerServices;

namespace Concierge.Chat.Cloud;

/// <summary>
/// Minimal SSE parser shared by every cloud provider. Reads <c>data: …</c> frames from a
/// streaming HTTP body and yields each frame's payload (the text after the <c>data: </c>
/// prefix). Each provider then decides what to do with that payload — OpenAI and Anthropic
/// emit JSON-per-frame, Gemini emits a continuous JSON stream chunked into SSE frames.
/// </summary>
internal static class ServerSentEventsReader
{
    /// <summary>
    /// Yields the payload of every <c>data:</c> frame in <paramref name="source"/>. Frames
    /// containing the OpenAI/Anthropic sentinel <c>[DONE]</c> terminate the stream.
    /// </summary>
    public static async IAsyncEnumerable<string> ReadFramesAsync(
        Stream source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(source);
        // Drive the loop off ReadLineAsync's null sentinel rather than EndOfStream — the latter
        // does a sync read under the hood (CA2024) and stalls the request thread on slow links.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].TrimStart();
            if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                yield break;
            }

            yield return payload;
        }
    }
}
