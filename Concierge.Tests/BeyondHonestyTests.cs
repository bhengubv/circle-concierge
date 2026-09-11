using Bunit;
using Concierge.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// The Beyond Code room, which said the product could do eleven things.
///
/// **Found by opening it and reading it.** A section headed "What it can do — 11", every row
/// carrying a green **Ready**, and six of the eleven with nothing behind them in any form:
/// no memory store, no helper teams, no simulations, no server access, no small-machine
/// profiles, no security checks. Not partly built, not blocked on a key — absent. A person
/// reading that room would believe the product did all eleven today, and the room's own
/// comment claimed it had stopped being a marketing page.
///
/// It is the same defect as an approvals badge that always said two, at eleven times the
/// size, on the screen somebody reads to decide what this thing is for.
///
/// `HardeningStatus` is why it was possible: it has no word for "nobody built this", so
/// everything unbuilt was written down as Ready. <c>BeyondState</c> has that word now.
/// </summary>
public sealed class BeyondHonestyTests : BunitContext
{
    private IRenderedComponent<Concierge.Shared.Components.Pages.Beyond> Room()
    {
        Services.AddLogging();
        Services.AddConciergeCore();
        JSInterop.Mode = JSRuntimeMode.Loose;

        return Render<Concierge.Shared.Components.Pages.Beyond>();
    }

    private static IReadOnlyList<BeyondCapability> Capabilities()
        => new BeyondClaudeService().GetCapabilities();

    /// <summary>
    /// The one that matters: nothing unbuilt may claim to be here. Asserted against the data
    /// rather than the markup, because the markup only shows three of them at a time.
    /// </summary>
    [Fact]
    public void Nothing_that_is_not_built_says_it_is()
        => Assert.All(
            Capabilities().Where(c => c.State == BeyondState.NotBuilt),
            c => Assert.Equal(string.Empty, c.Where));

    /// <summary>
    /// And everything that does claim to be here names where to go and look. A claim nobody
    /// can check is what the whole room was made of.
    /// </summary>
    [Fact]
    public void And_everything_that_is_here_says_where_to_find_it()
        => Assert.All(
            Capabilities().Where(c => c.State != BeyondState.NotBuilt),
            c => Assert.False(string.IsNullOrWhiteSpace(c.Where), $"{c.Id} claims to be here and says nowhere"));

    /// <summary>
    /// Six of the eleven were the wrong way round, so this fails if anybody quietly promotes
    /// the list back to all-green. It is not a count of a good number — it is a tripwire on a
    /// number that was once eleven out of eleven.
    /// </summary>
    [Fact]
    public void Most_of_the_eleven_are_still_directions_rather_than_features()
    {
        var capabilities = Capabilities();

        Assert.Equal(11, capabilities.Count);
        Assert.True(
            capabilities.Count(c => c.State == BeyondState.NotBuilt) >= 6,
            "more of these claim to be built than were built when this was written; check each one");
    }

    /// <summary>
    /// On screen: the heading no longer says the product can do all of them, and the unbuilt
    /// half is labelled rather than badged.
    /// </summary>
    [Fact]
    public void The_room_separates_what_is_here_from_what_is_not()
    {
        var markup = Room().Markup;

        Assert.Contains("Here now", markup, StringComparison.Ordinal);
        Assert.Contains("Not built yet", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("What it can do", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the memory rooms stopped saying "Follows you" — a promise about a device somebody
    /// else is holding, for a store that has never written a byte.
    /// </summary>
    [Fact]
    public void And_the_memory_rooms_stop_claiming_to_hold_anything()
    {
        var room = Room();

        // The section is folded, so its body is not in the markup until it is opened — which
        // is worth knowing before writing an assertion about a closed section and concluding
        // the words are missing. They were there; nobody had opened it.
        room.FindAll("button.room-sec-head")
            .Single(head => head.TextContent.Contains("What it would remember", StringComparison.Ordinal))
            .Click();

        var markup = room.Markup;

        Assert.DoesNotContain(">Follows you<", markup, StringComparison.Ordinal);
        Assert.Contains("Nothing is stored in", markup, StringComparison.Ordinal);
    }
}
