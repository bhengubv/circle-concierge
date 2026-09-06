using System.Text.RegularExpressions;

namespace Concierge.Shared.Design;

/// <summary>What a sentence turned out to mean.</summary>
/// <param name="Understood">Whether anything happened.</param>
/// <param name="Document">The design afterwards.</param>
/// <param name="What">What changed, in words, for the history strip.</param>
/// <param name="Reply">What to say back, when nothing was understood.</param>
public sealed record DesignHeard(bool Understood, DesignDocument Document, string What, string? Reply = null);

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
    public static DesignHeard Hear(DesignDocument design, string? sentence, string? pointedAt)
    {
        ArgumentNullException.ThrowIfNull(design);

        var said = (sentence ?? string.Empty).Trim();
        if (said.Length == 0)
        {
            return new DesignHeard(false, design, string.Empty);
        }

        try
        {
            return Read(design, said, pointedAt);
        }
        catch (RegexMatchTimeoutException)
        {
            return Puzzled(design);
        }
    }

    private static DesignHeard Read(DesignDocument design, string said, string? pointedAt)
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
                    "Touch the thing you want gone first, then say delete.");
            }

            return new DesignHeard(true, design.Remove(doomed.Id), $"Removed the {Name(doomed.Kind, design.Medium)}");
        }

        // Changing what something says, while pointing at it.
        // "change it to say X", "make this say X", "change that to read X". The
        // "to" is optional because both are things people write.
        if (Match(said, @"^(?:change|make)\s+(?:it|this|that)\s+(?:to\s+)?(?:saying|says|say|reading|reads|read)\s+(?<words>.+)$") is { } reword
            && design.Find(pointedAt) is { } target)
        {
            var words = Clean(reword.Groups["words"].Value);
            return new DesignHeard(true, design.Set(target.Id, "text", words),
                $"Changed the {Name(target.Kind, design.Medium)}");
        }

        // Bigger and smaller, on the thing being pointed at.
        if (Match(said, @"\b(bigger|larger|smaller|tinier)\b") is { } size)
        {
            if (design.Find(pointedAt) is not { } sized)
            {
                return new DesignHeard(false, design, string.Empty,
                    "Touch the thing you want changed first, then say bigger or smaller.");
            }

            var bigger = size.Value.StartsWith('b') || size.Value.StartsWith('l');
            return new DesignHeard(true, design.Set(sized.Id, "size", bigger ? "big" : "small"),
                bigger ? $"Made the {Name(sized.Kind, design.Medium)} bigger" : $"Made the {Name(sized.Kind, design.Medium)} smaller");
        }

        // Which kind of thing this is. "Slides", "make it a video", "space".
        foreach (var medium in DesignMediums.All)
        {
            if (Match(said, $@"^(?:make it |turn it into |as )?(?:a |an |some )?{Regex.Escape(medium.Name)}[.!]?$") is not null)
            {
                return new DesignHeard(true, design.As(medium.Medium), $"Made it {medium.Name.ToLowerInvariant()}");
            }
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
                    "A page is one surface. Say 'slides' or 'video' first, then add one.");
            }

            var node = DesignNode.New(kind, ParentFor(design, kind), ("text", words));

            return new DesignHeard(true, design.Add(node), $"Added a {Name(kind, design.Medium)}");
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
    private const string Adding =
        @"^(?:add|put|insert|make|create)\s+(?:a|an|some)?\s*(?<thing>title|heading|header|words|text|paragraph|sentence|button|picture|image|photo|box|group|slide|shot|scene|room|track|section)\b(?:\s*(?:that\s+)?(?:saying|says|say|reading|reads|read|with|of|:)\s*(?<words>.+))?$";

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
        "slide" or "shot" or "scene" or "room" or "track" or "section" => DesignNodeKind.Frame,
        _ => DesignNodeKind.Text,
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
    /// </summary>
    private static string Name(DesignNodeKind kind, DesignMedium medium = DesignMedium.Page) => kind switch
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
    /// Strips the quotes and the trailing full stop people type around a phrase
    /// they are quoting. Nobody wants a heading that reads <c>"Sports Day".</c>
    /// </summary>
    private static string Clean(string words)
        => words.Trim().Trim('"', '\'', '.', ' ').Trim();

    private static DesignHeard Puzzled(DesignDocument design)
        => new(false, design, string.Empty,
            "I did not catch that. Try: add a title that says Sports Day.");
}
