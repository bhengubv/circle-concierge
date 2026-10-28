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
    // WebView key events reach the textarea before Blazor. The Desktop hint promises
    // Enter to send and Shift+Enter for a new line, so enforce that at the DOM edge.
    document.addEventListener('keydown', event => {
        if (document.documentElement.dataset.cmHost !== 'windows-desktop'
            || event.key !== 'Enter' || event.shiftKey || event.ctrlKey || event.altKey
            || event.metaKey || event.isComposing
            || !(event.target instanceof HTMLTextAreaElement)
            || !event.target.matches('.ws .comp-input')) return;
        event.preventDefault();
        // Blazor binds the textarea on input. A pasted prompt followed immediately by
        // Enter can reach this handler before that binding finishes. Give the binding
        // time to cross the WebView bridge, and collapse repeated Enter presses.
        const field = event.target;
        if (field.dataset.enterPending === 'true') return;
        field.dataset.enterPending = 'true';
        setTimeout(() => {
            delete field.dataset.enterPending;
            const send = field.closest('.comp-box')?.querySelector('.icon-btn-send:not(:disabled)');
            send?.click();
        }, 300);
    }, true);

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
        focusComposer() {
            const focus = () => {
                const field = document.querySelector('.ws .comp-box textarea:not([disabled])');
                if (!field || document.querySelector('[aria-modal="true"]')) return false;
                field.focus(); return document.activeElement === field;
            };
            if (focus()) return;
            const observer = new MutationObserver(() => { if (focus()) observer.disconnect(); });
            observer.observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['disabled'] });
            setTimeout(() => observer.disconnect(), 5000);
        },
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

        async copyText(text) {
            if (navigator.clipboard && navigator.clipboard.writeText) {
                try {
                    await navigator.clipboard.writeText(text);
                    return true;
                } catch (_) { /* WebViews may deny the modern clipboard API. */ }
            }

            const field = document.createElement('textarea');
            const focused = document.activeElement;
            field.value = text;
            field.setAttribute('readonly', '');
            field.style.position = 'fixed';
            field.style.opacity = '0';
            document.body.appendChild(field);
            field.select();
            let copied = false;
            try { copied = document.execCommand('copy'); }
            finally {
                field.remove();
                if (focused && typeof focused.focus === 'function') focused.focus();
            }
            return copied;
        },
    };
})();
