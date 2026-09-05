using Concierge.Shared.Attachments;
using Concierge.Shared.Chat;

namespace Concierge.Tests;

/// <summary>
/// Telling a picture from a text file, before it reaches the prompt.
///
/// This exists because the workspace did not do it. Every attachment was
/// inlined with Encoding.UTF8.GetString, pictures included, and the camera
/// hand-off attaches a JPEG — so photographing something sent the model a few
/// hundred kilobytes of decoded binary and asked what was in the picture.
/// </summary>
public sealed class AttachmentKindTests
{
    // The first bytes of each format, which is all the detection reads.
    private static byte[] Jpeg() => Padded(0xFF, 0xD8, 0xFF, 0xE0);
    private static byte[] Png() => Padded(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
    private static byte[] Gif() => Padded(0x47, 0x49, 0x46, 0x38, 0x39, 0x61);

    private static byte[] Webp()
    {
        var bytes = new byte[16];
        "RIFF"u8.CopyTo(bytes);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        return bytes;
    }

    private static byte[] Padded(params byte[] header)
    {
        var bytes = new byte[Math.Max(header.Length, 16)];
        header.CopyTo(bytes, 0);
        return bytes;
    }

    [Fact]
    public void Every_format_a_provider_accepts_is_recognised()
    {
        Assert.Equal("image/jpeg", AttachmentKind.ImageMediaType(Jpeg()));
        Assert.Equal("image/png", AttachmentKind.ImageMediaType(Png()));
        Assert.Equal("image/gif", AttachmentKind.ImageMediaType(Gif()));
        Assert.Equal("image/webp", AttachmentKind.ImageMediaType(Webp()));
    }

    [Fact]
    public void Text_is_not_mistaken_for_a_picture()
    {
        Assert.Null(AttachmentKind.ImageMediaType("# A markdown file\n\nWith words."u8));
        Assert.Null(AttachmentKind.ImageMediaType(""u8));
        Assert.True(AttachmentKind.LooksLikeText("plain words"u8));
    }

    /// <summary>
    /// Content decides, never the file name. A model handed a renamed PNG gets
    /// noise either way; the magic bytes are the only thing that knows.
    /// </summary>
    [Fact]
    public void A_png_called_notes_txt_is_still_a_png()
        => Assert.Equal("image/png", AttachmentKind.ImageMediaType(Png()));

    /// <summary>
    /// A NUL byte is the giveaway: nothing a person types produces one, and
    /// every binary format is full of them.
    /// </summary>
    [Fact]
    public void Binary_that_is_not_a_picture_is_still_not_text()
    {
        var binary = new byte[] { 0x00, 0x01, 0x02, 0x00, 0x03 };

        Assert.Null(AttachmentKind.ImageMediaType(binary));
        Assert.False(AttachmentKind.LooksLikeText(binary));
    }

    [Fact]
    public void An_image_is_never_treated_as_text()
    {
        Assert.False(AttachmentKind.LooksLikeText(Jpeg()));
        Assert.False(AttachmentKind.LooksLikeText(Png()));
    }

    [Fact]
    public void Truncated_headers_are_not_guessed_at()
    {
        Assert.Null(AttachmentKind.ImageMediaType(new byte[] { 0xFF }));
        Assert.Null(AttachmentKind.ImageMediaType(new byte[] { 0x89, 0x50 }));
    }

    // ── The channel ───────────────────────────────────────────────────────

    /// <summary>
    /// Optional, so every existing caller and every text-only runtime is
    /// untouched — which is most of them, including the model that ships with
    /// Concierge.
    /// </summary>
    [Fact]
    public void A_turn_carries_no_pictures_unless_it_is_given_some()
    {
        var turn = new ChatTurn("user", "hello");

        Assert.Null(turn.Images);
    }

    [Fact]
    public void A_turn_can_carry_pictures_without_disturbing_its_text()
    {
        var turn = new ChatTurn("user", "what is this?") with
        {
            Images = new[] { new ChatImage("photo.jpg", "image/jpeg", Jpeg()) }
        };

        Assert.Equal("what is this?", turn.Content);
        Assert.Single(turn.Images!);
        Assert.Equal("image/jpeg", turn.Images![0].MediaType);
    }

    /// <summary>
    /// Capability is asked, not assumed. A runtime that does not implement the
    /// interface is never handed a picture, and the person is told why.
    /// </summary>
    [Fact]
    public void A_text_only_runtime_does_not_claim_to_see()
    {
        Assert.False(new TextOnlyRuntime() is IVisionCapableRuntime);
        Assert.True(new SeeingRuntime() is IVisionCapableRuntime);
    }

    private sealed class TextOnlyRuntime : IChatRuntime
    {
        public string Id => "text-only";
        public string EngineLabel => "Text only";
        public bool IsReady => true;
        public string StatusMessage => "ready";

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class SeeingRuntime : IChatRuntime, IVisionCapableRuntime
    {
        public string Id => "seeing";
        public string EngineLabel => "Can see";
        public bool IsReady => true;
        public string StatusMessage => "ready";

        public IReadOnlyCollection<string> SupportedImageMediaTypes => new[] { "image/jpeg", "image/png" };

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
