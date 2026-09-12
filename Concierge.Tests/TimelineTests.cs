using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// The timeline.
///
/// **`TASKS.md` refused one, in several places, as a principle**: *"what every renderer
/// refuses, because it is the whole point: no timeline, no track stack, no waveform, no node
/// graph, no keyframes."* The stated reason was a five-year-old and a ninety-seven-year-old.
///
/// That reasoning confused the display with the verb. A timeline is not a thing you have to
/// learn to operate if you never operate it — it is a thing you look at. Somebody making a
/// four-minute film needs to see the shape of it, and the argument against a cockpit was only
/// ever an argument about what you have to touch.
///
/// So: clips at their real proportions, named and measured, with the playhead crossing the
/// whole film. Nothing here can be dragged — a length changes by saying "make the shot four
/// seconds", which works while driving and dragging does not. Clicking one *points* at it,
/// which is what every other surface here already does.
/// </summary>
public sealed class TimelineTests
{
    private static DesignDocument AFilm(params int[] lengths)
    {
        var film = DesignDocument.Blank(medium: DesignMedium.Motion);

        for (var i = 0; i < lengths.Length; i++)
        {
            var shot = DesignNode.New(
                DesignNodeKind.Frame,
                null,
                ("text", $"Shot {i + 1}"),
                ("seconds", lengths[i].ToString(System.Globalization.CultureInfo.InvariantCulture)));

            film = film.Add(shot);
        }

        return film;
    }

    [Fact]
    public void A_film_has_a_timeline()
    {
        var html = DesignMediums.Render(AFilm(4, 2));

        Assert.Contains("class=\"tl\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"head\"", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// **At their real proportions.** A row of equal boxes says nothing about the shape of a
    /// film: four seconds has to look like twice two, or the timeline is decoration.
    /// </summary>
    [Fact]
    public void And_a_four_second_shot_is_twice_as_wide_as_a_two_second_one()
    {
        var html = DesignMediums.Render(AFilm(4, 2));

        Assert.Contains("flex:4 0 0%", html, StringComparison.Ordinal);
        Assert.Contains("flex:2 0 0%", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Named and measured, because a row of unlabelled blocks is a chart of nothing.
    /// </summary>
    [Fact]
    public void And_every_clip_says_what_it_is_and_how_long()
    {
        var html = DesignMediums.Render(AFilm(4, 2));

        Assert.Contains("Shot 1", html, StringComparison.Ordinal);
        Assert.Contains("4s", html, StringComparison.Ordinal);
        Assert.Contains("2s", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Clicking a clip points at that shot — the same `data-node` contract every other
    /// surface carries, so "that one, make it warmer" works from the timeline.
    /// </summary>
    [Fact]
    public void And_a_clip_can_be_pointed_at()
    {
        var film = AFilm(3);
        var shot = film.Frames[0];

        var html = DesignMediums.Render(film);

        var timeline = html[html.IndexOf("class=\"tl\"", StringComparison.Ordinal)..];

        Assert.Contains($"data-node=\"{shot.Id}\"", timeline, StringComparison.Ordinal);
    }

    /// <summary>
    /// And what is being pointed at is marked, so the timeline agrees with the canvas rather
    /// than being a second opinion about which shot is selected.
    /// </summary>
    [Fact]
    public void And_the_one_being_pointed_at_is_marked()
    {
        var film = AFilm(3, 3);

        var html = DesignMediums.Render(film, film.Frames[1].Id);
        var timeline = html[html.IndexOf("class=\"tl\"", StringComparison.Ordinal)..];

        Assert.Contains("clip picked", timeline, StringComparison.Ordinal);
    }

    /// <summary>
    /// **Nothing drags.** The verb is speech; a timeline you can pull about is the cockpit
    /// this product exists to avoid, and it is useless in the case that matters — you cannot
    /// drag a clip while driving, and you can say a sentence.
    /// </summary>
    [Fact]
    public void But_nothing_on_it_can_be_dragged()
    {
        var html = DesignMediums.Render(AFilm(4, 2));

        Assert.DoesNotContain("draggable", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onmousedown", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<input type=\"range\"", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The playhead crosses the whole film rather than restarting inside each clip — which is
    /// the difference between a timeline and a progress bar wearing one's clothes.
    /// </summary>
    [Fact]
    public void And_the_playhead_moves_across_the_whole_film()
    {
        var html = DesignMediums.Render(AFilm(4, 2));

        Assert.Contains("starts[at]", html, StringComparison.Ordinal);
        Assert.Contains("head.style.left", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A timing sentence reaches it. "play the shot at half speed" doubles how long it is on
    /// screen, and the clip has to grow — otherwise the picture and the timeline disagree.
    /// </summary>
    [Fact]
    public void And_what_was_said_about_time_changes_the_shape()
    {
        var film = AFilm(4);
        var slowed = DesignSpeech.Hear(film, "play the shot at half speed", null).Document;

        var html = DesignMediums.Render(slowed);

        Assert.Contains("flex:8 0 0%", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Along the bottom, where a timeline goes.
    ///
    /// The first version left it in normal flow, which put it at the *top* of the document —
    /// under the canvas's own caption overlay, half hidden, above the picture it describes.
    /// Shots are absolutely positioned, so "after the shots" in the markup is not "below
    /// them" on the screen. Caught by looking at it on the running app, not by reading it.
    /// </summary>
    [Fact]
    public void And_it_sits_along_the_bottom()
    {
        var html = DesignMediums.Render(AFilm(4, 2));

        var rule = html[html.IndexOf(".tl {", StringComparison.Ordinal)..];
        rule = rule[..rule.IndexOf('}', StringComparison.Ordinal)];

        Assert.Contains("position: absolute", rule, StringComparison.Ordinal);
        Assert.Contains("bottom:", rule, StringComparison.Ordinal);
    }
}
