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
    /// Sound and video only. A page and a deck are already printable from the
    /// surface itself, and a space is not a file yet.
    /// </summary>
    private sealed class SaveTheDesign(DesignWorkbench workbench) : IAgentTool
    {
        public string Name => "design_save";

        public string Description =>
            "Save the design as a file that can be kept and shared: an audio file when making "
            + "a sound, a video file when making a motion piece. Says where it put it.";

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
            var sound = document.Medium == DesignMedium.Sound;

            if (!sound && document.Medium != DesignMedium.Motion)
            {
                return new AgentToolResult(
                    false,
                    $"A {document.Medium.ToString().ToLowerInvariant()} is not saved as a file — "
                    + "it is printed from the page itself.");
            }

            var asked = Text(arguments, "name");
            var stem = string.IsNullOrWhiteSpace(asked) ? "design" : Tidy(asked);
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

    // ── Shared ────────────────────────────────────────────────────────────

    /// <summary>
    /// The canvas closed between the catalogue being read and the tool being
    /// called. A failure rather than a silent success, because a model told
    /// nothing happened can say so, and one told nothing at all will report that
    /// it did the thing.
    /// </summary>
    private static AgentToolResult NoCanvas()
        => new(false, string.Empty, "There is no design canvas open.");

    private static string Text(JsonNode? arguments, string key)
        => arguments?[key]?.GetValue<string>()?.Trim() ?? string.Empty;
}
