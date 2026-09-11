namespace Concierge.Shared.Design;

/// <summary>A look, as a person meets it: a name and a picture of itself.</summary>
/// <param name="Name">What it is called. Short, and a word people already use.</param>
/// <param name="Blurb">When you would pick it, in one plain sentence.</param>
/// <param name="Brief">
/// What this look is *for*, written for whoever is making something in it.
///
/// The blurb is the sentence under the picture — what a person reads while
/// pointing. The brief is the argument behind it, and it exists because a model
/// now drives the canvas: told only "Warm", it will add a hard-edged banner and
/// four accent colours and produce something wearing the name and none of the
/// intent.
///
/// Prose rather than more tokens, taken from open-design's 154 design-system
/// briefs. Theirs read as atmosphere → palette → type → components → motion with
/// the *reason* beside every rule, which is why they can be read by somebody who
/// is not a designer — the same bar this product sets for everything else.
/// </param>
public sealed record DesignLook(
    string Name,
    string Blurb,
    string Ink,
    string Ground,
    string Raised,
    string Accent,
    string Fonts,
    string Radius,
    string Brief);

/// <summary>
/// The looks a design can wear.
///
/// This is where a design tool normally puts a colour picker, a font menu and a
/// spacing scale, and where this one refuses to. A person choosing how something
/// should look does not want to nominate a hex value — they want to see two
/// options and point at the better one. So a look is a name and a picture of
/// itself, and everything underneath is nobody's business.
///
/// Six, because a wall of choices is the same as no choice, and because six can be
/// shown as pictures on one row without scrolling on a laptop or wrapping badly on
/// a phone.
///
/// The names are ordinary words. Not "Inter / Neutral 900 / 8px" — *Calm*, *Bold*,
/// *Warm*. A ninety-seven-year-old and a five-year-old can both tell you which of
/// those they want, which is the entire test.
/// </summary>
public static class DesignLooks
{
    public const string Default = "Calm";

    public static readonly IReadOnlyList<DesignLook> All =
    [
        new("Calm", "Quiet and easy to read. Good for most things.",
            Ink: "#1F2328", Ground: "#FFFFFF", Raised: "#F4F5F7", Accent: "#2F6FEB",
            Fonts: "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif", Radius: "10px",
            Brief: "The default, and it should look like nobody chose it. Ink on white, one blue that appears at most twice — a link, or the thing to press. Generous space between things; the page is doing the work, not the decoration. If something feels like it needs emphasis, give it room rather than colour. Nothing bounces, nothing pulses. Reach for another look when the answer is \"this needs to be noticed\" — Calm cannot shout and should not try."),

        new("Bold", "Loud and confident. Good when it has to be noticed.",
            Ink: "#0B0B0C", Ground: "#FFFFFF", Raised: "#FFE55C", Accent: "#111111",
            Fonts: "'Segoe UI', system-ui, Arial, sans-serif", Radius: "2px",
            Brief: "Loud on purpose, and that only works if almost everything else is quiet. Near-black on white with one yellow block, hard corners, and a heavy face. The yellow is a place, not a tint: one band, one panel, one word behind it — never a wash over the page and never two of them competing. Type carries the volume, so headings are large and body copy stays ordinary. If it starts to feel decorated, take something away rather than adding contrast."),

        new("Warm", "Soft and friendly. Good for people, not products.",
            Ink: "#3A2E27", Ground: "#FDF6EF", Raised: "#F3E4D4", Accent: "#C2683A",
            Fonts: "Georgia, 'Iowan Old Style', serif", Radius: "14px",
            Brief: "For people rather than products — an invitation, a note, something with a name on it. Cream ground, brown ink, a serif that looks handled rather than specified. Corners are soft and edges are few; a rule between sections is usually one rule too many. The terracotta accent is for a single small thing: a mark, an initial, one word. Warm goes wrong when it is used for data — numbers in a serif on cream read as a menu, not a report."),

        new("Night", "Dark and restful. Good for looking at for a long time.",
            Ink: "#EDEDEC", Ground: "#141414", Raised: "#1F1F1F", Accent: "#2196F3",
            Fonts: "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif", Radius: "10px",
            Brief: "Dark and meant to be looked at for hours, which makes contrast the whole discipline. Light text on near-black, and never pure white on pure black — both are already softened and pushing them further is where eye strain lives. Surfaces get lighter as they come forward, so a raised panel is brighter and not shadowed; shadow does nothing on a dark ground. One blue, used for the live thing. If a page needs more than one accent here, it needs fewer things."),

        new("Plain", "Nothing extra. Good when the words are the point.",
            Ink: "#000000", Ground: "#FFFFFF", Raised: "#EEEEEE", Accent: "#000000",
            Fonts: "Times, 'Times New Roman', serif", Radius: "0px",
            Brief: "Nothing extra, because the words are the point. Black on white, a book serif, no radius, no colour at all — the accent is the ink. Structure comes from spacing and size and nothing else: no rules, no boxes, no tints, no icons. This is the right look for a letter, a notice, a page somebody will print. It is the wrong one for anything that has to be scanned quickly, because everything is deliberately the same weight."),

        new("Fresh", "Bright and light. Good for something new.",
            Ink: "#10302A", Ground: "#F2FBF7", Raised: "#DCF3E9", Accent: "#12A277",
            Fonts: "system-ui, 'Segoe UI', Roboto, sans-serif", Radius: "18px",
            Brief: "Bright and new — a launch, a first version, something optimistic. Pale green ground, deep green ink, generous rounding that reads as friendly rather than childish. The green accent carries motion and progress; it belongs on the thing moving forward. Keep the palette to those greens and white: a second hue turns Fresh into a brochure. Space is part of the feeling, so when in doubt make the gaps bigger rather than the type larger."),
    ];

    /// <summary>
    /// The look by that name, or Calm.
    ///
    /// Never null and never an exception. A model asking for "professional" gets
    /// something readable rather than a failed edit and a blank canvas, and a
    /// person watching sees a design that did not change instead of one that broke.
    /// </summary>
    public static DesignLook Of(string? name)
        => All.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
           ?? All[0];

    /// <summary>The canonical name for whatever was asked for.</summary>
    public static string Resolve(string? name) => Of(name).Name;
}
