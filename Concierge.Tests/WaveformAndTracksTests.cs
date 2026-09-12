using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// A waveform and a track stack for Sound.
///
/// The third of the three professional displays, and the one where inventing something would
/// be least noticed and most dishonest. `TASKS.md` refused all of them on the grounds that a
/// waveform is what makes audio software a cockpit — which confused the display with the verb.
/// You do not have to operate a waveform to be told something by it.
///
/// **So both are built from real measurements or not drawn at all.** The waveform is peaks off
/// decoded samples; where the audio cannot be decoded — a track pointing at a file on
/// somebody's disk, a format this browser will not take — the canvas is hidden rather than
/// filled with a plausible shape. The lanes are proportional to what each track reports its
/// length to be, and a track that never reports one gets a labelled blank.
///
/// That is the board renderer's rule applied to sound: an invented metric is slop the moment
/// it is invented, and a drawn waveform standing in for unread audio is exactly that.
/// </summary>
public sealed class WaveformAndTracksTests
{
    private const string ATrack =
        "data:audio/mpeg;base64,SUQzBAAAAAAAI1RTU0UAAAAPAAADTGF2ZjU4Ljc2LjEwMAAAAAAAAAAAAAAA";

    private static DesignDocument ARunningOrder(params string[] names)
    {
        var order = DesignDocument.Blank(medium: DesignMedium.Sound);

        foreach (var name in names)
        {
            order = order.Add(DesignNode.New(
                DesignNodeKind.Sound, null, ("text", name), ("src", ATrack)));
        }

        return order;
    }

    [Fact]
    public void Every_track_gets_a_waveform_to_draw_into()
    {
        var html = DesignMediums.Render(ARunningOrder("Sinnerman", "Wild Is The Wind"));

        Assert.Contains("canvas class=\"wave\"", html, StringComparison.Ordinal);
        Assert.Contains($"data-src=\"{ATrack}\"", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// **Nothing is drawn in the markup.** The shape comes from decoded samples or not at all
    /// — no path data, no polyline, no bars baked into the HTML, because anything baked in
    /// would be a picture of sound rather than the sound.
    /// </summary>
    [Fact]
    public void But_no_shape_is_drawn_before_the_audio_is_read()
    {
        var html = DesignMediums.Render(ARunningOrder("Sinnerman"));

        Assert.DoesNotContain("<polyline", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<path", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("decodeAudioData", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Audio that cannot be decoded leaves no waveform rather than a made-up one.
    /// </summary>
    [Fact]
    public void And_audio_that_cannot_be_read_draws_nothing()
    {
        var html = DesignMediums.Render(ARunningOrder("Sinnerman"));

        Assert.Contains("c.classList.add('no')", html, StringComparison.Ordinal);
        Assert.Contains(".wave.no { display: none; }", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A track pointing at a file on this machine is a placeholder, and gets no canvas at all
    /// — the same rule the renderer already had about refusing a file:// source.
    /// </summary>
    [Fact]
    public void A_track_pointing_at_a_local_file_gets_no_waveform()
    {
        var order = DesignDocument.Blank(medium: DesignMedium.Sound)
            .Add(DesignNode.New(
                DesignNodeKind.Sound, null, ("text", "On my disk"), ("src", @"C:\Music\song.mp3")));

        var html = DesignMediums.Render(order);

        Assert.DoesNotContain("canvas class=\"wave\"", html, StringComparison.Ordinal);
        Assert.Contains("On my disk", html, StringComparison.Ordinal);
    }

    // ── The track stack ──────────────────────────────────────────────────

    [Fact]
    public void There_is_a_stack_for_the_running_order()
    {
        var html = DesignMediums.Render(ARunningOrder("One", "Two"));

        Assert.Contains("id=\"stack\"", html, StringComparison.Ordinal);
        Assert.Contains("The running order", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// **Empty in the markup, filled from real durations.** Lanes drawn before the audio says
    /// how long it is would have to be guesses, and every lane the same width is a picture of
    /// a running order rather than the running order.
    /// </summary>
    [Fact]
    public void And_its_lanes_come_from_what_the_audio_reports()
    {
        var html = DesignMediums.Render(ARunningOrder("One", "Two"));

        // Empty in the markup — the attribute order is the renderer's business, so this
        // asserts there is nothing between the tag and its close rather than an exact string.
        var open = html.IndexOf("<div class=\"stack\"", StringComparison.Ordinal);
        var shut = html.IndexOf("</div>", open, StringComparison.Ordinal);

        Assert.DoesNotContain("lane", html[open..shut], StringComparison.Ordinal);
        Assert.Contains("a.duration", html, StringComparison.Ordinal);
        Assert.Contains("loadedmetadata", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a track that never says how long it is gets a labelled blank rather than a lane
    /// sized from nothing — the board renderer's rule, applied to sound.
    /// </summary>
    [Fact]
    public void And_a_track_with_no_length_is_a_labelled_blank()
    {
        var html = DesignMediums.Render(ARunningOrder("One"));

        Assert.Contains("run.classList.add('no')", html, StringComparison.Ordinal);
        Assert.Contains(".run.no {", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// **Six tracks with no lengths is still six tracks.**
    ///
    /// The first version returned early when no duration had been reported, so a running order
    /// built by importing a music folder — every track pointing at a file on this machine,
    /// none of them decodable in a page — showed no stack at all. Seen on the running app with
    /// six tracks and an empty rectangle where the stack should be.
    ///
    /// An empty case is not a case. The lanes are labelled blanks now, which is exactly what
    /// the board renderer does with a panel that has no number.
    /// </summary>
    [Fact]
    public void Tracks_with_no_length_still_get_lanes()
    {
        var html = DesignMediums.Render(ARunningOrder("One", "Two"));

        Assert.DoesNotContain("if (whole <= 0) { return; }", html, StringComparison.Ordinal);
        Assert.Contains("length unknown", html, StringComparison.Ordinal);
        Assert.Contains("if (!tracks.length) { return; }", html, StringComparison.Ordinal);
    }
}
