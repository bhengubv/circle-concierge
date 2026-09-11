// Pointing at things on the canvas.
//
// The design renders inside an iframe so a look can bring its own background,
// fonts and spacing without any of it leaking into Concierge's stylesheet — or
// Concierge's leaking into the design, which would make what you see here not
// what you get anywhere else.
//
// The cost of that isolation is that clicks land in there, not out here. This
// reaches in and attaches one listener, which is possible only because the frame
// is srcdoc and therefore same-origin. A canvas loaded from a URL could not do
// this, which is the reason it is not.
//
// Exposed globals (window.conciergeDesign):
//   watch(frame, dotnet) — tell .NET which element was touched, by node id, and
//                           whether the room was drawn by the 3D engine.

(function () {
    'use strict';

    // Attribute written by DesignRenderer on every element it emits. Kept in one
    // place because it is the whole contract between the renderer and pointing:
    // if these two ever disagree, clicking silently selects nothing.
    var NODE = 'data-node';

    function watch(frame, dotnet) {
        if (!frame || !dotnet) {
            return;
        }

        // The frame's document is replaced whenever the design changes, taking
        // the listener with it, so this re-attaches on every load rather than
        // once. Attaching once is the bug where pointing works until the first
        // edit and then quietly stops.
        var attach = function () {
            var doc = frame.contentDocument;
            if (!doc || !doc.body) {
                return;
            }

            doc.body.addEventListener('click', function (event) {
                // The nearest thing with an id, walking up from whatever was
                // actually hit. A click on a word inside a heading means the
                // heading — nobody thinks they are pointing at a text node.
                var el = event.target;

                while (el && el !== doc.documentElement) {
                    if (el.getAttribute && el.getAttribute(NODE)) {
                        dotnet.invokeMethodAsync('Touched', el.getAttribute(NODE));
                        return;
                    }

                    el = el.parentElement;
                }

                // Clicking the empty space around the design means "nothing", not
                // "the page" — letting go is a thing people do on purpose.
                dotnet.invokeMethodAsync('Touched', null);
            });

            // The pointer says the thing is touchable. Without it a canvas looks
            // like a picture, and nobody clicks a picture.
            doc.body.style.cursor = 'pointer';

            // How the room got drawn. A room that falls back to the flat drawing
            // still works perfectly, which is the whole point of drawing it that
            // way — and is also why nobody would ever find out. This carries the
            // answer back so Engineering can say it.
            //
            // Both directions, because the frame decides asynchronously and can
            // land either side of this listener being attached: the bridge is
            // installed for anything still to come, and anything already decided
            // is drained now.
            var win = frame.contentWindow;
            if (win) {
                win.__conciergeDrew = function (drew, why) {
                    dotnet.invokeMethodAsync('Drew', !!drew, why || '');
                };

                if (win.__deepReport) {
                    win.__conciergeDrew(win.__deepReport.drew, win.__deepReport.why);
                }
            }
        };

        frame.addEventListener('load', attach);

        // Already loaded by the time this runs, which happens on a redraw.
        if (frame.contentDocument && frame.contentDocument.readyState === 'complete') {
            attach();
        }
    }

    window.conciergeDesign = { watch: watch };
})();
