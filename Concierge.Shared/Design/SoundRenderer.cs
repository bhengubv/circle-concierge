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
/// **A track shows what it knows about itself** — its cover, who made it, what it
/// belongs to, and its words, when it has been told any of that. Antra's half of
/// this was written off here as "a library, not a design surface", which was my
/// decision presented as a settled one. The line was never that clean: artwork and
/// lyrics are things you look at, and a surface showing neither was showing less
/// than the exported file already carried.
///
/// The words are folded away rather than printed down the page. Lyrics are long,
/// this is a running order, and something that pushes the next track off the
/// screen has stopped being a running order.
///
/// **What is still not here, and it is one thing:** fetching. Antra takes links in
/// — you give it an address and it goes and gets the music. Everything here is
/// already on the machine, because the export refuses to make a network request to
/// a string a model may have written. That refusal is deliberate, and undoing it is
/// a decision about what this program may reach.
///
/// What this is not: a mixer. It plays what you give it in the order you put it.
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
                WriteDetails(html, SoundDetails.Of(track, document));
                DesignMediums.WriteSound(html, track, string.Empty, string.Empty);
            }
            else
            {
                // A frame holding sounds — a section of the running order rather
                // than one piece. It carries what it is in exactly the same way:
                // saying "add a track" makes one of these, so drawing details only
                // for the other kind meant somebody who said "the artist is Nina
                // Simone" watched the history strip record it and the track show
                // nothing. Watched that happen.
                html.AppendLine($"<div class=\"grp\"><span class=\"nm\">{DesignMediums.Escape(track.Text.Length > 0 ? track.Text : "A section")}</span>");
                WriteDetails(html, SoundDetails.Of(track, document));
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

    /// <summary>
    /// What a track knows about itself, when it knows anything.
    ///
    /// Silent when it does not: a design that has said none of this looks exactly
    /// as it did, rather than growing a row of empty labels. An empty field on
    /// screen is a form, and this is not a form.
    /// </summary>
    private static void WriteDetails(StringBuilder html, SoundDetails details)
    {
        if (!details.WorthShowing)
        {
            return;
        }

        html.AppendLine("<div class=\"about\">");

        if (details.Artwork is { } cover)
        {
            html.AppendLine($"<img class=\"cover\" src=\"{DesignMediums.Escape(cover)}\" alt=\"\">");
        }

        var said = new List<string>();

        if (details.Artist.Length > 0)
        {
            said.Add(DesignMediums.Escape(details.Artist));
        }

        if (details.Album.Length > 0)
        {
            said.Add(DesignMediums.Escape(details.Album));
        }

        if (details.Year.Length > 0)
        {
            said.Add(DesignMediums.Escape(details.Year));
        }

        if (said.Count > 0)
        {
            html.AppendLine($"<span class=\"by\">{string.Join(" · ", said)}</span>");
        }

        if (details.Lyrics.Length > 0)
        {
            // Folded away. Lyrics are long and this is a running order; printing
            // them would push the next track off the screen.
            html.AppendLine("<details class=\"words\"><summary>Words</summary><pre>");
            html.AppendLine(DesignMediums.Escape(details.Lyrics));
            html.AppendLine("</pre></details>");
        }

        html.AppendLine("</div>");
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

        /* What a track knows about itself. Sits beside the player rather than
           above it, so the running order still reads as a list. */
        .about { display: flex; align-items: center; gap: .55rem; flex-wrap: wrap; }
        .cover {
          width: 44px; height: 44px; object-fit: cover;
          border-radius: calc(var(--radius) / 2); flex: none;
        }
        .by { font-size: .78rem; opacity: .65; }
        .words { font-size: .78rem; opacity: .75; width: 100%; }
        .words summary { cursor: pointer; }
        .words pre { margin: .35rem 0 0; white-space: pre-wrap; font: inherit; opacity: .85; }

        .pad { padding: 2rem; }
        """;
}
