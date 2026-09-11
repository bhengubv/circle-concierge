using System.Text;
using System.Text.Json.Nodes;
using Concierge.Shared.Tools;

namespace Concierge.Shared.Design;

/// <summary>
/// Whichever canvas is open, if one is.
///
/// The tools need the live session — the one the person is looking at — and the
/// session belongs to the workspace component, which is created by the UI rather
/// than by the container. This is the seam between the two: the workspace hands
/// its session over when the canvas opens and takes it back when it closes, and
/// the tool source reads whatever is there at the moment a turn runs.
///
/// Null when no canvas is open, and that is load-bearing rather than incidental —
/// it is what makes the design tools disappear from the catalogue instead of
/// being offered against a surface that is not on screen. A model offered
/// `design_add` while looking at a chat would use it, and the person would be
/// told a heading had been added to something they cannot see.
/// </summary>
public sealed class DesignWorkbench
{
    /// <summary>The canvas on screen, or null when none is.</summary>
    public DesignSession? Session { get; private set; }

    /// <summary>Opens the seam. Called when the canvas opens.</summary>
    public void Attach(DesignSession session)
        => Session = session ?? throw new ArgumentNullException(nameof(session));

    /// <summary>Closes it. Called when the canvas closes.</summary>
    public void Detach() => Session = null;

    /// <summary>
    /// What has been made lately, when a head keeps that. Null on one that does
    /// not, and the tools simply say nothing about variety rather than failing.
    /// </summary>
    public IDesignLog? Log { get; set; }

    /// <summary>
    /// What turns a design into a file, when this machine has an encoder. Null
    /// when it has none, and <c>design_save</c> is then simply not offered —
    /// the same rule the device capabilities follow, because a tool that is
    /// advertised and always fails is worse than one that is absent.
    /// </summary>
    public IMediaExport? Export { get; set; }

    /// <summary>
    /// What to make, rather than what colour to make it. Null on a head that keeps no file
    /// of its own, and the built-in guides are used instead — the advice is never absent.
    /// </summary>
    public DesignGuides? Guides { get; set; }

    /// <summary>
    /// The things a room can be furnished with, and the file anybody can add to.
    /// Null on a head that keeps none, and the built-ins are used instead.
    /// </summary>
    public RoomCatalogue? Catalogue { get; set; }

    /// <summary>
    /// Reaching the network, for bringing a track in from a link. Null on a head
    /// that has no web access, and the tool is then absent.
    /// </summary>
    public Concierge.Shared.Web.IWebAccess? Web { get; set; }

    /// <summary>
    /// Something that can make a picture, for putting one on a design. Null when nothing can
    /// — no key and no generator — and `design_picture` is then not offered.
    /// </summary>
    public Concierge.Shared.Media.IImageRuntime? Pictures { get; set; }

    /// <summary>
    /// Something that can speak, for turning written words into a track. Null when nothing
    /// on this machine can — no voice model and no cloud key — and `design_narrate` is then
    /// not offered rather than offered and always failing.
    /// </summary>
    public Concierge.Shared.Media.IVoiceRuntime? Speech { get; set; }

    /// <summary>
    /// Asking first. Required for the one design tool that leaves the device —
    /// without it that tool is not offered at all, because the alternative is a
    /// tool that reaches the internet without anybody agreeing to it.
    /// </summary>
    public IToolApprovalService? Approval { get; set; }
}

/// <summary>
/// The canvas, as things a model can do.
///
/// Design was a separate path. `SendAsync` returned early whenever the canvas was
/// open, so a sentence went to `DesignSpeech` and never to a model — no tools, no
/// plan, no approvals, no skills. The comment above it said "a model still handles
/// everything this cannot", and nothing did: there was no fall-through, and there
/// never had been. Fifth time in this repository that a comment described
/// behaviour that did not exist.
///
/// This is the seam that makes the canvas part of the harness rather than beside
/// it. Every tab that comes after — video, sound, space — arrives through the same
/// door instead of inventing another parallel path, which is the whole reason one
/// harness beats six products.
///
/// **These do not ask permission, and that is deliberate.** Everywhere else in
/// Concierge, acting asks first, because acting on somebody's files is hard to
/// reverse. On a canvas the opposite is true: asking permission to try something
/// is what makes a tool unusable for the people this is for, and going back is
/// free because every state is kept whole. A design tool that opened an approval
/// card would be applying a rule from a different problem.
///
/// The vocabulary is deliberately the same one <see cref="DesignSpeech"/> already
/// understands. A model that says "add a heading" and a person who types it should
/// reach the same code — two vocabularies for one canvas is how they drift.
/// </summary>
public sealed class DesignToolSource : IAgentToolSource
{
    private readonly DesignWorkbench _workbench;

    public DesignToolSource(DesignWorkbench workbench)
        => _workbench = workbench ?? throw new ArgumentNullException(nameof(workbench));

    /// <summary>
    /// Nothing at all when no canvas is open, so the model is never handed a tool
    /// for a surface that is not on screen.
    /// </summary>
    public IReadOnlyList<IAgentTool> Tools
        => _workbench.Session is null
            ? []
            : [
                new DescribeCanvas(_workbench),
                new AddToCanvas(_workbench),
                new ChangeOnCanvas(_workbench),
                new RemoveFromCanvas(_workbench),
                new ChooseLook(_workbench),
                new ChooseMedium(_workbench),
                new GoBackOnCanvas(_workbench),
                .. _workbench.Export is null ? Array.Empty<IAgentTool>() : [new SaveTheDesign(_workbench)],
                new AddFootage(_workbench),
                new CutTheShot(_workbench),
                new ColourTheShot(_workbench),
                new BlendTheShots(_workbench),
                new MoveTheShot(_workbench),
                new BuildTheRoom(_workbench),
                new CutAnOpening(_workbench),
                new LookAtTheFloors(_workbench),
                new PutOnAFloor(_workbench),
                new HangItOn(_workbench),
                new LayAPlan(_workbench),
                new FurnishTheRoom(_workbench),
                new SetAPanel(_workbench),
                new ReadTheGuide(_workbench),
                .. _workbench.Speech is null || !_workbench.Speech.SupportsSynthesis
                    ? Array.Empty<IAgentTool>()
                    : [new NarrateTheWords(_workbench)],
                .. _workbench.Pictures is null || !_workbench.Pictures.IsReady
                        || _workbench.Approval is null
                    ? Array.Empty<IAgentTool>()
                    : [new MakeAPictureForIt(_workbench)],
                .. _workbench.Web is null || _workbench.Approval is null
                    ? Array.Empty<IAgentTool>()
                    : [new BringASoundIn(_workbench)],
            ];

    // ── The tools ─────────────────────────────────────────────────────────

    /// <summary>
    /// What is on the canvas, in words.
    ///
    /// Read-only, and the only one of these a model should reach for first. A
    /// model that adds without looking produces a second heading under the first,
    /// which is the commonest way an assistant makes a mess of a page.
    /// </summary>
    private sealed class DescribeCanvas(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_describe";

        public string Description =>
            "Describe what is currently on the design canvas: what is being made, which look it "
            + "wears, and everything on the page in order. Use this before changing anything.";

        public JsonNode? ArgumentsSchema => null;

        public bool IsReadOnly => true;

        public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            var document = session.Current;
            var text = new StringBuilder();

            var look = DesignLooks.Of(document.Look);

            text.AppendLine($"Making: {DesignMediums.Of(document.Medium).Name}");
            text.AppendLine($"Look: {look.Name} — {look.Blurb}");

            // The argument behind the look, not just its name. Told only "Warm", a
            // model adds a hard-edged banner and four accent colours and produces
            // something wearing the name and none of the intent.
            text.AppendLine($"What that look is for: {look.Brief}");

            // What the last few wore, and why that is being mentioned. Informing
            // rather than enforcing: a person who asks for Calm twice is right,
            // and a canvas that argued with them would be worse than a repetitive
            // one. Variety is the default, not the rule.
            if (workbench.Log is { } log)
            {
                var recent = log.RecentAsync(3).GetAwaiter().GetResult();

                if (recent.Count > 0)
                {
                    text.AppendLine();
                    text.AppendLine(
                        "The last few designs wore: "
                        + string.Join(", ", recent.Select(made => $"{made.Look} ({made.Making})"))
                        + ". Unless the person asks for one of those, pick a different look — six "
                        + "looks with one of them used every time is the same product as one look.");
                }
            }

            if (document.IsEmpty)
            {
                text.AppendLine("The page is empty.");
                return Task.FromResult(new AgentToolResult(true, text.ToString()));
            }

            Describe(text, document, document.RootId, depth: 0);

            if (session.Selected is { } selected && document.Find(selected) is { } pointed)
            {
                text.AppendLine();
                text.AppendLine($"The person is pointing at: {pointed.Kind} {pointed.Id}.");
            }

            return Task.FromResult(new AgentToolResult(true, text.ToString()));
        }

        private static void Describe(StringBuilder text, DesignDocument document, string parentId, int depth)
        {
            foreach (var child in document.ChildrenOf(parentId))
            {
                var indent = new string(' ', depth * 2);
                var words = child.Text.Length > 0 ? $" \"{child.Text}\"" : string.Empty;

                text.AppendLine($"{indent}- {child.Kind} [{child.Id}]{words}");
                Describe(text, document, child.Id, depth + 1);
            }
        }
    }

    /// <summary>Puts something on the page.</summary>
    private sealed class AddToCanvas(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_add";

        public string Description =>
            "Add something to the design: a heading, some text, a picture, a button, a box, a "
            + "frame (a slide, shot, room or track), a sound, or a solid.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["kind"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "heading | text | image | button | box | frame | sound | solid",
                },
                ["text"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "What it says, when it says anything.",
                },
                ["inside"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The id of the box or frame to put it in. Omit for the page itself.",
                },
            },
            ["required"] = new JsonArray("kind"),
        };

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            var kindWord = Text(arguments, "kind");

            if (!Enum.TryParse<DesignNodeKind>(kindWord, ignoreCase: true, out var kind)
                || kind == DesignNodeKind.Page)
            {
                return Task.FromResult(new AgentToolResult(
                    false,
                    string.Empty,
                    $"'{kindWord}' is not something that can be added. Use heading, text, image, "
                    + "button, box, frame, sound or solid."));
            }

            var said = Text(arguments, "text");
            var inside = Text(arguments, "inside");

            var node = said.Length > 0
                ? DesignNode.New(kind, inside.Length > 0 ? inside : null, ("text", said))
                : DesignNode.New(kind, inside.Length > 0 ? inside : null);

            var what = said.Length > 0
                ? $"Added a {kind.ToString().ToLowerInvariant()}: {said}"
                : $"Added a {kind.ToString().ToLowerInvariant()}";

            session.Record(session.Current.Add(node), what);

            return Task.FromResult(new AgentToolResult(true, $"{what}. Its id is {node.Id}."));
        }
    }

    /// <summary>Changes one thing about one thing.</summary>
    private sealed class ChangeOnCanvas(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_change";

        public string Description =>
            "Change one property of one thing on the canvas — its text, its size, its colour. "
            + "Get the id from design_describe first.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Which thing." },
                ["property"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Which property — for example text, size, seconds, delay, rate.",
                },
                ["value"] = new JsonObject { ["type"] = "string", ["description"] = "The new value." },
            },
            ["required"] = new JsonArray("id", "property", "value"),
        };

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            var id = Text(arguments, "id");
            var property = Text(arguments, "property");
            var value = Text(arguments, "value");

            if (session.Current.Find(id) is null)
            {
                // Named rather than silent. A model working from a description it
                // read three edits ago will name something that has gone, and
                // "there is nothing called that" is a sentence it can recover
                // from — where a silent no-op is one it cannot.
                return Task.FromResult(new AgentToolResult(
                    false, string.Empty, $"There is nothing called {id} on the canvas."));
            }

            session.Record(
                session.Current.Set(id, property, value),
                $"Changed {property} to {value}");

            return Task.FromResult(new AgentToolResult(true, $"Changed {property} of {id} to {value}."));
        }
    }

    /// <summary>Takes something off the page.</summary>
    private sealed class RemoveFromCanvas(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_remove";

        public string Description => "Remove something from the canvas, and everything inside it.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Which thing." },
            },
            ["required"] = new JsonArray("id"),
        };

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            var id = Text(arguments, "id");

            if (session.Current.Find(id) is not { } node)
            {
                return Task.FromResult(new AgentToolResult(
                    false, string.Empty, $"There is nothing called {id} on the canvas."));
            }

            session.Record(
                session.Current.Remove(id),
                $"Removed the {node.Kind.ToString().ToLowerInvariant()}");

            return Task.FromResult(new AgentToolResult(true, $"Removed {id}."));
        }
    }

    /// <summary>Changes how it looks.</summary>
    private sealed class ChooseLook(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_look";

        public string Description =>
            "Change the look the whole design wears. One of: "
            + "Calm, Bold, Warm, Quiet, Fresh, Night.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["look"] = new JsonObject { ["type"] = "string", ["description"] = "The look's name." },
            },
            ["required"] = new JsonArray("look"),
        };

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            // Resolve rather than reject. An unknown name lands on the default,
            // which is what the picker does when somebody has not chosen — a
            // canvas wearing the wrong look is fixable in one more sentence, and
            // an error is a dead end.
            var look = DesignLooks.Of(DesignLooks.Resolve(Text(arguments, "look")));

            session.Record(session.Current.Wearing(look.Name), $"Wearing {look.Name}");

            // Recorded when the model chooses, not when a person does — this is a
            // memory of what the model has been reaching for.
            workbench.Log?.RecordAsync(look.Name, session.Current.Medium).GetAwaiter().GetResult();

            // The brief comes back with the change, so whatever is added next is
            // added in the look rather than merely alongside it.
            return Task.FromResult(new AgentToolResult(
                true, $"The design now wears {look.Name}. {look.Brief}"));
        }
    }

    /// <summary>
    /// The design as a file somebody keeps.
    ///
    /// Everything else on this canvas is undoable, which is why none of these
    /// tools stop to ask. This one leaves something behind on a real disk, so it
    /// keeps the promise a different way: **it never writes over anything.** A
    /// name already taken gets a number, so the worst this can do is leave a file
    /// nobody wanted — recoverable by deleting it — rather than replace one
    /// somebody did.
    ///
    /// **Every medium saves now.** A page, a deck and a room become one HTML file
    /// carrying its own look and pictures; a sound becomes an .m4a and a motion
    /// piece an .mp4. It refused the first three until 2026-09-11 on the grounds
    /// that they were "printed from the page itself", which meant the only way to
    /// hand anybody a design was a print dialog, by hand, on a head that has one.
    /// </summary>
    private sealed class SaveTheDesign(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_save";

        public string Description =>
            "Save the design as a file that can be kept and shared: an HTML file for a page, "
            + "a deck or a room, an audio file for a sound, a video file for a motion piece. "
            + "Add as=\"pdf\" to send to somebody, or as=\"pptx\" for an editable PowerPoint "
            + "file from a deck. Says where it put it.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "What to call it, without an extension. Optional.",
                },
                ["as"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] =
                        "\"pdf\" to send to somebody, or \"pptx\" for an editable PowerPoint "
                        + "file from a deck. Left out, it saves as HTML.",
                },
            },
        };

        public bool IsReadOnly => false;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return NoCanvas();
            }

            if (workbench.Export is not { } encoder)
            {
                return new AgentToolResult(
                    false, "There is no encoder on this machine, so nothing can be saved yet.");
            }

            var document = session.Current;
            var asked = Text(arguments, "name");
            var stem = string.IsNullOrWhiteSpace(asked) ? "design" : Tidy(asked);

            // A page, a deck and a room are saved as one HTML file.
            //
            // This used to refuse them — "it is printed from the page itself" —
            // which meant the only way to hand somebody a design was a browser
            // print dialog, by hand, on a head that has one. Three of the five
            // media could not be given to anybody at all.
            //
            // It costs nothing, and that is the point rather than an excuse: the
            // renderer already emits a complete standalone document with its look,
            // its fonts and its pictures inlined as data URIs, because it has to
            // stand alone inside a srcdoc frame. The file that opens in a browser
            // is the file that was on screen — not an export of it, the same
            // bytes.
            // PDF is the format people actually send each other, so every medium
            // that is a document can be one.
            if (string.Equals(Text(arguments, "as"), "pdf", StringComparison.OrdinalIgnoreCase)
                && document.Medium is not (DesignMedium.Sound or DesignMedium.Motion))
            {
                var pdf = Free(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), stem, ".pdf");

                var wrote = await PdfExport.WriteAsync(
                    document, pdf, encoder as FfmpegMediaExport, cancellationToken).ConfigureAwait(false);

                return wrote.Ok
                    ? new AgentToolResult(true, $"Saved to {wrote.Path}.")
                    : new AgentToolResult(false, string.Empty, wrote.Problem ?? "It could not be saved.");
            }

            // A deck also saves as a .pptx, because a deck that leaves as a
            // picture is a deck nobody can change — somebody who needs to fix one
            // word has to come back and ask.
            if (document.Medium == DesignMedium.Deck
                && string.Equals(Text(arguments, "as"), "pptx", StringComparison.OrdinalIgnoreCase))
            {
                var deck = Free(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), stem, ".pptx");

                var made = DeckExport.Write(document, deck);

                return made.Ok
                    ? new AgentToolResult(true, $"Saved to {made.Path}. It opens in PowerPoint and can be edited.")
                    : new AgentToolResult(false, string.Empty, made.Problem ?? "It could not be saved.");
            }

            if (document.Medium is not (DesignMedium.Sound or DesignMedium.Motion))
            {
                var page = Free(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    stem,
                    ".html");

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(page)!);
                    await File.WriteAllTextAsync(
                        page, DesignMediums.Render(document), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException problem)
                {
                    return new AgentToolResult(false, string.Empty, problem.Message);
                }
                catch (UnauthorizedAccessException problem)
                {
                    return new AgentToolResult(false, string.Empty, problem.Message);
                }

                return new AgentToolResult(
                    true, $"Saved to {page}. Open it in a browser, or print it to PDF from there.");
            }

            var sound = document.Medium == DesignMedium.Sound;
            var path = Free(Folder(sound), stem, sound ? ".m4a" : ".mp4");

            var result = sound
                ? await encoder.SoundAsync(document, path, cancellationToken).ConfigureAwait(false)
                : await encoder.VideoAsync(document, path, cancellationToken).ConfigureAwait(false);

            return result.Ok
                ? new AgentToolResult(true, $"Saved to {result.Path}.")
                : new AgentToolResult(false, result.Problem ?? "It could not be saved.");
        }

        /// <summary>Where a person already looks for this kind of thing.</summary>
        private static string Folder(bool sound)
        {
            var folder = Environment.GetFolderPath(
                sound ? Environment.SpecialFolder.MyMusic : Environment.SpecialFolder.MyVideos);

            // Not every platform has those, and a saved file nobody can find is
            // the same as no saved file.
            return string.IsNullOrWhiteSpace(folder)
                ? Environment.GetFolderPath(Environment.SpecialFolder.Personal)
                : folder;
        }

        /// <summary>
        /// A name from a sentence, made safe for a filename. Kept plain rather
        /// than clever: a model asked for a name may hand back a whole title.
        /// </summary>
        private static string Tidy(string asked)
        {
            var clean = new string(asked
                .Trim()
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)
                .ToArray());

            clean = clean.Trim('.', ' ', '-');

            return clean.Length == 0 ? "design"
                : clean.Length > 60 ? clean[..60].TrimEnd('.', ' ', '-')
                : clean;
        }

        /// <summary>A name nothing is using yet.</summary>
        private static string Free(string folder, string stem, string extension)
        {
            var path = Path.Combine(folder, stem + extension);

            for (var n = 2; File.Exists(path) && n < 1000; n++)
            {
                path = Path.Combine(folder, $"{stem} {n}{extension}");
            }

            return path;
        }
    }

    /// <summary>
    /// A track brought in from a link.
    ///
    /// **This is the one design tool that asks first, and the exception is the
    /// point.** Everything else on this canvas acts without asking because going
    /// back is free and asking is what makes the surface unusable for the people
    /// it is for. That argument holds exactly as far as the edge of the device.
    /// This one makes a request to an address a model may have written, which is
    /// not undoable by picking an earlier picture — the request has happened, and
    /// whoever is at the other end knows it. So it asks, with the address on the
    /// card.
    ///
    /// The fetch itself goes through the same `IWebAccess` the web tools use, so
    /// there is one set of rules about what this program may reach: http and https
    /// only, no loopback or private address on any hop, a redirect limit, a
    /// timeout. A second fetcher with its own idea of those rules is how a guard
    /// comes to cover one path and miss the newer one.
    ///
    /// What arrives is carried as a data URI, the same way the paperclip carries a
    /// picture, so the design stays a thing that travels rather than a set of
    /// references to somebody else's server that may be gone next week.
    /// </summary>
    private sealed class BringASoundIn(DesignWorkbench workbench) : IAgentTool
    {
        /// <summary>
        /// Ten megabytes: a four-minute track with room to spare.
        ///
        /// Not arbitrary — the design is written to disk whole on every change, so
        /// what comes in here is rewritten every time anything else is edited. A
        /// cap that allowed an album would make editing a heading cost a hundred
        /// megabytes of writing.
        /// </summary>
        private const int MostBytes = 10 * 1024 * 1024;

        public string Name => "design_bring_in_sound";

        public string Description =>
            "Fetch an audio file from a web address and add it to the running order. "
            + "This leaves the device, so the person is asked first.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["url"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The http or https address of the audio file.",
                },
                ["name"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "What to call it on the canvas. Optional.",
                },
            },
            ["required"] = new JsonArray("url"),
        };

        public bool IsReadOnly => false;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return NoCanvas();
            }

            if (workbench.Web is not { } web || workbench.Approval is not { } approval)
            {
                return new AgentToolResult(
                    false, string.Empty, "This head cannot reach the web, so nothing can be brought in.");
            }

            var url = Text(arguments, "url");

            if (string.IsNullOrWhiteSpace(url))
            {
                return new AgentToolResult(false, string.Empty, "Give the address of the audio file.");
            }

            // The address is on the card, not a summary of it. "Fetch a sound" is
            // not a thing anybody can make a decision about; the address is.
            var decision = await approval.RequestAsync(
                new ToolApprovalRequest(
                    Name,
                    url,
                    ConciergeToolRisk.Medium,
                    "It downloads that file from the internet and puts it in your design."),
                cancellationToken).ConfigureAwait(false);

            if (decision != ToolApprovalDecision.Allowed)
            {
                return new AgentToolResult(false, string.Empty, "Not allowed, so nothing was fetched.");
            }

            var got = await web.FetchBytesAsync(url, "audio/", MostBytes, cancellationToken).ConfigureAwait(false);

            if (!got.Success)
            {
                return new AgentToolResult(false, string.Empty, got.Problem ?? "That could not be fetched.");
            }

            var asked = Text(arguments, "name");
            var called = string.IsNullOrWhiteSpace(asked) ? NameFromAddress(got.Url) : asked;

            var track = DesignNode.New(
                DesignNodeKind.Sound, null, ("text", called), ("src", got.AsDataUri));

            session.Record(session.Current.Add(track), $"Brought in {called}");

            return new AgentToolResult(
                true, $"Added {called} to the running order, {got.Bytes.Length / 1024}KB of {got.MediaType}.");
        }

        /// <summary>
        /// A name out of the address, when nobody gave one.
        ///
        /// The last part of the path with its extension taken off, which is what a
        /// person would have called it anyway. A track labelled with a whole URL is
        /// a track nobody can read on a canvas.
        /// </summary>
        private static string NameFromAddress(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var address))
            {
                return "A sound";
            }

            var last = address.Segments.Length == 0 ? string.Empty : address.Segments[^1].Trim('/');
            var stem = Uri.UnescapeDataString(last);

            var dot = stem.LastIndexOf('.');
            if (dot > 0)
            {
                stem = stem[..dot];
            }

            stem = stem.Replace('-', ' ').Replace('_', ' ').Trim();

            return stem.Length == 0 ? "A sound" : stem;
        }
    }

    /// <summary>
    /// A shot that is real footage rather than a drawn card.
    ///
    /// This is the line between a slideshow and a film. Every shot until now was
    /// something the encoder drew — a colour and some words — because that is all
    /// a `Frame` could hold. A shot can point at a video file now, and the export
    /// cuts it rather than rebuilding it.
    ///
    /// **The footage stays where it is.** A picture and a track are carried inside
    /// the design as data so it travels; a clip is not, because a four-minute file
    /// is hundreds of megabytes and the design is rewritten to disk every time
    /// anybody edits a heading. A design with footage in it points at this machine,
    /// and `design_describe` says so rather than leaving it to be discovered when
    /// somebody sends it on.
    ///
    /// Read-only in the sense that matters: it reads a file that is already there
    /// and changes nothing outside the canvas, so it does not ask. Adding a shot is
    /// as undoable as everything else here.
    /// </summary>
    private sealed class AddFootage(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_add_footage";

        public string Description =>
            "Add a shot that plays real video from a file on this machine. "
            + "Optionally start it partway in and give it a length.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["path"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The video file.",
                },
                ["text"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "What to call the shot. Optional.",
                },
                ["from"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "Seconds into the file to start. Optional.",
                },
                ["seconds"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "How long the shot runs. Optional.",
                },
            },
            ["required"] = new JsonArray("path"),
        };

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            var path = Text(arguments, "path");

            if (string.IsNullOrWhiteSpace(path))
            {
                return Task.FromResult(
                    new AgentToolResult(false, string.Empty, "Give the path of the video file."));
            }

            bool there;

            try
            {
                there = File.Exists(path);
            }
            catch (ArgumentException)
            {
                there = false;
            }

            if (!there)
            {
                // Checked now rather than at export. A shot pointing at nothing
                // looks exactly like a shot pointing at something until somebody
                // presses save, which is the worst moment to find out.
                return Task.FromResult(new AgentToolResult(
                    false, string.Empty, $"There is no file at {path}."));
            }

            var props = new List<(string Key, string Value)>
            {
                ("src", path),
                ("text", Text(arguments, "text") is { Length: > 0 } named
                    ? named
                    : Path.GetFileNameWithoutExtension(path)),
            };

            if (Amount(arguments, "from") is > 0 and var from)
            {
                props.Add(("trim", Number(from)));
            }

            if (Amount(arguments, "seconds") is > 0 and var seconds)
            {
                props.Add(("seconds", Number(seconds)));
            }

            var shot = DesignNode.New(DesignNodeKind.Frame, null, [.. props]);
            var made = session.Current.Medium == DesignMedium.Motion
                ? session.Current
                : session.Current.As(DesignMedium.Motion);

            session.Record(made.Add(shot), $"Added {shot.Text}");

            return Task.FromResult(new AgentToolResult(
                true, $"Added a shot playing {Path.GetFileName(path)}."));
        }
    }

    /// <summary>
    /// Where a shot starts and how long it runs, in the words somebody uses.
    ///
    /// `design_change` can already set `trim` and `seconds` by name, and that is
    /// the wrong shape for this: it asks a model to know two property names and
    /// what they do to a file. "Cut the first four seconds" is the sentence, so
    /// this is the tool.
    /// </summary>
    private sealed class CutTheShot(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_cut";

        public string Description =>
            "Change where a shot starts inside its footage and how long it runs. "
            + "Use this for things like cutting the first few seconds off a shot.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Which shot." },
                ["from"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "Seconds into the footage to start.",
                },
                ["seconds"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "How long the shot runs.",
                },
            },
            ["required"] = new JsonArray("id"),
        };

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            var id = Text(arguments, "id");

            if (session.Current.Find(id) is null)
            {
                return Task.FromResult(new AgentToolResult(
                    false, string.Empty, $"There is nothing called {id} on the canvas."));
            }

            var from = Amount(arguments, "from");
            var seconds = Amount(arguments, "seconds");

            if (from is null && seconds is null)
            {
                return Task.FromResult(new AgentToolResult(
                    false, string.Empty, "Say where it should start, how long it should run, or both."));
            }

            var document = session.Current;
            var said = new List<string>();

            if (from is { } start)
            {
                document = document.Set(id, "trim", Number(Math.Max(0, start)));
                said.Add($"starting {Number(Math.Max(0, start))}s in");
            }

            if (seconds is { } length)
            {
                document = document.Set(id, "seconds", Number(Math.Max(0.1, length)));
                said.Add($"running {Number(Math.Max(0.1, length))}s");
            }

            session.Record(document, $"Cut {id}");

            return Task.FromResult(new AgentToolResult(true, $"That shot is now {string.Join(", ", said)}."));
        }
    }

    private static string Number(double value)
        => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// How a shot is graded, in the words somebody uses.
    ///
    /// Warmer, cooler, brighter, darker, black and white, faded, vivid. Not a
    /// filter graph — a person who wants `eq=saturation=1.4:contrast=1.1` is not
    /// who this is for, and a person who wants "make it warmer" should not have to
    /// become them.
    /// </summary>
    private sealed class ColourTheShot(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_colour";

        public string Description =>
            "Change how a shot looks: warm, cool, bright, dark, grey, faded or vivid. "
            + "Say \"none\" to take it back to how it was filmed.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Which shot." },
                ["colour"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "warm, cool, bright, dark, grey, faded, vivid, or none.",
                },
            },
            ["required"] = new JsonArray("id", "colour"),
        };

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            var id = Text(arguments, "id");

            if (session.Current.Find(id) is null)
            {
                return Task.FromResult(new AgentToolResult(
                    false, string.Empty, $"There is nothing called {id} on the canvas."));
            }

            var asked = Text(arguments, "colour").ToLowerInvariant();

            if (asked is "none" or "off" or "normal")
            {
                session.Record(session.Current.Set(id, "colour", string.Empty), $"Ungraded {id}");
                return Task.FromResult(new AgentToolResult(true, "That shot looks as it was filmed again."));
            }

            // Checked here rather than swallowed at export. A grade that does
            // nothing because nobody knows the word is a shot that looks unchanged
            // and a person who thinks it worked.
            if (FfmpegMediaExport.GradeOf(
                    DesignNode.New(DesignNodeKind.Frame, null, ("colour", asked))).Length == 0)
            {
                return Task.FromResult(new AgentToolResult(
                    false,
                    string.Empty,
                    $"There is no look called {asked}. Try: "
                    + $"{string.Join(", ", FfmpegMediaExport.Grades)}."));
            }

            session.Record(session.Current.Set(id, "colour", asked), $"Made {id} {asked}");

            return Task.FromResult(new AgentToolResult(true, $"That shot is {asked} now."));
        }
    }

    /// <summary>
    /// How one shot becomes the next.
    ///
    /// A cut by default, because a cut is what film is made of and a dissolve on
    /// every join is what a first attempt looks like. Asking for one puts it on the
    /// shot it leads into.
    /// </summary>
    private sealed class BlendTheShots(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_blend";

        public string Description =>
            "Fade one shot into the next instead of cutting. Say how long the fade lasts, "
            + "or \"none\" to go back to a straight cut.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The shot to fade into.",
                },
                ["seconds"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "How long the fade lasts. Leave out for half a second.",
                },
            },
            ["required"] = new JsonArray("id"),
        };

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            var id = Text(arguments, "id");

            if (session.Current.Find(id) is null)
            {
                return Task.FromResult(new AgentToolResult(
                    false, string.Empty, $"There is nothing called {id} on the canvas."));
            }

            if (string.Equals(Text(arguments, "seconds"), "none", StringComparison.OrdinalIgnoreCase))
            {
                session.Record(session.Current.Set(id, "blend", string.Empty), $"Cut to {id}");
                return Task.FromResult(new AgentToolResult(true, "That is a straight cut again."));
            }

            // Held under a second and a half. A long dissolve is the thing that
            // makes a first film look like a first film, and a number a model
            // picked at random should not be able to put four seconds of mush in
            // the middle of somebody's work.
            var seconds = Math.Clamp(Amount(arguments, "seconds") ?? 0.5, 0.1, 1.5);

            session.Record(session.Current.Set(id, "blend", Number(seconds)), $"Faded into {id}");

            return Task.FromResult(new AgentToolResult(
                true, $"The shot before it now fades into this one over {Number(seconds)}s."));
        }
    }

    /// <summary>
    /// A wall, a floor, a ceiling or a roof.
    ///
    /// Space drew four shapes on a floor until now, which is furniture without a
    /// building. Pascal is an architectural editor and this is the part of it worth
    /// having: the things a room is actually made of, said rather than drawn with a
    /// mouse.
    ///
    /// A wall runs between two points because that is how somebody describes one —
    /// "a wall along the back", "a wall from the corner to the door" — and not as a
    /// width and a rotation, which is how a box is described and is the reason
    /// putting up a wall with `design_add` never worked.
    /// </summary>
    private sealed class BuildTheRoom(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_build";

        public string Description =>
            "Put up a wall, lay a floor or a ceiling, or add a roof. A wall runs between two "
            + "points on the floor; a floor, ceiling or roof covers an area.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["what"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "wall, floor, ceiling or roof.",
                },
                ["text"] = new JsonObject { ["type"] = "string", ["description"] = "What to call it." },
                ["x"] = new JsonObject { ["type"] = "number", ["description"] = "Where it starts across." },
                ["y"] = new JsonObject { ["type"] = "number", ["description"] = "Where it starts back." },
                ["x2"] = new JsonObject { ["type"] = "number", ["description"] = "Where a wall ends across." },
                ["y2"] = new JsonObject { ["type"] = "number", ["description"] = "Where a wall ends back." },
                ["width"] = new JsonObject { ["type"] = "number", ["description"] = "How wide a floor, ceiling or roof is." },
                ["depth"] = new JsonObject { ["type"] = "number", ["description"] = "How deep it is." },
                ["height"] = new JsonObject { ["type"] = "number", ["description"] = "How tall a wall is, or how high a ceiling sits." },
            },
            ["required"] = new JsonArray("what"),
        };

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            var what = Text(arguments, "what").ToLowerInvariant();

            if (!RoomPieces.IsPiece(what))
            {
                return Task.FromResult(new AgentToolResult(
                    false, string.Empty, "Say wall, floor, ceiling or roof."));
            }

            // Built by RoomPieces rather than here, because "add a wall" typed into the
            // canvas has to produce the same wall this does. Two copies of these defaults is
            // how a sentence and a tool come to disagree about how thick a wall is.
            var props = RoomPieces.Props(
                what,
                Text(arguments, "text") is { Length: > 0 } named ? named : null,
                Amount(arguments, "x"),
                Amount(arguments, "y"),
                Amount(arguments, "x2"),
                Amount(arguments, "y2"),
                Amount(arguments, "width"),
                Amount(arguments, "depth"),
                Amount(arguments, "height"),
                Amount(arguments, "thickness"));

            var room = Room(session);
            var piece = DesignNode.New(DesignNodeKind.Solid, room, [.. props]);

            session.Record(session.Current.Add(piece), $"Put up a {what}");

            return Task.FromResult(new AgentToolResult(true, $"Added a {what}."));
        }
    }

    /// <summary>
    /// A door or a window, cut into a wall.
    ///
    /// It belongs to the wall rather than standing beside it, which is what makes
    /// the hole real: the wall is built as the stretches of solid either side of
    /// the opening and the piece over it, so there is nothing to cut and no CSG
    /// library to carry.
    /// </summary>
    private sealed class CutAnOpening(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_opening";

        public string Description =>
            "Cut a door or a window into a wall. Say which wall, how far along it starts, "
            + "and how big it is.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["wall"] = new JsonObject { ["type"] = "string", ["description"] = "Which wall." },
                ["what"] = new JsonObject { ["type"] = "string", ["description"] = "door or window." },
                ["at"] = new JsonObject { ["type"] = "number", ["description"] = "How far along the wall it starts." },
                ["width"] = new JsonObject { ["type"] = "number", ["description"] = "How wide the opening is." },
                ["height"] = new JsonObject { ["type"] = "number", ["description"] = "How tall it is." },
                ["sill"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "How far off the floor a window sits. Nought for a door.",
                },
            },
            ["required"] = new JsonArray("wall", "what"),
        };

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            var wallId = Text(arguments, "wall");
            var wall = session.Current.Find(wallId);

            if (wall is null)
            {
                return Task.FromResult(new AgentToolResult(
                    false, string.Empty, $"There is nothing called {wallId} on the canvas."));
            }

            // Checked rather than assumed. A door hung on a chair would be added
            // happily and drawn nowhere, which looks exactly like nothing happening.
            if (!wall.Props.TryGetValue("shape", out var shape)
                || !shape.Equals("wall", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new AgentToolResult(
                    false, string.Empty, "That is not a wall, so nothing can be cut into it."));
            }

            var what = Text(arguments, "what").ToLowerInvariant();

            if (!RoomPieces.IsOpening(what))
            {
                return Task.FromResult(new AgentToolResult(false, string.Empty, "Say door or window."));
            }

            var opening = DesignNode.New(
                DesignNodeKind.Solid,
                wall.Id,
                [.. RoomPieces.Opening(
                    what,
                    Amount(arguments, "at"),
                    Amount(arguments, "width"),
                    Amount(arguments, "height"),
                    Amount(arguments, "sill"))]);

            session.Record(session.Current.Add(opening), $"Cut a {what}");

            return Task.FromResult(new AgentToolResult(true, $"Cut a {what} into that wall."));
        }
    }

    /// <summary>
    /// Which room a new piece belongs to, making one if there is not one yet.
    ///
    /// Somebody who says "put up a wall" on an empty canvas means a wall in a room,
    /// not a wall and then a puzzle about why nothing appeared.
    /// </summary>
    private static string? Room(DesignSession session)
    {
        var rooms = DesignMediums.FramesOf(session.Current);

        if (rooms.Count > 0)
        {
            return rooms[^1].Id;
        }

        var room = DesignNode.New(DesignNodeKind.Frame, null, ("text", "A room"));

        session.Record(
            session.Current.As(DesignMedium.Scene).Add(room),
            "Started a room");

        return room.Id;
    }

    /// <summary>
    /// How a building with more than one floor is looked at.
    ///
    /// Whole, pulled apart, or one floor at a time. **Pulled apart is the one worth
    /// having**: a house from outside is a box, and a house with its floors lifted
    /// away from each other is a thing you can actually read — the one view no
    /// physical model gives you.
    ///
    /// Kept on the room rather than in the component, so going back to an earlier
    /// picture brings back how you were looking at it as well as what you were
    /// looking at.
    /// </summary>
    private sealed class LookAtTheFloors(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_floors";

        public string Description =>
            "Look at a building with more than one floor: all of it, pulled apart so the floors "
            + "are separated, or one floor at a time.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["how"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "whole, apart, or one.",
                },
                ["which"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "Which floor, when looking at one. Nought is the ground floor.",
                },
            },
            ["required"] = new JsonArray("how"),
        };

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            var rooms = DesignMediums.FramesOf(session.Current);

            if (rooms.Count == 0)
            {
                return Task.FromResult(new AgentToolResult(
                    false, string.Empty, "There is no building to look at yet."));
            }

            var how = Text(arguments, "how").ToLowerInvariant();

            how = how switch
            {
                "whole" or "all" or "together" => "whole",
                "apart" or "exploded" or "separated" => "apart",
                "one" or "single" or "just one" => "one",
                _ => string.Empty,
            };

            if (how.Length == 0)
            {
                return Task.FromResult(new AgentToolResult(
                    false, string.Empty, "Say whole, apart, or one."));
            }

            var room = rooms[^1];
            var document = session.Current.Set(room.Id, "showing", how);

            if (how == "one")
            {
                document = document.Set(
                    room.Id, "only", Number(Math.Max(0, Amount(arguments, "which") ?? 0)));
            }

            session.Record(document, how switch
            {
                "apart" => "Pulled the floors apart",
                "one" => "Showed one floor",
                _ => "Showed the whole building",
            });

            return Task.FromResult(new AgentToolResult(true, how switch
            {
                "apart" => "The floors are lifted away from each other.",
                "one" => "Showing one floor.",
                _ => "Showing the whole building.",
            }));
        }
    }

    /// <summary>
    /// What floor something is on.
    ///
    /// Its own tool rather than a property on `design_build`, because floors are
    /// decided after the fact far more often than before: somebody puts up a room,
    /// likes it, and then says the whole thing is upstairs.
    /// </summary>
    private sealed class PutOnAFloor(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_floor_of";

        public string Description =>
            "Say which floor of the building something is on. Nought is the ground floor.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Which thing." },
                ["floor"] = new JsonObject { ["type"] = "number", ["description"] = "Which floor." },
            },
            ["required"] = new JsonArray("id", "floor"),
        };

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            var id = Text(arguments, "id");

            if (session.Current.Find(id) is null)
            {
                return Task.FromResult(new AgentToolResult(
                    false, string.Empty, $"There is nothing called {id} on the canvas."));
            }

            var floor = (int)Math.Max(0, Amount(arguments, "floor") ?? 0);

            session.Record(
                session.Current.Set(id, "level", floor.ToString(Culture)),
                $"Moved it to floor {floor}");

            return Task.FromResult(new AgentToolResult(
                true, floor == 0 ? "That is on the ground floor." : $"That is on floor {floor}."));
        }
    }

    private static readonly System.Globalization.CultureInfo Culture =
        System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>
    /// Hangs something on a wall, or from the ceiling.
    ///
    /// **Furniture floating in the middle of a room is the commonest thing to get
    /// wrong in a 3D surface**, and it happens because a person says "a shelf on
    /// the back wall" and something has to turn that into three numbers. This is
    /// that something: which wall, how far along it, and how high off the floor.
    /// The pushing-out — so the thing touches the wall rather than sitting inside
    /// it — is worked out from the wall's own thickness rather than asked for.
    /// </summary>
    private sealed class HangItOn(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_hang";

        public string Description =>
            "Put something against a wall or hang it from the ceiling, at the right height. "
            + "Say which wall, how far along it, and how far off the floor.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "What to hang." },
                ["on"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "A wall's id, or \"ceiling\", or \"floor\" to take it off again.",
                },
                ["at"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "How far along the wall it sits.",
                },
                ["height"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "How far off the floor. Nought stands it on the floor.",
                },
            },
            ["required"] = new JsonArray("id", "on"),
        };

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            var id = Text(arguments, "id");

            if (session.Current.Find(id) is null)
            {
                return Task.FromResult(new AgentToolResult(
                    false, string.Empty, $"There is nothing called {id} on the canvas."));
            }

            var on = Text(arguments, "on");

            if (on.Equals("floor", StringComparison.OrdinalIgnoreCase) || on.Length == 0)
            {
                session.Record(session.Current.Set(id, "on", string.Empty), "Put it back on the floor");
                return Task.FromResult(new AgentToolResult(true, "That is standing on the floor again."));
            }

            if (!on.Equals("ceiling", StringComparison.OrdinalIgnoreCase))
            {
                var wall = session.Current.Find(on);

                // Checked, because hanging a shelf on a chair would be accepted
                // and then drawn wherever the shelf already was — which looks
                // exactly like the tool doing nothing.
                if (wall is null
                    || !wall.Props.TryGetValue("shape", out var shape)
                    || !shape.Equals("wall", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new AgentToolResult(
                        false, string.Empty, "That is not a wall, so nothing can hang on it."));
                }
            }

            var document = session.Current
                .Set(id, "on", on.ToLowerInvariant() == "ceiling" ? "ceiling" : on)
                .Set(id, "at", Number(Math.Max(0, Amount(arguments, "at") ?? 60)))
                .Set(id, "sill", Number(Math.Max(0, Amount(arguments, "height") ?? 0)));

            session.Record(document, "Hung it up");

            return Task.FromResult(new AgentToolResult(
                true,
                on.Equals("ceiling", StringComparison.OrdinalIgnoreCase)
                    ? "That hangs from the ceiling now."
                    : "That sits against the wall now."));
        }
    }

    /// <summary>
    /// A plan on the floor to build on top of.
    ///
    /// **This is how somebody with a drawing on paper starts.** Not by typing
    /// coordinates — by photographing what they already have, saying how wide the
    /// building is, and putting walls up over it. Pascal calls it a guide image and
    /// it is the single most useful thing in its editor for anybody who is not an
    /// architect.
    ///
    /// The picture is carried in the design as a data URI, like every other
    /// picture, so the design still travels. A plan is a photograph of a sheet of
    /// paper rather than footage, so the size that made footage stay on disk does
    /// not apply.
    /// </summary>
    private sealed class LayAPlan(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_plan";

        public string Description =>
            "Lay a picture of a floor plan flat on the ground to build on top of. "
            + "Say how wide and deep the building really is so it comes out at the right size.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["picture"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The plan, as a data URI.",
                },
                ["width"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "How wide the building really is.",
                },
                ["depth"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "How deep it really is.",
                },
            },
            ["required"] = new JsonArray("picture"),
        };

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            var picture = Text(arguments, "picture");

            // The same rule the paperclip follows everywhere else: a data URI
            // travels with the design and an address does not, so a plan that
            // lived on somebody's machine would make a design that looks complete
            // here and arrives somewhere else with nothing to trace.
            if (!picture.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new AgentToolResult(
                    false, string.Empty, "Give the plan as a picture carried in the design."));
            }

            var room = Room(session);

            var plan = DesignNode.New(
                DesignNodeKind.Solid,
                room,
                ("shape", "plan"),
                ("text", "The plan"),
                ("src", picture),
                ("width", Number(Math.Max(1, Amount(arguments, "width") ?? 400))),
                ("depth", Number(Math.Max(1, Amount(arguments, "depth") ?? 400))));

            session.Record(session.Current.Add(plan), "Laid the plan down");

            return Task.FromResult(new AgentToolResult(
                true, "The plan is on the floor. Put walls up over it."));
        }
    }

    /// <summary>
    /// Puts a named thing in the room — a desk, a sofa, a bed.
    ///
    /// A desk is three boxes and a sofa is four, and nobody should have to say so.
    /// The catalogue knows, and **anybody can add to it by writing a file** — no
    /// code, nothing loaded, nothing that can break the app. That is Pascal's
    /// plugin idea with the dangerous half left out.
    ///
    /// The parts are added as one thing with pieces under it, so moving the desk
    /// moves its legs, and going back undoes the whole desk rather than a leg at a
    /// time.
    /// </summary>
    private sealed class FurnishTheRoom(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_furnish";

        public string Description =>
            "Put a named piece of furniture in the room — a desk, a table, a chair, a sofa, "
            + "a bed, a shelf, a lamp. Ask what is available if you are not sure.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["what"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "What to put in, by name. Leave out to be told what there is.",
                },
                ["x"] = new JsonObject { ["type"] = "number", ["description"] = "Where it goes, across." },
                ["y"] = new JsonObject { ["type"] = "number", ["description"] = "Where it goes, back." },
            },
        };

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            var catalogue = workbench.Catalogue ?? new RoomCatalogue(
                Path.Combine(Path.GetTempPath(), "concierge-no-catalogue.json"));

            var what = Text(arguments, "what");

            if (what.Length == 0)
            {
                return Task.FromResult(new AgentToolResult(
                    true,
                    "There is: " + string.Join(", ", catalogue.Things.Select(thing => thing.Name)) + "."));
            }

            if (catalogue.Find(what) is not { } found)
            {
                // Named rather than silent, and the list comes back with the
                // refusal — a model that guessed once will guess again otherwise.
                return Task.FromResult(new AgentToolResult(
                    false,
                    string.Empty,
                    $"There is nothing called {what}. There is: "
                    + string.Join(", ", catalogue.Things.Select(thing => thing.Name)) + "."));
            }

            // Room first, then the document — `Room` makes one when there is none, and reading
            // `session.Current` in the same argument list would read it before that happened.
            // C# evaluates arguments left to right, so the room was created and then built on
            // top of the document from before it existed.
            var room = Room(session);

            // Placed by RoomPieces so a said desk and a tool-placed desk are one desk.
            var document = RoomPieces.Furnish(
                session.Current,
                room,
                found,
                Amount(arguments, "x"),
                Amount(arguments, "y"));

            session.Record(document, $"Put in a {found.Name}");

            return Task.FromResult(new AgentToolResult(true, $"Put a {found.Name} in the room."));
        }
    }

    /// <summary>
    /// What a panel on a board says.
    ///
    /// A number, which way it is moving, and a line of context. **The blank is the
    /// point when there is no number** — open-design's own rule, adopted here
    /// unchanged: an invented metric is slop the moment it is invented, and "10×
    /// faster" or "99.9% uptime" with nothing behind it is the same defect as an
    /// approvals badge that always said two.
    ///
    /// What makes a board *live* is where the numbers come from, which is the
    /// agent's job: it reads a file, runs a command, fetches a page, and sets the
    /// panel. This is the setting.
    /// </summary>
    private sealed class SetAPanel(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_panel";

        public string Description =>
            "Set what a panel on a board shows: the number, which way it is moving, and a note. "
            + "Leave the number out and it shows a blank rather than something made up.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Which panel." },
                ["value"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The number, as it should read. Leave out for a blank.",
                },
                ["change"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Which way it is moving — \"+12%\", \"-3\", \"steady\".",
                },
                ["note"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "A line of context under it.",
                },
            },
            ["required"] = new JsonArray("id"),
        };

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            var id = Text(arguments, "id");

            if (session.Current.Find(id) is null)
            {
                return Task.FromResult(new AgentToolResult(
                    false, string.Empty, $"There is nothing called {id} on the canvas."));
            }

            var document = session.Current;

            foreach (var field in new[] { "value", "change", "note" })
            {
                // Only what was actually said. Writing an empty string for a field
                // nobody mentioned would quietly wipe the note every time somebody
                // updated the number.
                if (arguments?[field] is not null)
                {
                    document = document.Set(id, field, Text(arguments, field));
                }
            }

            session.Record(document, "Set the panel");

            var value = Text(arguments, "value");

            return Task.FromResult(new AgentToolResult(
                true,
                value.Length > 0
                    ? $"That panel reads {value}."
                    : "That panel shows a blank, which is right when there is no number."));
        }
    }

    /// <summary>Changes what is being made.</summary>
    private sealed class ChooseMedium(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_making";

        public string Description =>
            "Change what is being made, keeping everything on it: page, deck, motion, space or sound.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["making"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "page | deck | motion | space | sound",
                },
            },
            ["required"] = new JsonArray("making"),
        };

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            var word = Text(arguments, "making");

            if (!Enum.TryParse<DesignMedium>(word, ignoreCase: true, out var medium))
            {
                return Task.FromResult(new AgentToolResult(
                    false, string.Empty, $"'{word}' is not something to make. Use page, deck, motion, space or sound."));
            }

            session.Record(session.Current.As(medium), $"Making a {DesignMediums.Of(medium).Name}");

            return Task.FromResult(new AgentToolResult(
                true, $"Now making a {DesignMediums.Of(medium).Name}. Nothing was thrown away."));
        }
    }

    /// <summary>Goes back to how it looked before.</summary>
    private sealed class GoBackOnCanvas(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_go_back";

        public string Description =>
            "Undo the last change to the design. Going back is free — every state is kept.";

        public JsonNode? ArgumentsSchema => null;

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            if (!session.CanGoBack)
            {
                return Task.FromResult(new AgentToolResult(
                    true, "This is as far back as it goes — nothing has been changed yet."));
            }

            session.Back();

            return Task.FromResult(new AgentToolResult(true, "Went back one step."));
        }
    }

    /// <summary>
    /// Written words, spoken, added to the running order.
    ///
    /// This is the piece that makes a sound design something a person can actually finish.
    /// A running order could hold music and a track brought in from a link, and the one
    /// thing almost every one of them needs — somebody saying the words — could only come
    /// from a microphone and a person willing to use it.
    ///
    /// **It does not ask, and that is the same rule the rest of this canvas follows.** It
    /// writes no file anybody else can see, reaches nothing, and going back is free. The
    /// one design tool that asks is the one that leaves the device, and this one does not:
    /// with the local voice in place, nothing about the words goes anywhere.
    ///
    /// The audio is carried inside the design as a data URI, the way a picture and a fetched
    /// track already are, so the design still travels rather than pointing at a file on this
    /// machine.
    /// </summary>
    private sealed class NarrateTheWords(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_narrate";

        public string Description =>
            "Say some written words out loud and add them to the running order as a track. "
            + "Use this for narration over a video or a spoken line in a sound design.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["words"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "What to say.",
                },
                ["name"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "What to call the track on the canvas. Optional.",
                },
            },
            ["required"] = new JsonArray("words"),
        };

        public bool IsReadOnly => false;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return NoCanvas();
            }

            if (workbench.Speech is not { } speech || !speech.SupportsSynthesis)
            {
                return new AgentToolResult(
                    false, string.Empty, "Nothing on this machine can speak, so there is nothing to add.");
            }

            var words = Text(arguments, "words");

            if (words.Length == 0)
            {
                return new AgentToolResult(false, string.Empty, "Give the words to say.");
            }

            var said = await speech.SynthesizeAsync(words, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // Nothing back is a failure rather than an empty track. A silent track on the
            // canvas looks exactly like one that worked, which is the defect this repository
            // keeps finding: a surface asserting something untrue.
            if (said.Audio.Length == 0)
            {
                return new AgentToolResult(
                    false, string.Empty, "Those words came back as nothing, so no track was added.");
            }

            var asked = Text(arguments, "name");
            var called = asked.Length > 0 ? asked : FirstFewWords(words);

            var track = DesignNode.New(
                DesignNodeKind.Sound,
                null,
                ("text", called),
                ("src", $"data:{said.MimeType};base64,{Convert.ToBase64String(said.Audio)}"),
                ("words", words));

            session.Record(session.Current.Add(track), $"Narrated {called}");

            return new AgentToolResult(
                true, $"Added {called} to the running order, {said.Audio.Length / 1024}KB of {said.MimeType}.");
        }

        /// <summary>
        /// A name out of the words, when nobody gave one. A whole paragraph as a caption is
        /// a running order nobody can read.
        /// </summary>
        private static string FirstFewWords(string words)
        {
            var flat = string.Join(' ', words.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            return flat.Length <= 40 ? flat : flat[..40].TrimEnd() + "…";
        }
    }

    /// <summary>
    /// What actually goes on the thing being made, and in what order.
    ///
    /// The looks answer how something appears. This answers what goes on it — the part
    /// somebody who is not a designer has no way to know, and the part a model gets wrong by
    /// producing something competently laid out that says nothing. A poster with the date in
    /// body copy is a poster nobody can read from the corridor, and no palette fixes it.
    ///
    /// Read-only: it produces advice and changes nothing, so it does not ask.
    ///
    /// **It is not offered a category to pick from.** Nobody says "artifact type:
    /// presentation"; they say "a deck for Thursday". So it takes what somebody said in their
    /// own words and finds the guide that fits, and with nothing said it lists what there is.
    /// </summary>
    private sealed class ReadTheGuide(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_guide";

        public string Description =>
            "Read the guide for the kind of thing being made — what goes on a poster, a pitch "
            + "deck, a dashboard, a room — before making it. Say what it is in ordinary words.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["about"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] =
                        "What is being made, in ordinary words — \"a poster for a school fair\". "
                        + "Leave it out to see what guides there are.",
                },
            },
        };

        public bool IsReadOnly => true;

        public Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var guides = workbench.Guides ?? new DesignGuides();
            var about = Text(arguments, "about");

            if (about.Length == 0)
            {
                var lines = guides.All.Select(guide => $"{guide.Name} — {guide.When}");

                return Task.FromResult(new AgentToolResult(
                    true, "There are guides for:" + Environment.NewLine + string.Join(Environment.NewLine, lines)));
            }

            if (guides.For(about) is not { } found)
            {
                // Named rather than silent: a model told "no guide" has no idea whether it
                // asked the wrong way or there is nothing for this at all.
                return Task.FromResult(new AgentToolResult(
                    true,
                    $"There is no guide for {about}. There are guides for: "
                    + string.Join(", ", guides.All.Select(guide => guide.Name))
                    + ". Make it well anyway — the guides are advice, not a gate."));
            }

            return Task.FromResult(new AgentToolResult(
                true, $"{found.Name} — {found.When}{Environment.NewLine}{Environment.NewLine}{found.Guide}"));
        }
    }

    /// <summary>
    /// How a shot moves while it is on screen.
    ///
    /// **A still picture held for four seconds looks like a fault**, and that is the whole
    /// of what a motion-graphics engine is wanted for here. A slow push in or a slow drift
    /// across turns a card or a photograph into a shot, and both are ordinary filters the
    /// encoder already has — no composition engine, no keyframes, no curve editor, which are
    /// the three things that make this category's software unusable for the people this is
    /// for.
    ///
    /// Four words: still, fade, grow, drift. Grow and drift need a still to work on; on real
    /// footage they fight a picture that is already moving, and the tool says so rather than
    /// accepting the word and quietly doing nothing.
    /// </summary>
    private sealed class MoveTheShot(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_move";

        public string Description =>
            "Say how a shot moves while it is on screen: still, fade, grow (a slow push in) "
            + "or drift (a slow pan across). Use it so a held picture does not look frozen.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Which shot." },
                ["move"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "still, fade, grow or drift.",
                },
            },
            ["required"] = new JsonArray("id", "move"),
        };

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return Task.FromResult(NoCanvas());
            }

            var id = Text(arguments, "id");

            if (session.Current.Find(id) is not { } shot)
            {
                return Task.FromResult(new AgentToolResult(
                    false, string.Empty, $"There is nothing called {id} on the canvas."));
            }

            var asked = Text(arguments, "move").ToLowerInvariant();

            if (asked is "still" or "none" or "off")
            {
                session.Record(session.Current.Set(id, "move", string.Empty), $"Stilled {id}");
                return Task.FromResult(new AgentToolResult(true, "That shot holds still."));
            }

            // Checked here rather than swallowed at export, for the same reason grading is:
            // a movement that does nothing because nobody knows the word is a shot that looks
            // unchanged and a person who believes it worked.
            var asFilter = FfmpegMediaExport.MoveOf(
                DesignNode.New(DesignNodeKind.Frame, null, ("move", asked)), seconds: 3, still: true);

            if (asFilter.Length == 0)
            {
                return Task.FromResult(new AgentToolResult(
                    false,
                    string.Empty,
                    $"There is no movement called {asked}. Try: {string.Join(", ", FfmpegMediaExport.Moves)}."));
            }

            // A word that only works on a still is refused on footage rather than accepted
            // and quietly dropped. Told "done", nobody looks at that shot again.
            var onFootage = shot.Props.TryGetValue("src", out var src) && !string.IsNullOrWhiteSpace(src);

            if (onFootage && FfmpegMediaExport.MoveOf(
                    DesignNode.New(DesignNodeKind.Frame, null, ("move", asked)), seconds: 3, still: false).Length == 0)
            {
                return Task.FromResult(new AgentToolResult(
                    false,
                    string.Empty,
                    $"{asked} needs a still to work on — that shot is filmed, and it is already "
                    + "moving. A fade works on either."));
            }

            session.Record(session.Current.Set(id, "move", asked), $"Moved {id}");

            return Task.FromResult(new AgentToolResult(true, $"That shot {asked}s while it is on screen."));
        }
    }

    /// <summary>
    /// A picture made to order, put straight on the design.
    ///
    /// The paperclip puts a picture somebody already has on the canvas; this makes the one
    /// they do not. It is the line open-design has and this did not, and the gap was never
    /// the generator — the seam and two providers have been here for months, and **nothing
    /// but the composer could reach them**, so a model asked to illustrate a page could not.
    ///
    /// **It asks, and the description is on the card.** It is the second design tool that
    /// does, for the same reason as the first: it leaves the device, and no amount of picking
    /// an earlier picture un-makes a request somebody else has already received and billed
    /// for.
    ///
    /// What arrives is carried as a data URI, the way the paperclip carries a picture, so the
    /// design still travels rather than pointing at a provider's address that stops working
    /// in an hour.
    /// </summary>
    private sealed class MakeAPictureForIt(DesignWorkbench workbench) : IAgentTool
    {
        /// <summary>
        /// The same cap the paperclip works to. The design is rewritten whole whenever
        /// anybody edits a heading, so a picture that makes every keystroke cost eight
        /// megabytes of writing is a picture that makes the canvas unusable.
        /// </summary>
        private const int MostBytes = 8 * 1024 * 1024;

        public string Name => "design_picture";

        public string Description =>
            "Make a picture from a description and put it straight on the design. The person "
            + "is asked first, because it leaves the device.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["of"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "What the picture should be of, in full.",
                },
                ["on"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Which slide, shot or panel it goes on. The page itself by default.",
                },
            },
            ["required"] = new JsonArray("of"),
        };

        public bool IsReadOnly => false;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            if (workbench.Session is not { } session)
            {
                return NoCanvas();
            }

            if (workbench.Pictures is not { IsReady: true } maker || workbench.Approval is not { } approval)
            {
                return new AgentToolResult(
                    false, string.Empty, "Nothing here can make a picture, so none was made.");
            }

            var of = Text(arguments, "of");

            if (of.Length == 0)
            {
                return new AgentToolResult(false, string.Empty, "Say what the picture should be of.");
            }

            var on = Text(arguments, "on");

            // Checked before anything is sent, because a picture generated onto a frame that
            // is not there costs a request somebody paid for and shows nobody anything.
            if (on.Length > 0 && session.Current.Find(on) is null)
            {
                return new AgentToolResult(
                    false, string.Empty, $"There is nothing called {on} on the canvas.");
            }

            var decision = await approval.RequestAsync(
                new ToolApprovalRequest(
                    Name,
                    of,
                    ConciergeToolRisk.Medium,
                    $"It sends those words to {maker.EngineLabel} and puts the picture on the design."),
                cancellationToken).ConfigureAwait(false);

            if (decision != ToolApprovalDecision.Allowed)
            {
                return new AgentToolResult(false, string.Empty, "Not allowed, so no picture was made.");
            }

            IReadOnlyList<Concierge.Shared.Media.ImageArtifact> made;

            try
            {
                made = await maker
                    .GenerateAsync(new Concierge.Shared.Media.ImageGenerationRequest(of), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException problem)
            {
                return new AgentToolResult(false, string.Empty, $"That did not come back: {problem.Message}");
            }
            catch (TaskCanceledException)
            {
                return new AgentToolResult(false, string.Empty, "That took too long and was stopped.");
            }

            if (made.Count == 0)
            {
                return new AgentToolResult(false, string.Empty, "Nothing came back, so nothing was added.");
            }

            var (bytes, problemGetting) = await Concierge.Shared.Media.ImageWords
                .BytesOf(made[0], workbench.Web, MostBytes, cancellationToken)
                .ConfigureAwait(false);

            if (bytes is null)
            {
                return new AgentToolResult(
                    false,
                    string.Empty,
                    problemGetting ?? "The picture came back as an address this head cannot fetch, "
                    + "and an address in a design stops working.");
            }

            var picture = DesignNode.New(
                DesignNodeKind.Image,
                on.Length > 0 ? on : null,
                ("text", of),
                ("src", $"data:{made[0].MimeType};base64,{Convert.ToBase64String(bytes)}"));

            session.Record(session.Current.Add(picture), $"Made a picture of {of}");

            return new AgentToolResult(
                true, $"Put a picture of {of} on the design, {bytes.Length / 1024}KB.");
        }
    }

    // ── Shared ────────────────────────────────────────────────────────────

    /// <summary>
    /// The canvas closed between the catalogue being read and the tool being
    /// called. A failure rather than a silent success, because a model told
    /// nothing happened can say so, and one told nothing at all will report that
    /// it did the thing.
    /// </summary>
    private static AgentToolResult NoCanvas()
        => new(false, string.Empty, "There is no design canvas open.");

    /// <summary>
    /// A number from the arguments, however it was written.
    ///
    /// `GetValue&lt;double&gt;()` throws on a JSON integer — and a model asked for
    /// seconds writes `4`, not `4.0`, nearly every time. Reading the raw value and
    /// parsing it takes both, and takes `"4"` as well, which is what a model that
    /// has decided every argument is a string will send.
    /// </summary>
    private static double? Amount(JsonNode? arguments, string key)
    {
        if (arguments?[key] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var number))
        {
            return number;
        }

        if (value.TryGetValue<int>(out var whole))
        {
            return whole;
        }

        return value.TryGetValue<string>(out var written)
            && double.TryParse(written, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// A string from the arguments, however it was written.
    ///
    /// `GetValue&lt;string&gt;()` throws when the value is a number, and a model
    /// answering "how many seconds" writes `0.8`, not `"0.8"`. Every tool here
    /// reads its arguments through this, so one tolerant reader is worth more than
    /// remembering which fields might arrive as numbers — and forgetting was
    /// exactly what happened twice in one afternoon.
    /// </summary>
    private static string Text(JsonNode? arguments, string key)
    {
        if (arguments?[key] is not JsonValue value)
        {
            return string.Empty;
        }

        if (value.TryGetValue<string>(out var written))
        {
            return written.Trim();
        }

        return value.ToJsonString().Trim('"').Trim();
    }
}
