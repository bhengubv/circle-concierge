using System.Text;
using System.Text.Json;

namespace Concierge.Shared.Design;

/// <summary>
/// Space you can look around.
///
/// A room seen in three dimensions, built from solids on a floor, turned with the
/// arrow keys.
///
/// **Drawn twice, and that is the design rather than a hedge.** The room is
/// written out first as the CSS-transform version it has always been, and then a
/// real engine is asked to draw the same room over the top. If the engine loads
/// and the machine has WebGL, the flat one is hidden and you get meshes, lights
/// and shadows. If anything at all goes wrong — no WebGL, no GPU, the script
/// missing, a webview that will not run modules — nothing is caught out: the flat
/// room is already on screen and simply stays there.
///
/// That ordering matters. The obvious build is to draw the engine's version and
/// fall back on failure, which means the failure path is the one nobody ever
/// exercises and the one that greets somebody on an old laptop with an empty
/// rectangle. Here the fallback is the default and the upgrade is what has to
/// succeed, so the worst case is the room this already drew perfectly well.
///
/// The engine is three.js, MIT, carried in the repository rather than fetched
/// from a CDN. A design surface that needs the internet to draw a box is not a
/// local-first product, and Concierge's whole claim is that it works on the
/// device.
///
/// No orbit-by-dragging, with or without an engine. Dragging to rotate a scene is
/// the gesture nobody discovers and everybody fights — four arrow keys are
/// learnable in a second and cannot be done by accident. Having a real engine
/// makes that gesture trivially available, which is exactly why it is worth
/// saying no to it again here.
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

        // The same room again, as numbers, for whatever can draw it better. Read
        // from a script tag rather than off the DOM: reading the flat version back
        // would make the engine's room a copy of a drawing instead of a second
        // drawing of the same thing, and the two would drift the first time either
        // changed.
        html.AppendLine("<canvas id=\"deep\" hidden></canvas>");
        html.AppendLine($"<script type=\"application/json\" id=\"room\">{RoomJson(document, showing, selectedId)}</script>");

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
                  // The engine took over, so this room is off screen and turning
                  // it would only fight the one somebody is looking at.
                  if (window.__deep) { return; }
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

        html.AppendLine("""
            <script>
              // The same room, drawn by a real engine when this machine has one.
              //
              // Everything here is an upgrade over something already on screen, so
              // every failure path is the same line: stop, and leave the flat room
              // alone. There is no error state to design because there is nothing
              // to recover from.
              (function () {
                var canvas = document.getElementById('deep');
                var flat = document.querySelector('.stage');
                var source = document.getElementById('room');
                if (!canvas || !flat || !source) { return; }

                // Said back to the app, so it can answer "why does my room look
                // flat?" without anybody having to look at the screen and guess.
                // Queued as well as called, because the frame's bridge is attached
                // on load and this can decide either side of that.
                function report(drew, why) {
                  window.__deepReport = { drew: drew, why: why };
                  if (window.__conciergeDrew) { window.__conciergeDrew(drew, why); }
                }

                var room;
                try {
                  room = JSON.parse(source.textContent);
                } catch (e) {
                  report(false, 'The room could not be read.');
                  return;
                }

                // Absolute rather than relative: this document is a srcdoc frame
                // and has no URL of its own for a relative path to hang off.
                import('/_content/Concierge.Shared.Components/lib/three/three.module.js').then(
                  function (THREE) {
                    try {
                      build(THREE);
                    } catch (e) {
                      // The flat room is still on screen, so nothing is broken for
                      // whoever is looking — but this is our own code failing and
                      // it must not look like a machine without a GPU. A single
                      // `.catch` on the chain hid exactly this once already: the
                      // room went over with capitalised names, the script read
                      // lowercase ones, and undefined is not an error in
                      // JavaScript until you ask it for a length.
                      console.error('Concierge: the engine loaded and the room did not draw.', e);
                      report(false, 'The engine loaded and the room did not draw. This is a fault, not a limit.');
                    }
                  },
                  function () {
                    // No engine on this machine. The flat room stays, and this is
                    // the ordinary case rather than a fault.
                    report(false, 'The 3D engine is not available on this machine.');
                  });

                function build(THREE) {
                  var renderer;

                  try {
                    renderer = new THREE.WebGLRenderer({ canvas: canvas, antialias: true });
                  } catch (e) {
                    // No WebGL. Nothing on screen has changed and nothing needs to.
                    report(false, 'This machine has no working WebGL.');
                    return;
                  }

                  renderer.setPixelRatio(Math.min(2, window.devicePixelRatio || 1));
                  renderer.shadowMap.enabled = true;
                  renderer.shadowMap.type = THREE.PCFSoftShadowMap;

                  var scene = new THREE.Scene();
                  scene.background = new THREE.Color(room.ground);

                  var camera = new THREE.PerspectiveCamera(42, 1, 1, 4000);

                  // Two lights, and the second is much of the point of having an
                  // engine at all: one that casts and one that fills. A single hard
                  // light makes every unlit face pure black, which reads as a hole
                  // rather than as a shadow.
                  var sun = new THREE.DirectionalLight(0xffffff, 2.2);
                  sun.position.set(260, 480, 200);
                  sun.castShadow = true;
                  sun.shadow.mapSize.set(2048, 2048);
                  sun.shadow.camera.left = -340;
                  sun.shadow.camera.right = 340;
                  sun.shadow.camera.top = 340;
                  sun.shadow.camera.bottom = -340;
                  sun.shadow.camera.near = 1;
                  sun.shadow.camera.far = 1400;
                  sun.shadow.bias = -0.0006;
                  scene.add(sun);
                  scene.add(new THREE.HemisphereLight(0xffffff, new THREE.Color(room.raised), 1.1));

                  var floor = new THREE.Mesh(
                    new THREE.PlaneGeometry(440, 440),
                    new THREE.MeshStandardMaterial({ color: new THREE.Color(room.raised), roughness: 1 }));
                  floor.rotation.x = -Math.PI / 2;
                  floor.receiveShadow = true;
                  scene.add(floor);

                  // The same 60px squares the flat room draws, so moving between
                  // the two is one room and not two.
                  var grid = new THREE.GridHelper(
                    440, 440 / 60, new THREE.Color(room.ink), new THREE.Color(room.ink));
                  grid.material.opacity = 0.16;
                  grid.material.transparent = true;
                  grid.position.y = 0.6;
                  scene.add(grid);

                  var pickable = [];

                  for (var i = 0; i < room.things.length; i++) {
                    var thing = room.things[i];
                    var mesh = thing.kind === 'solid' ? solidOf(thing) : signOf(thing);
                    if (!mesh) { continue; }
                    mesh.userData.id = thing.id;
                    scene.add(mesh);
                    pickable.push(mesh);
                  }

                  function solidOf(thing) {
                    var w = Math.max(1, thing.width);
                    var d = Math.max(1, thing.depth);
                    var h = Math.max(1, thing.height);
                    var across = Math.min(w, d) / 2;
                    var geometry;
                    var lift = h / 2;
                    var r;

                    switch (thing.shape) {
                      case 'sphere': case 'ball': case 'round':
                        r = Math.min(w, d, h) / 2;
                        geometry = new THREE.SphereGeometry(r, 40, 28);
                        lift = r;
                        break;
                      case 'cylinder': case 'tube': case 'post': case 'column':
                        geometry = new THREE.CylinderGeometry(across, across, h, 40);
                        break;
                      case 'cone':
                        geometry = new THREE.ConeGeometry(across, h, 40);
                        break;
                      default:
                        geometry = new THREE.BoxGeometry(w, h, d);
                    }

                    var picked = thing.id === room.picked;

                    var mesh = new THREE.Mesh(geometry, new THREE.MeshStandardMaterial({
                      color: new THREE.Color(room.accent),
                      roughness: 0.55,
                      metalness: 0.05,
                      emissive: new THREE.Color(picked ? room.accent : 0x000000),
                      emissiveIntensity: picked ? 0.55 : 0
                    }));

                    mesh.castShadow = true;
                    mesh.receiveShadow = true;
                    mesh.position.set(thing.x, lift, thing.y);
                    return mesh;
                  }

                  // Words lie on the floor like writing on a plan, exactly as they
                  // do in the flat room. Standing them up was tried there and is
                  // wrong here for the same reason: the room is looked at from
                  // above, so an upright label is nearly edge-on.
                  function signOf(thing) {
                    var words = thing.text && thing.text.length ? thing.text : 'A sign';
                    var pad = 28;
                    var face = '48px sans-serif';

                    var plate = document.createElement('canvas');
                    var pen = plate.getContext('2d');
                    if (!pen) { return null; }

                    pen.font = face;
                    plate.width = Math.ceil(pen.measureText(words).width) + pad * 2;
                    plate.height = 96;

                    // Sizing a canvas clears it and resets the pen, so the font is
                    // set again rather than once.
                    pen = plate.getContext('2d');
                    pen.font = face;
                    pen.fillStyle = room.raised;
                    pen.fillRect(0, 0, plate.width, plate.height);
                    pen.fillStyle = thing.id === room.picked ? room.accent : room.ink;
                    pen.textBaseline = 'middle';
                    pen.fillText(words, pad, plate.height / 2);

                    var texture = new THREE.CanvasTexture(plate);
                    texture.colorSpace = THREE.SRGBColorSpace;
                    texture.anisotropy = renderer.capabilities.getMaxAnisotropy();

                    var mesh = new THREE.Mesh(
                      new THREE.PlaneGeometry(plate.width / 2.6, plate.height / 2.6),
                      new THREE.MeshBasicMaterial({ map: texture, transparent: true }));

                    mesh.rotation.x = -Math.PI / 2;
                    mesh.position.set(thing.x, 1.2, thing.y);
                    return mesh;
                  }

                  var turn = -24 * Math.PI / 180;
                  var tilt = 52 * Math.PI / 180;
                  var away = 760;

                  function place() {
                    var flatness = Math.cos(tilt) * away;
                    camera.position.set(
                      Math.sin(turn) * flatness, Math.sin(tilt) * away, Math.cos(turn) * flatness);
                    camera.lookAt(0, 30, 0);
                  }

                  function size() {
                    var wide = canvas.clientWidth || document.documentElement.clientWidth;
                    var tall = canvas.clientHeight || document.documentElement.clientHeight;
                    if (!wide || !tall) { return; }

                    renderer.setSize(wide, tall, false);
                    camera.aspect = wide / tall;

                    // A narrow canvas cuts the room off at the sides, so it is
                    // backed away from rather than cropped. Measured every time,
                    // the way the flat room measures its scale and for the same
                    // reason: this canvas is whatever size the window leaves it.
                    away = 760 * Math.max(1, 1.15 / Math.max(0.35, camera.aspect));
                    camera.updateProjectionMatrix();
                    place();
                  }

                  document.addEventListener('keydown', function (e) {
                    if (e.key === 'ArrowLeft') { turn -= 6 * Math.PI / 180; }
                    else if (e.key === 'ArrowRight') { turn += 6 * Math.PI / 180; }
                    else if (e.key === 'ArrowUp') { tilt = Math.min(1.53, tilt + 0.07); }
                    else if (e.key === 'ArrowDown') { tilt = Math.max(0.12, tilt - 0.07); }
                    else { return; }
                    e.preventDefault();
                    place();
                    draw();
                  });

                  // The whole scene is one element, so there is nothing for the
                  // existing click handler to walk up from. Rather than teach it
                  // about meshes, what was hit is written onto the canvas as the
                  // same data-node attribute every other thing on every other
                  // surface carries — during capture, so it is already there by
                  // the time the handler on the body reads it. One contract, five
                  // surfaces.
                  var ray = new THREE.Raycaster();
                  var pointer = new THREE.Vector2();

                  canvas.addEventListener('click', function (e) {
                    var box = canvas.getBoundingClientRect();
                    pointer.x = ((e.clientX - box.left) / box.width) * 2 - 1;
                    pointer.y = -((e.clientY - box.top) / box.height) * 2 + 1;
                    ray.setFromCamera(pointer, camera);

                    var hit = ray.intersectObjects(pickable, false)[0];

                    if (hit && hit.object.userData.id) {
                      canvas.setAttribute('data-node', hit.object.userData.id);
                    } else {
                      // Clicking the floor means "nothing", not "the room".
                      canvas.removeAttribute('data-node');
                    }
                  }, true);

                  function draw() { renderer.render(scene, camera); }

                  window.addEventListener('resize', function () { size(); draw(); });

                  // Last, and only now: the flat room has been on screen the whole
                  // time this was being built, so a throw anywhere above leaves
                  // somebody looking at a room rather than at nothing.
                  flat.hidden = true;
                  canvas.hidden = false;
                  window.__deep = true;

                  size();
                  draw();
                  report(true, '');
                }
              })();
            </script>
            """);

        html.AppendLine("</body></html>");
        return html.ToString();
    }

    /// <summary>
    /// The room as numbers: what is in it, where, how big, and the colours it
    /// wears.
    ///
    /// Written into the page rather than fetched, because the canvas is a srcdoc
    /// frame with no URL of its own to fetch anything relative to. Serialised
    /// properly rather than concatenated: a room's things carry somebody's words,
    /// and a title containing a quote must not be able to end the script tag it is
    /// sitting inside.
    /// </summary>
    private static string RoomJson(DesignDocument document, DesignNode room, string? selectedId)
    {
        var look = DesignLooks.Of(document.Look);

        var things = document.ChildrenOf(room.Id)
            .Where(thing => thing.Kind is DesignNodeKind.Solid
                or DesignNodeKind.Heading or DesignNodeKind.Text or DesignNodeKind.Image)
            .Select(thing => new Thing(
                thing.Id,
                thing.Kind == DesignNodeKind.Solid ? "solid" : "sign",
                thing.Text,
                thing.Props.TryGetValue("shape", out var shape) ? shape.Trim().ToLowerInvariant() : "box",
                DesignMediums.Number(thing, "x", 0),
                DesignMediums.Number(thing, "y", 0),
                DesignMediums.Number(thing, "width", 120),
                DesignMediums.Number(thing, "depth", 120),
                DesignMediums.Number(thing, "height", 80)))
            .ToList();

        // Named the way the script that reads them names them. Left to the
        // default, this wrote `Things` and the script read `things`, which is
        // undefined in JavaScript rather than an error — so the room was built
        // from nothing and the failure was swallowed by the catch meant for a
        // missing engine. Two silent things agreeing to be silent.
        var json = JsonSerializer.Serialize(
            new Room(look.Ground, look.Ink, look.Raised, look.Accent, selectedId ?? string.Empty, things),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        // The one character that could close the tag this sits in. Escaped as a
        // unicode point, which JSON reads back as the same character, so the words
        // are unchanged and the page cannot be broken by them.
        return json.Replace("<", "\u003c", StringComparison.Ordinal);
    }

    private sealed record Room(
        string Ground, string Ink, string Raised, string Accent, string Picked,
        IReadOnlyList<Thing> Things);

    private sealed record Thing(
        string Id, string Kind, string Text, string Shape,
        int X, int Y, int Width, int Depth, int Height);

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
        /* The engine's room, once there is one. Hidden until it has actually
           drawn, so a machine that cannot run it never sees an empty rectangle
           where a room used to be. */
        #deep { position: absolute; inset: 0; width: 100%; height: 100%; display: block; cursor: pointer; }
        #deep[hidden] { display: none; }
        .stage[hidden] { display: none; }

        .hint { position: absolute; left: 0; right: 0; bottom: .75rem; text-align: center; font-size: .78rem; opacity: .5; }
        .pad { padding: 2rem; }
        """;
}
