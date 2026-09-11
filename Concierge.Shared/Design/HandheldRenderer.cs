using System.Text;

namespace Concierge.Shared.Design;

/// <summary>
/// A phone screen, drawn the size of one.
///
/// Its own medium rather than a narrow page, and the difference that matters is
/// not the width. A phone has a strip at the top nothing may go under, a strip at
/// the bottom the same, a thumb that comfortably reaches about two thirds of the
/// way up, and somebody holding it in one hand on a bus. **A page drawn narrow is
/// still a page.** This draws the constraints.
///
/// open-design ships mobile-app designs as one of its six artifact types, and it
/// is right to: a design that looks fine at 1200px and falls apart at 390 is the
/// commonest thing to get wrong, and nobody finds out until it is built.
///
/// **What it shows that a page cannot.** The safe area is drawn rather than
/// described, so anything pushed under the notch is visible as wrong while it is
/// being made. And the reach line is drawn — the height above which a thumb has to
/// stretch — because "put the button where somebody can press it" is a rule
/// everybody agrees with and nobody applies.
///
/// No device frame, no chrome, no fake battery icon. Those make a mock-up look
/// finished and tell you nothing; the two lines that matter are the ones drawn.
/// </summary>
public static class HandheldRenderer
{
    /// <summary>A phone, in the units the rest of the canvas uses.</summary>
    private const int ScreenWide = 390;

    private const int ScreenTall = 844;

    /// <summary>What the top strip covers, and the bottom.</summary>
    private const int SafeTop = 59;

    private const int SafeBottom = 34;

    /// <summary>
    /// How far up a thumb reaches comfortably, holding it one-handed.
    ///
    /// About two thirds. Not a precise number anywhere — it depends on the hand
    /// and the phone — which is why it is drawn as a line to think about rather
    /// than a rule that refuses anything.
    /// </summary>
    private const int Reach = 560;

    public static string ToHtml(DesignDocument document, string? selectedId = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var html = new StringBuilder();
        DesignMediums.OpenDocument(html, DesignLooks.Of(document.Look), Style);

        // Screens are frames when there are any, and the loose content otherwise,
        // so turning a page into a phone screen keeps what was on it.
        var screens = DesignMediums.FramesOf(document);

        html.AppendLine("<main class=\"phones\">");

        foreach (var screen in screens)
        {
            var picked = string.Equals(screen.Id, selectedId, StringComparison.Ordinal) ? " picked" : string.Empty;

            html.AppendLine($"<figure class=\"phone{picked}\" data-node=\"{DesignMediums.Escape(screen.Id)}\">");
            html.AppendLine("<div class=\"screen\">");

            // Drawn first so everything sits over them.
            html.AppendLine("<div class=\"safe top\" aria-hidden=\"true\"></div>");
            html.AppendLine("<div class=\"safe bottom\" aria-hidden=\"true\"></div>");
            html.AppendLine("<div class=\"reach\" aria-hidden=\"true\"><span>within reach</span></div>");

            html.AppendLine("<div class=\"content\">");
            DesignMediums.WriteContents(html, document, screen, selectedId);
            html.AppendLine("</div>");

            html.AppendLine("</div>");

            html.AppendLine(
                $"<figcaption>{DesignMediums.Escape(screen.Text.Length > 0 ? screen.Text : "A screen")}</figcaption>");

            html.AppendLine("</figure>");
        }

        if (screens.Count == 0)
        {
            html.AppendLine("<p class=\"nothing pad\">No screens yet. Say: add a screen.</p>");
        }

        html.AppendLine("</main>");
        html.AppendLine("</body></html>");

        return html.ToString();
    }

    private static readonly string Style = $$"""
        body { overflow: auto; }

        .phones {
          display: flex; flex-wrap: wrap; gap: 1.6rem;
          padding: 1.25rem; align-items: flex-start;
        }

        .phone { margin: 0; }

        /* The screen only — no device frame, no fake battery. A drawn phone makes
           a mock-up look finished and tells nobody anything. */
        .screen {
          position: relative;
          width: {{ScreenWide}}px; height: {{ScreenTall}}px;
          background: var(--ground);
          border: 1px solid var(--raised);
          border-radius: 28px;
          overflow: hidden;
        }

        /* Drawn rather than described, so anything pushed under the top or bottom
           strip is visible as wrong while it is being made. */
        .safe {
          position: absolute; left: 0; right: 0;
          background: repeating-linear-gradient(
            45deg, var(--raised) 0 6px, transparent 6px 12px);
          opacity: .5;
          pointer-events: none;
        }
        .safe.top { top: 0; height: {{SafeTop}}px; }
        .safe.bottom { bottom: 0; height: {{SafeBottom}}px; }

        /* The line above which a thumb has to stretch. A line to think about, not
           a rule that refuses anything. */
        .reach {
          position: absolute; left: 0; right: 0; top: {{ScreenTall - Reach}}px;
          border-top: 1px dashed var(--accent);
          opacity: .45;
          pointer-events: none;
        }
        .reach span {
          position: absolute; right: 6px; top: 3px;
          font-size: .62rem; color: var(--accent);
        }

        .content {
          position: absolute;
          top: {{SafeTop}}px; bottom: {{SafeBottom}}px; left: 0; right: 0;
          padding: 1rem;
          overflow: auto;
        }

        figcaption { font-size: .78rem; opacity: .6; margin-top: .5rem; text-align: center; }
        .pad { padding: 2rem; }
        """;
}
