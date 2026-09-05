using Concierge.Shared.Chat;

namespace Concierge.Chat.Cloud;

/// <summary>
/// Turning a turn with pictures into whatever shape a provider wants.
///
/// Every one of the three takes images and every one takes them differently,
/// which is why this is here rather than in the runtimes: three near-identical
/// content builders in three files is three places for the same mistake.
///
/// The shapes, since they are easy to confuse:
///
///   Anthropic  content is a list; an image is
///              { type: "image", source: { type: "base64", media_type, data } }
///   OpenAI     content is a list; an image is
///              { type: "image_url", image_url: { url: "data:<media>;base64,..." } }
///   Gemini     parts is a list; an image is
///              { inline_data: { mime_type, data } }
///
/// A turn with no pictures keeps its plain string content in every case. That
/// matters: a provider given a one-element list where it expected a string
/// still works, but every existing conversation would change shape on the wire
/// for no reason, and a diff nobody can explain is a diff nobody can review.
/// </summary>
internal static class VisionContent
{
    /// <summary>Media types the three providers agree on.</summary>
    internal static readonly IReadOnlyCollection<string> Supported =
        new[] { "image/jpeg", "image/png", "image/gif", "image/webp" };

    internal static bool HasImages(ChatTurn turn) => turn.Images is { Count: > 0 };

    /// <summary>Anthropic: images first, then the text that asked about them.</summary>
    internal static object AnthropicContent(ChatTurn turn)
    {
        if (!HasImages(turn))
        {
            return turn.Content;
        }

        var parts = new List<object>();

        foreach (var image in turn.Images!)
        {
            parts.Add(new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = image.MediaType,
                    data = Convert.ToBase64String(image.Bytes),
                },
            });
        }

        // The question goes after the pictures: a model reading in order should
        // see what it is being asked about before being asked.
        parts.Add(new { type = "text", text = turn.Content });

        return parts;
    }

    /// <summary>OpenAI: the same idea, as data URIs.</summary>
    internal static object OpenAiContent(ChatTurn turn)
    {
        if (!HasImages(turn))
        {
            return turn.Content;
        }

        var parts = new List<object>();

        foreach (var image in turn.Images!)
        {
            parts.Add(new
            {
                type = "image_url",
                image_url = new
                {
                    url = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Bytes)}",
                },
            });
        }

        parts.Add(new { type = "text", text = turn.Content });

        return parts;
    }

    /// <summary>Gemini: parts, with inline data.</summary>
    internal static object[] GeminiParts(ChatTurn turn)
    {
        if (!HasImages(turn))
        {
            return [new { text = turn.Content }];
        }

        var parts = new List<object>();

        foreach (var image in turn.Images!)
        {
            parts.Add(new
            {
                inline_data = new
                {
                    mime_type = image.MediaType,
                    data = Convert.ToBase64String(image.Bytes),
                },
            });
        }

        parts.Add(new { text = turn.Content });

        return parts.ToArray();
    }
}
