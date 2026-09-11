using System.Text.Json;
using System.Text.Json.Serialization;

namespace Concierge.Shared.Design;

/// <summary>Advice for making one kind of thing.</summary>
/// <param name="Name">What it is called — "a poster", "a pitch deck".</param>
/// <param name="When">When to reach for it, in one line.</param>
/// <param name="Guide">The advice itself, in prose, with the reason beside every rule.</param>
public sealed record DesignGuide(string Name, string When, string Guide);

/// <summary>
/// What to make, rather than what colour to make it.
///
/// A look is how something appears; a guide is what actually goes on it and in what order —
/// the part somebody who is not a designer has no way to know, and the part a model gets
/// wrong by producing something that is competently laid out and says nothing.
///
/// **Prose, with the reason beside every rule**, which is open-design's shape and the reason
/// their briefs can be read by anybody. A rule with no reason gets followed where it does not
/// apply, and a rule nobody understands gets ignored the first time it is inconvenient.
///
/// **The count, said plainly rather than implied.** open-design ships 298 of these. This
/// ships a starter set and a file anybody can add to — the same answer `RoomCatalogue` gives
/// for furniture, for the same reason: what people want is more of them, and more of them
/// needs no code. Claiming parity on a number would be claiming something nobody has written.
///
/// Read from `%LOCALAPPDATA%/Concierge/guides.json`, absent unless somebody wrote one. A
/// broken file adds nothing and says so rather than taking the built-ins down with it.
/// </summary>
public sealed class DesignGuides
{
    private readonly string? _path;
    private IReadOnlyList<DesignGuide>? _read;

    /// <param name="path">
    /// Where the file is. Null for the built-ins alone, which is what a head with nowhere to
    /// keep one gets — the guides still work, there is simply nothing to add to them.
    /// </param>
    public DesignGuides(string? path = null) => _path = path;

    /// <summary>Where it reads from, or null when it reads from nowhere.</summary>
    public string? Path => _path;

    /// <summary>What went wrong with the file, or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>Everything it knows about, built-in first.</summary>
    public IReadOnlyList<DesignGuide> All => _read ??= Read();

    /// <summary>Forgets what it read, so a changed file is picked up.</summary>
    public void Again() => _read = null;

    /// <summary>
    /// The one that fits what somebody said they are making.
    ///
    /// Words, not a category: nobody says "artifact type: presentation", they say "a deck for
    /// Thursday". The name is matched first because somebody who names a guide means it, then
    /// the words of the name and of the line saying when to use it.
    /// </summary>
    public DesignGuide? For(string? said)
    {
        var words = (said ?? string.Empty).Trim();

        if (words.Length == 0)
        {
            return null;
        }

        var exact = All.FirstOrDefault(guide =>
            guide.Name.Equals(words, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
        {
            return exact;
        }

        var asked = Words(words);

        if (asked.Count == 0)
        {
            return null;
        }

        return All
            .Select(guide => (guide, score: Score(guide, asked)))
            .Where(pair => pair.score > 0)
            .OrderByDescending(pair => pair.score)
            .Select(pair => pair.guide)
            .FirstOrDefault();
    }

    private static int Score(DesignGuide guide, IReadOnlyCollection<string> asked)
    {
        var inName = Words(guide.Name);
        var inWhen = Words(guide.When);

        // The name is worth more than the sentence under it: "deck" in a guide called "a
        // pitch deck" is what somebody meant, and "deck" appearing once in another guide's
        // description is a coincidence.
        return asked.Count(word => inName.Contains(word)) * 4
               + asked.Count(word => inWhen.Contains(word));
    }

    /// <summary>
    /// The words worth matching on. Anything shorter than four letters is dropped rather than
    /// listed as noise — "a", "the", "for" and "to" match everything and mean nothing, and a
    /// list of stop words is a list somebody has to maintain.
    /// </summary>
    private static HashSet<string> Words(string text)
        => new(
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.Trim('.', ',', '"', '\'', '(', ')', '—', '-', ':', ';', '?', '!').ToLowerInvariant())
                .Where(word => word.Length >= 4),
            StringComparer.Ordinal);

    private IReadOnlyList<DesignGuide> Read()
    {
        Problem = null;

        if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
        {
            return BuiltIn;
        }

        try
        {
            var written = JsonSerializer.Deserialize<IReadOnlyList<Written>>(
                File.ReadAllText(_path),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

            if (written is null)
            {
                Problem = "That file held nothing.";
                return BuiltIn;
            }

            var theirs = written
                .Where(guide => !string.IsNullOrWhiteSpace(guide.Name) && !string.IsNullOrWhiteSpace(guide.Guide))
                .Select(guide => new DesignGuide(
                    guide.Name!.Trim(), (guide.When ?? string.Empty).Trim(), guide.Guide!.Trim()))
                .ToList();

            // Theirs wins on a clash. Somebody who wrote a guide called "a poster" meant to
            // replace the one that ships, not to sit behind it and never be found.
            var names = theirs.Select(guide => guide.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            return [.. theirs, .. BuiltIn.Where(guide => !names.Contains(guide.Name))];
        }
        catch (JsonException problem)
        {
            Problem = $"That file could not be read: {problem.Message}";
            return BuiltIn;
        }
        catch (IOException problem)
        {
            Problem = $"That file could not be opened: {problem.Message}";
            return BuiltIn;
        }
    }

    private sealed class Written
    {
        public string? Name { get; set; }

        public string? When { get; set; }

        public string? Guide { get; set; }
    }

    /// <summary>
    /// The ones that ship.
    ///
    /// Chosen for what people actually make rather than to fill a list, and each one carries
    /// its reasons — a rule with no reason gets applied where it does not belong. They are
    /// deliberately about *what goes on the thing*, because how it looks is already answered
    /// by the six looks and their briefs.
    /// </summary>
    public static readonly IReadOnlyList<DesignGuide> BuiltIn =
    [
        new("a poster",
            "Something to put on a wall that a stranger reads while walking past.",
            "A poster is read from across a corridor by somebody who is not looking for it, so "
            + "it has one job: what, and when. Put those two in the largest type on the page "
            + "and everything else below them at a size you would use in a letter. The "
            + "commonest failure is a poster that reads perfectly at arm's length and is a "
            + "grey rectangle from four metres — if the biggest thing on it is not legible at a "
            + "glance, nothing else on it matters. Three lines of detail at most. Anything "
            + "longer belongs on the page the poster points at, not on the poster."),

        new("a pitch deck",
            "Slides for asking somebody for money, time or a decision.",
            "One idea a slide, and the slide's title is that idea stated as a sentence rather "
            + "than a label — \"Renewals pay for the sales team\" tells somebody something and "
            + "\"Renewals\" does not. Say what you want in the first two slides; a deck that "
            + "builds to the ask is a deck people stop reading before it arrives. Numbers go on "
            + "their own slide with the source under them, because an unsourced number is the "
            + "thing a sceptical reader stops on. Never write a slide you would read out word "
            + "for word: if the words are for saying, the slide should carry the picture "
            + "instead."),

        new("a talk",
            "Slides for standing in front of people and speaking.",
            "The opposite of a deck somebody reads alone: the words are yours and the slide is "
            + "what the room looks at while you say them. So a slide is usually one line or one "
            + "picture, and a slide with a paragraph on it means the room is reading instead of "
            + "listening. Plan for a slide roughly every minute — fewer and people drift, more "
            + "and you are flicking. Put the thing you want remembered on its own slide and "
            + "leave it up longer than feels comfortable."),

        new("a report",
            "Something read at a desk and referred back to.",
            "Start with the answer. A report that reveals its conclusion at the end is a report "
            + "whose conclusion is read first and out of context anyway. Then the reasoning, "
            + "then the detail, so somebody can stop at any point and have a complete, shorter "
            + "version. Headings are signposts for a reader skimming for the bit they need — "
            + "write them as statements. Every number carries where it came from and when it "
            + "was true; a figure with no date is a figure that will be quoted next year."),

        new("a newsletter",
            "Something sent to people who did not ask for it today.",
            "The first line has to earn the second, because most people read one and stop. So "
            + "lead with the thing that happened, not with a greeting and a paragraph about how "
            + "busy the term has been. Short sections with their own headings, because this is "
            + "read on a phone between other things. One picture is worth more than four. End "
            + "with what you want the reader to do and make it a single thing — a newsletter "
            + "with five calls to action gets none of them done."),

        new("a landing page",
            "A page somebody arrives at from a link and leaves in twenty seconds.",
            "Say what the thing is in plain words at the top, in a sentence that would make "
            + "sense to somebody who has never heard of it. Not a slogan: a slogan is what you "
            + "write once people already know. Then the one thing to press, then the two or "
            + "three reasons somebody would. Every claim either has evidence beside it or is "
            + "cut — \"10× faster\" and \"trusted by thousands\" with nothing behind them are "
            + "slop the moment they are written, and a reader who catches one stops believing "
            + "the rest."),

        new("a phone screen",
            "A design meant for a phone, drawn at the size of one.",
            "Design for one thumb on a moving bus. The thing to press goes in the lower two "
            + "thirds where a thumb reaches, and the top of the screen is for reading rather "
            + "than touching. Nothing goes under the top or bottom strip; both are drawn on the "
            + "canvas so anything pushed under them is visible as wrong while it is being made. "
            + "One screen, one job — a screen that does two things becomes two screens the "
            + "moment somebody uses it in a hurry. Text under about 15px is text nobody reads "
            + "outdoors."),

        new("a dashboard",
            "Numbers somebody glances at from across a room.",
            "The question is always \"is it up or down, and by how much\", and that is two "
            + "words and a number. So the number is enormous and its name is small above it — "
            + "the other way round is how a dashboard becomes unreadable from more than an arm "
            + "away. Which way it is moving carries an arrow, never colour alone, because a "
            + "board is read by whoever is walking past and some of them cannot tell red from "
            + "green. Six panels is a board; twenty is a wall nobody reads. A panel with no "
            + "number shows a labelled blank, because an invented metric is slop the moment it "
            + "is invented."),

        new("a film",
            "Shots that play one after another.",
            "Decide what the first five seconds have to do, because that is how long you have. "
            + "A shot lasts as long as it takes to read what is on it and no longer — words on "
            + "screen need roughly a second for every three words, and a shot that outstays "
            + "that is the one people remember as slow. Cut by default: a dissolve on every "
            + "join is what a first attempt looks like. If there is narration, write it first "
            + "and let the shots follow it, not the other way round."),

        new("a room",
            "A space you can look around, drawn at real size.",
            "Work in real measurements from the start. A desk is about 1400 by 700 and 730 off "
            + "the floor; a door is 900 wide and 2100 high; a corridor under 900 is a corridor "
            + "people turn sideways in. A room laid out in numbers that feel right is a room "
            + "that cannot be built. Walk it in your head, door first: what do you see, what do "
            + "you bump into, where does the light come from. Put the fixed things in before "
            + "the furniture, because a socket, a window and a door do not move and a sofa "
            + "does."),

        new("a running order",
            "Music, voices or sound, one after another.",
            "Decide how it should end and work backwards; a running order assembled forwards "
            + "drifts. Put the strongest thing second rather than first — first is where "
            + "somebody is still settling. Say out loud how long the whole thing should be "
            + "before adding anything, because the commonest failure is forty minutes of good "
            + "material where twenty was wanted. Silence between tracks is part of the "
            + "arrangement, not an absence of one."),

        new("a form",
            "Something a person has to fill in.",
            "Every field is a reason to give up, so ask for the fewest things that let you do "
            + "the job, and ask why each of the rest is there until one of them cannot answer. "
            + "One column, labels above the boxes, and the label stays visible while somebody "
            + "types — a label that disappears into the box is a form people finish and get "
            + "wrong. Say what a thing is for at the point of asking, not in a paragraph at the "
            + "top nobody reads. Errors say what to do, not what went wrong."),

        new("an invitation",
            "Asking people to come to something.",
            "What, when, where, and who it is from — in that order, and all four before "
            + "anything about how lovely it will be. The date carries the day of the week, "
            + "because \"the 14th\" makes people count and \"Saturday the 14th\" does not. Say "
            + "what somebody should do to accept and by when. Tone is the whole design here: an "
            + "invitation that reads like a notice makes people feel summoned."),

        new("a certificate",
            "Something given to somebody to keep.",
            "The name is the largest thing on it, because the person it is for will look at "
            + "their own name first and for longer than anything else. Then what it is for, in "
            + "words they would use themselves. The date and who gave it go small at the "
            + "bottom. Leave a generous margin: a certificate is going to be held, framed or "
            + "pinned up, and edges that are crowded look cheap in a way that is hard to name "
            + "and easy to see."),

        new("a menu",
            "A list of things somebody chooses from.",
            "Group by when it is eaten rather than by price, and put no more than about seven "
            + "things in a group — past that people stop reading and pick the first thing they "
            + "recognise. Prices sit right after the description rather than in a column down "
            + "the edge, because a column turns a menu into a price list and people order from "
            + "the bottom of it. Describe two or three ingredients, not five; a long "
            + "description reads as an apology."),

        new("a timetable",
            "When things happen, for people finding their own bit.",
            "Nobody reads a timetable; they look for one row. So the thing that makes a row "
            + "findable — the time, or the name — goes first and lines up down the page, and "
            + "everything else follows it. Do not centre the columns. Mark the breaks as "
            + "clearly as the sessions, because \"when can I leave\" is the second question "
            + "everybody has. If it spans days, one day a page beats one grid."),

        new("a notice",
            "Telling people something they need to know.",
            "Say the thing in the first line, in the words somebody would use talking about it. "
            + "A notice that opens with context and arrives at the point in the third paragraph "
            + "is a notice half the readers get wrong. Say what changes, when it starts, and "
            + "who to ask. Keep it under a screen. If it is bad news, say so plainly — a notice "
            + "written to soften something reads as a notice hiding something."),

        new("a how-to",
            "Instructions somebody follows while doing the thing.",
            "Numbered steps, one action each, in the order they happen. Say what should happen "
            + "after each step so somebody can tell they are still on track — instructions "
            + "without confirmation are instructions people abandon in the middle, unsure "
            + "whether it worked. Put what they need before step one. Warnings go before the "
            + "step they apply to, never after it, which is the difference between a warning "
            + "and an epitaph."),

        new("a summary",
            "The short version, for somebody who will not read the long one.",
            "It has to stand alone. A summary that only makes sense beside the thing it "
            + "summarises has not saved anybody anything. Lead with the conclusion, then the "
            + "two or three things it rests on, then what happens next. Resist listing "
            + "everything that was covered — that is a table of contents, and a table of "
            + "contents is not a summary. If it is longer than a screen, it is the long one."),

        new("a comparison",
            "Setting two or more things side by side so somebody can choose.",
            "Choose the rows first — the things that actually differ and matter — and drop "
            + "every row where the answer is the same for everybody, because a table full of "
            + "ticks says nothing. Same order everywhere. Say which one you would pick and why, "
            + "since a comparison that refuses to conclude leaves the reader exactly where they "
            + "started. Be fair about the one you did not pick: a comparison that reads as an "
            + "advertisement is one nobody trusts."),
    ];
}
