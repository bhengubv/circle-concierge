namespace Concierge.Shared.Design;

/// <summary>A look, as a person meets it: a name and a picture of itself.</summary>
/// <param name="Name">What it is called. Short, and a word people already use.</param>
/// <param name="Blurb">When you would pick it, in one plain sentence.</param>
public sealed record DesignLook(
    string Name,
    string Blurb,
    string Ink,
    string Ground,
    string Raised,
    string Accent,
    string Fonts,
    string Radius);

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
            Fonts: "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif", Radius: "10px"),

        new("Bold", "Loud and confident. Good when it has to be noticed.",
            Ink: "#0B0B0C", Ground: "#FFFFFF", Raised: "#FFE55C", Accent: "#111111",
            Fonts: "'Segoe UI', system-ui, Arial, sans-serif", Radius: "2px"),

        new("Warm", "Soft and friendly. Good for people, not products.",
            Ink: "#3A2E27", Ground: "#FDF6EF", Raised: "#F3E4D4", Accent: "#C2683A",
            Fonts: "Georgia, 'Iowan Old Style', serif", Radius: "14px"),

        new("Night", "Dark and restful. Good for looking at for a long time.",
            Ink: "#EDEDEC", Ground: "#141414", Raised: "#1F1F1F", Accent: "#2196F3",
            Fonts: "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif", Radius: "10px"),

        new("Plain", "Nothing extra. Good when the words are the point.",
            Ink: "#000000", Ground: "#FFFFFF", Raised: "#EEEEEE", Accent: "#000000",
            Fonts: "Times, 'Times New Roman', serif", Radius: "0px"),

        new("Fresh", "Bright and light. Good for something new.",
            Ink: "#10302A", Ground: "#F2FBF7", Raised: "#DCF3E9", Accent: "#12A277",
            Fonts: "system-ui, 'Segoe UI', Roboto, sans-serif", Radius: "18px"),
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
