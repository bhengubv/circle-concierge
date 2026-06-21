# Concierge v1 — Apple-grade Design Spec

**Status:** Draft 1
**Owner:** Tendai Bhengu (Founder)
**Author:** Concierge dev
**Date:** 2026-06-05

A spec for the standard "Apple-grade Concierge v1" must meet before the kids-take-it-home demo. The bar is *Pixar / Apple at their best.* Anything that doesn't reach that bar is a gap, not a "good enough."

The Visual-Basic-of-AI positioning is fixed. So is the audience: tech-averse executives and their kids. Spark (CircleUp's mascot) tracks Bell's spec section-by-section so both products feel like siblings without sharing a brain.

---

## 1. Brand principles

Three rules. Test every design decision against them.

1. **Warmth before competence.** The first emotion is *welcome*, not *capability*. If a screen makes someone feel *"smart things happen here"* before it makes them feel *"you're in good hands"*, it fails.
2. **A face before a feature.** Bell is the product. Every screen has him within reach (literally or implicitly). When in doubt, draw Bell.
3. **Plain words before precise words.** "Help me write something" beats "Engage the writing skill" every time. If we wouldn't say it to a 9-year-old at dinner, we don't say it on screen.

---

## 2. Bell — the character system

### 2.1 Anatomy

| Element | Spec |
|---|---|
| **Body** | Brass concierge bell, dome with black base + brass plunger handle |
| **Body palette** | Highlight #FFF8DC → main #FFD76B → midshade #E8A93A → shadow #B97A1A |
| **Base palette** | #3A3A44 → #1D1D24 |
| **Face placement** | 60% down the dome (so Bell reads as "looking up at you") |
| **Eye style** | Ovals, 7×9 with single 2.5×3 white highlight at 30% offset top-left |
| **Mouth** | Smile, 28° arc, 3.5px stroke, rounded caps, ink #2C3E50 |
| **Cheeks** | 5px circles, #FF8A8A at 55% opacity, ~12px outside eyes |
| **Outline** | None — the gradient does the heavy lifting |

### 2.2 Six animation states

| State | Trigger | Visual signature | Duration |
|---|---|---|---|
| **idle** | Default | Body breathes ±3px vertical at 4s cycle; blink every 5s (200ms eyes-closed) | infinite |
| **listening** | Mic active | Body leans ±2° left/right at 1.6s cycle; halo pulses; 3 concentric radar arcs fade in/out | infinite while listening |
| **thinking** | Stream-in flight | Plunger handle wobbles ±6°; eyes glance up-right; radar arcs slower (2.4s) | until result arrives |
| **cheering** | First successful result | Bell bounces — translateY -18px overshoot with `cubic-bezier(.34, 1.6, .64, 1)`; 3 sparkles burst at 120°/240°/360° | 600ms × 2 |
| **listening-failed** | Mic denied / no audio | Body shakes ±4° at 80ms; eyes close briefly; ✗ ghost mark fades | 600ms |
| **sleeping** | App idle 60s on home | Bell's eyes close (lid path drawn); body breathes slower (6s); halo dims to 50% | until wake |

State transitions must overlap smoothly (200ms crossfade). No hard cuts.

### 2.3 Voice (Bell's "lines")

Bell never speaks user-facing text directly — he reacts. But his *implied* voice shapes the copy.

| Moment | Copy Bell would say |
|---|---|
| First launch | *"Hi! I'm Bell. Tap the mic and tell me what's on your mind, or pick a tile if you're not sure where to start."* |
| First-time mic prompt | *"I'll only listen when you tell me to."* |
| After cheering | *"Want to keep going, or save this?"* |
| Error | *"I couldn't quite catch that. Can you try again?"* |
| Sleeping | *"Tap me when you're back."* |

Tone: warm British concierge crossed with Pixar's Wall·E.

### 2.4 Spark — CircleUp sibling

Spark follows Bell's animation states 1:1 with one swap: **listening → writing** (handle/lightbulb tilts left-right at 0.8s cycle like a hand writing). Filament glows brighter during thinking.

---

## 3. Motion design

### 3.1 Easing palette (lock these — no ad-hoc curves)

| Use | Cubic-bezier | Duration |
|---|---|---|
| **Standard** (UI in/out) | `cubic-bezier(0.4, 0, 0.2, 1)` | 220ms |
| **Decel** (entering screens) | `cubic-bezier(0, 0, 0.2, 1)` | 280ms |
| **Accel** (leaving screens) | `cubic-bezier(0.4, 0, 1, 1)` | 180ms |
| **Spring** (Bell + delight) | `cubic-bezier(0.34, 1.6, 0.64, 1)` | 600ms |

### 3.2 First-launch orchestration

Total: 1100ms from app paint to interactive.

```
t=0    : warm canvas fills (status bar matches; no white flash)
t=120  : brand "Concierge" + sun-dot fade in (decel)
t=280  : Bell wakes (eyes open from sleeping state, 240ms)
t=520  : greeting + invitation fade in, staggered (50ms apart)
t=760  : composer rises from below (translateY 40 → 0 + opacity, decel)
t=980  : tiles cascade in 6×60ms (top-left → bottom-right diagonal)
t=1100 : "Show me what you can do" pill scales 0.8 → 1.0 with spring
```

After 1100ms Bell breathes idle. If 60s passes without input → he sleeps.

### 3.3 Send-press orchestration

```
t=0    : send button presses (scale 0.95 + brighten)
t=60   : composer collapses (height → 0 over 200ms, accel)
t=200  : Bell goes thinking + radar arcs appear
       : stream tokens animate into a fresh chat bubble
t=N    : Bell goes cheering on completion → settles to idle in 1200ms
```

---

## 4. Sound design

Three sounds. Each 200-400ms. All loaded at boot, played via `<audio>` with `preload="auto"`.

| Sound | Trigger | Character | File |
|---|---|---|---|
| **`ting.mp3`** | Mic press (Bell starts listening) | Brass bell single ding, soft mallet, decay 350ms | concierge-sounds/ting.mp3 |
| **`whoosh.mp3`** | Send (message sent / camera capture) | Soft paper-airplane swoosh, 280ms | concierge-sounds/whoosh.mp3 |
| **`pop.mp3`** | Result delivered + cheering | Light percussive pop with subtle reverb tail | concierge-sounds/pop.mp3 |

Volume: -18 LUFS reference, -6 dB headroom. Compressed for phone speakers.
Mute respect: skip if device is silent or "Do Not Disturb."

CircleUp uses the same three with a slight pitch shift (-2 semitones) so it sounds like Spark not Bell.

---

## 5. Haptic design

Three patterns. Wired through `navigator.vibrate()` on Android (translated to CoreHaptics on iOS via interop).

| Pattern | Trigger | Pattern (ms) | Intent |
|---|---|---|---|
| **Tap-light** | Tile / button press | `[8]` | Acknowledgement |
| **Success** | Send completed | `[12, 60, 12]` | Two soft beats |
| **Attention** | Error / Bell shakes head | `[30, 40, 30, 40, 30]` | Gentle nudge |

Respect iOS / Android "Reduce Motion" — if on, skip haptics + suppress Bell animations beyond idle.

---

## 6. Typography

### 6.1 Stack

```
-apple-system, BlinkMacSystemFont,
'SF Pro Rounded', 'SF Pro Text',
'Inter', 'Nunito',
'Segoe UI', 'Roboto', 'Helvetica Neue', Arial, sans-serif
```

`SF Pro Rounded` first because rounded geometry reads warmer. Inter as the cross-platform shoring.

### 6.2 Scale (mobile)

| Token | Size | Weight | LineH | Tracking | Use |
|---|---|---|---|---|---|
| Display | 36px | 700 | 1.15 | -0.025em | First-launch title only |
| H1 | clamp(1.35, 5.5vw, 2.2)rem | 700 | 1.15 | -0.020em | Home greeting |
| H2 | 1.25rem | 600 | 1.3 | -0.010em | Section heads inside chat |
| Body | 1.05rem | 400 | 1.5 | -0.005em | Default |
| Lede | 1.05rem | 400 | 1.5 | -0.005em (muted color) | Invitation, hints |
| Caption | 0.85rem | 500 | 1.4 | 0 | Microcopy |
| Eyebrow | 0.7rem | 600 | 1 | 0.10em UPPERCASE | Section labels |

### 6.3 Headline craft

Use `text-wrap: balance` everywhere a headline can wrap. Reject any break that strands a single short word on its own line ("we / do?").

---

## 7. Iconography

### 7.1 App icon

Specs (locked):
- Canvas: 1024×1024
- Background: radial gradient #FFF4D6 (centre) → #FFD76B (edge) — same as Bell's halo
- Centred Bell at 65% scale, with one variant: a soft drop shadow under the base (Apple-style depth)
- Rounded-rect mask (iOS handles automatically; Android adaptive icon outputs both circle + square)
- Tile color (Mac dock): solid #2196F3 (brand blue), Bell at 70% white opacity

Effort: 1 designer-day for the master + adaptive exports.

### 7.2 Six home tiles

Replace emoji (which renders differently per OEM) with custom illustrated icons. Same Bell-family palette + 2.5px stroke language.

| Tile | Glyph |
|---|---|
| Help me write something | Quill on a folded letter, ink dot dripping |
| Help with homework | Open book with a small Bell peeking over the top |
| Tell me a story | A campfire + the silhouette of a dragon's tail curling around it |
| Plan a meal | Sun-yellow frying pan with a single egg, herb sprig |
| Make a picture | A square canvas with a paintbrush mid-stroke |
| Explain something | A glowing question mark inside a thought-bubble outline |

48×48 at @1x, exported at @2x and @3x. SVG sources + PNG fallbacks.

Effort: 1 illustrator-day for all six.

### 7.3 In-app icons

Lucide / Phosphor stays for utility (mic, camera, send, profile, settings). 2.4px stroke (slightly chunkier than the Linear/Vercel default). Brand color #2196F3 for primary actions; muted text color elsewhere.

---

## 8. First-launch experience

A 4-screen, ~30-second sequence. Skippable. Triggered only on the very first launch.

**Screen 1 — Welcome** (3s auto-advance or tap)
- Bell wakes from sleeping state.
- Speech bubble (rendered, not voice): *"Hi! I'm Bell."*
- Single CTA: **"Hello, Bell"** (button)

**Screen 2 — Who's this for?** (interactive)
- *"Quick — who'll mostly use this?"*
- Three big tappable cards: **"A grown-up"** / **"A kid"** / **"Both of us"**
- Selection sets default content filter + tone + tile choices, never visible later. Can change in Settings.

**Screen 3 — Permission with a face** (interactive)
- Bell in listening state demo-pulse.
- *"I'll only listen when you tell me to. Can I have permission for the mic?"*
- Two CTAs: **"Yes, of course"** (triggers OS mic prompt) / **"Maybe later"** (skip)

**Screen 4 — One magic moment** (auto)
- Bell shifts to thinking.
- A pre-recorded scripted result types in: *"Here's a tiny example. I just wrote a haiku for you:"* + animated 3-line haiku.
- Bell cheers + sparkles.
- CTA: **"My turn"** (advances to Home).

Total wall-clock ~28s. Stored under `Concierge.Shared.Components/Onboarding/`.

---

## 9. Accessibility (WCAG 2.2 AA + Apple HIG)

Mandatory checklist:

- [ ] Every interactive element has `aria-label` (Bell: "Concierge helper", composer mic: "Hold to talk", etc.)
- [ ] All button labels speakable in one breath
- [ ] Contrast ratio ≥ 4.5:1 for all body text against canvas
- [ ] Contrast ratio ≥ 3.0:1 for large text + icons
- [ ] Focus rings visible on keyboard nav (web + external-keyboard on tablets)
- [ ] Respect `prefers-reduced-motion`: Bell freezes on idle pose, transitions cut to 0ms
- [ ] Respect `prefers-color-scheme`: switch to dark variant (see §11)
- [ ] VoiceOver / TalkBack pass: linear reading order matches visual order, no orphan elements
- [ ] Dynamic Type respected on iOS (text scales up to 200% without breaking layout)
- [ ] Tap targets ≥ 44×44 (Apple HIG) — currently ≥ 56×56 (overshoots; fine)

Test matrix: VoiceOver on iPhone, TalkBack on Pixel, NVDA on Windows. **Empirical pass mandatory before ship.**

---

## 10. Light / dark adaptive theme

Defaults to light (warm cream). Auto-switches with `prefers-color-scheme: dark`. User can override in Settings.

### Dark variant (Bell stays brass — never turns dim)

| Token | Light | Dark |
|---|---|---|
| `--fc-bg` | #FDF9F1 | #1A1716 |
| `--fc-panel` | #FFFFFF | #2A2624 |
| `--fc-warm` | #FFF4D6 | #2E2818 |
| `--fc-line` | #EBE3D2 | #3D3733 |
| `--fc-text` | #2C3E50 | #F7EFE0 |
| `--fc-muted` | #6B7385 | #BDB5A3 |
| `--fc-brand` | #2196F3 | #4DAEFF |

Bell's gradients DO NOT change. He's always brass, warm, and friendly.
Cheek tint shifts slightly cooler in dark mode (#FF9AA8) for color-balance.

---

## 11. State coverage

For every action, define explicitly: **idle / loading / empty / error / success.** No screen ships without all five.

Examples for v1 surfaces:

| Surface | Idle | Loading | Empty | Error | Success |
|---|---|---|---|---|---|
| Home composer | Default | Mic listening; send streaming | (n/a — placeholder is the empty state) | "I couldn't catch that — try again" + Bell shake | "Got it. Sending…" + Bell cheer |
| Chat thread | Conversation list | Skeleton bubbles | Bell + *"What shall we talk about?"* | "Couldn't send. Try once more." | Streaming result + Bell cheers |
| Skill picker | Search box + suggestions | Skeleton tiles | "No skills match that yet" | Network error toast | Skill chip appears in composer |
| Camera | Camera preview | Capturing… | (n/a) | "I couldn't reach the camera" | Photo preview + caption prompt |

Microcopy in Bell's voice everywhere. No raw stack traces. No "Something went wrong."

---

## 12. Tap targets + safe areas

- Minimum tap target: **56×56**. Buttons in composer already meet this.
- Spacing: **8px minimum** between two tap targets.
- Top safe area: `env(safe-area-inset-top)` + 14px to brand row.
- Bottom safe area: `env(safe-area-inset-bottom)` + 56px. The +56 is to clear Android 3-button nav which reports inset 0 but still occupies pixels (fix already in shell.css, verified on Redmi 9).

---

## 13. Performance budget

Phone (Redmi-class hardware) targets:

| Metric | Budget |
|---|---|
| Cold launch (paint) | ≤ 1.6s |
| Time to Bell awake | ≤ 1.2s after paint |
| Mic-tap → audible "ting" | ≤ 80ms |
| Send → first streaming token | ≤ 1.8s (provider-dependent; we own the UI) |
| First-launch total to interactive | ≤ 1.4s |

If any cold-launch metric regresses by >100ms, that's a release blocker.

---

## 14. Execution plan

| Workstream | Effort | Owner |
|---|---|---|
| App icon (master + adaptive) | 1 day | Illustrator |
| 6 home tile icons (SVG) | 1 day | Illustrator |
| Bell — 6 animation state CSS / Lottie | 1.5 days | Motion designer |
| Sound design (3 SFX) | 0.5 day | Sound designer |
| First-launch sequence (Razor + copy) | 1 day | Concierge dev |
| Light/dark adaptive theme | 0.5 day | Concierge dev |
| State coverage pass (idle/loading/empty/error/success) | 1 day | Concierge dev |
| Accessibility empirical pass | 0.5 day | Concierge dev + QA |
| Performance audit + tuning | 0.5 day | Concierge dev |

**Total wall-clock if serial:** ~7.5 days.
**If illustrator + dev parallel:** ~5 days.

That gets us from honest 4.5/10 to honest 8/10 (Apple-grade-adjacent — not Pixar yet, but no Apple reviewer would call us amateur).

Pixar 9/10 needs an illustrator + sound designer hired for two weeks. Not in scope for v1.

---

## 15. What we explicitly are NOT spec'ing in v1

- Multiple mascots (Bell only; Spark for CircleUp tracks the same beats)
- Live AR / 3D
- Original soundtrack
- Voice synthesis ("Bell talks back")
- Localisation beyond English (it's English-first; we localise after lift-off)

Each of these is a v2+ conversation.

---

## 16. Bottom line

This spec describes the bar. The bar isn't "ship more features." The bar is "would Tendai be proud to hand this phone to his 9-year-old niece + his 70-year-old aunt, in the same room, on the same day, and watch them both succeed in 30 seconds?"

Anything we build to that bar wins. Anything that doesn't, we ship as "draft" not "v1."
