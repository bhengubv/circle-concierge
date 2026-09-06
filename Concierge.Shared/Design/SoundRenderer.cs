using System.Text;

namespace Concierge.Shared.Design;

/// <summary>
/// Something to listen to.
///
/// Tracks in order, each with real playback, and a button that plays the lot end
/// to end. That is what a person means by "make me a thing to listen to" — a
/// running order, not a mixing desk.
///
/// **No waveform, no faders, no tracks stacked vertically.** A waveform is a
/// picture of something nobody reads, and a mixer is the thing that makes audio
/// software feel like a cockpit. What replaces them is what somebody actually
/// says: this one, then that one, and this bit is too quiet.
///
/// Antra is the other half of this and is deliberately not here. Getting music
/// onto the machine — links in, tagged files out, artwork, lyrics, organised on
/// disk — is a library, not a design surface. This draws from a library; it does
/// not try to be one.
///
/// What this is not: a mixer or an encoder. It plays what you give it in the order
/// you put it. It does not render a single audio file, which needs an encoder, and
/// saying so beats implying otherwise.
/// </summary>
public static class SoundRenderer
{
    public static string ToHtml(DesignDocument document, string? selectedId = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var html = new StringBuilder();
        DesignMediums.OpenDocument(html, DesignLooks.Of(document.Look), Style);

        // Tracks live either as frames or loose on the root, because "add some
        // music" should work before anybody has thought about running order.
        var tracks = document.Frames.Count > 0
            ? document.Frames
            : document.ChildrenOf(document.RootId);

        if (tracks.Count == 0)
        {
            html.AppendLine("<p class=\"nothing pad\">Nothing to listen to yet. Add a sound with the paperclip.</p>");
            html.AppendLine("</body></html>");
            return html.ToString();
        }

        html.AppendLine("<main class=\"list\">");
        html.AppendLine("<button type=\"button\" class=\"all\" id=\"all\">Play everything</button>");

        var order = 1;

        foreach (var track in tracks)
        {
            var picked = string.Equals(track.Id, selectedId, StringComparison.Ordinal) ? " picked" : string.Empty;
            var attr = $" data-node=\"{DesignMediums.Escape(track.Id)}\"";

            html.AppendLine($"<div class=\"track{picked}\"{attr}>");
            html.AppendLine($"<span class=\"n\">{order++}</span>");

            if (track.Kind == DesignNodeKind.Sound)
            {
                DesignMediums.WriteSound(html, track, string.Empty, string.Empty);
            }
            else
            {
                // A frame holding sounds — a section of the running order rather
                // than one piece.
                html.AppendLine($"<div class=\"grp\"><span class=\"nm\">{DesignMediums.Escape(track.Text.Length > 0 ? track.Text : "A section")}</span>");
                DesignMediums.WriteContents(html, document, track, selectedId);
                html.AppendLine("</div>");
            }

            html.AppendLine("</div>");
        }

        html.AppendLine("</main>");

        html.AppendLine("""
            <script>
              (function () {
                var all = [].slice.call(document.querySelectorAll('audio'));
                var button = document.getElementById('all');
                if (!button || !all.length) { return; }
                var at = -1;

                function next() {
                  at++;
                  if (at >= all.length) { at = -1; button.textContent = 'Play everything'; return; }
                  button.textContent = 'Stop';
                  all[at].currentTime = 0;
                  all[at].play().catch(next);
                }

                all.forEach(function (a) { a.addEventListener('ended', next); });

                button.addEventListener('click', function () {
                  if (at >= 0) {
                    all.forEach(function (a) { a.pause(); });
                    at = -1;
                    button.textContent = 'Play everything';
                    return;
                  }
                  next();
                });
              })();
            </script>
            """);

        html.AppendLine("</body></html>");
        return html.ToString();
    }

    private const string Style = """
        .list { padding: clamp(1rem, 4vw, 2rem); display: flex; flex-direction: column; gap: .6rem; }
        .all {
          align-self: flex-start;
          font: inherit; font-weight: 600;
          color: var(--ground); background: var(--accent);
          border: 0; border-radius: 999px; padding: .55rem 1.1rem; cursor: pointer;
          margin-bottom: .4rem;
        }
        .track {
          display: flex; align-items: center; gap: .75rem;
          background: var(--raised); border-radius: var(--radius); padding: .6rem .8rem;
        }
        .n { opacity: .45; font-size: .8rem; min-width: 1.2rem; }
        .snd { margin: 0; flex: 1; }
        .snd figcaption { font-size: .85rem; margin-bottom: .25rem; }
        .snd audio { width: 100%; }
        .ph { opacity: .6; font-size: .85rem; }
        .grp { flex: 1; display: flex; flex-direction: column; gap: .4rem; }
        .nm { font-weight: 600; font-size: .9rem; }
        .t { font-size: .85rem; opacity: .8; margin: 0; }
        .pad { padding: 2rem; }
        """;
}
