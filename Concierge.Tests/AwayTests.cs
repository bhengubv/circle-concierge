using Concierge.Shared.Away;
using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// A sentence said on a device that is not here.
///
/// **The watch listened and the words went nowhere.** Measured on the Wear OS emulator:
/// `Concierge.Wear` has one project reference and no network code at all, so what it heard
/// was drawn on the face and dropped. This is the half of closing that which needs no model
/// — `DesignSpeech.Hear` is pure and synchronous, so a wrist on a train with no signal can
/// still change a design.
/// </summary>
public sealed class AwayTests
{
    private sealed class Remembers : IDesignStore
    {
        public DesignDocument? Held { get; set; }

        public int Saves { get; private set; }

        public Task<DesignRestore> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DesignRestore(Held, null));

        public Task SaveAsync(DesignDocument document, CancellationToken cancellationToken = default)
        {
            Held = document;
            Saves++;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Held = null;
            return Task.CompletedTask;
        }
    }

    private static AwayContext FromAWatch(DateTimeOffset? at = null)
        => new(at ?? DateTimeOffset.UtcNow, "watch");

    private static AwayDesk DeskOver(IDesignStore designs)
        => new(designs, turn: null!, conversations: null!, runtimes: []);

    /// <summary>
    /// The whole point: speak into a watch, and the design on the desk changes.
    /// </summary>
    [Fact]
    public async Task A_sentence_from_a_watch_changes_the_design()
    {
        var store = new Remembers();
        var desk = DeskOver(store);

        var answer = await desk.SayAsync(new AwaySaid("add a heading that says Sports Day", FromAWatch()));

        Assert.True(answer.Understood, answer.Reply);
        Assert.Equal(1, store.Saves);
        Assert.Contains(
            store.Held!.Nodes.Values,
            node => string.Equals(node.Text, "Sports Day", StringComparison.Ordinal));
    }

    /// <summary>
    /// **And with no model anywhere near it.** The runner and the conversation store are
    /// deliberately null here: if the canvas route ever stopped answering on its own, this
    /// test would throw rather than quietly pass through to something else.
    /// </summary>
    [Fact]
    public async Task And_needs_nothing_loaded_to_do_it()
    {
        var desk = DeskOver(new Remembers());

        Assert.True((await desk.SayAsync(new AwaySaid("make it night", FromAWatch()))).Understood);
    }

    /// <summary>
    /// Nothing said is not a change, and saying so beats a blank face.
    /// </summary>
    [Fact]
    public async Task Nothing_said_changes_nothing()
    {
        var store = new Remembers();

        var answer = await DeskOver(store).SayAsync(new AwaySaid("   ", FromAWatch()));

        Assert.False(answer.Understood);
        Assert.Equal(0, store.Saves);
        Assert.False(string.IsNullOrWhiteSpace(answer.Reply));
    }

    /// <summary>
    /// A sentence the canvas cannot place, with nothing able to answer it, comes back with
    /// the canvas's own words rather than silence. That reply has existed on
    /// <see cref="DesignHeard"/> since before the medium switch and was read by nobody.
    /// </summary>
    [Fact]
    public async Task Something_it_cannot_place_still_says_something()
    {
        var answer = await DeskOver(new Remembers())
            .SayAsync(new AwaySaid("rotate the bass by fourteen degrees", FromAWatch()));

        Assert.False(answer.Understood);
        Assert.False(string.IsNullOrWhiteSpace(answer.Reply));
    }

    // ── What the device knew when it heard it ─────────────────────────────

    /// <summary>
    /// The situation reads as a sentence, because it rides in a system prompt where a small
    /// model reads prose more reliably than fields.
    /// </summary>
    [Fact]
    public void The_situation_is_said_in_words()
    {
        var said = new AwayContext(
            new DateTimeOffset(2026, 9, 13, 6, 14, 0, TimeSpan.Zero),
            "watch",
            Motion: "walking").AsSaid();

        Assert.Contains("watch", said!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("06:14", said, StringComparison.Ordinal);
        Assert.Contains("walking", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A blank is a blank.** With location off, nothing is said about where — not zero,
    /// not "unknown", not a guess. The board renderer's rule, and the reason it exists: an
    /// invented number is believed by whatever reads it next.
    /// </summary>
    [Fact]
    public void And_says_nothing_about_what_it_does_not_know()
    {
        var said = new AwayContext(DateTimeOffset.UtcNow, "watch").AsSaid()!;

        Assert.DoesNotContain("near", said, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0,", said, StringComparison.Ordinal);
        Assert.DoesNotContain("heart", said, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Light is reported as what a person would say, not as a number nobody can picture.
    /// </summary>
    [Theory]
    [InlineData(2, "dark")]
    [InlineData(300, "indoors")]
    [InlineData(20000, "daylight")]
    public void And_light_is_a_place_rather_than_a_measurement(double lux, string expected)
        => Assert.Contains(
            expected,
            new AwayContext(DateTimeOffset.UtcNow, "watch", AmbientLux: lux).AsSaid()!,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// With no approver wired, nothing is waiting and nothing can be answered — rather than
    /// a face claiming it allowed something that never ran.
    /// </summary>
    [Fact]
    public async Task With_nobody_to_ask_nothing_is_waiting()
    {
        var desk = DeskOver(new Remembers());

        Assert.Empty(await desk.WaitingAsync());
        Assert.False(await desk.AnswerAsync(Guid.NewGuid(), allowed: true));
    }
}
