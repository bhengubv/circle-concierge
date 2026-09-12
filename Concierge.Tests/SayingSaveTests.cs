using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// Asking for the design as a file.
///
/// **"save it" had nowhere to go at all.** `design_save` was reachable only by a model, on
/// the surface whose entire answer to open-design is "real files" — and a page, a deck and a
/// room need no encoder to produce one, so the bytes on screen are the bytes written.
///
/// It cannot be a <c>DesignHeard</c>: everything else here is a document in and a document
/// out, and this writes a file and changes no document. So the workspace asks this whether a
/// sentence was a save, then awaits the tool and says where the file went.
/// </summary>
public sealed class SayingSaveTests
{
    [Theory]
    [InlineData("save it")]
    [InlineData("save this")]
    [InlineData("export it")]
    [InlineData("download it")]
    [InlineData("give me the file")]
    public void The_ways_people_ask_for_a_file(string said)
        => Assert.NotNull(DesignSpeech.HeardASave(said));

    /// <summary>
    /// And the form, when they name one. A deck as a PowerPoint is the case that matters —
    /// a deck that leaves as a picture is a deck nobody can change.
    /// </summary>
    [Theory]
    [InlineData("save it as a pdf", "pdf")]
    [InlineData("export it to powerpoint", "pptx")]
    [InlineData("give me the pdf", "pdf")]
    [InlineData("save it", "")]
    public void And_the_form_when_they_name_one(string said, string expected)
        => Assert.Equal(expected, DesignSpeech.HeardASave(said));

    /// <summary>
    /// Everything else is not a save. This runs before the rest of the vocabulary, so a
    /// pattern that matched loosely would swallow sentences that already worked — and
    /// "save" is not a rare word.
    /// </summary>
    [Theory]
    [InlineData("add a heading saying Save the date")]
    [InlineData("make it warm")]
    [InlineData("add a wall")]
    [InlineData("set the panel to 48,200")]
    [InlineData("")]
    public void But_nothing_else_is(string said)
        => Assert.Null(DesignSpeech.HeardASave(said));
}
