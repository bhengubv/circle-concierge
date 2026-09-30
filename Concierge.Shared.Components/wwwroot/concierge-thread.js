// Keeping the thread where a person is actually reading.
//
// **There was no scrolling code in this workspace at all.** A conversation opened at its
// first message — 2,215 pixels of transcript with scrollTop at 0 — so reopening something
// showed you the beginning of a chat you had already read, and a reply streaming in arrived
// below the fold while you watched the top. Found by measuring the running app after
// mistaking it for the composer clipping the text.
//
// The rule every chat surface converges on, and the reason it is a rule: follow the bottom
// while the person is at the bottom, and never move the page while they are reading
// something further up. An assistant that yanks you back to the newest token while you are
// reading what it said a minute ago is worse than one that does not scroll at all.
window.conciergeThread = (function () {
    // How close to the end still counts as being at the end. A person who has scrolled up
    // two lines to reread a sentence is still following; one who has gone up a screen is
    // not. Generous, because the cost of being wrong in this direction is a small jump and
    // the cost of being wrong in the other is losing your place mid-sentence.
    const NEAR = 120;

    function find(selector) {
        return document.querySelector(selector);
    }

    function atBottom(el) {
        return el.scrollHeight - el.scrollTop - el.clientHeight <= NEAR;
    }

    return {
        // Opening a conversation puts you at the end of it, without animation: this is
        // where you were, not somewhere you are being taken.
        toEnd(selector) {
            const el = find(selector);

            if (el) {
                el.scrollTop = el.scrollHeight;
            }
        },

        // While a reply arrives. Returns whether it actually moved, so nothing has to
        // guess what happened.
        follow(selector) {
            const el = find(selector);

            if (!el || !atBottom(el)) {
                return false;
            }

            el.scrollTop = el.scrollHeight;
            return true;
        },

        // Whether the person is reading the end. Exposed so a surface can offer to take
        // them back rather than doing it to them.
        isAtEnd(selector) {
            const el = find(selector);
            return el ? atBottom(el) : true;
        },
    };
})();
