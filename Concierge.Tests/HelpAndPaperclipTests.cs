using Bunit;
using Concierge.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// Help, and the paperclip it describes.
///
/// **Found by reading Help and then looking at the composer it describes.** It said: *"The
/// switch under the composer is 'May act on its own'. Off means it asks every time."* There is
/// no switch. Nothing in the app carries that name. The composer has three buttons — Plan
/// only, Ask first, Act freely — and Help is the one room whose entire job is telling somebody
/// where to look.
///
/// It also said the paperclip takes a text file up to 256 KB, and that was the kinder half of
/// a worse problem. The file picker offers .png, .jpg, .gif and .webp. The send path handles
/// pictures properly — it reads the kind from the bytes, builds a ChatImage, and says outright
/// when the model cannot see one. The canvas takes pictures up to 4 MB through this same
/// paperclip. And the chat path capped everything at 256 KB and told anything over it that it was
/// text only.
///
/// So one button, in one app, accepted a 3 MB photograph onto a design and refused it in a
/// conversation, giving a reason that was not the reason — and almost every real photograph is
/// over 256 KB.
/// </summary>
public sealed class HelpAndPaperclipTests : BunitContext
{
    private IRenderedComponent<Concierge.Shared.Components.Pages.Help> Help()
    {
        Services.AddLogging();
        Services.AddConciergeCore();
        JSInterop.Mode = JSRuntimeMode.Loose;

        return Render<Concierge.Shared.Components.Pages.Help>();
    }

    private static string HelpSource => File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..",
        "Concierge.Shared.Components", "Pages", "Help.razor")));

    /// <summary>
    /// Nothing in Help names a control that does not exist.
    /// </summary>
    [Fact]
    public void Help_does_not_name_a_switch_that_is_not_there()
    {
        var room = Help();

        room.FindAll("button.room-sec-head")
            .Single(head => head.TextContent.Contains("When it wants to do something", StringComparison.Ordinal))
            .Click();

        Assert.DoesNotContain("May act on its own", room.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// And names the three that are, in the words the composer uses.
    /// </summary>
    [Theory]
    [InlineData("Plan only")]
    [InlineData("Ask first")]
    [InlineData("Act freely")]
    public void And_names_the_three_that_are(string mode)
    {
        var room = Help();

        room.FindAll("button.room-sec-head")
            .Single(head => head.TextContent.Contains("When it wants to do something", StringComparison.Ordinal))
            .Click();

        Assert.Contains(mode, room.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The paperclip line mentions pictures, because the picker offers them and the send path
    /// carries them.
    /// </summary>
    [Fact]
    public void And_the_paperclip_line_mentions_pictures()
        => Assert.Contains("picture up to 4 MB", Help().Markup, StringComparison.Ordinal);

    /// <summary>
    /// And the two limits are one decision rather than two numbers in two methods. This reads
    /// the source, which is crude and is what is available without driving a real file picker
    /// — there is no browser here and `InputFileChangeEventArgs` cannot be built from a test.
    /// </summary>
    [Fact]
    public void A_picture_in_a_conversation_gets_the_same_limit_as_a_picture_on_a_canvas()
    {
        var lines = File.ReadAllLines(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Concierge.Shared.Components", "Workspace", "WorkspaceBase.cs")));

        var from = Array.FindIndex(lines, line => line.Contains("Task OnAttachmentSelected", StringComparison.Ordinal));
        var to = Array.FindIndex(lines, line => line.Contains("Task PutPicturesOnThePageAsync", StringComparison.Ordinal));

        // Comments dropped: the explanation of this defect quotes the sentence it replaced,
        // and a test that matches its own evidence is measuring the wrong thing.
        var attach = string.Join(
            Environment.NewLine,
            lines[from..to].Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.Contains("MaxPictureBytes", attach, StringComparison.Ordinal);
        Assert.DoesNotContain("Text attachments only in v1", attach, StringComparison.Ordinal);
    }

    /// <summary>
    /// And Help stopped repeating the Release room's old promise that every gate says how it
    /// was checked — it was corrected there and left standing here, which is how a fixed claim
    /// survives somewhere else.
    /// </summary>
    [Fact]
    public void And_help_does_not_repeat_a_claim_that_was_corrected_elsewhere()
        => Assert.DoesNotContain("and how each was checked", HelpSource, StringComparison.Ordinal);
}
