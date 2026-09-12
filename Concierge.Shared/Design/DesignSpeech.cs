using System.Text.RegularExpressions;

namespace Concierge.Shared.Design;

/// <summary>What a sentence turned out to mean.</summary>
/// <param name="Understood">Whether anything happened.</param>
/// <param name="Document">The design afterwards.</param>
/// <param name="What">What changed, in words, for the history strip.</param>
/// <param name="Reply">What to say back, when nothing was understood.</param>
/// <param name="Reply">
/// What the canvas has to say, when it has something to say. Read by nobody for most of this
/// file's life — every sentence in it was dropped and the words handed to a model instead.
/// </param>
/// <param name="Final">
/// Whether that reply is the last word. True when the canvas understood the intent and is
/// explaining — "say 'board' first", "there is no wall yet" — and sending it on to a model
/// would replace a correct four-word answer with a minute of unrelated prose.
///
/// False when the canvas simply did not place the sentence. Then the reply is a fallback for
/// a host with no model at all, and anywhere there is one the model gets its turn: answering
/// everything here would quietly cut the model out of the canvas, which is the opposite
/// mistake and a worse one. A test holds that line.
/// </param>
public sealed record DesignHeard(
    bool Understood, DesignDocument Document, string What, string? Reply = null, bool Final = false);

/// <summary>
/// Understanding the ordinary sentences without asking a model.
///
/// Not a replacement for one. A model handles "make it feel like a school
/// newsletter"; this handles "add a title that says Sports Day", which is the
/// sentence people actually type first and which should not require a language
/// model, a network, or a loaded engine to work.
///
/// Three reasons it earns its place rather than being a shortcut:
///
///   The surface has to be usable while the engine is loading, or offline, or
///   broken — and on this machine it is currently all three. A canvas that does
///   nothing until a model answers is a canvas nobody can evaluate.
///
///   It is instant. "Add a title" landing in fifteen milliseconds rather than four
///   seconds is the difference between a tool that feels like a pen and one that
///   feels like a form submission, and for a five-year-old that gap is the whole
///   experience.
///
///   It is predictable. The same sentence does the same thing every time, which is
///   what somebody needs while they are still learning what they can say.
///
/// What it does not understand, it says so about — plainly, with an example. A
/// surface that silently ignores a sentence teaches people that it is broken.
/// </summary>
public static class DesignSpeech
{
    private static readonly RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Reads a sentence against the design, and whatever is being pointed at.
    /// </summary>
    /// <param name="pointedAt">
    /// What the person last touched on the canvas. This is what makes "make this
    /// bigger" mean anything — pointing supplies the subject, so the sentence does
    /// not have to describe which part it means.
    /// </param>
    /// <param name="catalogue">
    /// What a room may be furnished with, when there is one. Optional because this class is
    /// otherwise pure — it takes a document and a sentence and hands back a document — and
    /// furniture is the one thing in the vocabulary that has to be read off disk. A host that
    /// passes nothing simply cannot say "add a desk"; nothing else changes.
    /// </param>
    public static DesignHeard Hear(
        DesignDocument design, string? sentence, string? pointedAt, RoomCatalogue? catalogue = null)
    {
        ArgumentNullException.ThrowIfNull(design);

        var said = (sentence ?? string.Empty).Trim();
        if (said.Length == 0)
        {
            return new DesignHeard(false, design, string.Empty);
        }

        try
        {
            return Read(design, said, pointedAt, catalogue);
        }
        catch (RegexMatchTimeoutException)
        {
            return Puzzled(design);
        }
    }

    private static DesignHeard Read(
        DesignDocument design, string said, string? pointedAt, RoomCatalogue? catalogue)
    {
        // Start again. First, because "start again" contains words that would
        // otherwise be read as an instruction to add something.
        if (Match(said, @"^(start again|clear|empty|delete everything|start over)\b") is not null)
        {
            // Keeps the look and the medium. "Start again" means this page is
            // wrong, not that you have changed your mind about making slides.
            return new DesignHeard(true, DesignDocument.Blank(design.Look, design.Medium), "Started again");
        }

        // A look, by name — but only when the whole sentence is asking for one.
        //
        // Anchored deliberately. Scanning anywhere in the sentence meant "add a
        // title that says Night Market" changed the look and added nothing, and a
        // surface that does something adjacent to what you asked is worse than one
        // that admits it did not understand.
        foreach (var look in DesignLooks.All)
        {
            var name = Regex.Escape(look.Name);

            if (Match(said, $@"^(?:make it |use the |switch to |change to |go )?{name}(?: look| style| theme)?[.!]?$") is not null)
            {
                return new DesignHeard(true, design.Wearing(look.Name), $"Made it {look.Name.ToLowerInvariant()}");
            }
        }

        // Deleting what is being pointed at. Only what is pointed at: a sentence
        // that deletes something the person was not touching is the one mistake
        // this surface must never make, because the whole promise is try it and see.
        if (Match(said, @"^(delete|remove|get rid of|take away)\b") is not null)
        {
            if (design.Find(pointedAt) is not { } doomed)
            {
                return new DesignHeard(false, design, string.Empty,
                    "Touch the thing you want gone first, then say delete.", Final: true);
            }

            return new DesignHeard(true, design.Remove(doomed.Id), $"Removed the {NameFor(doomed.Kind, design.Medium)}");
        }

        // What a piece of music is, beyond the sound of it. "the artist is Nina
        // Simone", "the album is Wild Is The Wind", "it came out in 1965", and the
        // words.
        //
        // Applied to whatever is being pointed at, and to the design itself when
        // nothing is — because an album name said once should cover the whole
        // running order, and saying it twelve times is filling in a form.
        if (Match(said, Telling) is { } told)
        {
            var about = told.Groups["about"].Value.ToLowerInvariant() switch
            {
                "artist" or "singer" or "band" or "musician" or "composer" => "artist",
                "album" or "record" => "album",
                "year" or "date" => "year",
                "words" or "lyrics" => "lyrics",
                _ => "title",
            };

            var value = Clean(told.Groups["value"].Value);
            var which = design.Find(pointedAt)?.Id ?? design.RootId;

            return new DesignHeard(
                true, design.Set(which, about, value), $"The {about} is {value}");
        }

        // Changing what something says, while pointing at it.
        // "change it to say X", "make this say X", "change that to read X". The
        // "to" is optional because both are things people write.
        if (Match(said, @"^(?:change|make)\s+(?:it|this|that)\s+(?:to\s+)?(?:saying|says|say|reading|reads|read)\s+(?<words>.+)$") is { } reword
            && design.Find(pointedAt) is { } target)
        {
            var words = Clean(reword.Groups["words"].Value);
            return new DesignHeard(true, design.Set(target.Id, "text", words),
                $"Changed the {NameFor(target.Kind, design.Medium)}");
        }

        // Bigger and smaller, on the thing being pointed at.
        if (Match(said, @"\b(bigger|larger|smaller|tinier)\b") is { } size)
        {
            if (design.Find(pointedAt) is not { } sized)
            {
                return new DesignHeard(false, design, string.Empty,
                    "Touch the thing you want changed first, then say bigger or smaller.", Final: true);
            }

            var bigger = size.Value.StartsWith('b') || size.Value.StartsWith('l');
            return new DesignHeard(true, design.Set(sized.Id, "size", bigger ? "big" : "small"),
                bigger ? $"Made the {NameFor(sized.Kind, design.Medium)} bigger" : $"Made the {NameFor(sized.Kind, design.Medium)} smaller");
        }

        // Which kind of thing this is. "Slides", "make it a video", "space".
        foreach (var medium in DesignMediums.All)
        {
            if (Match(said, $@"^(?:make it |turn it into |as )?(?:a |an |some )?{Regex.Escape(medium.Name)}[.!]?$") is not null)
            {
                return new DesignHeard(true, design.As(medium.Medium), $"Made it {medium.Name.ToLowerInvariant()}");
            }
        }

        // Building a room: walls, floors, ceilings, roofs. Before the general "add a thing"
        // branch, because "wall" would otherwise fall through to the noun list, not be found
        // there, and the whole sentence would go to a model.
        if (Match(said, Building) is { } built)
        {
            if (design.Medium != DesignMedium.Scene)
            {
                return new DesignHeard(false, design, string.Empty,
                    "Walls and floors go in a room. Say 'space' first.", Final: true);
            }

            var piece = built.Groups["piece"].Value.ToLowerInvariant();
            var named = Clean(built.Groups["name"].Value);

            var node = DesignNode.New(
                DesignNodeKind.Solid,
                ParentFor(design, DesignNodeKind.Solid),
                [.. RoomPieces.Props(piece, named.Length > 0 ? named : null)]);

            return new DesignHeard(true, design.Add(node), $"Put up a {piece}");
        }

        // A door or a window, cut into a wall that is already there.
        if (Match(said, Cutting) is { } cut)
        {
            if (design.Medium != DesignMedium.Scene)
            {
                return new DesignHeard(false, design, string.Empty,
                    "Doors and windows go in a room. Say 'space' first.", Final: true);
            }

            var wall = WallToCut(design, pointedAt);

            if (wall is null)
            {
                // Said rather than swallowed: without this the sentence goes to a model and,
                // on a machine with none that can act, produces nothing and no reason.
                return new DesignHeard(false, design, string.Empty,
                    "There is no wall to cut into yet. Say 'add a wall' first.", Final: true);
            }

            var opening = cut.Groups["opening"].Value.ToLowerInvariant();

            return new DesignHeard(
                true,
                design.Add(DesignNode.New(
                    DesignNodeKind.Solid, wall.Id, [.. RoomPieces.Opening(opening)])),
                $"Cut a {opening}");
        }

        // Looking at a building with more than one floor.
        if (Match(said, Levels) is { } levels)
        {
            var rooms = DesignMediums.FramesOf(design);

            if (rooms.Count == 0)
            {
                return new DesignHeard(false, design, string.Empty,
                    "There is no building to look at yet. Say 'add a room' first.", Final: true);
            }

            var phrase = levels.Groups["how"].Value.ToLowerInvariant()
                + " " + levels.Value.ToLowerInvariant();

            var how = phrase.Contains("apart", StringComparison.Ordinal) ? "apart"
                : phrase.Contains("whole", StringComparison.Ordinal)
                  || phrase.Contains("together", StringComparison.Ordinal) ? "whole"
                : "one";

            var room = rooms[^1];
            var document = design.Set(room.Id, "showing", how);

            if (how == "one")
            {
                var which = levels.Groups["which"].Value;
                document = document.Set(
                    room.Id, "only", int.TryParse(which, out var floor) ? $"{Math.Max(0, floor)}" : "0");
            }

            return new DesignHeard(true, document, how switch
            {
                "apart" => "Pulled the floors apart",
                "whole" => "Showed the whole building",
                _ => "Showed one floor",
            });
        }

        // Adding something. The workhorse, and the sentence everybody types first.
        if (Match(said, Adding) is { } add)
        {
            var kind = KindOf(add.Groups["thing"].Value);
            var words = Clean(add.Groups["words"].Value);

            // A page is one surface and has no sequence to add to. Saying so beats
            // silently making a slide that nothing will ever draw.
            if (kind == DesignNodeKind.Frame && design.Medium == DesignMedium.Page)
            {
                return new DesignHeard(false, design, string.Empty,
                    "A page is one surface. Say 'slides' or 'video' first, then add one.", Final: true);
            }

            // In a room, a thing is a thing you can walk round. "Add a box" on a
            // page groups what is under it; in a room it is a box on the floor,
            // and there was previously no sentence at all that put a solid in a
            // room — the whole medium was unreachable by talking, which is the
            // only way this surface is meant to be reached.
            var shape = ShapeOf(add.Groups["thing"].Value);

            if (design.Medium == DesignMedium.Scene
                && kind is DesignNodeKind.Box or DesignNodeKind.Text)
            {
                kind = DesignNodeKind.Solid;
            }

            var where = ParentFor(design, kind);

            var node = kind == DesignNodeKind.Solid
                ? Standing(design, where, words, shape)
                : DesignNode.New(kind, where, ("text", words));

            return new DesignHeard(true, design.Add(node), $"Added {AnA(NameFor(kind, design.Medium))}");
        }

        // When a shot happens and how fast it runs.
        if (Match(said, HowLong) is { } lasts)
        {
            return Timed(design, pointedAt, "seconds", lasts, 0.1, 600,
                amount => $"Held the shot for {amount} seconds");
        }

        if (Match(said, StartsAt) is { } starts)
        {
            return Timed(design, pointedAt, "trim", starts, 0, 86_400,
                amount => $"Started the shot at {amount} seconds");
        }

        if (Match(said, WaitsFor) is { } waits)
        {
            return Timed(design, pointedAt, "delay", waits, 0, 600,
                amount => $"Waited {amount} seconds first");
        }

        if (Match(said, HowFast) is { } fast)
        {
            return Timed(design, pointedAt, "rate", fast, 0.1, 8,
                amount => $"Played the shot at {amount}x");
        }

        // Naming the thing you are on, which is what a board's small name is.
        if (Match(said, Naming) is { } naming)
        {
            var frames = design.Frames;

            if (frames.Count == 0)
            {
                return new DesignHeard(false, design, string.Empty,
                    $"There is no {naming.Groups["kind"].Value} to name yet.", Final: true);
            }

            var named = pointedAt is { Length: > 0 }
                                && design.Find(pointedAt) is { Kind: DesignNodeKind.Frame } picked
                ? picked
                : frames[^1];

            var name = Clean(naming.Groups["name"].Value);

            return new DesignHeard(
                true, design.Set(named.Id, "text", name), $"Called it {name}");
        }

        // What a panel on a board says.
        if (Match(said, PanelChange) is { } change)
        {
            var way = change.Groups["change"].Value.ToLowerInvariant();
            var amount = change.Groups["amount"].Value;

            var reads = way switch
            {
                "steady" or "flat" or "unchanged" => "steady",
                _ when amount.Length == 0 => way == "up" ? "+" : "-",
                _ => (way == "up" ? "+" : "-") + amount.TrimStart('+', '-'),
            };

            return AboutAPanel(design, pointedAt, "change", reads, $"Panel {reads}");
        }

        if (Match(said, PanelValue) is { } panel)
        {
            var value = Clean(panel.Groups["value"].Value);

            // A blank is a real answer and the one this medium is built around: an invented
            // metric is slop the moment it is invented, so "nothing" has to be sayable.
            var blank = value.ToLowerInvariant() is "blank" or "nothing" or "unknown" or "empty";

            return AboutAPanel(
                design,
                pointedAt,
                "value",
                blank ? string.Empty : value,
                blank ? "Left the panel blank" : $"Set the panel to {value}");
        }

        // How a shot looks, how it moves, and how it joins the one before it. All three take
        // a shot as their subject and had no sentence at all — `design_colour`, `design_move`
        // and `design_blend` were reachable only by a model calling them.
        if (Match(said, Grading) is { } graded)
        {
            return AboutAShot(design, pointedAt, "colour", graded.Groups["colour"].Value switch
            {
                "warmer" => "warm",
                "cooler" => "cool",
                "brighter" => "bright",
                "darker" => "dark",
                "gray" => "grey",
                "normal" or "none" => string.Empty,
                var other => other,
            });
        }

        if (Match(said, Moving) is { } moved)
        {
            var how = moved.Groups["move"].Value;

            return AboutAShot(design, pointedAt, "move", how == "still" ? string.Empty : how);
        }

        if (Match(said, Blending) is { } blended)
        {
            // Half a second, the same default the tool uses. Long enough to read as a
            // dissolve and short enough not to become the point of the film.
            return AboutAShot(
                design, pointedAt, "blend", blended.Groups["cut"].Success ? string.Empty : "0.5");
        }

        // Furniture comes last, and that ordering is the whole of it. This pattern matches
        // almost any noun, so in front of the branch above it swallowed "add a sphere" and
        // every other word the canvas already knew — nine tests went red at once saying so.
        // Known nouns win; the catalogue only gets what nothing else claimed.
        if (Match(said, Furnishing) is { } furnish)
        {
            var what = Clean(furnish.Groups["what"].Value);

            if (design.Medium != DesignMedium.Scene)
            {
                return new DesignHeard(false, design, string.Empty,
                    "Furniture goes in a room. Say 'space' first.", Final: true);
            }

            // No catalogue is not the same as nothing called that, and the two must not both
            // come back as silence. A host with no catalogue wired says so.
            if (catalogue is null)
            {
                return new DesignHeard(false, design, string.Empty,
                    "Nothing is set up to furnish a room on this device.", Final: true);
            }

            if (catalogue.Find(what) is not { } thing)
            {
                // The list comes back with the refusal: somebody who guessed once will guess
                // again otherwise, and the whole catalogue is a handful of names.
                return new DesignHeard(false, design, string.Empty,
                    $"There is nothing called {what}. There is: "
                    + string.Join(", ", catalogue.Things.Select(item => item.Name)) + ".");
            }

            return new DesignHeard(
                true,
                RoomPieces.Furnish(design, ParentFor(design, DesignNodeKind.Solid), thing),
                $"Put in a {thing.Name}");
        }


        return Puzzled(design);
    }

    /// <summary>
    /// "add a title that says Sports Day", "add a heading: Sports Day", "add some
    /// words saying Saturday at ten", "add a button".
    ///
    /// Written to accept the ways people actually phrase it rather than one
    /// canonical form. Somebody who has to discover the phrasing has been handed a
    /// syntax, which is the thing this surface exists to avoid.
    /// </summary>
    /// <summary>
    /// "add a wall", "put up a wall", "build a wall", "lay a floor", "add a roof".
    ///
    /// **`design_build` existed for months and no sentence reached it.** The word "wall" was
    /// not in this file at all, nor floor-as-a-slab, ceiling or roof — so the one medium that
    /// is meant to be a room could be given cubes and spheres by talking, and the things a
    /// room is actually made of only by a model calling a tool. On a machine whose model does
    /// not call tools, that is a feature nobody can use.
    ///
    /// "put up" and "lay" are here because they are what people say about walls and floors,
    /// and somebody who has to discover that "add" is the only verb has been handed a syntax.
    /// </summary>
    private const string Building =
        @"^(?:add|put up|put|build|lay|make|create|erect)\s+(?:a|an|some)?\s*(?<piece>wall|floor|ceiling|roof)\b"
        + @"(?:\s+(?:called|named)\s+(?<name>.+))?[.!]?$";

    /// <summary>
    /// "put a door in the wall", "add a window", "cut a door into it".
    ///
    /// Goes into the most recently built wall, or the selected one if a wall is selected.
    /// Which is a guess, and the right one: somebody who has just put up a wall and says
    /// "put a door in it" means that wall, and if they meant another they can say so to a
    /// model. Guessing beats refusing here because refusing leaves nothing on screen to
    /// correct, and correcting is how this surface works.
    /// </summary>
    private const string Cutting =
        @"^(?:add|put|cut|insert|make)\s+(?:a|an)?\s*(?<opening>door|window)\b"
        + @"(?:\s+(?:in|into|through|to)\b.*)?[.!]?$";

    /// <summary>
    /// "pull the floors apart", "show one floor", "show the whole building", "show floor 2".
    ///
    /// `design_floors` was in the same position as `design_build`: a real capability with no
    /// sentence that reached it. Pulling a building apart to look at one floor is the one
    /// thing a model of a building does that a physical model cannot, and it was reachable
    /// only by a tool call.
    /// </summary>
    private const string Levels =
        @"^(?:show|view|look at|pull|take|put|see)\s+(?:me\s+)?(?:the\s+)?"
        + @"(?<how>whole building|building|floors apart|apart|them apart|one floor|a floor|floor|level)"
        + @"(?:\s+(?:apart|together|back together))?(?:\s+(?<which>\d+))?[.!]?$";

    /// <summary>
    /// "add a desk", "put in a chair", "put a lamp in the room".
    ///
    /// The catalogue decides what the words may be, not this file — `shapes.json` is the
    /// half of Pascal's plugin idea that is safe to have, and a name somebody adds to it
    /// should be sayable the same day without a code change. So this matches a name loosely
    /// and lets the catalogue refuse it, rather than listing the furniture here and going out
    /// of date the first time anybody adds a wardrobe.
    ///
    /// Deliberately narrow on the verb side — "put in", "add", "put" — because a pattern that
    /// swallowed every unknown noun would stop anything ever reaching a model.
    /// </summary>
    /// <summary>
    /// "make the shot warm", "grade this shot cooler", "make the clip faded".
    ///
    /// **The word "shot" is required, and that is not fussiness.** The six looks are called
    /// Calm, Bold, Warm, Night, Plain and Fresh, so "make it warm" already means the Warm
    /// look and has since looks existed. Grading a shot and dressing a whole design are
    /// different acts that happen to share a word, and the only honest way to tell them apart
    /// is to make one of them say what it is about.
    /// </summary>
    private const string Grading =
        @"^(?:make|grade|turn)\s+(?:the|this|that)\s+(?:shot|clip|footage)\s+"
        + @"(?<colour>warmer|warm|cooler|cool|brighter|bright|darker|dark|grey|gray|faded|vivid|normal|none)"
        + @"(?:\s+again)?[.!]?$";

    /// <summary>
    /// "make the shot grow", "let the shot drift", "hold the shot still".
    ///
    /// A still picture held for four seconds looks like a fault, which is the whole reason
    /// `design_move` exists — and it could only be reached by a model calling it.
    /// </summary>
    private const string Moving =
        @"^(?:make|let|hold|keep)\s+(?:the|this|that)\s+(?:shot|clip|picture)\s+"
        + @"(?<move>still|fade|grow|drift)(?:\s+(?:in|out|across))?[.!]?$";

    /// <summary>
    /// "fade into the next shot", "fade between the shots", "cut to it instead".
    ///
    /// A dissolve on every join is what a first attempt looks like, so a cut stays the
    /// default and this is how somebody asks for the other thing.
    /// </summary>
    private const string Blending =
        @"^(?:(?<cut>cut)|fade|dissolve|blend)\s+(?:into|to|between|in to)?\s*"
        + @"(?:the\s+)?(?:next\s+|last\s+|this\s+)?(?:shot|clip|one|shots|it)(?:\s+instead)?[.!]?$";

    /// <summary>
    /// "set the panel to 48,200", "make the panel blank", "the panel is up 12%".
    ///
    /// A board is a small name, an enormous number and which way it is moving. `design_panel`
    /// could set all three and no sentence reached any of them, so the one medium built
    /// entirely around a number could be given empty panels by talking and nothing else.
    ///
    /// "panel" is required for the same reason "shot" is: without it, "set it to 48,200"
    /// competes with every other sentence on the surface.
    /// </summary>
    /// <summary>
    /// "call the panel Signups", "name this slide Introduction".
    ///
    /// **Every panel on a board was called "A PANEL".** The small name above the number is
    /// the frame's own text, nothing set it, and "add a title saying Signups" puts a heading
    /// *inside* the panel rather than naming it — so a board of four panels read A PANEL four
    /// times over four different numbers, which is the one thing a board must never do.
    /// </summary>
    /// <summary>
    /// How long a shot stays, where it starts, how fast it plays, how long it waits.
    ///
    /// `DesignTiming` reads all four off a shot and none of them could be said. `design_cut`
    /// reached two and only a model could call it — so a medium whose entire subject is
    /// *when* things happen had no sentence about time.
    ///
    /// Four patterns rather than one clever one. The first attempt was a single expression
    /// with everything optional, and nobody reading it could say what it matched; a rule you
    /// cannot predict is how "make the panel 12" quietly becomes a duration.
    ///
    /// The noun is required in all four, for the same reason "shot" is required for grading:
    /// "make it four seconds" has to keep meaning whatever it meant before.
    /// </summary>
    private const string HowLong =
        @"^(?:make|hold|keep|give)\s+(?:the|this|that)\s+(?<piece>shot|clip|slide|track)\s+"
        + @"(?:last\s+|run\s+for\s+|for\s+)?(?<value>[\w.]+)\s*(?:seconds?|secs?)[.!]?$";

    private const string StartsAt =
        @"^(?:start|begin|cut)\s+(?:the|this|that)\s+(?<piece>shot|clip)\s+"
        + @"(?:at|from)\s+(?<value>[\w.]+)\s*(?:seconds?|secs?)?[.!]?$";

    private const string HowFast =
        @"^(?:play|run)\s+(?:the|this|that)\s+(?<piece>shot|clip|track)\s+"
        + @"(?:at\s+)?(?<value>[\w.]+)\s*(?:speed)?[.!]?$";

    private const string WaitsFor =
        @"^(?:wait|pause|delay)\s+(?<value>[\w.]+)\s*(?:seconds?|secs?)?\s+"
        + @"before\s+(?:the|this|that)\s+(?<piece>shot|clip|slide|track)[.!]?$";

    private const string Naming =
        @"^(?:call|name)\s+(?:the|this|that)\s+(?<kind>panel|slide|shot|room|screen|track|section)\s+"
        + @"(?<name>.+?)[.!]?$";

    private const string PanelValue =
        @"^(?:set|make|put)\s+(?:the|this|that)\s+panel\s+(?:to\s+|at\s+)?(?<value>.+?)[.!]?$";

    /// <summary>
    /// Which way it is moving, said the way somebody says it.
    /// </summary>
    private const string PanelChange =
        @"^(?:the\s+|this\s+)?panel\s+is\s+(?<change>up|down|steady|flat|unchanged)"
        + @"(?:\s+(?<amount>[-+]?[\d][\d.,]*%?))?[.!]?$";

    private const string Furnishing =
        @"^(?:add|put in|put|place|bring in)\s+(?:a|an|the|some)?\s*(?<what>[a-z][a-z \-]{1,28}?)"
        + @"(?:\s+(?:in|into|to)(?:\s+the)?(?:\s+room)?)?[.!]?$";

    private const string Adding =
        @"^(?:add|put|insert|make|create)\s+(?:a|an|some)?\s*(?<thing>title|heading|header|words|text|paragraph|sentence|button|picture|image|photo|screen|panel|box|group|block|solid|shape|cube|sphere|ball|cylinder|column|post|cone|slide|shot|scene|room|track|section)\b(?:\s*(?:that\s+)?(?:saying|says|say|reading|reads|read|with|of|:)\s*(?<words>.+))?$";

    /// <summary>
    /// "the artist is Nina Simone", "the album is called Wild Is The Wind", "it
    /// came out in 1965", "the words are ...".
    ///
    /// Written for the ways somebody says it rather than one form, like every
    /// other sentence here. A person who has to discover the phrasing has been
    /// handed a syntax.
    /// </summary>
    private const string Telling =
        @"^(?:the\s+)?(?<about>artist|singer|band|musician|composer|album|record|year|date|words|lyrics|title|name)\s+"
        + @"(?:is|are|was|were)\s*(?:called\s+)?(?<value>.+)$";

    /// <summary>
    /// Which wall a door or a window goes into.
    ///
    /// Whatever is being pointed at, if that is a wall — somebody who has clicked a wall and
    /// says "put a door in it" means that one. Otherwise the wall most recently added, which
    /// is the one they just built and are looking at.
    ///
    /// A guess, and the right one. Refusing until somebody names a wall would mean naming
    /// walls, and there is nothing on this surface that gives a wall a name a person could
    /// use. Guessing wrong leaves a door in the wrong wall, which can be seen and undone;
    /// refusing leaves nothing on screen at all.
    /// </summary>
    /// <summary>
    /// Sets one timing property on the shot somebody means.
    /// </summary>
    /// <remarks>
    /// Bounded rather than trusted, the same way the tools bound what a model asks for: a
    /// shot of nought seconds is a shot nobody can see, and one of four hours is a mistake
    /// rather than a wish. Out-of-range is clamped, not refused — somebody who said "half a
    /// second" and got a tenth can see it and say it again, and a refusal leaves nothing on
    /// screen to correct.
    /// </remarks>
    private static DesignHeard Timed(
        DesignDocument design,
        string? pointedAt,
        string property,
        Match match,
        double least,
        double most,
        Func<string, string> said)
    {
        if (Amount(match.Groups["value"].Value) is not { } value)
        {
            return new DesignHeard(false, design, string.Empty,
                $"I did not catch how {(property == "rate" ? "fast" : "long")} that should be.",
                Final: true);
        }

        var frames = design.Frames;

        if (frames.Count == 0)
        {
            return new DesignHeard(false, design, string.Empty,
                $"There is nothing to time yet. Say 'add a {match.Groups["piece"].Value}' first.",
                Final: true);
        }

        var piece = pointedAt is { Length: > 0 }
                    && design.Find(pointedAt) is { Kind: DesignNodeKind.Frame } picked
            ? picked
            : frames[^1];

        var bounded = Math.Clamp(value, least, most);
        var written = bounded.ToString("0.###", Culture);

        return new DesignHeard(true, design.Set(piece.Id, property, written), said(written));
    }

    /// <summary>
    /// A number, written however somebody writes one.
    ///
    /// "four seconds" and "half speed" are what people say, and a number nobody can write in
    /// words is a syntax — which is the one thing this surface is built to avoid.
    /// </summary>
    private static double? Amount(string word) => word.ToLowerInvariant() switch
    {
        "a" or "an" or "one" => 1,
        "two" or "twice" or "double" => 2,
        "three" => 3,
        "four" => 4,
        "five" => 5,
        "six" => 6,
        "seven" => 7,
        "eight" => 8,
        "nine" => 9,
        "ten" => 10,
        "half" => 0.5,
        "normal" or "normally" => 1,
        var other when double.TryParse(
            other.TrimEnd('x'), System.Globalization.NumberStyles.Float, Culture, out var parsed) => parsed,
        _ => null,
    };

    /// <summary>
    /// Sets one field on the panel somebody means — the one being pointed at, or the last.
    /// </summary>
    private static DesignHeard AboutAPanel(
        DesignDocument design, string? pointedAt, string field, string value, string what)
    {
        if (design.Medium != DesignMedium.Board)
        {
            return new DesignHeard(false, design, string.Empty,
                "Panels are on a board. Say 'board' first.", Final: true);
        }

        var panels = design.Frames;

        if (panels.Count == 0)
        {
            return new DesignHeard(false, design, string.Empty,
                "There are no panels yet. Say 'add a panel' first.", Final: true);
        }

        var panel = pointedAt is { Length: > 0 }
                    && design.Find(pointedAt) is { Kind: DesignNodeKind.Frame } picked
            ? picked
            : panels[^1];

        return new DesignHeard(true, design.Set(panel.Id, field, value), what);
    }

    /// <summary>
    /// Sets one property on the shot somebody means, and says what happened in their words.
    ///
    /// The shot being pointed at, or the last one — the same guess <c>WallToCut</c> makes and
    /// for the same reason: there is nothing on this surface that gives a shot a name a
    /// person could use, so refusing until one is named would mean naming shots.
    /// </summary>
    private static DesignHeard AboutAShot(
        DesignDocument design, string? pointedAt, string property, string value)
    {
        if (design.Medium is not (DesignMedium.Motion or DesignMedium.Deck))
        {
            return new DesignHeard(false, design, string.Empty,
                "Shots are in a video. Say 'video' first.", Final: true);
        }

        var shots = design.Frames;

        if (shots.Count == 0)
        {
            return new DesignHeard(false, design, string.Empty,
                "There are no shots yet. Say 'add a shot' first.", Final: true);
        }

        var shot = pointedAt is { Length: > 0 } && design.Find(pointedAt) is { Kind: DesignNodeKind.Frame } picked
            ? picked
            : shots[^1];

        var what = property switch
        {
            "colour" => value.Length == 0 ? "Ungraded the shot" : $"Graded the shot {value}",
            "move" => value.Length == 0 ? "Held the shot still" : $"Made the shot {value}",
            _ => value.Length == 0 ? "Cut to the shot" : "Faded into the shot",
        };

        return new DesignHeard(true, design.Set(shot.Id, property, value), what);
    }

    private static DesignNode? WallToCut(DesignDocument design, string? pointedAt)
    {
        static bool IsWall(DesignNode node)
            => node.Kind == DesignNodeKind.Solid
               && node.Props.TryGetValue("shape", out var shape)
               && shape.Equals("wall", StringComparison.OrdinalIgnoreCase);

        if (pointedAt is { Length: > 0 } && design.Find(pointedAt) is { } picked && IsWall(picked))
        {
            return picked;
        }

        // The room's children in the order somebody put them there. `Nodes` is a dictionary
        // and has no order, so "the wall I just built" cannot be asked of it.
        //
        // Falling back to the root is the case that matters, not an edge one: a fresh Space
        // has no frames at all, so `Add` re-parents a wall to the root and "add a wall, put a
        // door in it" — the first two sentences anybody says — found nothing to cut into.
        var room = ParentFor(design, DesignNodeKind.Solid) ?? design.RootId;

        return design.ChildrenOf(room).LastOrDefault(IsWall);
    }

    /// <summary>
    /// Whether somebody asked for the design as a file, and in what form.
    ///
    /// Separate from <see cref="Hear"/> because saving is the one thing on this surface that
    /// is not a document going in and a document coming out — it writes a file and changes
    /// nothing. So it cannot be a <see cref="DesignHeard"/>, and the workspace calls this,
    /// awaits the tool, and says where the file went.
    ///
    /// **"save it" had nowhere to go at all** until this existed: `design_save` was reachable
    /// only by a model, on the surface whose whole answer to open-design is "real files", and
    /// a page, a deck and a room need no encoder to produce one.
    /// </summary>
    /// <returns>
    /// The form asked for — "pdf", "pptx" — or an empty string for whatever suits the medium.
    /// Null when this was not a save at all.
    /// </returns>
    public static string? HeardASave(string? sentence)
    {
        var said = (sentence ?? string.Empty).Trim();

        if (said.Length == 0)
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            said,
            SavingPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        if (!match.Success)
        {
            return null;
        }

        return match.Groups["as"].Value.ToLowerInvariant() switch
        {
            "pdf" => "pdf",
            "powerpoint" or "pptx" or "slides file" => "pptx",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// "save it", "save it as a pdf", "give me the file", "export it to powerpoint".
    ///
    /// "Export" is in there reluctantly — it is the word every other tool uses and the one
    /// people will type, and refusing to understand it to make a point about vocabulary would
    /// be the surface being clever at somebody's expense.
    /// </summary>
    private const string SavingPattern =
        @"^(?:save|export|download|keep)\s+(?:it|this|the\s+\w+)?\s*"
        + @"(?:(?:as|to|in)\s+(?:an?\s+)?(?<as>pdf|powerpoint|pptx|slides file|file))?[.!]?$"
        + @"|^(?:give|hand)\s+me\s+(?:the|a)\s+(?<as>pdf|powerpoint|pptx|file)[.!]?$";

    private static Match? Match(string input, string pattern)
    {
        var match = Regex.Match(input, pattern, Options, Patience);
        return match.Success ? match : null;
    }

    private static DesignNodeKind KindOf(string word) => word.ToLowerInvariant() switch
    {
        "title" or "heading" or "header" => DesignNodeKind.Heading,
        "button" => DesignNodeKind.Button,
        "picture" or "image" or "photo" => DesignNodeKind.Image,
        "box" or "group" => DesignNodeKind.Box,
        "block" or "solid" or "shape" or "cube" or "sphere" or "ball"
            or "cylinder" or "column" or "post" or "cone" => DesignNodeKind.Solid,
        "slide" or "shot" or "scene" or "room" or "track" or "section"
            or "screen" or "panel" => DesignNodeKind.Frame,
        _ => DesignNodeKind.Text,
    };

    /// <summary>
    /// A solid, standing somewhere there is not already one.
    ///
    /// Everything a person adds lands at the middle of the floor otherwise, and
    /// the second thing is inside the first. Watched exactly that happen: a ball
    /// added to a room with a box in it was drawn correctly, in the same place,
    /// and looked for all the world like nothing had been added.
    ///
    /// Laid out in rows rather than at random, so adding the same things twice
    /// gives the same room, and so somebody can say "move it" about a thing they
    /// can see rather than hunt for one they cannot.
    /// </summary>
    private static DesignNode Standing(
        DesignDocument design, string? room, string words, string? shape)
    {
        var already = room is null
            ? 0
            : design.ChildrenOf(room).Count(child => child.Kind == DesignNodeKind.Solid);

        // A 440px floor, four to a row, kept off the edges.
        const int step = 120;
        var x = (already % 4) * step - 180;
        var y = (already / 4 % 4) * step - 180;

        return shape is null
            ? DesignNode.New(DesignNodeKind.Solid, room,
                ("text", words), ("x", x.ToString(Culture)), ("y", y.ToString(Culture)))
            : DesignNode.New(DesignNodeKind.Solid, room,
                ("text", words), ("shape", shape),
                ("x", x.ToString(Culture)), ("y", y.ToString(Culture)));
    }

    private static readonly System.Globalization.CultureInfo Culture =
        System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>
    /// The shape somebody actually named, or null when they said no more than
    /// "block".
    ///
    /// Separate from the kind because a sphere and a cube are the same thing in a
    /// room, differing only in how they are drawn — folding that into the kind
    /// would put a drawing decision into the document, which is the one thing the
    /// document is supposed to know nothing about.
    /// </summary>
    private static string? ShapeOf(string word) => word.ToLowerInvariant() switch
    {
        "sphere" or "ball" => "sphere",
        "cylinder" or "column" or "post" => "cylinder",
        "cone" => "cone",
        _ => null,
    };

    /// <summary>
    /// Where a new thing goes.
    ///
    /// Onto the last frame when there is one. Somebody who has just made a slide
    /// and then says "add a title" means on that slide — putting it beside the
    /// slides instead would be technically defensible and obviously wrong.
    /// </summary>
    private static string? ParentFor(DesignDocument design, DesignNodeKind kind)
        => kind == DesignNodeKind.Frame || design.Frames.Count == 0
            ? null
            : design.Frames[^1].Id;

    /// <summary>
    /// What to call a thing when telling somebody what just happened. The words
    /// under a picture in the history strip, so they are the ones a person uses.
    ///
    /// Public because the canvas needs the same words when it says what you have
    /// just pointed at, and it had its own list. The two disagreed: this one names
    /// every kind, and that one had no case for a solid, a sound or a frame, so
    /// pointing at a block in a room said "The page". Two vocabularies for one
    /// canvas is how they drift, which is the rule the tools were written under
    /// and the component was not.
    /// </summary>
    public static string NameFor(DesignNodeKind kind, DesignMedium medium = DesignMedium.Page) => kind switch
    {
        DesignNodeKind.Heading => "title",
        DesignNodeKind.Text => "words",
        DesignNodeKind.Button => "button",
        DesignNodeKind.Image => "picture",
        DesignNodeKind.Box => "box",
        DesignNodeKind.Sound => "sound",
        DesignNodeKind.Solid => "block",
        // Named for what this document is: a slide in a deck, a shot in a video,
        // a room in a space. One word, and it is the one the person just used.
        DesignNodeKind.Frame => DesignMediums.PieceOf(medium),
        _ => "page",
    };

    /// <summary>
    /// A name with the right word in front of it.
    ///
    /// **The strip said "Added a words".** `NameFor` answers "words" for a paragraph, which
    /// reads correctly everywhere it is used with "the" and wrongly in the one place it is
    /// used with "a" — so every paragraph anybody has ever added has been recorded under a
    /// picture in the history strip with that on it.
    ///
    /// Small, and on the surface whose whole argument is that it can be read by a five-year-
    /// old and a ninety-seven-year-old. Both of them can see it.
    /// </summary>
    private static string AnA(string name)
        => name.EndsWith('s')
            ? name
            : (name.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(name[0])) ? "an " : "a ") + name;

    /// <summary>
    /// Strips the quotes and the trailing full stop people type around a phrase
    /// they are quoting. Nobody wants a heading that reads <c>"Sports Day".</c>
    /// </summary>
    private static string Clean(string words)
        => words.Trim().Trim('"', '\'', '.', ' ').Trim();

    private static DesignHeard Puzzled(DesignDocument design)
        => new(false, design, string.Empty,
            "I did not catch that. Try: add a title that says Sports Day.");
}
