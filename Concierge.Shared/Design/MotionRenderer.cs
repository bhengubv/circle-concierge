using System.Text;

namespace Concierge.Shared.Design;

/// <summary>
/// Shots that play.
///
/// Each frame is a shot with a duration; they run in order and it loops. Sound on
/// a shot plays with it, which is why Sound was built before this rather than
/// after — a shot with no audio is not a shot, and bolting it on later would have
/// meant building this twice.
///
/// **No timeline.** That is the decision this renderer turns on. A timeline is the
/// single most excluding thing in video software: scrubbing, tracks, keyframes,
/// snapping, a playhead. What replaces it is what a person actually says — "this
/// bit is too long" — with the shots shown as pictures in order, the same strip
/// the history uses. Something that cannot be dragged cannot be dragged wrong.
///
/// What this is not: an encoder. It plays; it does not produce an MP4. Encoding
/// needs FFmpeg, which is a real dependency and a real download, and pretending
/// otherwise by calling this "video" without saying so would be the kind of claim
/// this product exists not to make.
/// </summary>
public static class MotionRenderer
{
    /// <summary>How long a shot lasts when nobody said.</summary>
    public const int DefaultSeconds = 4;

    public static string ToHtml(DesignDocument document, string? selectedId = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var html = new StringBuilder();
        DesignMediums.OpenDocument(html, DesignLooks.Of(document.Look), Style);

        var shots = DesignMediums.FramesOf(document);

        if (shots.Count == 0)
        {
            html.AppendLine("<div class=\"shot on\"><p class=\"nothing\">No shots yet. Say: add a shot.</p></div>");
            html.AppendLine("</body></html>");
            return html.ToString();
        }

        var seconds = new List<int>();

        for (var i = 0; i < shots.Count; i++)
        {
            // Timing rather than a single number: a shot can now wait before it
            // starts and play at a speed, which is what makes "start this a beat
            // after the last one" sayable at all. A shot carrying only `seconds`
            // reads exactly as it did.
            var timing = DesignTiming.Of(shots[i], DefaultSeconds);
            var length = timing.Seconds;
            seconds.Add(timing.TotalSeconds);

            var picked = string.Equals(shots[i].Id, selectedId, StringComparison.Ordinal) ? " picked" : string.Empty;

            // How it moves while it is up. A still picture held for four seconds looks
            // like a fault, and the preview has to show that it will not be one — a
            // movement that only appears in the exported file is a movement nobody can
            // judge until it is too late to change it.
            var moving = Moving(shots[i]);

            html.AppendLine(
                $"<div class=\"shot{(i == 0 ? " on" : string.Empty)}{picked}{moving}\" data-node=\"{DesignMediums.Escape(shots[i].Id)}\" data-seconds=\"{length}\" data-delay=\"{timing.DelaySeconds}\" data-rate=\"{timing.Rate.ToString(System.Globalization.CultureInfo.InvariantCulture)}\" style=\"--held:{length}s\">");

            if (shots[i].Text.Length > 0)
            {
                html.AppendLine($"<h1 class=\"h\">{DesignMediums.Escape(shots[i].Text)}</h1>");
            }

            DesignMediums.WriteContents(html, document, shots[i], selectedId);
            html.AppendLine("</div>");
        }

        // A timeline, because somebody making a four-minute film needs to see where they are
        // in it. Clips laid out at their real proportions, the playhead crossing the whole
        // film, each one named and measured.
        //
        // **Read-only, and that is the design rather than a shortfall.** Nothing here can be
        // dragged: the verb on this surface is speech, and "make the shot four seconds" is
        // how a length changes. What a timeline is for is *looking* — you cannot drag a clip
        // while driving, and you can say that sentence.
        //
        // Clicking one points at it, which is not dragging. Pointing supplies the subject the
        // way it does everywhere else here, so "that one — make it warmer" works.
        html.AppendLine("<div class=\"tl\">");

        for (var i = 0; i < shots.Count; i++)
        {
            var length = Math.Max(1, seconds[i]);
            var named = shots[i].Text.Length > 0 ? shots[i].Text : $"Shot {i + 1}";
            var picked = string.Equals(shots[i].Id, selectedId, StringComparison.Ordinal) ? " picked" : string.Empty;

            html.AppendLine(
                $"<div class=\"clip{picked}\" data-node=\"{DesignMediums.Escape(shots[i].Id)}\" "
                + $"style=\"flex:{length} 0 0%\">"
                + $"<span class=\"clip-name\">{DesignMediums.Escape(named)}</span>"
                + $"<span class=\"clip-len\">{length}s</span>"
                + "</div>");
        }

        html.AppendLine("<div class=\"head\"></div>");
        html.AppendLine("</div>");

        html.AppendLine($"<div class=\"len\">{seconds.Sum()} seconds</div>");

        html.AppendLine("""
            <script>
              (function () {
                var shots = [].slice.call(document.querySelectorAll('.shot'));
                if (!shots.length) { return; }
                var at = 0, started = Date.now();

                function play(i) {
                  at = i % shots.length;
                  started = Date.now();
                  shots.forEach(function (s, n) { s.classList.toggle('on', n === at); });
                  // Sound belongs to its shot: anything left playing from the last
                  // one would carry over and be heard under the wrong picture.
                  shots.forEach(function (s) {
                    [].forEach.call(s.querySelectorAll('audio'), function (a) {
                      a.pause(); a.currentTime = 0;
                    });
                  });
                  var here = shots[at].querySelector('audio');
                  if (here) { here.play().catch(function () { }); }
                }

                // Where each shot starts inside the whole film, so the playhead crosses the
                // timeline rather than restarting inside every clip.
                var lengths = shots.map(function (s) {
                  return Math.max(1, parseInt(s.dataset.seconds, 10) || 4);
                });
                var whole = lengths.reduce(function (a, b) { return a + b; }, 0) || 1;
                var starts = [];
                lengths.reduce(function (a, b, i) { starts[i] = a; return a + b; }, 0);

                var clips = [].slice.call(document.querySelectorAll('.clip'));
                var head = document.querySelector('.head');

                function tick() {
                  var length = lengths[at] * 1000;
                  var gone = Date.now() - started;

                  if (head) {
                    var across = (starts[at] + Math.min(lengths[at], gone / 1000)) / whole;
                    head.style.left = (across * 100) + '%';
                  }

                  clips.forEach(function (c, n) { c.classList.toggle('on', n === at); });

                  if (gone >= length) { play(at + 1); }
                  requestAnimationFrame(tick);
                }

                play(0);
                requestAnimationFrame(tick);
              })();
            </script>
            """);

        html.AppendLine("</body></html>");
        return html.ToString();
    }

    /// <summary>
    /// The class for how this shot moves, matching what the export will do.
    ///
    /// Two places knowing the same words is how they drift, so the words themselves live in
    /// `MediaExport.Moves` and a test holds the two together.
    /// </summary>
    private static string Moving(DesignNode shot)
        => shot.Props.TryGetValue("move", out var said) && !string.IsNullOrWhiteSpace(said)
            ? said.Trim().ToLowerInvariant() switch
            {
                "fade" or "fade in" or "in" => " moves fade",
                "grow" or "push" or "zoom" or "closer" => " moves grow",
                "drift" or "pan" or "across" => " moves drift",
                _ => string.Empty,
            }
            : string.Empty;

    private const string Style = """
        .shot {
          display: none;
          position: absolute; inset: 0;
          padding: clamp(1.5rem, 5vw, 3.5rem);
          flex-direction: column; justify-content: center; gap: .9rem;
          animation: in .45s ease both;
        }
        .shot.on { display: flex; }
        @keyframes in { from { opacity: 0; transform: scale(1.02); } to { opacity: 1; transform: none; } }
        /* How a shot moves while it is up, matching what the export does to it. The
           length is the shot's own, so what is on screen is what will be in the file. */
        .shot.moves.fade { animation: fadein .5s ease both; }
        .shot.moves.grow { animation: in .45s ease both, grow var(--held, 3s) linear both; }
        .shot.moves.drift { animation: in .45s ease both, drift var(--held, 3s) linear both; }

        @keyframes fadein { from { opacity: 0; } to { opacity: 1; } }
        @keyframes grow { from { transform: scale(1); } to { transform: scale(1.10); } }
        @keyframes drift { from { transform: scale(1.10) translateX(2.5%); }
                             to { transform: scale(1.10) translateX(-2.5%); } }

        .h { font-size: clamp(1.6rem, 5.5vw, 3rem); margin: 0; line-height: 1.12; }
        .t { font-size: clamp(1rem, 2.5vw, 1.25rem); margin: 0; }
        .im, .ph { max-width: 100%; max-height: 50vh; object-fit: contain; border-radius: var(--radius); }
        .ph { background: var(--raised); display: grid; place-items: center; min-height: 8rem; opacity: .75; }
        .snd { margin: 0; }
        .snd figcaption { font-size: .8rem; opacity: .65; }

        /* Where you are, not something to drag. */
        /* The timeline. Clips at their real proportions, so four seconds looks like twice
           two — a row of equal boxes would say nothing about the shape of the film. */
        /* Along the bottom, where a timeline goes.
         *
         * The first version left it in normal flow, which put it at the top of the document
         * — under the canvas's own caption overlay, half hidden, above the picture it
         * describes. Shots are absolutely positioned, so "after the shots" is not "below
         * them". Seen on the running app. */
        .tl {
          /* Clear of the canvas's own resting controls, which float over this document at
             its bottom-left. They are two pills at rest; when they open they cover the
             timeline, which is fair — at that moment you are choosing a medium, not watching
             a film. */
          position: absolute; left: 1rem; right: 1rem; bottom: 3.7rem;
          display: flex; gap: 2px;
          height: 2.4rem;
          border-radius: 4px; overflow: hidden;
          background: var(--raised);
        }
        .clip {
          position: relative; overflow: hidden;
          display: flex; flex-direction: column; justify-content: center;
          padding: 0 .4rem; min-width: 0;
          background: color-mix(in srgb, var(--accent) 18%, var(--raised));
          cursor: pointer;
        }
        .clip.on { background: color-mix(in srgb, var(--accent) 42%, var(--raised)); }
        .clip.picked { outline: 2px solid var(--accent); outline-offset: -2px; }
        .clip-name {
          font-size: .62rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
        }
        .clip-len { font-size: .55rem; opacity: .65; }

        /* Where you are in the whole film, not inside one clip. */
        .head {
          position: absolute; top: 0; bottom: 0; width: 2px; left: 0;
          background: var(--ink); pointer-events: none;
        }
        .len { position: absolute; right: 1rem; bottom: .55rem; font-size: .75rem; opacity: .45; }
        """;
}
