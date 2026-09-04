using AngleSharp.Dom;
using Bunit;
using Concierge.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Concierge.Tests;

/// <summary>
/// The seven rooms, plus the approval queue.
///
/// Rooms are the one part of Concierge that is not a conversation — Pricing,
/// Roadmap, Release, Product, Engineering, Beyond Code, Business APIs. They
/// kept their routes through the redesign because they are destinations you go
/// and read, and the workspace sidebar is now the only thing that reaches them.
///
/// What these tests can and cannot check, stated once: bUnit has no layout
/// engine, so nothing here knows whether a room FITS. That is measured against
/// the running desktop app — every room ran past the bottom of the window on
/// the first build, Pricing by 1303px, and no assertion in this file moved.
/// What is asserted here is the structure that makes fitting possible: one
/// section unfolded on arrival, and long lists capped.
///
/// Synchronous by design; see WorkspaceHandoffTests for why.
/// </summary>
public sealed class RoomTests : BunitContext
{
    private void Compose()
    {
        Services.AddLogging();
        Services.AddConciergeCore();
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<T> Open<T>() where T : Microsoft.AspNetCore.Components.IComponent
    {
        Compose();
        return Render<T>();
    }

    private static IElement SectionHeader<T>(IRenderedComponent<T> cut, string title)
        where T : Microsoft.AspNetCore.Components.IComponent
        => cut.FindAll("button.room-sec-head")
              .First(b => b.QuerySelector(".room-sec-name")!.TextContent.Trim() == title);

    // ── What every room owes the reader ───────────────────────────────────

    /// <summary>
    /// A room opens with a heading you can see and a screen reader announces,
    /// and a way back to the work. The layout is @Body and nothing else, so if
    /// a room does not draw its own way out there is not one.
    ///
    /// One test over every room rather than a [Theory]: bUnit 2 renders through
    /// a generic Render&lt;T&gt;() and has no overload taking a Type, so a room
    /// cannot be passed as InlineData.
    /// </summary>
    [Fact]
    public void Every_room_names_itself_and_offers_a_way_back()
    {
        Compose();

        Check<Concierge.Shared.Components.Pages.Product>("Product");
        Check<Concierge.Shared.Components.Pages.Engineering>("Engineering");
        Check<Concierge.Shared.Components.Pages.Beyond>("Beyond Code");
        Check<Concierge.Shared.Components.Pages.BusinessApis>("Business APIs");
        Check<Concierge.Shared.Components.Pages.Roadmap>("Roadmap");
        Check<Concierge.Shared.Components.Pages.Release>("Release");
        Check<Concierge.Shared.Components.Pages.Pricing>("Pricing");
        Check<Concierge.Shared.Components.Pages.Approvals>("Approvals");

        void Check<T>(string expectedName) where T : Microsoft.AspNetCore.Components.IComponent
        {
            var cut = Render<T>();
            Assert.Equal(expectedName, cut.Find(".room h1").TextContent.Trim());
            Assert.Equal("/", cut.Find("a.room-back").GetAttribute("href"));
        }
    }

    /// <summary>
    /// One section unfolded on arrival. Scrollbars are hidden app-wide, so a
    /// room that opens with everything expanded runs past the bottom of the
    /// window and says nothing about it — which is exactly what happened, in
    /// all seven, before the sections folded.
    ///
    /// Roadmap is not here: it has a single section, so "exactly one open" is
    /// true of it for a reason that has nothing to do with this guarantee.
    /// </summary>
    [Fact]
    public void A_room_opens_with_exactly_one_section_unfolded()
    {
        Compose();

        Check<Concierge.Shared.Components.Pages.Product>();
        Check<Concierge.Shared.Components.Pages.Engineering>();
        Check<Concierge.Shared.Components.Pages.Beyond>();
        Check<Concierge.Shared.Components.Pages.BusinessApis>();
        Check<Concierge.Shared.Components.Pages.Release>();
        Check<Concierge.Shared.Components.Pages.Pricing>();

        void Check<T>() where T : Microsoft.AspNetCore.Components.IComponent
        {
            var cut = Render<T>();
            var headers = cut.FindAll("button.room-sec-head");
            var open = headers.Count(b => b.GetAttribute("aria-expanded") == "true");

            Assert.True(headers.Count > 1, $"{typeof(T).Name} should have several sections to fold");
            Assert.True(open == 1, $"{typeof(T).Name} opened with {open} sections unfolded, expected 1");
        }
    }

    [Fact]
    public void A_folded_section_renders_none_of_its_content_until_it_is_asked()
    {
        var cut = Open<Concierge.Shared.Components.Pages.Release>();

        // "Evidence" is folded on arrival.
        Assert.Equal(1, cut.FindAll(".room-sec-body").Count);

        SectionHeader(cut, "Evidence").Click();

        Assert.Equal(2, cut.FindAll(".room-sec-body").Count);
    }

    /// <summary>
    /// A fold must hide the content and not the fact that there is any — the
    /// count on the header is what stops a folded section reading as an empty
    /// one.
    /// </summary>
    [Fact]
    public void A_folded_section_still_says_how_much_is_behind_it()
    {
        var cut = Open<Concierge.Shared.Components.Pages.Pricing>();

        var plans = SectionHeader(cut, "Plans");
        Assert.Equal("false", plans.GetAttribute("aria-expanded"));
        Assert.False(string.IsNullOrWhiteSpace(plans.QuerySelector(".room-sec-count")?.TextContent));
    }

    // ── The long lists ────────────────────────────────────────────────────

    /// <summary>
    /// Fifteen roadmap entries ran the room 540px past the bottom of the
    /// window. The order is the content here, so this shows the top of the
    /// order and offers the rest — it does not filter or re-rank.
    /// </summary>
    [Fact]
    public void The_roadmap_shows_the_top_of_the_order_and_offers_the_rest()
    {
        var cut = Open<Concierge.Shared.Components.Pages.Roadmap>();

        var shown = cut.FindAll(".room-sec-body .row");
        Assert.True(shown.Count <= 5, $"expected at most 5 entries, found {shown.Count}");

        cut.Find("button.ws-more").Click();

        Assert.True(cut.FindAll(".room-sec-body .row").Count > shown.Count);
    }

    [Fact]
    public void Beyond_shows_some_capabilities_and_offers_the_rest()
    {
        var cut = Open<Concierge.Shared.Components.Pages.Beyond>();

        var shown = cut.FindAll(".room-grid .room-card");
        Assert.True(shown.Count <= 3, $"expected at most 3 cards, found {shown.Count}");

        cut.Find("button.ws-more").Click();

        Assert.True(cut.FindAll(".room-grid .room-card").Count > shown.Count);
    }

    // ── What each room is actually for ────────────────────────────────────

    /// <summary>
    /// Capabilities are named in plain English. BeyondCapability carries both
    /// Name and PlainEnglishName precisely because the internal one is not for
    /// reading.
    /// </summary>
    [Fact]
    public void Beyond_names_capabilities_in_plain_english()
    {
        var cut = Open<Concierge.Shared.Components.Pages.Beyond>();
        var expected = new BeyondClaudeService().GetCapabilities();

        var names = cut.FindAll(".room-card-name").Select(e => e.TextContent.Trim()).ToArray();

        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.Contains(expected, c => c.PlainEnglishName == n));
    }

    /// <summary>
    /// A capability without its boundary is a claim rather than a description,
    /// so the limit is rendered with it rather than behind it.
    /// </summary>
    [Fact]
    public void Beyond_states_the_limit_beside_the_capability()
    {
        var cut = Open<Concierge.Shared.Components.Pages.Beyond>();

        Assert.All(cut.FindAll(".room-card"),
                   card => Assert.Contains("Limit:", card.TextContent));
    }

    /// <summary>
    /// The room that earns trust: what only reads is separated from what can
    /// change something, because "it asks first" is the claim this room exists
    /// to let somebody check.
    /// </summary>
    [Fact]
    public void Engineering_separates_what_reads_from_what_changes()
    {
        var cut = Open<Concierge.Shared.Components.Pages.Engineering>();

        var titles = cut.FindAll(".room-sec-name").Select(e => e.TextContent.Trim()).ToArray();
        Assert.Contains("Reads only", titles);
        Assert.Contains("Can change things", titles);

        SectionHeader(cut, "Can change things").Click();

        Assert.Contains(cut.FindAll(".room-sec-body .state").Select(e => e.TextContent),
                        t => t.Contains("Asks first"));
    }

    /// <summary>
    /// The half a pricing page usually leaves out. Both come from the service
    /// already; rendering only the included list would have been the easy and
    /// dishonest version.
    /// </summary>
    [Fact]
    public void Pricing_shows_what_is_not_included_and_when_you_outgrow_it()
    {
        var cut = Open<Concierge.Shared.Components.Pages.Pricing>();
        SectionHeader(cut, "Plans").Click();

        var text = cut.Find(".room").TextContent;
        Assert.Contains("Not included:", text);
        Assert.Contains("Outgrow it when:", text);
    }

    /// <summary>
    /// A gate without its evidence is a tick box.
    /// </summary>
    [Fact]
    public void Release_pairs_every_gate_with_how_it_was_checked()
    {
        var cut = Open<Concierge.Shared.Components.Pages.Release>();
        var gates = new ConciergeStateService().GetSnapshot().ProductionGates;

        SectionHeader(cut, "Evidence").Click();

        var evidence = cut.FindAll(".room-sec-body .row-val").Select(e => e.TextContent.Trim()).ToArray();
        Assert.All(gates, gate => Assert.Contains(gate.Evidence, evidence));
    }

    [Fact]
    public void Product_points_at_every_other_room()
    {
        var cut = Open<Concierge.Shared.Components.Pages.Product>();
        SectionHeader(cut, "The other rooms").Click();

        var hrefs = cut.FindAll(".room-sec-body a.row").Select(a => a.GetAttribute("href")).ToArray();

        Assert.Equal(
            new[] { "engineering", "beyond", "business-apis", "roadmap", "release", "pricing" },
            hrefs);
    }

    [Fact]
    public void Business_apis_says_what_is_built_in_and_what_is_not()
    {
        var cut = Open<Concierge.Shared.Components.Pages.BusinessApis>();
        SectionHeader(cut, "What it does").Click();

        var states = cut.FindAll(".room-card .state").Select(e => e.TextContent.Trim()).ToArray();
        Assert.NotEmpty(states);
        Assert.All(states, s => Assert.True(s is "Built in" or "Not yet", $"unexpected state '{s}'"));
    }

    // ── The queue ─────────────────────────────────────────────────────────

    [Fact]
    public void The_queue_puts_the_riskiest_first()
    {
        var cut = Open<Concierge.Shared.Components.Pages.Approvals>();
        var expected = new ConciergeStateService().GetSnapshot().Approvals
            .OrderByDescending(a => (a.Risk ?? "").ToLowerInvariant() switch
            {
                "critical" => 4,
                "high" => 3,
                "medium" => 2,
                "low" => 1,
                _ => 0
            })
            .ThenBy(a => a.CreatedAt)
            .Select(a => a.Title)
            .ToArray();

        var shown = cut.FindAll(".room .row-name").Select(e => e.TextContent.Trim()).ToArray();

        Assert.NotEmpty(shown);
        Assert.All(expected.Zip(shown), pair => Assert.StartsWith(pair.First, pair.Second));
    }

    /// <summary>
    /// Deciding, and then saying so. The old build rendered risk as a coloured
    /// pill, which made "low" shout as loudly as "critical" and made the queue
    /// read as an alarm panel — status is a dot and a word everywhere else.
    /// </summary>
    [Fact]
    public void Deciding_replaces_the_choice_with_a_dot_and_a_word()
    {
        var cut = Open<Concierge.Shared.Components.Pages.Approvals>();

        Assert.Empty(cut.FindAll(".room .pill"));

        var before = cut.FindAll(".ask-actions").Count;
        Assert.True(before > 0, "expected something waiting on a decision");

        cut.FindAll(".ask-actions button.btn-primary")[0].Click();

        Assert.Equal(before - 1, cut.FindAll(".ask-actions").Count);
        Assert.Contains(cut.FindAll(".room .state").Select(e => e.TextContent.Trim()), t => t == "Allowed");
    }
}
