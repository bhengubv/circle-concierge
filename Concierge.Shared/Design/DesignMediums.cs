using System.Net;
using System.Text;

namespace Concierge.Shared.Design;

/// <summary>A medium, as a person meets it: a name and a sentence.</summary>
/// <param name="Medium">Which one.</param>
/// <param name="Name">What it is called. A word people already use.</param>
/// <param name="Blurb">What you would make with it, in plain words.</param>
/// <param name="Piece">What one of its frames is called — a slide, a shot, a room.</param>
public sealed record DesignMediumInfo(DesignMedium Medium, string Name, string Blurb, string Piece);

/// <summary>
/// Drawing the four things that are sequences, and the one that is not.
///
/// The document model knows nothing about any of this. A poster and a film differ
/// only in which function here is called, which is why matching five separate
/// tools is five renderers rather than five products.
///
/// This was justified for a while by a claim about the projects it took
/// inspiration from — that each welds its renderer to its document, so none can
/// become another. Having since read them rather than their READMEs: that is
/// false. Pascal's core carries an architecture *test* that fails the build on a
/// runtime `three` import, with the rule written above it: "core is pure logic —
/// no Three.js, no rendering." Diffusion Studio's runtime describes itself as
/// "headless… No DOM, no solid-js", with encoding and reconciling as separate
/// packages.
///
/// So the separation here is not a differentiator. It is what the serious
/// projects in this space also do, which is evidence it is right rather than
/// evidence anybody is clever. What actually differs is the bar the surface is
/// held to, and that is where the argument belongs.
///
/// All five produce a standalone HTML document, which sounds like a limitation
/// and is mostly a choice. It is the one format Concierge can show inside its own
/// window, screenshot, print and hand to somebody, on every head it ships on, with
/// no dependency and no export step. Where that genuinely is a limitation — an MP4
/// rather than something that plays, a real 3D engine rather than boxes in space —
/// it is said out loud in the renderer that has it, rather than implied by
/// silence.
/// </summary>
public static class DesignMediums
{
    /// <summary>The five, in the order they are offered.</summary>
    public static readonly IReadOnlyList<DesignMediumInfo> All =
    [
        new(DesignMedium.Page, "Page", "A poster, a note, something to read.", "page"),
        new(DesignMedium.Deck, "Slides", "Things to show one after another.", "slide"),
        new(DesignMedium.Motion, "Video", "Shots that play, one after another.", "shot"),
        new(DesignMedium.Scene, "Space", "A room you can look around.", "room"),
        new(DesignMedium.Sound, "Sound", "Music, a voice, something to listen to.", "track"),
        new(DesignMedium.Handheld, "Phone", "A screen, drawn the size of a phone.", "screen"),
        new(DesignMedium.Board, "Board", "Numbers to glance at.", "panel"),
    ];

    /// <summary>
    /// The frames to draw — including the one that is implied.
    ///
    /// A page keeps its content loose on the root, so turning a page into slides
    /// found no frames and drew "No slides yet" over a design that was still all
    /// there. `As` claimed changing your mind threw nothing away and that was true
    /// of the data and false of the screen, which is the worse half.
    ///
    /// So loose content counts as one frame. A page becomes a one-slide deck, a
    /// one-shot video, a one-room space — which is what somebody who just pressed
    /// Slides expects to see, and they can add a second whenever they like.
    /// </summary>
    public static IReadOnlyList<DesignNode> FramesOf(DesignDocument document)
    {
        var frames = document.Frames;

        if (frames.Count > 0)
        {
            return frames;
        }

        // The root itself, standing in for the frame nobody has made yet. Its
        // children are the content, which is exactly what a frame's children are.
        return document.IsEmpty ? [] : [document.Nodes[document.RootId]];
    }

    public static DesignMediumInfo Of(DesignMedium medium)
        => All.FirstOrDefault(m => m.Medium == medium) ?? All[0];

    /// <summary>What one frame is called here — "slide", "shot", "room", "track".</summary>
    public static string PieceOf(DesignMedium medium) => Of(medium).Piece;

    /// <summary>
    /// Draws whatever this is. The one place the medium decides anything.
    /// </summary>
    public static string Render(DesignDocument document, string? selectedId = null)
        => document.Medium switch
        {
            DesignMedium.Deck => DeckRenderer.ToHtml(document, selectedId),
            DesignMedium.Motion => MotionRenderer.ToHtml(document, selectedId),
            DesignMedium.Scene => SceneRenderer.ToHtml(document, selectedId),
            DesignMedium.Sound => SoundRenderer.ToHtml(document, selectedId),
            DesignMedium.Handheld => HandheldRenderer.ToHtml(document, selectedId),
            DesignMedium.Board => BoardRenderer.ToHtml(document, selectedId),
            _ => DesignRenderer.ToHtml(document, selectedId),
        };

    // ── Shared bits ───────────────────────────────────────────────────────

    internal static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>
    /// The head every medium shares: the look's colours, and a page that fills its
    /// frame. Written per document rather than linked, because a rendered design
    /// has to stand alone in an iframe, a saved file, or a print dialog.
    /// </summary>
    internal static void OpenDocument(StringBuilder html, DesignLook look, string extra)
    {
        html.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.AppendLine("<style>");
        html.AppendLine($$"""
            :root {
              --ink: {{look.Ink}};
              --ground: {{look.Ground}};
              --raised: {{look.Raised}};
              --accent: {{look.Accent}};
              --radius: {{look.Radius}};
            }
            * { box-sizing: border-box; }
            html, body { height: 100%; }
            body {
              margin: 0;
              background: var(--ground);
              color: var(--ink);
              font-family: {{look.Fonts}};
              line-height: 1.5;
            }
            .picked { outline: 2px solid var(--accent); outline-offset: 3px; }
            .nothing { opacity: .55; font-style: italic; }

            /* A word longer than the frame. Somebody pastes a URL or a file path and every
               medium that shares this head would otherwise grow wider than the window — on
               screen and in the saved file. Breaking the word is the lesser harm. */
            body, h1, h2, p, button, section, figcaption, div, span {
              overflow-wrap: anywhere;
            }
            """);
        html.AppendLine(extra);
        html.AppendLine("</style></head><body>");
    }

    /// <summary>The content of one frame, drawn the way a page draws things.</summary>
    /// <param name="alreadyDrawn">
    /// Whether the caller has already drawn something for this frame out of its own
    /// properties, so an empty child list is not an empty frame.
    ///
    /// **A board panel showing 48,200 said "Nothing on this panel yet" directly underneath
    /// it.** A panel's number, direction and note are properties of the frame, not children,
    /// so the only test for emptiness here counted nought and said so — on the one medium
    /// built entirely around a number, under the number. Seen on the running app the day
    /// panels became sayable.
    /// </param>
    internal static void WriteContents(
        StringBuilder html,
        DesignDocument document,
        DesignNode frame,
        string? selectedId,
        bool alreadyDrawn = false)
    {
        var children = document.ChildrenOf(frame.Id);

        if (children.Count == 0)
        {
            if (!alreadyDrawn)
            {
                html.AppendLine($"<p class=\"nothing\">Nothing on this {Escape(PieceOf(document.Medium))} yet.</p>");
            }

            return;
        }

        foreach (var child in children)
        {
            var picked = string.Equals(child.Id, selectedId, StringComparison.Ordinal) ? " picked" : string.Empty;
            var node = $" data-node=\"{Escape(child.Id)}\"";

            switch (child.Kind)
            {
                case DesignNodeKind.Heading:
                    html.AppendLine($"<h1 class=\"h{picked}\"{node}>{Escape(child.Text)}</h1>");
                    break;

                case DesignNodeKind.Text:
                    html.AppendLine($"<p class=\"t{picked}\"{node}>{Escape(child.Text)}</p>");
                    break;

                case DesignNodeKind.Button:
                    html.AppendLine($"<button type=\"button\" class=\"b{picked}\"{node}>{Escape(child.Text)}</button>");
                    break;

                case DesignNodeKind.Image:
                    var src = child.Props.TryGetValue("src", out var s) ? s : null;
                    html.AppendLine(string.IsNullOrWhiteSpace(src) || !DesignRenderer.IsSafeSource(src)
                        ? $"<div class=\"ph{picked}\"{node}>{Escape(child.Text.Length > 0 ? child.Text : "A picture")}</div>"
                        : $"<img class=\"im{picked}\" src=\"{Escape(src)}\" alt=\"{Escape(child.Text)}\"{node}>");
                    break;

                case DesignNodeKind.Sound:
                    WriteSound(html, child, picked, node);
                    break;

                case DesignNodeKind.Solid:
                    WriteSolid(html, child, picked, node);
                    break;
            }
        }
    }

    /// <summary>
    /// A sound, with controls. Real playback rather than an icon: a person adding a
    /// piece of music to a slide wants to hear whether it is the right one, and a
    /// picture of a speaker answers nothing.
    /// </summary>
    internal static void WriteSound(StringBuilder html, DesignNode node, string picked, string attr)
    {
        var src = node.Props.TryGetValue("src", out var s) ? s : null;
        var name = node.Text.Length > 0 ? node.Text : "A sound";

        if (string.IsNullOrWhiteSpace(src) || !IsSafeAudio(src))
        {
            html.AppendLine($"<div class=\"snd ph{picked}\"{attr}>{Escape(name)}</div>");
            return;
        }

        html.AppendLine($"<figure class=\"snd{picked}\"{attr}>");
        html.AppendLine($"<figcaption>{Escape(name)}</figcaption>");

        // A real waveform, drawn from the decoded audio, or nothing.
        //
        // Nothing is drawn here — the canvas is filled in by script once the samples have
        // actually been read. A shape invented to look like sound is the same defect as a
        // number invented to look like a measurement, and this medium is the one where it
        // would be least noticed and most dishonest.
        html.AppendLine($"<canvas class=\"wave\" height=\"48\" data-src=\"{Escape(src)}\"></canvas>");
        html.AppendLine($"<audio controls preload=\"metadata\" src=\"{Escape(src)}\"></audio>");
        html.AppendLine("</figure>");
    }

    /// <summary>
    /// Something with three dimensions, positioned in space.
    ///
    /// CSS transforms rather than a 3D engine — and this is now the *fallback*
    /// rather than the whole story. `SceneRenderer` draws the same room again with
    /// three.js over the top and hides this one once it has succeeded, so meshes,
    /// lights and shadows are there on a machine that can manage them.
    ///
    /// This one stays because it is what somebody sees when the engine cannot
    /// load, and because it is what is on screen for the moment it takes to load.
    /// It draws boxes and only boxes: a solid asked to be a sphere is a box here,
    /// which is a fallback being worse rather than a fallback being wrong.
    /// </summary>
    internal static void WriteSolid(StringBuilder html, DesignNode node, string picked, string attr)
    {
        var w = Number(node, "width", 120);
        var d = Number(node, "depth", 120);
        var h = Number(node, "height", 80);
        var x = Number(node, "x", 0);
        var y = Number(node, "y", 0);

        // A plan lies flat on the floor with the picture on it, rather than standing up as a
        // box labelled "The plan".
        //
        // The engine has drawn it properly since it was written; this renderer never learned
        // the shape, and this renderer is the default — it is what a machine with no WebGL
        // shows, and the whole ordering argument is that the fallback is the one that has to
        // work. A solid where a flat tracing sheet should be does not merely look worse; it
        // stands in front of the walls somebody is drawing over it.
        if (node.Props.TryGetValue("shape", out var kind)
            && kind.Equals("plan", StringComparison.OrdinalIgnoreCase))
        {
            var picture = node.Props.TryGetValue("src", out var src) && DesignRenderer.IsSafeSource(src)
                ? src
                : string.Empty;

            var flat =
                $"position:absolute;width:{w}px;height:{d}px;"
                + $"transform:translate3d({x}px,0,{y}px) rotateX(90deg);transform-origin:top left;"
                + "opacity:.85;border-radius:2px;"
                + (picture.Length > 0
                    ? $"background-image:url('{Escape(picture)}');background-size:cover;"
                    : "background:rgba(128,128,128,.25);");

            html.AppendLine($"<div class=\"plan{picked}\" style=\"{flat}\"{attr}></div>");
            return;
        }

        var style =
            $"--w:{w}px;--d:{d}px;--h:{h}px;transform:translate3d({x}px,0,{y}px)";

        html.AppendLine($"<div class=\"solid{picked}\" style=\"{style}\"{attr}>");
        html.AppendLine("<div class=\"face top\"></div><div class=\"face front\"></div><div class=\"face side\"></div>");

        if (node.Text.Length > 0)
        {
            html.AppendLine($"<span class=\"label\">{Escape(node.Text)}</span>");
        }

        html.AppendLine("</div>");
    }

    internal static int Number(DesignNode node, string key, int fallback)
        => node.Props.TryGetValue(key, out var raw) && int.TryParse(raw, out var value)
            ? Math.Clamp(value, -4000, 4000)
            : fallback;

    /// <summary>
    /// Audio that is already here or already public, for the reason pictures have
    /// the same rule: a model can write any string into a source, and a file://
    /// address would pull something off the machine into a document that might be
    /// shared.
    /// </summary>
    internal static bool IsSafeAudio(string source)
        => source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
           || source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || source.StartsWith("data:audio/", StringComparison.OrdinalIgnoreCase);
}
