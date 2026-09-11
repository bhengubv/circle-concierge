using Bunit;
using Concierge.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// The Business APIs room, which said it had eight things and had none of them.
///
/// **Found by opening it and reading it.** "What it does — 8", a green dot and the words
/// "Built in" beside every row: a reference renderer, a request client, an OpenAPI validator,
/// an example generator, a mock server, an SDK generator, an agent that explains an API, and
/// offline API work. None of those exist anywhere in the tree.
/// `ScalarCapabilityKind.MockServer` is an enum value with nothing behind it, and no tool in
/// the registry reaches any of it.
///
/// The room's own comment said what is built and what is not was "stated per capability, not
/// averaged into a claim" — above eight capabilities all carrying the same literal `true`.
/// Seventh comment in this repository found describing behaviour that did not exist.
///
/// The honest word was already there: the room renders "Not yet" when the flag is false. It
/// had simply never been false.
/// </summary>
public sealed class ApiRoomHonestyTests : BunitContext
{
    private IRenderedComponent<Concierge.Shared.Components.Pages.BusinessApis> Room()
    {
        Services.AddLogging();
        Services.AddConciergeCore();
        JSInterop.Mode = JSRuntimeMode.Loose;

        return Render<Concierge.Shared.Components.Pages.BusinessApis>();
    }

    [Fact]
    public void Nothing_in_the_api_room_claims_to_be_built()
        => Assert.All(
            new ScalarApiService().GetSnapshot().Capabilities,
            capability => Assert.False(
                capability.BuiltInByDefault,
                $"{capability.Id} says it is built in; name what delivers it before setting this"));

    /// <summary>
    /// And the room shows that rather than keeping it in the data. The badge is the whole
    /// finding: a person reads the word, not the flag.
    /// </summary>
    [Fact]
    public void And_the_room_says_so_on_every_card()
    {
        var room = Room();

        room.FindAll("button.room-sec-head")
            .Single(head => head.TextContent.Contains("What it would do", StringComparison.Ordinal))
            .Click();

        var markup = room.Markup;

        Assert.DoesNotContain(">Built in<", markup, StringComparison.Ordinal);
        Assert.Contains("Not yet", markup, StringComparison.Ordinal);

        // And the heading agrees with the rows. "What it does" over eight rows each saying
        // "Not yet" is the same claim in a larger font.
        Assert.DoesNotContain("What it does", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The lede stopped describing the room as something you can use. "Read the reference,
    /// try a call" is an invitation to do three things it cannot do.
    /// </summary>
    [Fact]
    public void And_the_lede_stops_inviting_people_to_use_it()
        => Assert.Contains("None of it is built yet", Room().Markup, StringComparison.Ordinal);

    /// <summary>
    /// The media library showed two named files with durations, codecs and creation times
    /// recomputed on every launch, so they always looked freshly added. They are sample
    /// entries the service seeds; nothing plays them and no such files exist. The room says
    /// that above the list now — it only ever said anything when the list was empty, which is
    /// exactly backwards, because an empty list makes no claim and two named files do.
    /// </summary>
    [Fact]
    public void And_the_media_library_says_its_entries_are_samples()
    {
        // With the real media service rather than the null one — the null studio holds
        // nothing, so the room draws its (correct) empty state and proves nothing about the
        // two seeded rows a person actually sees on the desktop app.
        Services.AddLogging();
        Services.AddConciergeCore();
        Concierge.Media.ConciergeMediaServiceCollectionExtensions.AddConciergeMedia(Services);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var room = Render<Concierge.Shared.Components.Pages.BusinessApis>();

        room.FindAll("button.room-sec-head")
            .Single(head => head.TextContent.Contains("Media library", StringComparison.Ordinal))
            .Click();

        Assert.Contains("sample entries", room.Markup, StringComparison.Ordinal);
    }
}
