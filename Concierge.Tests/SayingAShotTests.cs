using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// Grading, moving and joining a shot by saying so.
///
/// `design_colour`, `design_move` and `design_blend` were in the same position as the whole
/// of Pascal's list: real capabilities with no sentence that reached them, on a machine whose
/// model does not call tools. A still picture held for four seconds looks like a fault, and
/// the tool that fixes it could only be had by a model.
/// </summary>
public sealed class SayingAShotTests
{
    private static DesignDocument AFilm()
        => DesignSpeech.Hear(
            DesignDocument.Blank(medium: DesignMedium.Motion), "add a shot", null).Document;

    private static DesignHeard Say(DesignDocument design, string words, string? pointedAt = null)
        => DesignSpeech.Hear(design, words, pointedAt);

    private static string PropOf(DesignDocument design, string key)
        => design.Frames[^1].Props.TryGetValue(key, out var value) ? value : string.Empty;

    [Theory]
    [InlineData("make the shot warm", "warm")]
    [InlineData("make the shot warmer", "warm")]
    [InlineData("grade this shot cooler", "cool")]
    [InlineData("make the clip faded", "faded")]
    [InlineData("make the footage vivid", "vivid")]
    public void A_shot_can_be_graded_by_saying_so(string said, string expected)
    {
        var heard = Say(AFilm(), said);

        Assert.True(heard.Understood, $"'{said}' was not understood");
        Assert.Equal(expected, PropOf(heard.Document, "colour"));
    }

    /// <summary>
    /// And it can be put back. "none" has to mean something, or a grade is a one-way door.
    /// </summary>
    [Fact]
    public void And_a_grade_can_be_taken_off_again()
    {
        var graded = Say(AFilm(), "make the shot warm").Document;

        Assert.Equal(string.Empty, PropOf(Say(graded, "make the shot normal").Document, "colour"));
    }

    /// <summary>
    /// **The word "shot" is required and this is why.** "Warm" is one of the six looks, so
    /// "make it warm" has meant the Warm look since looks existed, and it still must.
    /// </summary>
    [Fact]
    public void Making_it_warm_is_still_the_look_rather_than_a_grade()
    {
        var heard = Say(AFilm(), "make it warm");

        Assert.True(heard.Understood);
        Assert.Equal(string.Empty, PropOf(heard.Document, "colour"));
        Assert.Contains("warm", heard.What, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("make the shot grow", "grow")]
    [InlineData("let the shot drift", "drift")]
    [InlineData("make the shot fade", "fade")]
    public void A_shot_can_be_made_to_move(string said, string expected)
    {
        var heard = Say(AFilm(), said);

        Assert.True(heard.Understood, $"'{said}' was not understood");
        Assert.Equal(expected, PropOf(heard.Document, "move"));
    }

    [Fact]
    public void And_held_still_again()
    {
        var moving = Say(AFilm(), "make the shot grow").Document;

        Assert.Equal(string.Empty, PropOf(Say(moving, "hold the shot still").Document, "move"));
    }

    /// <summary>
    /// A dissolve on every join is what a first attempt looks like, so a cut stays the
    /// default and both directions are sayable.
    /// </summary>
    [Fact]
    public void Shots_can_be_faded_into_and_cut_back_to()
    {
        var faded = Say(AFilm(), "fade into the next shot").Document;
        Assert.Equal("0.5", PropOf(faded, "blend"));

        Assert.Equal(string.Empty, PropOf(Say(faded, "cut to the shot instead").Document, "blend"));
    }

    /// <summary>
    /// Whatever is being pointed at wins over the last shot — pointing supplies the subject,
    /// which is the whole reason this surface lets you point at things.
    /// </summary>
    [Fact]
    public void And_the_shot_being_pointed_at_is_the_one_that_changes()
    {
        var two = Say(AFilm(), "add a shot").Document;
        var first = two.Frames[0];

        var heard = Say(two, "make the shot warm", first.Id);

        Assert.Equal("warm", heard.Document.Find(first.Id)!.Props["colour"]);
        Assert.Equal(string.Empty, PropOf(heard.Document, "colour"));
    }

    /// <summary>
    /// With no shots it says so, and on a page it says where shots live — rather than going
    /// quiet, which on a machine with no model that acts means nothing happens and nothing
    /// explains why.
    /// </summary>
    [Fact]
    public void With_no_shots_it_says_so()
    {
        var heard = Say(DesignDocument.Blank(medium: DesignMedium.Motion), "make the shot warm");

        Assert.False(heard.Understood);
        Assert.Contains("no shots", heard.Reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void And_on_a_page_it_says_where_shots_live()
    {
        var heard = Say(DesignDocument.Blank(medium: DesignMedium.Page), "make the shot warm");

        Assert.False(heard.Understood);
        Assert.Contains("video", heard.Reply, StringComparison.OrdinalIgnoreCase);
    }
}
