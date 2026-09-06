using System.Text;

namespace Concierge.Shared.Design;

/// <summary>
/// Space you can look around.
///
/// A room seen in three dimensions, built from solids on a floor, turned with the
/// arrow keys. CSS transforms rather than a 3D engine — and the trade is the point
/// rather than a compromise: no dependency, no download, nothing to install, works
/// offline on every head Concierge ships on, and it draws a room with walls and
/// furniture perfectly well.
///
/// What it will not do, said plainly rather than discovered: meshes, lights,
/// shadows, textures, or anything imported. A real engine is a real dependency and
/// a real download, and that is a decision to make deliberately rather than by
/// quietly adding a script tag. Pascal's editor is Three.js and WebGPU and is
/// right to be; this is the version that a ninety-seven-year-old can open without
/// installing anything.
///
/// No orbit-by-dragging either. Dragging to rotate a scene is the gesture nobody
/// discovers and everybody fights — four arrow keys are learnable in a second and
/// cannot be done by accident.
/// </summary>
public static class SceneRenderer
{
    public static string ToHtml(DesignDocument document, string? selectedId = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var html = new StringBuilder();
        DesignMediums.OpenDocument(html, DesignLooks.Of(document.Look), Style);

        var rooms = DesignMediums.FramesOf(document);

        if (rooms.Count == 0)
        {
            html.AppendLine("<p class=\"nothing pad\">No rooms yet. Say: add a room.</p>");
            html.AppendLine("</body></html>");
            return html.ToString();
        }

        var showing = rooms[0];

        foreach (var room in rooms)
        {
            if (room.Id == selectedId || document.ChildrenOf(room.Id).Any(c => c.Id == selectedId))
            {
                showing = room;
                break;
            }
        }

        html.AppendLine("<div class=\"stage\"><div class=\"world\" id=\"world\">");
        html.AppendLine("<div class=\"floor\"></div>");

        foreach (var thing in document.ChildrenOf(showing.Id))
        {
            var picked = string.Equals(thing.Id, selectedId, StringComparison.Ordinal) ? " picked" : string.Empty;
            var attr = $" data-node=\"{DesignMediums.Escape(thing.Id)}\"";

            if (thing.Kind == DesignNodeKind.Solid)
            {
                DesignMediums.WriteSolid(html, thing, picked, attr);
                continue;
            }

            // Anything that is not a solid lies on the floor as a label, so words
            // and pictures can be in a room without needing a second concept.
            if (thing.Kind is DesignNodeKind.Heading or DesignNodeKind.Text or DesignNodeKind.Image)
            {
                var x = DesignMediums.Number(thing, "x", 0);
                var y = DesignMediums.Number(thing, "y", 0);

                // Flat on the floor, like writing on a plan. Standing them up was
                // the obvious thing and the wrong one: the room is seen at a steep
                // tilt, so a vertical plane is nearly edge-on and rendered 112x0 —
                // present in the DOM, invisible on screen. Words lying on the
                // floor read from every angle and need no billboard maths.
                html.AppendLine($"<div class=\"sign{picked}\" style=\"transform:translate3d({x}px,{y}px,1px)\"{attr}>");
                html.AppendLine(DesignMediums.Escape(thing.Text.Length > 0 ? thing.Text : "A sign"));
                html.AppendLine("</div>");
            }
        }

        html.AppendLine("</div></div>");
        html.AppendLine($"<div class=\"hint\">{DesignMediums.Escape(showing.Text.Length > 0 ? showing.Text : "A room")} · arrow keys to look around</div>");

        html.AppendLine("""
            <script>
              (function () {
                var world = document.getElementById('world');
                if (!world) { return; }
                var turn = -24, tilt = 62;
                function draw() {
                  world.style.transform =
                    'rotateX(' + tilt + 'deg) rotateZ(' + turn + 'deg) scale(' + fit() + ')';
                }

                // A room has to fit the view it is in, and the view is whatever
                // size the canvas happens to be — 321px tall on a laptop with the
                // strips showing. Measured each draw rather than assumed once,
                // because the canvas resizes with the window.
                function fit() {
                  var room = 440 * Math.abs(Math.sin(tilt * Math.PI / 180)) + 160;
                  var have = document.documentElement.clientHeight;
                  return Math.min(1, Math.max(0.35, have / room));
                }
                document.addEventListener('keydown', function (e) {
                  if (e.key === 'ArrowLeft') { turn -= 6; }
                  else if (e.key === 'ArrowRight') { turn += 6; }
                  else if (e.key === 'ArrowUp') { tilt = Math.min(88, tilt + 4); }
                  else if (e.key === 'ArrowDown') { tilt = Math.max(8, tilt - 4); }
                  else { return; }
                  e.preventDefault();
                  draw();
                });
                draw();
              })();
            </script>
            """);

        html.AppendLine("</body></html>");
        return html.ToString();
    }

    private const string Style = """
        .stage { position: absolute; inset: 0; display: grid; place-items: center; perspective: 1100px; overflow: hidden; }

        /* Scaled to fit. At a 62-degree tilt a 600px floor is taller than the
           canvas it sits in, so a room ran off the top and the bottom of its own
           view — measured, not guessed: floor 600 tall in a 321 viewport. */
        .world { transform-style: preserve-3d; transition: transform .18s ease-out; }
        .floor {
          position: absolute; left: -220px; top: -220px;
          width: 440px; height: 440px;
          background:
            repeating-linear-gradient(0deg, var(--raised) 0 1px, transparent 1px 60px),
            repeating-linear-gradient(90deg, var(--raised) 0 1px, transparent 1px 60px);
          opacity: .8;
        }
        .solid { position: absolute; transform-style: preserve-3d; width: var(--w); height: var(--d); }
        .face { position: absolute; background: var(--accent); opacity: .85; }
        .top { inset: 0; transform: translateZ(var(--h)); }
        .front { left: 0; top: 100%; width: var(--w); height: var(--h); transform-origin: top; transform: rotateX(90deg); opacity: .6; }
        .side { left: 100%; top: 0; width: var(--h); height: var(--d); transform-origin: left; transform: rotateY(-90deg); opacity: .45; }
        .label { position: absolute; left: 0; top: 100%; transform: translateZ(var(--h)); font-size: .7rem; opacity: .8; white-space: nowrap; }
        .sign {
          position: absolute; padding: .3rem .55rem;
          background: var(--raised); border-radius: var(--radius);
          font-size: .8rem; white-space: nowrap;
          transform-origin: center;
        }
        .hint { position: absolute; left: 0; right: 0; bottom: .75rem; text-align: center; font-size: .78rem; opacity: .5; }
        .pad { padding: 2rem; }
        """;
}
