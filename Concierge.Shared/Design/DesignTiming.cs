using System.Globalization;

namespace Concierge.Shared.Design;

/// <summary>
/// When something happens, and for how long.
///
/// A shot used to carry a single <c>seconds</c> property, which can only answer
/// "how long is this on screen". Diffusion Studio separates Delay, Trim,
/// SourceFrameRate, PlaybackRate and Workarea into traits that compose, and the
/// difference shows the moment anybody asks for something ordinary: start this a
/// beat after the last one; skip the first four seconds of that recording; play it
/// at half speed. With one number, each of those is impossible to say.
///
/// Three of their five are taken. The other two are declined on purpose, and
/// saying why is part of the decision:
///
///   **Workarea** — which slice of a timeline gets rendered. There is no timeline
///   here and there is not going to be one; it is first on the list of things
///   every renderer refuses, because it is what makes a video tool feel like a
///   cockpit to somebody who has never used one.
///
///   **SourceFrameRate** — for cutting on exact frames against source material.
///   Concierge does not decode video, so it would be a field that is stored,
///   never read, and eventually believed.
///
/// This is a reading of properties rather than a new node shape, which is what
/// keeps it honest: nothing has to be migrated, an older document simply has
/// defaults, and a model that writes only <c>seconds</c> still works exactly as
/// it did.
/// </summary>
public readonly record struct DesignTiming
{
    /// <summary>How long it stays, once it has started.</summary>
    public int Seconds { get; private init; }

    /// <summary>How long to wait before starting it.</summary>
    public int DelaySeconds { get; private init; }

    /// <summary>How far into the source to begin — the unplayed head of a recording.</summary>
    public int TrimSeconds { get; private init; }

    /// <summary>
    /// How fast it plays. 1 is as recorded, 0.5 is half speed, 2 is double.
    /// </summary>
    public double Rate { get; private init; }

    /// <summary>
    /// How long this actually occupies, waiting included and speed accounted for.
    ///
    /// The number a person means when they ask how long the film is. Rounded up,
    /// because a shot that needs 2.4 seconds gets 3 — rounding down would cut it
    /// off, and being a fraction generous is invisible while being a fraction
    /// short is a truncated word.
    /// </summary>
    public int TotalSeconds
        => DelaySeconds + Math.Max(1, (int)Math.Ceiling(Seconds / Rate));

    /// <summary>
    /// Reads the timing off something, with everything optional.
    ///
    /// Every trait falls back rather than failing. What lands in these properties
    /// comes from a model or from a person's sentence, and "a couple of seconds"
    /// does not parse — a renderer that threw on that would turn a clumsy
    /// sentence into a broken canvas.
    /// </summary>
    public static DesignTiming Of(DesignNode node, int defaultSeconds)
    {
        ArgumentNullException.ThrowIfNull(node);

        return new DesignTiming
        {
            // Bounds are the same ones the renderer already applied: a shot
            // shorter than a second cannot be read, and one over two minutes is
            // somebody having typed a phone number into the wrong box.
            Seconds = Clamp(Whole(node, "seconds", defaultSeconds), 1, 120),

            // No upper bound worth arguing about, but a minute of black before a
            // shot starts is already far past anything anybody wants.
            DelaySeconds = Clamp(Whole(node, "delay", 0), 0, 60),

            // An hour, because a trim is measured against source material that
            // may be long even when the shot that uses it is not.
            TrimSeconds = Clamp(Whole(node, "trim", 0), 0, 3600),

            // Quarter speed to quadruple. Past those, speech stops being speech
            // and the result is a novelty rather than an edit.
            Rate = Clamp(Fraction(node, "rate", 1.0), 0.25, 4.0),
        };
    }

    private static int Whole(DesignNode node, string key, int fallback)
        => node.Props.TryGetValue(key, out var raw)
           && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static double Fraction(DesignNode node, string key, double fallback)
        => node.Props.TryGetValue(key, out var raw)
           && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
           && double.IsFinite(value)
            ? value
            : fallback;

    private static int Clamp(int value, int low, int high) => Math.Clamp(value, low, high);

    private static double Clamp(double value, double low, double high) => Math.Clamp(value, low, high);
}
