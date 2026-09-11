namespace Concierge.Shared.Design;

/// <summary>
/// The pieces a room is built from — walls, floors, ceilings, roofs, and the doors and
/// windows cut into them.
///
/// **Here because two callers need to make exactly the same thing and only one of them
/// existed.** `design_build` and `design_opening` could put up a wall and cut a door into it,
/// and neither could be reached by saying anything: "wall" was not a word the canvas knew,
/// nor door, window, roof, ceiling, or any word for a level. Driving the feature list on the
/// running app on 2026-09-12 found twenty of the twenty-five design tools in that position —
/// built, wired, and reachable only by a model calling them, on a machine whose model did not
/// call one in three attempts.
///
/// So the sentences were added. The obvious way to add them is to write the node-building a
/// second time inside <see cref="DesignSpeech"/>, and that is how "add a wall" and
/// `design_build` come to disagree about how thick a wall is — one of them gets a default
/// changed and the other does not. The shape of a piece is decided once, here, and both ways
/// in call it.
/// </summary>
public static class RoomPieces
{
    /// <summary>What a wall, floor, ceiling or roof is made of.</summary>
    /// <remarks>
    /// Every measurement is optional, and the defaults are the ones the tool already used, so
    /// a sentence with no numbers in it produces the piece somebody would have got by asking
    /// a model for the same thing with nothing specified. That matters more than it sounds:
    /// "add a wall" has to put up a wall somebody can see and then move, because a refusal
    /// leaves them with nothing to move.
    /// </remarks>
    public static IReadOnlyList<(string Key, string Value)> Props(
        string what,
        string? text = null,
        double? x = null,
        double? y = null,
        double? x2 = null,
        double? y2 = null,
        double? width = null,
        double? depth = null,
        double? height = null,
        double? thickness = null)
    {
        var kind = what.ToLowerInvariant();

        var props = new List<(string Key, string Value)>
        {
            ("shape", kind),
            ("text", string.IsNullOrWhiteSpace(text) ? kind : text),
            ("x", Number(x ?? 0)),
            ("y", Number(y ?? 0)),
        };

        if (kind == "wall")
        {
            // A wall needs somewhere to end. Without one it would be drawn as nothing at
            // all, so it runs two metres along rather than being refused.
            props.Add(("x2", Number(x2 ?? (x ?? 0) + 200)));
            props.Add(("y2", Number(y2 ?? (y ?? 0))));
            props.Add(("height", Number(height ?? 240)));
            props.Add(("thickness", Number(thickness ?? 12)));
        }
        else
        {
            props.Add(("width", Number(width ?? 400)));
            props.Add(("depth", Number(depth ?? 400)));
            props.Add(("height", Number(height ?? (kind == "roof" ? 90 : 240))));
        }

        return props;
    }

    /// <summary>What a door or a window is made of.</summary>
    /// <remarks>
    /// An opening belongs to its wall rather than standing beside it, which is what makes the
    /// hole real: the wall is drawn as the stretches of solid either side and the piece over
    /// the top, so there is nothing to cut and no CSG library to carry.
    /// </remarks>
    public static IReadOnlyList<(string Key, string Value)> Opening(
        string what,
        double? at = null,
        double? width = null,
        double? height = null,
        double? sill = null)
    {
        var kind = what.ToLowerInvariant();
        var door = kind == "door";

        return
        [
            ("shape", kind),
            ("text", kind),
            ("at", Number(at ?? 60)),
            ("width", Number(width ?? (door ? 90 : 120))),
            ("height", Number(height ?? (door ? 200 : 120))),

            // A door sits on the floor whatever anybody says; only a window has a sill.
            ("sill", Number(door ? 0 : sill ?? 90)),
        ];
    }

    /// <summary>The words a room can be built from, and nothing else.</summary>
    public static bool IsPiece(string what)
        => what.ToLowerInvariant() is "wall" or "floor" or "ceiling" or "roof";

    /// <summary>The words that can be cut into a wall, and nothing else.</summary>
    public static bool IsOpening(string what)
        => what.ToLowerInvariant() is "door" or "window";

    /// <summary>
    /// Puts a named thing from the catalogue into a room, and hands back the document with it
    /// in. Shared for the same reason as everything else here: "add a desk" typed into the
    /// canvas has to put in the desk `design_furnish` puts in.
    /// </summary>
    /// <remarks>
    /// The thing itself carries nothing to draw; its parts do. That keeps moving it and
    /// undoing it one act rather than several.
    /// </remarks>
    public static DesignDocument Furnish(
        DesignDocument design, string? room, CatalogueThing thing, double? x = null, double? y = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(thing);

        // Somewhere there is not already something, when nobody said where.
        //
        // This is the defect `DesignSpeech.Standing` was written for, arriving a second time
        // through a different door: a desk, a chair and a lamp all landed at the middle of
        // the floor, inside one another, and the room looked as though only the last one had
        // been added. Watched it happen on the desktop head the day the sentences went in.
        //
        // A tool that says where still gets exactly where it said.
        var (atX, atY) = Somewhere(design, room, x, y);

        var across = (int)atX;
        var back = (int)atY;

        var document = design.Add(DesignNode.New(
            DesignNodeKind.Box,
            room,
            ("text", thing.Name),
            ("x", Number(across)),
            ("y", Number(back))));

        foreach (var part in thing.Parts)
        {
            document = document.Add(DesignNode.New(
                DesignNodeKind.Solid,
                room,
                ("text", thing.Name),
                ("shape", string.IsNullOrWhiteSpace(part.Shape) ? "box" : part.Shape),
                ("width", Number(Math.Max(1, part.Width))),
                ("depth", Number(Math.Max(1, part.Depth))),
                ("height", Number(Math.Max(1, part.Height))),
                ("x", Number(across + part.X)),
                ("y", Number(back + part.Y)),
                ("sill", Number(Math.Max(0, part.Sill)))));
        }

        return document;
    }

    /// <summary>
    /// Where the next thing goes when nobody said: laid out in rows rather than at random, so
    /// adding the same things twice gives the same room, and so somebody can say "move it"
    /// about something they can see rather than hunt for one they cannot.
    ///
    /// The same arrangement <see cref="DesignSpeech"/> uses for a bare solid, and the same
    /// 440px floor — furniture standing in a different grid from the boxes beside it would
    /// look like a bug rather than a layout.
    /// </summary>
    private static (double X, double Y) Somewhere(
        DesignDocument design, string? room, double? x, double? y)
    {
        if (x is not null || y is not null)
        {
            return (x ?? 0, y ?? 0);
        }

        // The root when there is no frame, not "nowhere". A fresh Space has no frames, so
        // `Add` re-parents to the root — and counting a room that does not exist gave nought
        // every time, which put every piece of furniture in the same spot. The test caught
        // this; the first version of the fix looked right and changed nothing.
        var already = design
            .ChildrenOf(room ?? design.RootId)
            .Count(child => child.Kind == DesignNodeKind.Box);

        const int step = 120;

        return ((already % 4) * step - 180, (already / 4 % 4) * step - 180);
    }

    /// <summary>
    /// Written out the way the rest of the design does it — invariant, and without a trailing
    /// ".0" on a whole number, because these end up in markup a person may well read.
    /// </summary>
    private static string Number(double value)
        => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
