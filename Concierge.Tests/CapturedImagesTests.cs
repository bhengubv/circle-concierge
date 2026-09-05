using Concierge.Shared.Chat;

namespace Concierge.Tests;

/// <summary>
/// Pictures a capability produced, on their way into the conversation.
///
/// Concierge could be shown a picture and could not take one — not a single
/// reference to screen capture in the repository. Adding the capability meant
/// answering a question the tool layer had never faced: a tool result is a
/// string, and a screenshot described in words is not a screenshot.
///
/// Carrying an image through the tool-result path would mean changing how
/// conversations are stored, which is a much larger thing and the wrong reason
/// to do it. The image channel that already exists is the composer's, so a
/// capability writes into the same queue a person attaching a file uses.
///
/// The contract that matters is draining, not reading: a picture rides on
/// exactly one turn.
/// </summary>
public sealed class CapturedImagesTests
{
    private static ChatImage APicture(string name = "screen.png")
        => new(name, "image/png", [1, 2, 3]);

    [Fact]
    public void Nothing_is_waiting_to_begin_with()
    {
        var captured = new CapturedImages();

        Assert.Equal(0, captured.Count);
        Assert.Empty(captured.TakeAll());
    }

    [Fact]
    public void A_captured_picture_waits_for_the_next_turn()
    {
        var captured = new CapturedImages();

        captured.Add(APicture());

        Assert.Equal(1, captured.Count);
        Assert.Single(captured.TakeAll());
    }

    /// <summary>
    /// The contract. Left in place, a screenshot would ride on every turn after
    /// it — a picture of a window from ten minutes ago silently attached to an
    /// unrelated question, which is worse than no screenshot at all.
    /// </summary>
    [Fact]
    public void A_picture_rides_on_exactly_one_turn()
    {
        var captured = new CapturedImages();
        captured.Add(APicture());

        Assert.Single(captured.TakeAll());
        Assert.Empty(captured.TakeAll());
        Assert.Equal(0, captured.Count);
    }

    [Fact]
    public void Several_captures_all_travel_together()
    {
        var captured = new CapturedImages();

        captured.Add(APicture("one.png"));
        captured.Add(APicture("two.png"));

        var taken = captured.TakeAll();

        Assert.Equal(2, taken.Count);
        Assert.Equal("one.png", taken[0].FileName);
    }

    /// <summary>
    /// A model calling screenshot in a loop would otherwise fill memory with
    /// bitmaps nobody asked for. The oldest go, not the newest: something that
    /// just captured wants the thing it just captured.
    /// </summary>
    [Fact]
    public void A_runaway_capture_loop_cannot_fill_memory()
    {
        var captured = new CapturedImages();

        for (var i = 0; i < 20; i++)
        {
            captured.Add(APicture($"{i}.png"));
        }

        var taken = captured.TakeAll();

        Assert.Equal(CapturedImages.MaxWaiting, taken.Count);
        Assert.Equal("19.png", taken[^1].FileName);
    }

    [Fact]
    public void A_new_conversation_starts_with_nothing_attached()
    {
        var captured = new CapturedImages();
        captured.Add(APicture());

        captured.Clear();

        Assert.Equal(0, captured.Count);
    }

    /// <summary>A surface needs to know something arrived so it can say so.</summary>
    [Fact]
    public void Arriving_announces_itself()
    {
        var captured = new CapturedImages();
        var announcements = 0;
        captured.Changed += (_, _) => announcements++;

        captured.Add(APicture());
        captured.Clear();

        Assert.Equal(2, announcements);
    }

    /// <summary>
    /// Clearing an empty queue announces nothing. A surface redrawing on every
    /// turn for a queue that was already empty is noise that eventually gets the
    /// event ignored.
    /// </summary>
    [Fact]
    public void Clearing_nothing_announces_nothing()
    {
        var captured = new CapturedImages();
        var announcements = 0;
        captured.Changed += (_, _) => announcements++;

        captured.Clear();

        Assert.Equal(0, announcements);
    }
}
