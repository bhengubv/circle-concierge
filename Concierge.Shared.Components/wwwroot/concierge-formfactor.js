// Which recipe the workspace should render.
//
// Read from the viewport and the pointer rather than from the OS: the same
// Android head runs on a phone and on a Wear OS watch, and a desktop window
// is dragged through every range while the app is open. Asking the platform
// would put a watch on the phone recipe and would never notice a resize.
window.conciergeFormFactor = (function () {
    // Below this a sidebar and a thread cannot sit side by side. It is the
    // breakpoint the drawer already used.
    const HANDHELD_MAX = 759.98;

    // Above this it is a small phone, below it a watch. Wear OS round displays
    // report about 227 CSS px and Apple Watch about 198; the smallest phone
    // still in use reports 320.
    const WEARABLE_MAX = 319.98;

    function measure() {
        const w = window.innerWidth;
        if (w <= WEARABLE_MAX) return "wearable";
        if (w <= HANDHELD_MAX) return "handheld";
        return "desktop";
    }

    let observer = null;

    return {
        current: measure,

        // Reports only when the answer changes, not on every resize frame:
        // switching recipe remounts the workspace, so this must be rare.
        watch: function (dotNetRef) {
            let last = measure();
            const onResize = () => {
                const next = measure();
                if (next !== last) {
                    last = next;
                    dotNetRef.invokeMethodAsync("OnFormFactorChanged", next);
                }
            };
            window.addEventListener("resize", onResize);
            observer = onResize;
            return last;
        },

        unwatch: function () {
            if (observer) {
                window.removeEventListener("resize", observer);
                observer = null;
            }
        }
    };
})();
