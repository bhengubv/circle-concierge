using System.Text;

namespace Concierge.Shared.Design;

/// <summary>
/// Slides.
///
/// One slide filling the frame, the rest stacked behind it, and arrow keys to move
/// between them — which is every presentation tool's core in about eighty lines,
/// because the document already knows what the slides are.
///
/// No speaker-notes pane, no transitions menu, no master-slide editor. Those are
/// the three things that make presentation software feel like work, and none of
/// them is what somebody making six slides on a Sunday needs.
///
/// It prints. A browser printing this gets one slide per sheet, which is how a
/// deck becomes a handout without an export step and without anybody being told
/// about PDF settings.
/// </summary>
public static class DeckRenderer
{
    public static string ToHtml(DesignDocument document, string? selectedId = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var html = new StringBuilder();
        DesignMediums.OpenDocument(html, DesignLooks.Of(document.Look), Style);

        var slides = document.Frames;

        if (slides.Count == 0)
        {
            html.AppendLine("<section class=\"slide on\"><p class=\"nothing\">No slides yet. Say: add a slide.</p></section>");
            html.AppendLine("</body></html>");
            return html.ToString();
        }

        // Which slide shows: the one holding whatever is being pointed at, so
        // clicking a thumbnail and correcting what is on it are the same gesture.
        var showing = IndexHolding(document, slides, selectedId);

        for (var i = 0; i < slides.Count; i++)
        {
            var picked = string.Equals(slides[i].Id, selectedId, StringComparison.Ordinal) ? " picked" : string.Empty;

            html.AppendLine(
                $"<section class=\"slide{(i == showing ? " on" : string.Empty)}{picked}\" data-node=\"{DesignMediums.Escape(slides[i].Id)}\">");

            if (slides[i].Text.Length > 0)
            {
                html.AppendLine($"<h1 class=\"h\">{DesignMediums.Escape(slides[i].Text)}</h1>");
            }

            DesignMediums.WriteContents(html, document, slides[i], selectedId);
            html.AppendLine($"<span class=\"num\">{i + 1} of {slides.Count}</span>");
            html.AppendLine("</section>");
        }

        // Arrows and space, because that is what everybody already presses. No
        // on-screen controls: they would be in every screenshot and every photo
        // somebody takes of the screen.
        html.AppendLine("""
            <script>
              (function () {
                var slides = [].slice.call(document.querySelectorAll('.slide'));
                var at = Math.max(0, slides.findIndex(function (s) { return s.classList.contains('on'); }));
                function show(next) {
                  at = Math.max(0, Math.min(slides.length - 1, next));
                  slides.forEach(function (s, i) { s.classList.toggle('on', i === at); });
                }
                document.addEventListener('keydown', function (e) {
                  if (e.key === 'ArrowRight' || e.key === ' ' || e.key === 'PageDown') { show(at + 1); e.preventDefault(); }
                  if (e.key === 'ArrowLeft' || e.key === 'PageUp') { show(at - 1); e.preventDefault(); }
                });
              })();
            </script>
            """);

        html.AppendLine("</body></html>");
        return html.ToString();
    }

    /// <summary>
    /// Which slide to show: the one containing the selection, or the first.
    /// </summary>
    private static int IndexHolding(DesignDocument document, IReadOnlyList<DesignNode> slides, string? selectedId)
    {
        if (selectedId is null)
        {
            return 0;
        }

        for (var i = 0; i < slides.Count; i++)
        {
            if (slides[i].Id == selectedId
                || document.ChildrenOf(slides[i].Id).Any(c => c.Id == selectedId))
            {
                return i;
            }
        }

        return 0;
    }

    private const string Style = """
        .slide {
          display: none;
          position: absolute; inset: 0;
          padding: clamp(1.5rem, 5vw, 4rem);
          flex-direction: column; justify-content: center; gap: 1rem;
        }
        .slide.on { display: flex; }
        .h { font-size: clamp(1.8rem, 6vw, 3.4rem); margin: 0; line-height: 1.1; }
        .t { font-size: clamp(1rem, 2.6vw, 1.35rem); margin: 0; }
        .im, .ph { max-width: 100%; max-height: 45vh; object-fit: contain; border-radius: var(--radius); }
        .ph { background: var(--raised); display: grid; place-items: center; min-height: 8rem; opacity: .75; }
        .b {
          font: inherit; font-weight: 600; color: var(--ground); background: var(--accent);
          border: 0; border-radius: var(--radius); padding: .7rem 1.2rem; align-self: flex-start;
        }
        .snd { margin: 0; }
        .snd figcaption { font-size: .85rem; opacity: .7; margin-bottom: .35rem; }
        .num { position: absolute; right: 1.25rem; bottom: 1rem; font-size: .8rem; opacity: .45; }

        /* Printing turns a deck into a handout: every slide on its own sheet, no
           export step and nobody told about PDF settings. */
        @media print {
          .slide { display: flex !important; position: relative; break-after: page; min-height: 100vh; }
          .num { position: static; margin-top: auto; }
        }
        """;
}
