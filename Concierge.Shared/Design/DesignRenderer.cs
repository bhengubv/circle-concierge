using System.Net;
using System.Text;

namespace Concierge.Shared.Design;

/// <summary>
/// Turning a design into something you can look at.
///
/// HTML first, and not because HTML is the goal. It is the one thing Concierge can
/// already show you inside its own window, screenshot, and print — so the loop
/// "say it, see it, point at it" closes today rather than after a rendering
/// engine. The document model knows nothing about HTML; a second renderer is a
/// second file, not a rewrite.
///
/// Two things this deliberately does that a template engine would not:
///
/// **Every element carries its node id.** That is what makes pointing work. Click
/// something on the canvas and the surface knows which node you meant, so "make
/// this bigger" has a subject. Without it, correcting a design means describing
/// which part you mean, which is precisely the kind of work this surface exists to
/// remove.
///
/// **Everything is escaped.** The text in a design comes from a model or from a
/// sentence somebody typed, and it is rendered inside the application's own
/// window. Unescaped, a heading containing a script tag would run in there. Nobody
/// is attacking a five-year-old's poster, and it costs one function to never find
/// out.
/// </summary>
public static class DesignRenderer
{
    /// <summary>The attribute carrying a node's id, so a click can be traced back.</summary>
    public const string NodeAttribute = "data-node";

    /// <summary>
    /// The whole design as a standalone HTML document.
    ///
    /// Standalone so it can be dropped into an iframe, saved, or opened in a
    /// browser without anything else being true.
    /// </summary>
    public static string ToHtml(DesignDocument document, string? selectedId = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var look = DesignLooks.Of(document.Look);
        var html = new StringBuilder();

        html.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.AppendLine("<style>");
        html.AppendLine(Stylesheet(look));
        html.AppendLine("</style></head><body>");

        Write(html, document, document.RootId, selectedId, depth: 0);

        html.AppendLine("</body></html>");

        return html.ToString();
    }

    private static void Write(
        StringBuilder html, DesignDocument document, string id, string? selectedId, int depth)
    {
        if (document.Find(id) is not { } node)
        {
            return;
        }

        // A design deep enough to hit this is either a mistake or a loop, and a
        // renderer that recurses forever takes the window with it.
        if (depth > 32)
        {
            return;
        }

        // Classes: what is pointed at, and how big it was asked to be. Size is a
        // word rather than a number — "bigger" is what somebody says, and a
        // surface that answered it with 1.4rem would have invented a precision
        // nobody asked for.
        var classes = new List<string>();

        if (string.Equals(node.Id, selectedId, StringComparison.Ordinal))
        {
            classes.Add("picked");
        }

        if (node.Props.TryGetValue("size", out var size) && (size == "big" || size == "small"))
        {
            classes.Add(size);
        }

        var attrs = $" {NodeAttribute}=\"{Escape(node.Id)}\""
                    + (classes.Count > 0 ? $" class=\"{string.Join(' ', classes)}\"" : string.Empty);
        var children = document.ChildrenOf(node.Id);

        switch (node.Kind)
        {
            case DesignNodeKind.Page:
                html.AppendLine($"<main{attrs}>");
                foreach (var child in children)
                {
                    Write(html, document, child.Id, selectedId, depth + 1);
                }

                // An empty page says so, in the second person, because a blank
                // white rectangle looks like something that failed to load.
                if (children.Count == 0)
                {
                    html.AppendLine("<p class=\"nothing\">Nothing here yet. Say what you would like.</p>");
                }

                html.AppendLine("</main>");
                break;

            case DesignNodeKind.Box:
                html.AppendLine($"<section{attrs}>");
                foreach (var child in children)
                {
                    Write(html, document, child.Id, selectedId, depth + 1);
                }

                html.AppendLine("</section>");
                break;

            case DesignNodeKind.Heading:
                html.AppendLine($"<h1{attrs}>{Escape(node.Text)}</h1>");
                break;

            case DesignNodeKind.Text:
                html.AppendLine($"<p{attrs}>{Escape(node.Text)}</p>");
                break;

            case DesignNodeKind.Button:
                html.AppendLine($"<button type=\"button\"{attrs}>{Escape(node.Text)}</button>");
                break;

            // A slide, a shot, a room, a screen or a panel, drawn on a page.
            //
            // **Without this, turning any of them back into a page lost everything on
            // screen.** The document still had it — going back to Slides brought it
            // all up again — but the page drew an empty rectangle with no explanation,
            // which is the worst way to be told nothing is wrong.
            //
            // This is the twin of a defect already fixed in the other direction: a page's
            // loose content found no frames and drew "No slides yet" over a design that was
            // still all there. Both directions now keep what is on them, which is the claim
            // the whole surface rests on.
            case DesignNodeKind.Frame:
                html.AppendLine($"<section{attrs}>");

                if (node.Text.Length > 0)
                {
                    html.AppendLine($"<h2>{Escape(node.Text)}</h2>");
                }

                foreach (var child in children)
                {
                    Write(html, document, child.Id, selectedId, depth + 1);
                }

                html.AppendLine("</section>");
                break;

            // Something to listen to, on a page. Real controls rather than an icon, for the
            // same reason the running order has them: somebody adding a piece of music wants
            // to hear whether it is the right one.
            case DesignNodeKind.Sound:
                var track = node.Props.TryGetValue("src", out var heard) ? heard : null;
                var called = node.Text.Length > 0 ? node.Text : "A sound";

                html.AppendLine(string.IsNullOrWhiteSpace(track) || !DesignMediums.IsSafeAudio(track)
                    ? $"<div class=\"placeholder\"{attrs}>{Escape(called)}</div>"
                    : $"<figure{attrs}><figcaption>{Escape(called)}</figcaption>"
                      + $"<audio controls preload=\"metadata\" src=\"{Escape(track)}\"></audio></figure>");
                break;

            // A thing from a room, on a page that has no room to put it in. Named rather
            // than drawn: a page is not a floor, and a labelled block says what is there
            // without pretending to be a view of it.
            case DesignNodeKind.Solid:
                html.AppendLine(
                    $"<div class=\"placeholder\"{attrs}>{Escape(node.Text.Length > 0 ? node.Text : "A thing")}</div>");
                break;

            case DesignNodeKind.Image:
                // A picture that has not been chosen yet is a labelled space, not a
                // broken-image icon. The design is unfinished, not wrong.
                var source = node.Props.TryGetValue("src", out var src) ? src : null;
                var caption = node.Text.Length > 0 ? node.Text : "A picture";

                html.AppendLine(string.IsNullOrWhiteSpace(source) || !IsSafeSource(source)
                    ? $"<div class=\"placeholder\"{attrs}>{Escape(caption)}</div>"
                    : $"<img src=\"{Escape(source)}\" alt=\"{Escape(caption)}\"{attrs}>");
                break;
        }
    }

    /// <summary>
    /// Only pictures that are already here or already public.
    ///
    /// A model can write any string into a src. A <c>file:///</c> address would
    /// pull something off the machine into a document that might be shared, and a
    /// <c>javascript:</c> one is a script. Anything not on this list renders as the
    /// placeholder, which is a design that is unfinished rather than a design that
    /// reached somewhere it should not.
    /// </summary>
    internal static bool IsSafeSource(string source)
        => source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
           || source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);

    private static string Escape(string value) => WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>
    /// The look, as CSS.
    ///
    /// Written per document rather than shipped as a stylesheet, because the
    /// rendered page has to stand on its own — in an iframe, in a file somebody
    /// saved, in a print dialog — with nothing else loaded.
    /// </summary>
    private static string Stylesheet(DesignLook look) => $$"""
        :root {
          --ink: {{look.Ink}};
          --ground: {{look.Ground}};
          --raised: {{look.Raised}};
          --accent: {{look.Accent}};
          --radius: {{look.Radius}};
        }
        * { box-sizing: border-box; }
        body {
          margin: 0;
          background: var(--ground);
          color: var(--ink);
          font-family: {{look.Fonts}};
          line-height: 1.5;
        }

        /* A word longer than the page.
           **A design that scrolls sideways is a design nobody can read, and it is the one
           thing the standing check says must never happen.** Somebody pastes a URL, or a
           German compound noun, or a file path, and the whole page grows wider than the
           window — on screen, and in the file when it is saved and sent to somebody else.
           Breaking the word is the lesser harm by a distance. */
        body, main, h1, h2, p, button, section, figcaption, div {
          overflow-wrap: anywhere;
          word-break: normal;
        }
        main {
          max-width: 46rem;
          margin: 0 auto;
          padding: clamp(1.25rem, 4vw, 3rem);
          display: flex;
          flex-direction: column;
          gap: 1rem;
        }
        section {
          background: var(--raised);
          border-radius: var(--radius);
          padding: 1.25rem;
          display: flex;
          flex-direction: column;
          gap: .75rem;
        }
        h1 { font-size: clamp(1.6rem, 5vw, 2.6rem); margin: 0; line-height: 1.15; }
        p { margin: 0; font-size: clamp(1rem, 2.4vw, 1.075rem); }
        img { max-width: 100%; height: auto; border-radius: var(--radius); display: block; }
        button {
          font: inherit;
          font-weight: 600;
          color: var(--ground);
          background: var(--accent);
          border: 0;
          border-radius: var(--radius);
          padding: .7rem 1.2rem;
          align-self: flex-start;
          cursor: pointer;
        }
        .placeholder {
          background: var(--raised);
          border-radius: var(--radius);
          min-height: 9rem;
          display: grid;
          place-items: center;
          opacity: .75;
          font-size: .9rem;
        }
        .nothing { opacity: .55; font-style: italic; }

        /* What you last pointed at. An outline rather than a fill, so the thing
           you are correcting still looks like itself while you correct it. */
        .picked { outline: 2px solid var(--accent); outline-offset: 3px; }

        /* Bigger and smaller, as words. Relative to whatever the thing already is,
           so "bigger" works the same on a heading and on a caption. */
        .big { font-size: 1.4em; }
        .small { font-size: 0.8em; }
        .placeholder.big { min-height: 14rem; }
        .placeholder.small { min-height: 6rem; }
        """;
}
