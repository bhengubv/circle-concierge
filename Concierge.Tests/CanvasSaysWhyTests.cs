using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// The canvas saying why it did not do something.
///
/// **`DesignHeard.Reply` was read by nothing.** Not one file in the repository outside its own
/// declaration. It has carried sentences like *"A page is one surface. Say 'slides' or 'video'
/// first, then add one."* since before the medium switch existed — and the send path dropped
/// every one of them and handed the words to a model instead.
///
/// So the single case where the canvas knows exactly what is wrong and can say it in one
/// sentence was the case where it went quiet and a model answered about something else. Found
/// by clicking Board, having the click not land, saying "set the panel to 48,200" into a
/// video, and watching a minute of "thinking" instead of four words.
///
/// This file pins the sentences. That they reach the screen is pinned in the workspace tests,
/// because that is where the dropping happened.
/// </summary>
public sealed class CanvasSaysWhyTests
{
    private static string? ReplyTo(DesignMedium medium, string said)
        => DesignSpeech.Hear(DesignDocument.Blank(medium: medium), said, null).Reply;

    /// <summary>
    /// Every one of these is a case where the canvas knows the answer, and every one of them
    /// was silence.
    /// </summary>
    [Theory]
    [InlineData(DesignMedium.Page, "add a slide", "one surface")]
    [InlineData(DesignMedium.Page, "add a wall", "room")]
    [InlineData(DesignMedium.Page, "put a door in the wall", "room")]
    [InlineData(DesignMedium.Page, "make the shot warm", "video")]
    [InlineData(DesignMedium.Page, "set the panel to 4", "board")]
    [InlineData(DesignMedium.Scene, "put a door in the wall", "no wall")]
    [InlineData(DesignMedium.Motion, "make the shot warm", "no shots")]
    [InlineData(DesignMedium.Board, "set the panel to 4", "no panels")]
    [InlineData(DesignMedium.Board, "call the panel Signups", "no panel")]
    public void The_canvas_says_why_rather_than_going_quiet(
        DesignMedium medium, string said, string expected)
    {
        var reply = ReplyTo(medium, said);

        Assert.False(string.IsNullOrWhiteSpace(reply), $"'{said}' answered with nothing");
        Assert.Contains(expected, reply!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And a sentence the canvas genuinely cannot place still has no reply, so it still goes
    /// to a model. Answering everything here would quietly cut the model out of the canvas,
    /// which is the opposite mistake and a worse one.
    /// </summary>
    /// <summary>
    /// And every one of them is the last word — sending "say 'board' first" on to a model
    /// replaces a correct four-word answer with a minute of unrelated prose, which is exactly
    /// what happened before.
    /// </summary>
    [Fact]
    public void And_those_answers_are_the_last_word()
        => Assert.True(
            DesignSpeech.Hear(DesignDocument.Blank(medium: DesignMedium.Page), "add a wall", null).Final);

    /// <summary>
    /// A sentence the canvas genuinely cannot place is **not** final, so it still goes to a
    /// model. Answering everything here would quietly cut the model out of the canvas, which
    /// is the opposite mistake and a worse one — and the first version of this fix did
    /// exactly that. This test caught it.
    /// </summary>
    [Fact]
    public void But_something_it_cannot_place_at_all_still_belongs_to_the_model()
    {
        var heard = DesignSpeech.Hear(
            DesignDocument.Blank(medium: DesignMedium.Page), "make it feel like a school newsletter", null);

        Assert.False(heard.Final);
    }
}
