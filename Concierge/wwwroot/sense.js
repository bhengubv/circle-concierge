// sense.js — synthesised sound + haptic + theme runtime for Concierge / CircleUp.
//
// Why we synthesise: shipping MP3s through MAUI's MauiAsset pipeline adds
// 40-200 KB per sound and platform-permission gates. The three sounds we
// need (ting / whoosh / pop) are simple enough to generate cleanly via
// WebAudio at runtime — zero binary asset cost, perfect platform fidelity.
//
// Haptics use the Vibration API on Android; on iOS Safari they degrade to
// no-op (Apple gates haptics behind CoreHaptics which we'd need a native
// bridge for; the polish-spec calls that out as a v2 item).
//
// Theme override: writes `data-theme` on <body> and persists in localStorage.

(function () {
    'use strict';

    let audioCtx = null;
    function ensureCtx() {
        // Lazy: first interaction creates the context (Apple requires this).
        if (!audioCtx) {
            const Ctx = window.AudioContext || window.webkitAudioContext;
            if (Ctx) audioCtx = new Ctx();
        }
        return audioCtx;
    }

    function envelope(gain, attack, sustain, release, peakAt = 0.7) {
        const t = audioCtx.currentTime;
        gain.gain.setValueAtTime(0, t);
        gain.gain.linearRampToValueAtTime(peakAt, t + attack);
        gain.gain.linearRampToValueAtTime(peakAt * 0.7, t + attack + sustain);
        gain.gain.exponentialRampToValueAtTime(0.001, t + attack + sustain + release);
        return t + attack + sustain + release;
    }

    /**
     * "ting" — Bell starting to listen. A clean bell-like sine with a soft
     * partial overtone. ~350ms decay.
     */
    function playTing() {
        const ctx = ensureCtx(); if (!ctx) return;
        const fund = ctx.createOscillator();
        const fifth = ctx.createOscillator();
        const gain = ctx.createGain();
        fund.type = 'sine';
        fund.frequency.value = 988;   // B5
        fifth.type = 'sine';
        fifth.frequency.value = 1480; // F#6 (perfect fifth above)
        gain.connect(ctx.destination);
        fund.connect(gain);
        fifth.connect(gain);
        const stop = envelope(gain, 0.005, 0.04, 0.32);
        fund.start(); fifth.start();
        fund.stop(stop); fifth.stop(stop);
    }

    /**
     * "whoosh" — message sent. Filtered noise sweeping high to mid. ~280ms.
     */
    function playWhoosh() {
        const ctx = ensureCtx(); if (!ctx) return;
        const buffer = ctx.createBuffer(1, ctx.sampleRate * 0.28, ctx.sampleRate);
        const data = buffer.getChannelData(0);
        for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;
        const src = ctx.createBufferSource();
        const filter = ctx.createBiquadFilter();
        const gain = ctx.createGain();
        src.buffer = buffer;
        filter.type = 'bandpass';
        filter.frequency.setValueAtTime(4000, ctx.currentTime);
        filter.frequency.exponentialRampToValueAtTime(800, ctx.currentTime + 0.28);
        filter.Q.value = 0.8;
        src.connect(filter).connect(gain).connect(ctx.destination);
        const stop = envelope(gain, 0.01, 0.06, 0.21, 0.32);
        src.start();
        src.stop(stop);
    }

    /**
     * "pop" — result delivered + Bell cheers. Quick rising sine + click. ~220ms.
     */
    function playPop() {
        const ctx = ensureCtx(); if (!ctx) return;
        const osc = ctx.createOscillator();
        const gain = ctx.createGain();
        osc.type = 'sine';
        osc.frequency.setValueAtTime(440, ctx.currentTime);
        osc.frequency.exponentialRampToValueAtTime(1320, ctx.currentTime + 0.12);
        osc.connect(gain).connect(ctx.destination);
        const stop = envelope(gain, 0.005, 0.04, 0.18, 0.45);
        osc.start();
        osc.stop(stop);
    }

    function vibrate(pattern) {
        if (!navigator.vibrate) return;
        if (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
        try { navigator.vibrate(pattern); } catch { /* iOS Safari no-op */ }
    }

    // Haptic primitives (per design spec §5).
    function hapticTapLight() { vibrate(8); }
    function hapticSuccess()  { vibrate([12, 60, 12]); }
    function hapticAttention(){ vibrate([30, 40, 30, 40, 30]); }

    // ── CircleMetro appearance ────────────────────────────────────────────
    const THEME_KEY = 'cu-theme';
    const SKIN_KEY = 'cu-skin';
    const THEMES = ['light', 'dark', 'auto'];
    const SKINS = ['circle', 'tide', 'berry'];
    // CircleMetro is being rolled out to the Windows desktop head first.
    // Other MAUI heads keep their current theme until their own design pass.
    const WINDOWS_DESKTOP = Boolean(window.chrome && window.chrome.webview);
    if (WINDOWS_DESKTOP) {
        document.documentElement.setAttribute('data-cm-host', 'windows-desktop');
    }

    function readPreference(key, allowed, fallback) {
        try {
            const value = localStorage.getItem(key);
            return allowed.includes(value) ? value : fallback;
        } catch {
            return fallback;
        }
    }

    function getTheme() { return readPreference(THEME_KEY, THEMES, 'auto'); }
    function getSkin() {
        return readPreference(SKIN_KEY, SKINS,
            SKINS.includes(document.documentElement.dataset.skin) ? document.documentElement.dataset.skin : 'circle');
    }

    function resolvedTheme(mode) {
        return mode === 'auto'
            ? (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
            : mode;
    }

    function paintAppearance(theme, skin) {
        const root = document.documentElement;
        root.setAttribute('data-theme', theme);
        root.setAttribute('data-skin', skin);
        if (document.body) {
            // Keep body-scoped legacy styles in step; CircleMetro tokens live on :root.
            document.body.setAttribute('data-theme', theme);
            document.body.setAttribute('data-skin', skin);
        }

        const meta = document.querySelector('meta[name="theme-color"]');
        if (meta) {
            const canvas = getComputedStyle(root).getPropertyValue('--cm-color-canvas').trim();
            if (canvas) meta.setAttribute('content', canvas);
        }
    }

    function applyAppearance(mode, skin) {
        if (!WINDOWS_DESKTOP) return;
        const theme = THEMES.includes(mode) ? mode : 'auto';
        const nextSkin = SKINS.includes(skin) ? skin : 'circle';
        try {
            if (theme === 'auto') localStorage.removeItem(THEME_KEY);
            else localStorage.setItem(THEME_KEY, theme);
            localStorage.setItem(SKIN_KEY, nextSkin);
        } catch { /* Theme still applies for this session when storage is unavailable. */ }
        paintAppearance(resolvedTheme(theme), nextSkin);
    }

    function applyTheme(mode) {
        if (!WINDOWS_DESKTOP) {
            // Preserve the pre-existing MAUI behaviour on the heads not in this pass.
            try {
                if (mode === 'auto') {
                    document.body?.removeAttribute('data-theme');
                    localStorage.removeItem(THEME_KEY);
                } else {
                    document.body?.setAttribute('data-theme', mode);
                    localStorage.setItem(THEME_KEY, mode);
                }
            } catch { /* Best effort when storage is unavailable. */ }
            return;
        }
        applyAppearance(mode, getSkin());
    }

    function applySkin(skin) {
        if (WINDOWS_DESKTOP) applyAppearance(getTheme(), skin);
    }

    function loadAppearance() {
        if (!WINDOWS_DESKTOP) {
            // Keep Android/iOS/Mac Catalyst's previous saved theme behavior.
            const saved = readPreference(THEME_KEY, ['light', 'dark'], null);
            if (saved && document.body) document.body.setAttribute('data-theme', saved);
            return;
        }
        paintAppearance(resolvedTheme(getTheme()), getSkin());
    }

    // This file is loaded in the document head: set :root before its stylesheets
    // arrive, then bring body-scoped legacy rules along once body exists.
    loadAppearance();
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', loadAppearance, { once: true });
    }
    if (window.matchMedia) {
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener?.('change', () => {
            if (getTheme() === 'auto') loadAppearance();
        });
    }

    // Public surface.
    window.conciergeSense = {
        playTing, playWhoosh, playPop,
        hapticTapLight, hapticSuccess, hapticAttention,
        applyTheme, getTheme, applySkin, getSkin, applyAppearance,
        isWindowsDesktop: () => WINDOWS_DESKTOP,
    };
})();
