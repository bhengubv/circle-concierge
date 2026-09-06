using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// The design surface: say it, see it, point at it.
///
/// The bar this is built to is the spec: a five-year-old and a ninety-seven-year-
/// old can both use it. That rules out, before anything is designed, syntax,
/// modes, timelines, node graphs, file names, panels, and every word somebody
/// would have to look up. What both of them do naturally is say what they want,
/// look at it, and say "no, not like that".
///
/// Two decisions carry most of the weight, and both are tested here:
///
///   **Design acts freely.** Everywhere else in Concierge, acting asks first,
///   because acting on your files is hard to reverse. On a canvas that is exactly
///   wrong — being asked permission to try something is what makes a tool unusable
///   for the people this is for, and it teaches somebody to stop reading prompts,
///   which ruins every other approval in the product.
///
///   **So going back has to be free and total.** Not an undo stack, which is a
///   second structure that disagrees with the document sooner or later. Every
///   state is kept whole and going back is choosing one — which is also why the
///   history can be shown as pictures rather than a list of changes.
/// </summary>
public sealed class DesignTests
{
    // ── What is on the canvas ─────────────────────────────────────────────

    [Fact]
    public void A_new_design_is_an_empty_page()
    {
        var design = DesignDocument.Blank();

        Assert.True(design.IsEmpty);
        Assert.NotNull(design.Find(design.RootId));
        Assert.Empty(design.ChildrenOf(design.RootId));
    }

    [Fact]
    public void Things_added_stay_in_the_order_they_were_added()
    {
        var design = DesignDocument.Blank();

        design = design.Add(DesignNode.New(DesignNodeKind.Heading, null, ("text", "First")));
        design = design.Add(DesignNode.New(DesignNodeKind.Text, null, ("text", "Second")));

        var children = design.ChildrenOf(design.RootId);

        Assert.Equal("First", children[0].Text);
        Assert.Equal("Second", children[1].Text);
    }

    [Fact]
    public void Something_can_go_inside_a_box()
    {
        var design = DesignDocument.Blank();
        var box = DesignNode.New(DesignNodeKind.Box, null);
        design = design.Add(box);
        design = design.Add(DesignNode.New(DesignNodeKind.Text, box.Id, ("text", "Inside")));

        Assert.Single(design.ChildrenOf(box.Id));
        Assert.Single(design.ChildrenOf(design.RootId));
    }

    /// <summary>
    /// A model naming somewhere that is not there any more — a node it remembered
    /// from three edits ago. Losing whatever it was making would be a worse answer
    /// than putting it on the page.
    /// </summary>
    [Fact]
    public void Something_added_to_a_place_that_is_gone_lands_on_the_page()
    {
        var design = DesignDocument.Blank()
            .Add(DesignNode.New(DesignNodeKind.Text, "nowhere", ("text", "Orphan")));

        Assert.Single(design.ChildrenOf(design.RootId));
    }

    [Fact]
    public void Changing_one_thing_leaves_everything_else_alone()
    {
        var design = DesignDocument.Blank();
        var heading = DesignNode.New(DesignNodeKind.Heading, null, ("text", "Before"));
        var body = DesignNode.New(DesignNodeKind.Text, null, ("text", "Untouched"));
        design = design.Add(heading).Add(body);

        design = design.Set(heading.Id, "text", "After");

        Assert.Equal("After", design.Find(heading.Id)!.Text);
        Assert.Equal("Untouched", design.Find(body.Id)!.Text);
    }

    [Fact]
    public void Removing_a_box_removes_what_was_inside_it()
    {
        var design = DesignDocument.Blank();
        var box = DesignNode.New(DesignNodeKind.Box, null);
        var inner = DesignNode.New(DesignNodeKind.Text, box.Id, ("text", "Inside"));
        design = design.Add(box).Add(inner);

        design = design.Remove(box.Id);

        Assert.Null(design.Find(inner.Id));
        Assert.True(design.IsEmpty);
    }

    /// <summary>
    /// A canvas with no page is not an empty canvas, it is a broken one — and the
    /// person who said "delete that" meant the thing they were pointing at.
    /// </summary>
    [Fact]
    public void The_page_itself_cannot_be_deleted()
    {
        var design = DesignDocument.Blank();

        Assert.NotNull(design.Remove(design.RootId).Find(design.RootId));
    }

    // ── How it looks ──────────────────────────────────────────────────────

    /// <summary>
    /// This is where a design tool normally puts a colour picker. A person
    /// choosing how something should look wants to see two options and point at
    /// the better one, not nominate a hex value.
    /// </summary>
    [Fact]
    public void Looks_are_named_in_words_a_person_would_use()
    {
        Assert.Contains(DesignLooks.All, l => l.Name == "Calm");
        Assert.Contains(DesignLooks.All, l => l.Name == "Warm");
        Assert.All(DesignLooks.All, l => Assert.False(string.IsNullOrWhiteSpace(l.Blurb)));
    }

    [Fact]
    public void Wearing_a_different_look_moves_nothing_on_the_canvas()
    {
        var design = DesignDocument.Blank().Add(DesignNode.New(DesignNodeKind.Heading, null, ("text", "Hello")));

        var after = design.Wearing("Night");

        Assert.Equal("Night", after.Look);
        Assert.Single(after.ChildrenOf(after.RootId));
    }

    /// <summary>
    /// A model asking for "professional" gets something readable rather than a
    /// failed edit and a blank canvas.
    /// </summary>
    [Fact]
    public void A_look_nobody_has_heard_of_falls_back_rather_than_breaking()
    {
        Assert.Equal("Calm", DesignLooks.Resolve("professional"));
        Assert.NotNull(DesignLooks.Of(null));
    }

    // ── Seeing it ─────────────────────────────────────────────────────────

    [Fact]
    public void A_design_renders_as_a_page_you_can_look_at()
    {
        var design = DesignDocument.Blank()
            .Add(DesignNode.New(DesignNodeKind.Heading, null, ("text", "Sports day")))
            .Add(DesignNode.New(DesignNodeKind.Text, null, ("text", "Saturday at ten")));

        var html = DesignRenderer.ToHtml(design);

        Assert.Contains("<h1", html);
        Assert.Contains("Sports day", html);
        Assert.Contains("Saturday at ten", html);
    }

    /// <summary>
    /// A blank white rectangle looks like something that failed to load. An empty
    /// page should say so, in the second person.
    /// </summary>
    [Fact]
    public void An_empty_page_says_what_to_do_rather_than_showing_nothing()
        => Assert.Contains("Say what you would like", DesignRenderer.ToHtml(DesignDocument.Blank()));

    /// <summary>
    /// What makes pointing work. Without an id on every element, correcting a
    /// design means describing which part you mean — which is the work this
    /// surface exists to remove.
    /// </summary>
    [Fact]
    public void Every_element_carries_the_id_of_the_thing_it_is()
    {
        var heading = DesignNode.New(DesignNodeKind.Heading, null, ("text", "Title"));
        var html = DesignRenderer.ToHtml(DesignDocument.Blank().Add(heading));

        Assert.Contains($"{DesignRenderer.NodeAttribute}=\"{heading.Id}\"", html);
    }

    [Fact]
    public void What_you_pointed_at_is_marked_on_the_page()
    {
        var heading = DesignNode.New(DesignNodeKind.Heading, null, ("text", "Title"));
        var design = DesignDocument.Blank().Add(heading);

        Assert.Contains("class=\"picked\"", DesignRenderer.ToHtml(design, heading.Id));
        Assert.DoesNotContain("class=\"picked\"", DesignRenderer.ToHtml(design));
    }

    /// <summary>
    /// The text in a design comes from a model or from a sentence somebody typed,
    /// and it renders inside the application's own window.
    /// </summary>
    [Fact]
    public void Text_cannot_smuggle_markup_into_the_page()
    {
        var html = DesignRenderer.ToHtml(DesignDocument.Blank()
            .Add(DesignNode.New(DesignNodeKind.Heading, null, ("text", "<script>alert(1)</script>"))));

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    /// <summary>
    /// A model can write any string into a picture's address. A file:// one would
    /// pull something off the machine into a document that might be shared.
    /// </summary>
    [Fact]
    public void A_picture_from_somewhere_it_should_not_be_is_not_loaded()
    {
        Assert.True(DesignRenderer.IsSafeSource("https://example.com/a.png"));
        Assert.True(DesignRenderer.IsSafeSource("data:image/png;base64,AAAA"));
        Assert.False(DesignRenderer.IsSafeSource("file:///C:/Users/me/passport.jpg"));
        Assert.False(DesignRenderer.IsSafeSource("javascript:alert(1)"));

        var html = DesignRenderer.ToHtml(DesignDocument.Blank()
            .Add(DesignNode.New(DesignNodeKind.Image, null, ("src", "file:///C:/secrets.png"))));

        Assert.DoesNotContain("file:///", html);
        Assert.Contains("placeholder", html);
    }

    /// <summary>
    /// A picture not chosen yet is a labelled space, not a broken-image icon. The
    /// design is unfinished, not wrong.
    /// </summary>
    [Fact]
    public void A_picture_not_chosen_yet_is_a_labelled_space()
    {
        var html = DesignRenderer.ToHtml(DesignDocument.Blank()
            .Add(DesignNode.New(DesignNodeKind.Image, null, ("text", "The team"))));

        Assert.Contains("placeholder", html);
        Assert.Contains("The team", html);
    }

    // ── Going back ────────────────────────────────────────────────────────

    [Fact]
    public void A_new_session_has_nothing_to_go_back_to()
    {
        var session = new DesignSession();

        Assert.False(session.CanGoBack);
        Assert.False(session.CanGoForward);
        Assert.True(session.Current.IsEmpty);
    }

    [Fact]
    public void Every_state_it_has_been_in_is_still_there()
    {
        var session = new DesignSession();

        session.Record(session.Current.Add(DesignNode.New(DesignNodeKind.Heading, null, ("text", "One"))), "Added a heading");
        session.Record(session.Current.Add(DesignNode.New(DesignNodeKind.Text, null, ("text", "Two"))), "Added some words");

        Assert.Equal(3, session.Moments.Count);
        Assert.True(session.CanGoBack);
    }

    /// <summary>
    /// Read under a picture by somebody deciding whether that is the version they
    /// liked. "Set(node_4f2a, text)" is not that.
    /// </summary>
    [Fact]
    public void Each_state_says_what_happened_in_words()
    {
        var session = new DesignSession();
        session.Record(session.Current, "Added a heading");

        Assert.Equal("Added a heading", session.Moments[^1].What);
        Assert.Equal("Started", session.Moments[0].What);
    }

    [Fact]
    public void Going_back_returns_the_design_that_was_there()
    {
        var session = new DesignSession();
        session.Record(session.Current.Add(DesignNode.New(DesignNodeKind.Heading, null, ("text", "Oops"))), "Added a heading");

        session.Back();

        Assert.True(session.Current.IsEmpty);
        Assert.True(session.CanGoForward);
    }

    [Fact]
    public void And_forward_again_if_you_went_too_far()
    {
        var session = new DesignSession();
        session.Record(session.Current.Add(DesignNode.New(DesignNodeKind.Heading, null, ("text", "Keep"))), "Added a heading");

        session.Back();
        session.Forward();

        Assert.False(session.Current.IsEmpty);
    }

    /// <summary>
    /// The history is a strip of pictures, so choosing one has to be a single act
    /// rather than pressing back four times.
    /// </summary>
    [Fact]
    public void Any_state_can_be_picked_straight_out_of_the_history()
    {
        var session = new DesignSession();

        for (var i = 1; i <= 4; i++)
        {
            session.Record(session.Current.Add(DesignNode.New(DesignNodeKind.Text, null, ("text", $"Line {i}"))), $"Added line {i}");
        }

        session.GoTo(1);

        Assert.Single(session.Current.ChildrenOf(session.Current.RootId));
    }

    /// <summary>
    /// You went back and took a different turn, so the road you did not take is
    /// gone. The alternative is a tree, and nobody has wanted to navigate one of
    /// those to get a poster finished.
    /// </summary>
    [Fact]
    public void Changing_something_after_going_back_drops_what_came_after()
    {
        var session = new DesignSession();
        session.Record(session.Current.Add(DesignNode.New(DesignNodeKind.Heading, null, ("text", "First try"))), "Added a heading");
        session.Record(session.Current.Add(DesignNode.New(DesignNodeKind.Text, null, ("text", "Second try"))), "Added some words");

        session.Back();
        session.Record(session.Current.Add(DesignNode.New(DesignNodeKind.Button, null, ("text", "Different"))), "Added a button");

        Assert.False(session.CanGoForward);
        Assert.DoesNotContain(session.Moments, m => m.What == "Added some words");
    }

    /// <summary>
    /// A long afternoon must not accumulate a thousand copies — but the first is
    /// kept whatever happens, so "start again" always works.
    /// </summary>
    [Fact]
    public void A_long_session_forgets_the_middle_and_never_the_beginning()
    {
        var session = new DesignSession();

        for (var i = 0; i < DesignSession.MaxMoments * 2; i++)
        {
            session.Record(session.Current.Add(DesignNode.New(DesignNodeKind.Text, null, ("text", $"{i}"))), $"Added {i}");
        }

        Assert.Equal(DesignSession.MaxMoments, session.Moments.Count);
        Assert.Equal("Started", session.Moments[0].What);
        Assert.True(session.Moments[0].Document.IsEmpty);
    }

    // ── Pointing at things ────────────────────────────────────────────────

    [Fact]
    public void Pointing_at_something_gives_the_next_sentence_a_subject()
    {
        var session = new DesignSession();
        var heading = DesignNode.New(DesignNodeKind.Heading, null, ("text", "Title"));
        session.Record(session.Current.Add(heading), "Added a heading");

        session.Point(heading.Id);

        Assert.Equal(heading.Id, session.Selected);
        Assert.Equal("Title", session.Pointed!.Text);
    }

    [Fact]
    public void Pointing_at_nothing_clears_it()
    {
        var session = new DesignSession();
        var heading = DesignNode.New(DesignNodeKind.Heading, null, ("text", "Title"));
        session.Record(session.Current.Add(heading), "Added a heading");
        session.Point(heading.Id);

        session.Point(null);

        Assert.Null(session.Selected);
    }

    /// <summary>
    /// Otherwise the next sentence has a subject that is not on screen.
    /// </summary>
    [Fact]
    public void Pointing_at_something_that_is_then_deleted_stops_pointing()
    {
        var session = new DesignSession();
        var heading = DesignNode.New(DesignNodeKind.Heading, null, ("text", "Title"));
        session.Record(session.Current.Add(heading), "Added a heading");
        session.Point(heading.Id);

        session.Record(session.Current.Remove(heading.Id), "Removed the heading");

        Assert.Null(session.Selected);
    }

    [Fact]
    public void Going_back_past_the_thing_you_pointed_at_stops_pointing()
    {
        var session = new DesignSession();
        var heading = DesignNode.New(DesignNodeKind.Heading, null, ("text", "Title"));
        session.Record(session.Current.Add(heading), "Added a heading");
        session.Point(heading.Id);

        session.Back();

        Assert.Null(session.Selected);
    }

    /// <summary>
    /// A thumbnail of how it looked should show how it looked — not what happened
    /// to be pointed at while it did.
    /// </summary>
    [Fact]
    public void A_thumbnail_does_not_show_the_selection()
    {
        var session = new DesignSession();
        var heading = DesignNode.New(DesignNodeKind.Heading, null, ("text", "Title"));
        session.Record(session.Current.Add(heading), "Added a heading");
        session.Point(heading.Id);

        Assert.Contains("class=\"picked\"", session.Html());
        Assert.DoesNotContain("class=\"picked\"", session.HtmlAt(session.Position));
    }

    [Fact]
    public void The_canvas_redraws_whenever_something_changes()
    {
        var session = new DesignSession();
        var redraws = 0;
        session.Changed += (_, _) => redraws++;

        session.Record(session.Current, "Something");
        session.Back();
        session.Point(null);

        Assert.Equal(3, redraws);
    }
}
