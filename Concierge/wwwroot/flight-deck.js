// Flight-deck tap dispatcher.
//
// Android WebView has a known quirk where overflow-clipped content STILL
// intercepts pointer events at its pre-clip (natural) coordinates. In
// Concierge this manifested as the bottom row of the Dashboard's Quick
// Start grid stealing taps from the bottom tab bar, even though the cards
// were visually clipped above the tab bar by `overflow: hidden` and
// `contain: strict` on .cu-content.
//
// This script binds a capture-phase document-level click handler. Any
// click whose pixel coordinates land inside the rendered .cu-tabbar gets
// re-dispatched to the correct .cu-tab — beating any overlapping content
// to the punch. Result: tab taps are always reliable, regardless of what
// the underlying WebView's CSS hit-test decides to do.
//
// Capture phase is mandatory: by the time the click bubbles, the overlapping
// card's onclick has already fired and possibly navigated/scrolled.

(function () {
    'use strict';

    function inside(rect, x, y) {
        return x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom;
    }

    function pickTab(x, y) {
        const tabbar = document.querySelector('.cu-tabbar');
        if (!tabbar) return null;
        const bar = tabbar.getBoundingClientRect();
        if (y < bar.top) return null;
        const tabs = tabbar.querySelectorAll('.cu-tab');
        for (const t of tabs) {
            const r = t.getBoundingClientRect();
            if (inside(r, x, y)) return t;
        }
        return null;
    }

    function bindOnce() {
        if (window.__cuTabDispatcherBound) return;
        window.__cuTabDispatcherBound = true;

        // Use 'click' (not pointerdown) because Blazor's @onclick is wired
        // to click events. Capture phase so we intercept BEFORE any
        // overlapping element's bubble-phase handler fires.
        document.addEventListener('click', function (e) {
            const tab = pickTab(e.clientX, e.clientY);
            if (!tab) return;
            // If the click landed inside the tab itself, let it through —
            // no redirect needed.
            if (tab === e.target || tab.contains(e.target)) return;
            // Click landed in the tab-bar y zone but on an overlapping
            // element (a scrolled-into-place card). Redirect to the tab.
            e.stopImmediatePropagation();
            e.preventDefault();
            // Synthesise a fresh click so Blazor's handler fires correctly.
            tab.click();
        }, /* useCapture */ true);
    }

    // BlazorWebView mounts the app after the splash; retry until the tab
    // bar exists in the DOM.
    function watchForShell() {
        if (document.querySelector('.cu-tabbar')) {
            bindOnce();
            return;
        }
        setTimeout(watchForShell, 200);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', watchForShell);
    } else {
        watchForShell();
    }
})();
