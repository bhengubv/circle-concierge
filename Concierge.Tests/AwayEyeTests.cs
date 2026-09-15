using Concierge.Shared.Away;
using Concierge.Shared.Chat;
using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// The phone as the eye.
///
/// **A watch has no camera worth the name and a desk sees only what somebody carries back to
/// it.** That is the cost of not seeing: every session starts with describing your own
/// situation in words. A picture removes it — "like this one" works when there is a *this*.
///
/// What is checked here is the half that goes quietly wrong: a picture reaching something
/// that cannot look at it, and being discussed as though it had been seen.
/// </summary>
public sealed class AwayEyeTests
{
    /// <summary>A real PNG, one pixel. The kind is read from these bytes, never from a name.</summary>
    private static readonly byte[] APng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private sealed class Nothing : IDesignStore
    {
        public DesignDocument? Held { get; private set; }

        public Task<DesignRestore> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DesignRestore(Held, null));

        public Task SaveAsync(DesignDocument document, CancellationToken cancellationToken = default)
        {
            Held = document;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static AwaySaid WithAPicture(string text = "what is this?")
        => new(
            text,
            new AwayContext(DateTimeOffset.UtcNow, "phone"),
            new AwayPicture("wall.png", "image/png", APng));

    /// <summary>
    /// **A picture never goes to the canvas.** `DesignSpeech` is a document in and a document
    /// out and carries no bytes, so answering a sentence that arrived with a photograph would
    /// drop the photograph in silence — on the one road a camera has into the product.
    ///
    /// With nothing able to answer, the design is untouched and it says so rather than
    /// quietly succeeding at half the job.
    /// </summary>
    [Fact]
    public async Task A_sentence_with_a_picture_never_goes_to_the_canvas()
    {
        var designs = new Nothing();
        var desk = new AwayDesk(designs, turn: () => null, conversations: null!, runtimes: []);

        // A sentence the canvas would certainly have understood on its own.
        var answer = await desk.SayAsync(WithAPicture("make it night"));

        Assert.Null(designs.Held);
        Assert.False(answer.Understood);
        Assert.False(string.IsNullOrWhiteSpace(answer.Reply));
    }

    /// <summary>
    /// And without one the canvas still answers, so the ordinary case is untouched.
    /// </summary>
    [Fact]
    public async Task But_without_one_the_canvas_still_answers()
    {
        var designs = new Nothing();
        var desk = new AwayDesk(designs, turn: () => null, conversations: null!, runtimes: []);

        var answer = await desk.SayAsync(
            new AwaySaid("make it night", new AwayContext(DateTimeOffset.UtcNow, "phone")));

        Assert.True(answer.Understood, answer.Reply);
        Assert.NotNull(designs.Held);
    }

    // ── What the door lets through ────────────────────────────────────────

    /// <summary>
    /// The kind comes from the bytes. A file called photo.jpg that is not a JPEG is not one,
    /// and the same helper already guards the paperclip and the vision path — one answer to
    /// "is this a picture", not a third.
    /// </summary>
    [Fact]
    public void A_picture_is_what_its_bytes_say_it_is()
    {
        Assert.Equal("image/png", Concierge.Shared.Attachments.AttachmentKind.ImageMediaType(APng));

        Assert.Null(Concierge.Shared.Attachments.AttachmentKind.ImageMediaType(
            System.Text.Encoding.UTF8.GetBytes("this is not a picture, whatever it is called")));
    }

    /// <summary>
    /// A picture goes as one image on the turn, with its own kind and name — not pasted into
    /// the words, which is what happens when nobody builds the channel.
    /// </summary>
    [Fact]
    public void And_crosses_as_a_picture_rather_than_as_text()
    {
        var picture = WithAPicture().Picture!;
        var carried = new ChatImage(picture.FileName, picture.MediaType, picture.Bytes);

        Assert.Equal("wall.png", carried.FileName);
        Assert.Equal("image/png", carried.MediaType);
        Assert.Equal(APng, carried.Bytes);
    }

    /// <summary>
    /// **The situation still travels with it.** A photograph of a wall at 6am walking is a
    /// different question from the same photograph at a desk at midnight, and the whole
    /// argument for carrying the context is lost if a picture replaces it.
    /// </summary>
    [Fact]
    public void And_the_situation_goes_with_it()
    {
        var said = new AwaySaid(
            "what is this?",
            new AwayContext(
                new DateTimeOffset(2026, 9, 15, 6, 14, 0, TimeSpan.Zero), "phone", Motion: "walking"),
            new AwayPicture("wall.png", "image/png", APng));

        var situation = said.Context.AsSaid()!;

        Assert.Contains("phone", situation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("walking", situation, StringComparison.Ordinal);
    }
}
