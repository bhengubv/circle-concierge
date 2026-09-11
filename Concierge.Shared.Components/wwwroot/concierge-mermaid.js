// concierge-mermaid.js
//
// Draws a diagram, using a copy of mermaid carried in this project.
//
// **It used to fetch mermaid from a CDN, and that was wrong for this product.** three.js is
// vendored here for a stated reason — a design surface that needs the internet to draw a box
// is not a local-first product — and diagrams quietly needed a connection anyway. On a machine
// with no internet the screen said "watch it draw" and then showed a module-import error.
//
// The standalone build rather than the ES module one: mermaid 11's ESM entry is a 30KB loader
// that pulls a few dozen chunk files at run time, and vendoring those is a build step nobody
// here wants to own. The standalone file is one file, defines one global, and is the same
// 3.5MB either way. MIT, and the licence is beside it.

let mermaidPromise = null;

async function ensureMermaid() {
    if (mermaidPromise) return mermaidPromise;

    mermaidPromise = loadScript('./_content/Concierge.Shared.Components/lib/mermaid/mermaid.min.js')
        .then(() => {
            const mermaid = window.mermaid;

            if (!mermaid) {
                throw new Error('mermaid loaded but defined nothing.');
            }

            mermaid.initialize({
                startOnLoad: false,
                theme: 'base',
                themeVariables: {
                    // Brand palette only — #2196F3, #2c3e50, #ffffff.
                    primaryColor: '#2196F3',
                    primaryTextColor: '#ffffff',
                    primaryBorderColor: '#2c3e50',
                    lineColor: '#2c3e50',
                    background: '#ffffff',
                    secondaryColor: '#ffffff',
                    tertiaryColor: '#f7f9fc',
                    nodeBorder: '#2c3e50',
                    clusterBkg: '#f7f9fc',
                    clusterBorder: '#2c3e50',
                    titleColor: '#2c3e50',
                    edgeLabelBackground: '#ffffff',
                    textColor: '#2c3e50',
                },
                securityLevel: 'strict',
                fontFamily: 'Helvetica Neue, Helvetica, Arial, sans-serif',
            });

            return mermaid;
        });

    return mermaidPromise;
}

// A plain script tag, because the standalone build is not an ES module. Loaded once and
// remembered: a page with forty diagrams on it loads 3.5MB once, not forty times.
function loadScript(src) {
    return new Promise((resolve, reject) => {
        const already = document.querySelector('script[data-concierge-mermaid]');

        if (already) {
            if (window.mermaid) {
                resolve();
            } else {
                already.addEventListener('load', () => resolve());
                already.addEventListener('error', () => reject(new Error('mermaid could not be loaded.')));
            }

            return;
        }

        const tag = document.createElement('script');
        tag.src = src;
        tag.async = true;
        tag.setAttribute('data-concierge-mermaid', '');
        tag.addEventListener('load', () => resolve());
        tag.addEventListener('error', () => reject(new Error(
            'The diagram drawer is missing from this build — lib/mermaid/mermaid.min.js.')));

        document.head.appendChild(tag);
    });
}

export async function renderInto(elementId, renderId, source) {
    const host = document.getElementById(elementId);
    if (!host) return;
    try {
        const mermaid = await ensureMermaid();
        const { svg, bindFunctions } = await mermaid.render(renderId, source);
        host.innerHTML = svg;
        if (typeof bindFunctions === 'function') {
            bindFunctions(host);
        }
    } catch (err) {
        host.innerHTML =
            '<pre class="mermaid-error" data-nosnippet>' +
            'Mermaid render failed: ' +
            String(err && err.message ? err.message : err)
                .replace(/&/g, '&amp;')
                .replace(/</g, '&lt;')
                .replace(/>/g, '&gt;') +
            '</pre>';
    }
}
