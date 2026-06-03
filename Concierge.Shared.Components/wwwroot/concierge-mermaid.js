// concierge-mermaid.js
//
// Lazy-loads mermaid.js from a CDN on first use and exposes a single export
// (renderInto) that the MermaidBlock Razor component calls via JS interop.
//
// We deliberately use the ESM build via dynamic import so the bytes only
// land in the browser when the user actually views a chat reply containing
// a diagram — Blazor's initial circuit handshake stays small.

let mermaidPromise = null;

async function ensureMermaid() {
    if (mermaidPromise) return mermaidPromise;
    mermaidPromise = import('https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.esm.min.mjs')
        .then((mod) => {
            const mermaid = mod.default;
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
