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

        // The track stack: one lane per track, at its real length.
        //
        // **Built from what the audio reports, never from a guess.** A stack drawn before the
        // durations are known would have to invent them, and an invented measurement is slop
        // the moment it is invented — the rule the board renderer already follows with its
        // labelled blank. So the lanes appear as each track says how long it is, and a track
        // that never says stays a labelled blank.
        html.AppendLine("<div class=\"stack\" id=\"stack\" aria-label=\"The running order\"></div>");

        html.AppendLine("""
            <script>
              (function () {
                // ── The waveform ──────────────────────────────────────────────
                //
                // Real peaks off the decoded samples. Where the audio cannot be decoded —
                // a track pointing at a file on somebody's disk, a format this browser will
                // not take — the canvas is left empty and says so. Drawing a plausible shape
                // would be inventing a measurement, which is the one thing this medium must
                // not do.
                var Ctx = window.AudioContext || window.webkitAudioContext;

                [].forEach.call(document.querySelectorAll('canvas.wave'), function (c) {
                  var src = c.dataset.src;
                  if (!src || !Ctx) { c.classList.add('no'); return; }

                  fetch(src)
                    .then(function (r) { return r.arrayBuffer(); })
                    .then(function (bytes) { return new Ctx().decodeAudioData(bytes); })
                    .then(function (buffer) { draw(c, buffer); })
                    .catch(function () { c.classList.add('no'); });
                });

                function draw(canvas, buffer) {
                  var width = canvas.clientWidth || 300;
                  var height = canvas.height;
                  canvas.width = width;

                  var pen = canvas.getContext('2d');
                  var samples = buffer.getChannelData(0);
                  var per = Math.max(1, Math.floor(samples.length / width));

                  pen.clearRect(0, 0, width, height);
                  pen.fillStyle = getComputedStyle(canvas).color;

                  for (var x = 0; x < width; x++) {
                    var top = 0;
                    for (var i = 0; i < per; i++) {
                      var v = Math.abs(samples[(x * per) + i] || 0);
                      if (v > top) { top = v; }
                    }
                    var tall = Math.max(1, top * height);
                    pen.fillRect(x, (height - tall) / 2, 1, tall);
                  }

                  canvas.classList.add('drawn');
                }

                // ── The track stack ───────────────────────────────────────────
                //
                // One lane per track at its real length, filled in as each track says how
                // long it is. A lane drawn before the duration is known would be a guess.
                var stack = document.getElementById('stack');

                function lanes() {
                  if (!stack) { return; }

                  var tracks = [].slice.call(document.querySelectorAll('.track'));
                  var known = tracks.map(function (t) {
                    var a = t.querySelector('audio');
                    return a && isFinite(a.duration) ? a.duration : 0;
                  });

                  if (!tracks.length) { return; }

                  // Nothing known is still six tracks. The first version returned here when
                  // no duration had been reported, so a running order of six tracks pointing
                  // at files on this machine — which is what importing a music folder gives
                  // you — showed no stack at all. An empty case is not a case: the lanes are
                  // drawn as labelled blanks instead, which is what the board does with a
                  // panel that has no number.
                  var whole = known.reduce(function (a, b) { return a + b; }, 0);

                  stack.innerHTML = '';

                  tracks.forEach(function (t, i) {
                    // A placeholder track carries its name as its own text rather than in a
                    // caption, so a lane read "Track 3" for something called "Sinnerman" —
                    // which is the one thing a running order must not do.
                    var cap = t.querySelector('figcaption, .nm, .snd.ph');
                    var lane = document.createElement('div');
                    lane.className = 'lane';

                    var run = document.createElement('div');
                    run.className = 'run';
                    run.textContent = cap ? cap.textContent : ('Track ' + (i + 1));

                    if (known[i] && whole > 0) {
                      run.style.width = ((known[i] / whole) * 100) + '%';
                    } else {
                      // No length reported, so no width can be honest. Full-bleed and marked,
                      // rather than a bar sized from a number nobody has.
                      run.classList.add('no');
                      run.style.width = '100%';
                      run.textContent += ' — length unknown';
                    }

                    lane.appendChild(run);
                    stack.appendChild(lane);
                  });
                }

                [].forEach.call(document.querySelectorAll('audio'), function (a) {
                  a.addEventListener('loadedmetadata', lanes);
                });
                lanes();

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

        /* A waveform, when there is one to draw. Empty until the samples are read, and
           marked when they cannot be — never a plausible shape standing in for sound. */
        .wave { display: block; width: 100%; height: 48px; color: var(--accent); opacity: .75; }
        .wave.no { display: none; }

        /* The running order as lanes, each at its real length. */
        .stack { display: flex; flex-direction: column; gap: 3px; margin-top: .9rem; }
        .lane { height: 1.15rem; background: var(--raised); border-radius: 3px; overflow: hidden; }
        .run {
          height: 100%; display: flex; align-items: center;
          padding: 0 .4rem; font-size: .6rem; white-space: nowrap; overflow: hidden;
          background: color-mix(in srgb, var(--accent) 30%, var(--raised));
        }
        .run.no { background: none; opacity: .45; font-style: italic; }
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
