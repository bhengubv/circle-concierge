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
            var length = Math.Clamp(DesignMediums.Number(shots[i], "seconds", DefaultSeconds), 1, 120);
            seconds.Add(length);

            var picked = string.Equals(shots[i].Id, selectedId, StringComparison.Ordinal) ? " picked" : string.Empty;

            html.AppendLine(
                $"<div class=\"shot{(i == 0 ? " on" : string.Empty)}{picked}\" data-node=\"{DesignMediums.Escape(shots[i].Id)}\" data-seconds=\"{length}\">");

            if (shots[i].Text.Length > 0)
            {
                html.AppendLine($"<h1 class=\"h\">{DesignMediums.Escape(shots[i].Text)}</h1>");
            }

            DesignMediums.WriteContents(html, document, shots[i], selectedId);
            html.AppendLine("</div>");
        }

        // A progress bar rather than a scrubber. It says where you are and cannot
        // be dragged, which is the whole difference.
        html.AppendLine($"<div class=\"bar\"><span></span></div>");
        html.AppendLine($"<div class=\"len\">{seconds.Sum()} seconds</div>");

        html.AppendLine("""
            <script>
              (function () {
                var shots = [].slice.call(document.querySelectorAll('.shot'));
                var fill = document.querySelector('.bar span');
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

                function tick() {
                  var length = (parseInt(shots[at].dataset.seconds, 10) || 4) * 1000;
                  var gone = Date.now() - started;
                  if (fill) { fill.style.width = Math.min(100, (gone / length) * 100) + '%'; }
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
        .h { font-size: clamp(1.6rem, 5.5vw, 3rem); margin: 0; line-height: 1.12; }
        .t { font-size: clamp(1rem, 2.5vw, 1.25rem); margin: 0; }
        .im, .ph { max-width: 100%; max-height: 50vh; object-fit: contain; border-radius: var(--radius); }
        .ph { background: var(--raised); display: grid; place-items: center; min-height: 8rem; opacity: .75; }
        .snd { margin: 0; }
        .snd figcaption { font-size: .8rem; opacity: .65; }

        /* Where you are, not something to drag. */
        .bar { position: absolute; left: 0; right: 0; bottom: 0; height: 3px; background: var(--raised); }
        .bar span { display: block; height: 100%; width: 0; background: var(--accent); }
        .len { position: absolute; right: 1rem; bottom: .75rem; font-size: .75rem; opacity: .45; }
        """;
}
