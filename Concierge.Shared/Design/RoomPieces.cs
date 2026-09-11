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
    /// Written out the way the rest of the design does it — invariant, and without a trailing
    /// ".0" on a whole number, because these end up in markup a person may well read.
    /// </summary>
    private static string Number(double value)
        => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
