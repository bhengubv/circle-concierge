using System.Text.Json;
using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// The room drawn by a real engine, and the room drawn without one.
///
/// Space had a stated ceiling: "it will not draw a mesh, a light, or a shadow".
/// It does now — three.js, MIT, carried in the repository rather than fetched,
/// because a design surface that needs the internet to draw a box is not a
/// local-first product.
///
/// **What these mostly test is the fallback**, which is the part that would
/// otherwise never be exercised. The flat room is written first and the engine
/// hides it only once it has actually drawn, so every failure — no WebGL, no GPU,
/// a webview that will not run modules, the script missing — leaves somebody
/// looking at the room this always drew rather than at an empty rectangle. The
/// usual way round has the failure path as the one nobody ever runs.
/// </summary>
public sealed class SceneEngineTests
{
    private static DesignDocument Room(params (string Text, (string Key, string Value)[] Props)[] things)
    {
        var scene = DesignDocument.Blank(medium: DesignMedium.Scene);
        var room = DesignNode.New(DesignNodeKind.Frame, null, ("text", "Kitchen"));
        scene = scene.Add(room);

        foreach (var thing in things)
        {
            scene = scene.Add(DesignNode.New(
                DesignNodeKind.Solid, room.Id, [("text", thing.Text), .. thing.Props]));
        }

        return scene;
    }

    private static (string, (string, string)[]) Solid(string text, params (string Key, string Value)[] props)
        => (text, props);

    /// <summary>The room as numbers, pulled back out of the page.</summary>
    private static JsonElement Numbers(string html)
    {
        const string open = "<script type=\"application/json\" id=\"room\">";

        var start = html.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, "The room was never written out as numbers.");

        start += open.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);

        return JsonDocument.Parse(html[start..end]).RootElement;
    }

    // ── The fallback, which is the default ────────────────────────────────

    /// <summary>
    /// Both rooms are in the page. The flat one is visible, and it is the engine
    /// that has to succeed to replace it — not the engine that has to fail for it
    /// to appear.
    /// </summary>
    [Fact]
    public void The_room_that_needs_nothing_is_the_one_that_is_drawn_first()
    {
        var html = DesignMediums.Render(Room(Solid("Table")));

        var flat = html.IndexOf("class=\"world\"", StringComparison.Ordinal);
        var deep = html.IndexOf("id=\"deep\"", StringComparison.Ordinal);

        Assert.True(flat >= 0, "The flat room is gone.");
        Assert.True(deep > flat, "The engine's canvas comes before the room it replaces.");
    }

    [Fact]
    public void The_engines_canvas_starts_hidden()
        => Assert.Contains("id=\"deep\" hidden", DesignMediums.Render(Room(Solid("Table"))), StringComparison.Ordinal);

    /// <summary>
    /// The flat room is put away only after the engine has drawn. Ordering it the
    /// other way is how a machine without WebGL ends up looking at nothing.
    /// </summary>
    [Fact]
    public void The_flat_room_is_only_put_away_after_the_engine_has_drawn()
    {
        var html = DesignMediums.Render(Room(Solid("Table")));

        var made = html.IndexOf("new THREE.WebGLRenderer", StringComparison.Ordinal);
        var away = html.IndexOf("flat.hidden = true", StringComparison.Ordinal);

        Assert.True(made >= 0 && away > made);
    }

    /// <summary>
    /// Two different failures, told apart, because for a while they were not.
    ///
    /// A machine with no engine is ordinary and says nothing. Our own code failing
    /// after the engine loaded is a defect and says so out loud. One `.catch` on
    /// the whole chain made them identical, and hid a real one: the room went over
    /// with capitalised names, the script read lowercase, and asking undefined for
    /// its length threw straight into the handler meant for a missing GPU. The
    /// room drew empty and nothing anywhere said why.
    /// </summary>
    [Fact]
    public void A_missing_engine_and_a_broken_one_are_not_the_same_failure()
    {
        var html = DesignMediums.Render(Room(Solid("Table")));

        // The rejection handler is the second argument to then, so it sees only
        // the import failing — never anything build throws, and it says which of
        // the two happened rather than swallowing both alike.
        Assert.Contains("The 3D engine is not available on this machine.", html, StringComparison.Ordinal);
        Assert.Contains("console.error('Concierge: the engine loaded and the room did not draw.'", html, StringComparison.Ordinal);

        // And a machine with no WebGL is still ordinary: the context is tried and
        // its failure just stops.
        Assert.Contains("} catch (e) {", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The names the room is written with are the names the script reads. This is
    /// the assertion that would have caught it: two files agreeing on a spelling,
    /// with nothing in between to notice when they stop.
    /// </summary>
    [Fact]
    public void The_room_is_written_with_the_names_the_script_reads()
    {
        var html = DesignMediums.Render(Room(Solid("Table")));
        var numbers = Numbers(html);

        foreach (var name in new[] { "things", "ground", "ink", "raised", "accent", "picked" })
        {
            Assert.True(numbers.TryGetProperty(name, out _), $"The room does not carry {name}.");
            Assert.Contains($"room.{name}", html, StringComparison.Ordinal);
        }

        foreach (var name in new[] { "kind", "text", "shape", "x", "y", "width", "depth", "height" })
        {
            Assert.True(
                numbers.GetProperty("things")[0].TryGetProperty(name, out _),
                $"A thing in the room does not carry {name}.");
            Assert.Contains($"thing.{name}", html, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Carried, not fetched. A CDN would make a room something you need the
    /// internet to look at.
    /// </summary>
    [Fact]
    public void The_engine_comes_from_this_machine()
    {
        var html = DesignMediums.Render(Room(Solid("Table")));

        Assert.Contains("/_content/Concierge.Shared.Components/lib/three/three.module.js", html, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", html, StringComparison.Ordinal);
    }

    // ── What the engine is given ──────────────────────────────────────────

    [Fact]
    public void The_room_is_handed_over_as_numbers_rather_than_read_back_off_the_drawing()
    {
        var numbers = Numbers(DesignMediums.Render(Room(
            Solid("Table", ("width", "160"), ("depth", "90"), ("height", "70")))));

        var thing = numbers.GetProperty("things")[0];

        Assert.Equal("Table", thing.GetProperty("text").GetString());
        Assert.Equal(160, thing.GetProperty("width").GetInt32());
        Assert.Equal(90, thing.GetProperty("depth").GetInt32());
        Assert.Equal(70, thing.GetProperty("height").GetInt32());
    }

    [Fact]
    public void The_look_goes_with_it_so_the_two_rooms_are_the_same_room()
    {
        var numbers = Numbers(DesignMediums.Render(Room(Solid("Table")).Wearing("Night")));
        var look = DesignLooks.Of("Night");

        Assert.Equal(look.Ground, numbers.GetProperty("ground").GetString());
        Assert.Equal(look.Accent, numbers.GetProperty("accent").GetString());
        Assert.Equal(look.Ink, numbers.GetProperty("ink").GetString());
    }

    /// <summary>
    /// A box is what you get without saying, because that is what a room is
    /// mostly made of and nobody should have to know the word.
    /// </summary>
    [Fact]
    public void A_solid_that_says_nothing_about_its_shape_is_a_box()
        => Assert.Equal("box", Numbers(DesignMediums.Render(Room(Solid("Table"))))
            .GetProperty("things")[0].GetProperty("shape").GetString());

    [Fact]
    public void A_shape_can_be_asked_for_and_reaches_the_engine()
        => Assert.Equal("sphere", Numbers(DesignMediums.Render(Room(
                Solid("A globe", ("shape", "Sphere")))))
            .GetProperty("things")[0].GetProperty("shape").GetString());

    [Fact]
    public void The_shapes_the_engine_knows_are_all_reachable()
    {
        var html = DesignMediums.Render(Room(Solid("Table")));

        foreach (var shape in new[] { "sphere", "cylinder", "cone" })
        {
            Assert.Contains($"case '{shape}'", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Words_in_a_room_go_over_as_signs_rather_than_solids()
    {
        var scene = DesignDocument.Blank(medium: DesignMedium.Scene);
        var room = DesignNode.New(DesignNodeKind.Frame, null, ("text", "Kitchen"));
        scene = scene.Add(room)
            .Add(DesignNode.New(DesignNodeKind.Text, room.Id, ("text", "Mind the step")));

        var thing = Numbers(DesignMediums.Render(scene)).GetProperty("things")[0];

        Assert.Equal("sign", thing.GetProperty("kind").GetString());
        Assert.Equal("Mind the step", thing.GetProperty("text").GetString());
    }

    [Fact]
    public void What_is_pointed_at_goes_over_so_the_engine_can_show_it()
    {
        var scene = DesignDocument.Blank(medium: DesignMedium.Scene);
        var room = DesignNode.New(DesignNodeKind.Frame, null, ("text", "Kitchen"));
        var table = DesignNode.New(DesignNodeKind.Solid, room.Id, ("text", "Table"));
        scene = scene.Add(room).Add(table);

        var numbers = Numbers(SceneRenderer.ToHtml(scene, table.Id));

        Assert.Equal(table.Id, numbers.GetProperty("picked").GetString());
    }

    /// <summary>
    /// A room's things carry somebody's words, and those words sit inside a script
    /// tag. A title containing a closing tag must not be able to end it.
    /// </summary>
    [Fact]
    public void Words_cannot_close_the_tag_they_are_written_inside()
    {
        var html = DesignMediums.Render(Room(Solid("</script><img onerror=alert(1)>")));

        // Not present as written — and still there as the words somebody typed.
        Assert.DoesNotContain("</script><img", html, StringComparison.Ordinal);
        Assert.Equal(
            "</script><img onerror=alert(1)>",
            Numbers(html).GetProperty("things")[0].GetProperty("text").GetString());
    }

    // ── What it still refuses ─────────────────────────────────────────────

    /// <summary>
    /// Having a real engine makes orbit-by-dragging trivially available, which is
    /// exactly why it is worth refusing again. Four arrow keys are learnable in a
    /// second and cannot be done by accident.
    /// </summary>
    [Fact]
    public void An_engine_does_not_buy_a_gesture_nobody_discovers()
    {
        var html = DesignMediums.Render(Room(Solid("Table")));

        Assert.Contains("ArrowLeft", html, StringComparison.Ordinal);
        Assert.DoesNotContain("mousemove", html, StringComparison.Ordinal);
        Assert.DoesNotContain("OrbitControls", html, StringComparison.Ordinal);
        Assert.DoesNotContain("pointermove", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only one of the two rooms takes the arrow keys, or turning left turns a room nobody is
    /// looking at.
    ///
    /// Asserted on the rule rather than on one line of it. This test used to pin the exact
    /// characters `if (window.__deep) { return; }`, and went red the day the flat room's
    /// turning became a function the page could call — a change that kept the rule exactly.
    /// A test that fails when the wording changes and passes when the behaviour changes is
    /// the wrong way round.
    /// </summary>
    [Fact]
    public void The_hidden_room_stops_listening_when_the_engine_takes_over()
    {
        var html = DesignMediums.Render(Room(Solid("Table")));

        Assert.Contains("window.__deep", html, StringComparison.Ordinal);

        // The flat room's own turning bails out when the engine has taken over, whatever it
        // bails out with.
        var at = html.IndexOf("window.__deep)", StringComparison.Ordinal);

        Assert.True(at > 0, "the flat room no longer checks whether the engine took over");
        Assert.Contains("return", html[at..(at + 40)], StringComparison.Ordinal);
    }

    /// <summary>
    /// Looking around is reachable from outside the frame.
    ///
    /// **The screen says "arrow keys to look around" and that was only true while the frame
    /// had keyboard focus** — so somebody who had just opened Design, or typed a sentence,
    /// pressed an arrow and nothing turned. The page forwards the key in; this is the door it
    /// comes through, and both rooms answer at it so the page never has to know which one is
    /// drawing.
    /// </summary>
    [Fact]
    public void Both_rooms_can_be_turned_from_outside_the_frame()
    {
        var html = DesignMediums.Render(Room(Solid("Table")));

        // Twice: the flat room defines it, and the engine replaces it once it has taken over.
        Assert.Equal(
            2,
            html.Split("window.__conciergeLook = function", StringSplitOptions.None).Length - 1);
    }

    /// <summary>
    /// The scene is one element, so picking has to end up somewhere the existing
    /// click handler already looks. It writes the same data-node attribute
    /// everything on every other surface carries — one contract, five surfaces,
    /// rather than a second way to point at things.
    /// </summary>
    [Fact]
    public void Pointing_at_a_mesh_uses_the_same_contract_as_pointing_at_anything_else()
    {
        var html = DesignMediums.Render(Room(Solid("Table")));

        Assert.Contains("canvas.setAttribute('data-node'", html, StringComparison.Ordinal);
        Assert.Contains("canvas.removeAttribute('data-node')", html, StringComparison.Ordinal);

        // During capture, so it is there before the handler on the body reads it.
        Assert.Contains("}, true);", html, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_room_says_so_rather_than_starting_an_engine_for_nothing()
    {
        var html = DesignMediums.Render(DesignDocument.Blank(medium: DesignMedium.Scene));

        Assert.Contains("No rooms yet", html, StringComparison.Ordinal);
        Assert.DoesNotContain("WebGLRenderer", html, StringComparison.Ordinal);
    }

    // ── Saying it ─────────────────────────────────────────────────────────

    /// <summary>
    /// The room had nothing you could put in it.
    ///
    /// Found by opening the app and trying, not by reading: "add a table" did
    /// nothing, and so did every other sentence, because no word anywhere mapped
    /// to a solid. Space could draw a room full of furniture and there was no way
    /// to get any furniture into it — a whole medium reachable only by a model
    /// calling a tool, on a surface whose entire claim is that you say what you
    /// want.
    /// </summary>
    [Fact]
    public void In_a_room_the_thing_you_add_is_something_you_can_walk_round()
    {
        var scene = DesignDocument.Blank(medium: DesignMedium.Scene);
        var room = DesignNode.New(DesignNodeKind.Frame, null, ("text", "Kitchen"));
        var heard = DesignSpeech.Hear(scene.Add(room), "add a block", null);

        Assert.True(heard.Understood);
        Assert.Contains(heard.Document.ChildrenOf(room.Id), n => n.Kind == DesignNodeKind.Solid);
    }

    /// <summary>
    /// A box on a page groups what is under it. A box in a room is a box. Same
    /// word, and the medium decides — which is the rule the whole surface runs on.
    /// </summary>
    [Fact]
    public void The_same_word_means_different_things_on_a_page_and_in_a_room()
    {
        var page = DesignSpeech.Hear(DesignDocument.Blank(medium: DesignMedium.Page), "add a box", null);
        Assert.Contains(page.Document.ChildrenOf(page.Document.RootId), n => n.Kind == DesignNodeKind.Box);

        var scene = DesignDocument.Blank(medium: DesignMedium.Scene);
        var room = DesignNode.New(DesignNodeKind.Frame, null, ("text", "Kitchen"));
        var heard = DesignSpeech.Hear(scene.Add(room), "add a box", null);

        Assert.Contains(heard.Document.ChildrenOf(room.Id), n => n.Kind == DesignNodeKind.Solid);
    }

    [Theory]
    [InlineData("add a ball", "sphere")]
    [InlineData("add a sphere", "sphere")]
    [InlineData("add a cylinder", "cylinder")]
    [InlineData("add a post", "cylinder")]
    [InlineData("add a cone", "cone")]
    public void A_shape_can_be_asked_for_in_words(string said, string expected)
    {
        var scene = DesignDocument.Blank(medium: DesignMedium.Scene);
        var room = DesignNode.New(DesignNodeKind.Frame, null, ("text", "Kitchen"));

        var heard = DesignSpeech.Hear(scene.Add(room), said, null);
        var solid = heard.Document.ChildrenOf(room.Id).Single();

        Assert.Equal(expected, solid.Props["shape"]);
    }

    /// <summary>
    /// Saying "block" is saying nothing about shape, and a box is what you get.
    /// Nobody should have to know a word to get the ordinary thing.
    /// </summary>
    [Fact]
    public void Saying_nothing_about_shape_leaves_it_unsaid()
    {
        var scene = DesignDocument.Blank(medium: DesignMedium.Scene);
        var room = DesignNode.New(DesignNodeKind.Frame, null, ("text", "Kitchen"));

        var solid = DesignSpeech.Hear(scene.Add(room), "add a block", null)
            .Document.ChildrenOf(room.Id).Single();

        Assert.False(solid.Props.ContainsKey("shape"));
    }

    /// <summary>
    /// Watched this fail: a ball added to a room that already had a box was drawn
    /// correctly, in exactly the same place, and looked for all the world like
    /// nothing had happened. Two things a person added must be two things a person
    /// can see.
    /// </summary>
    [Fact]
    public void Two_things_added_to_a_room_do_not_stand_in_the_same_place()
    {
        var scene = DesignDocument.Blank(medium: DesignMedium.Scene);
        var room = DesignNode.New(DesignNodeKind.Frame, null, ("text", "Kitchen"));

        var after = DesignSpeech.Hear(scene.Add(room), "add a block", null).Document;
        after = DesignSpeech.Hear(after, "add a ball", null).Document;

        var standing = after.ChildrenOf(room.Id)
            .Select(thing => (thing.Props["x"], thing.Props["y"]))
            .ToList();

        Assert.Equal(2, standing.Count);
        Assert.Equal(standing.Count, standing.Distinct().Count());
    }

    /// <summary>
    /// The same room twice, not a room that shuffles. Somebody who says "move the
    /// ball" has to be able to find the ball.
    /// </summary>
    [Fact]
    public void The_same_words_twice_make_the_same_room()
    {
        static DesignDocument Built()
        {
            var made = DesignDocument.Blank(medium: DesignMedium.Scene)
                .Add(DesignNode.New(DesignNodeKind.Frame, null, ("text", "Kitchen")));

            foreach (var said in new[] { "add a block", "add a ball", "add a cone" })
            {
                made = DesignSpeech.Hear(made, said, null).Document;
            }

            return made;
        }

        static string Places(DesignDocument document) => string.Join(
            "|",
            document.Nodes.Values
                .Where(node => node.Kind == DesignNodeKind.Solid)
                .Select(node => $"{node.Props["x"]},{node.Props["y"]}")
                .Order());

        Assert.Equal(Places(Built()), Places(Built()));
    }

    /// <summary>
    /// Every kind a person can point at has a name, because the one that has not
    /// falls through to "The page" and the strip then says something untrue.
    ///
    /// Found by pointing at a box in a room: the pick worked, the id reached the
    /// component, and the chip said "The page". Three kinds were missing — a
    /// solid, a sound and a frame — so the same silence covered a track in a
    /// running order and a slide in a deck.
    ///
    /// The test is on the renderer's vocabulary rather than the component's,
    /// because bUnit cannot click a mesh: what it holds is that every kind the
    /// document can hold has a word, so a new kind cannot be added and quietly
    /// left unnamed.
    /// </summary>
    [Fact]
    public void Every_kind_of_thing_has_a_name_a_person_would_use()
    {
        foreach (var kind in Enum.GetValues<DesignNodeKind>())
        {
            if (kind == DesignNodeKind.Page)
            {
                continue;
            }

            var word = DesignSpeech.NameFor(kind, DesignMedium.Scene);

            Assert.False(string.IsNullOrWhiteSpace(word), $"{kind} has no name.");
            Assert.NotEqual("page", word);
        }
    }

    // ── Lights and shadows, which is what was missing ─────────────────────

    [Fact]
    public void There_is_a_light_that_casts_and_a_light_that_fills()
    {
        var html = DesignMediums.Render(Room(Solid("Table")));

        Assert.Contains("DirectionalLight", html, StringComparison.Ordinal);
        Assert.Contains("HemisphereLight", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Solids_cast_shadows_and_the_floor_takes_them()
    {
        var html = DesignMediums.Render(Room(Solid("Table")));

        Assert.Contains("shadowMap.enabled = true", html, StringComparison.Ordinal);
        Assert.Contains("mesh.castShadow = true", html, StringComparison.Ordinal);
        Assert.Contains("floor.receiveShadow = true", html, StringComparison.Ordinal);
    }
}
