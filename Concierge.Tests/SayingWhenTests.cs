using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// When a shot happens, and how fast it runs.
///
/// `DesignTiming` reads four things off a shot — how long it stays, how long it waits first,
/// how far into the source it begins, and how fast it plays. **None of them could be said.**
/// `design_cut` reached two and only a model could call it, so a medium whose entire subject
/// is *when* things happen had no sentence about time.
/// </summary>
public sealed class SayingWhenTests
{
    private static DesignDocument AFilm()
        => DesignSpeech.Hear(
            DesignDocument.Blank(medium: DesignMedium.Motion), "add a shot", null).Document;

    private static DesignHeard Say(DesignDocument design, string words, string? pointedAt = null)
        => DesignSpeech.Hear(design, words, pointedAt);

    private static string PropOf(DesignDocument design, string key)
        => design.Frames[^1].Props.TryGetValue(key, out var value) ? value : string.Empty;

    /// <summary>
    /// Words as well as numbers. "four seconds" is what people say, and a number nobody can
    /// write in words is a syntax.
    /// </summary>
    [Theory]
    [InlineData("make the shot four seconds", "4")]
    [InlineData("hold the shot for 2.5 seconds", "2.5")]
    [InlineData("keep the shot ten seconds", "10")]
    public void How_long_a_shot_stays_can_be_said(string words, string expected)
    {
        var heard = Say(AFilm(), words);

        Assert.True(heard.Understood, $"'{words}' was not understood");
        Assert.Equal(expected, PropOf(heard.Document, "seconds"));
    }

    [Theory]
    [InlineData("start the shot at four seconds", "trim", "4")]
    [InlineData("start the clip from 1.5 seconds", "trim", "1.5")]
    [InlineData("wait two seconds before the shot", "delay", "2")]
    [InlineData("play the shot at half speed", "rate", "0.5")]
    [InlineData("play the shot at double speed", "rate", "2")]
    [InlineData("play the clip at normal speed", "rate", "1")]
    public void And_the_rest_of_what_timing_means(string words, string property, string expected)
    {
        var heard = Say(AFilm(), words);

        Assert.True(heard.Understood, $"'{words}' was not understood");
        Assert.Equal(expected, PropOf(heard.Document, property));
    }

    /// <summary>
    /// Bounded rather than trusted, the same way the tools bound what a model asks for: a
    /// shot of nought seconds is a shot nobody can see. Clamped rather than refused —
    /// somebody can see what they got and say it again, and a refusal leaves nothing on
    /// screen to correct.
    /// </summary>
    [Fact]
    public void A_shot_of_no_seconds_is_still_a_shot_somebody_can_see()
    {
        var heard = Say(AFilm(), "make the shot 0 seconds");

        Assert.True(heard.Understood);
        Assert.Equal("0.1", PropOf(heard.Document, "seconds"));
    }

    [Fact]
    public void And_an_hour_long_shot_is_brought_back_to_something_sensible()
        => Assert.Equal("600", PropOf(Say(AFilm(), "make the shot 99999 seconds").Document, "seconds"));

    /// <summary>
    /// Saying one thing about time does not clear the others — four separate facts about one
    /// shot, and the sentences must not be what breaks that.
    /// </summary>
    [Fact]
    public void And_the_four_do_not_clear_each_other()
    {
        var held = Say(AFilm(), "make the shot four seconds").Document;
        var trimmed = Say(held, "start the shot at two seconds").Document;
        var slowed = Say(trimmed, "play the shot at half speed").Document;

        Assert.Equal("4", PropOf(slowed, "seconds"));
        Assert.Equal("2", PropOf(slowed, "trim"));
        Assert.Equal("0.5", PropOf(slowed, "rate"));
    }

    /// <summary>
    /// With nothing to time it says so rather than going quiet.
    /// </summary>
    [Fact]
    public void With_no_shots_it_says_so()
    {
        var heard = Say(
            DesignDocument.Blank(medium: DesignMedium.Motion), "make the shot four seconds");

        Assert.False(heard.Understood);
        Assert.Contains("nothing to time", heard.Reply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And a number nobody can read is said so, rather than silently becoming a default —
    /// a shot that quietly became one second is worse than being asked again.
    /// </summary>
    [Fact]
    public void And_a_length_nobody_can_read_is_asked_about()
    {
        var heard = Say(AFilm(), "make the shot ages seconds");

        Assert.False(heard.Understood);
        Assert.Contains("how long", heard.Reply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The noun is required, so sentences that already meant something else still do.
    /// </summary>
    [Theory]
    [InlineData("make it warm")]
    [InlineData("add a shot")]
    public void And_sentences_that_already_worked_still_do(string words)
        => Assert.True(Say(AFilm(), words).Understood, $"'{words}' stopped working");
}
