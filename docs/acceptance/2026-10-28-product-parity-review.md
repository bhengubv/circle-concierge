# Concierge product parity review — 28 October 2026

## Verdict and scope

Concierge does **not yet match** Codex, Claude Code, or Gemini as an end-to-end
assistant. The released Circle AI 3.9.2 host is connected to Desktop chat, but
an engine connection is one component of the product. This review evaluates
features and completed user journeys, not whether the products have identical
screens or models. It does not assert an exhaustive inventory of any competitor.

Evidence inspected: the running Desktop accessibility tree (Chat, Projects,
Library, Notebooks, Scheduled, approvals, and action controls); the running
PixieDust Concierge Skills screen and installed package metadata (`1.0`, last
updated 26 October at 20:42:34); current Concierge source; and the linked live
acceptance records. The Windows UI helper captured Desktop but failed on a
subsequent navigation, so this review does not claim a fresh click-through of
every Desktop surface. StarDust was absent from the existing ADB connections,
so no new Watch outcome is claimed. AngelDust and PixieDust were connected;
PixieDust had Concierge in the foreground.

## Current competitor reference points

- [Codex app](https://openai.com/index/introducing-the-codex-app/) documents
  skills that can be chosen automatically, parallel worktrees, reviewable
  changes, and scheduled automations. [OpenAI's later product update](https://openai.com/index/codex-for-almost-everything/)
  adds computer/browser work and broader plugins. These are current product
  claims, not outcomes tested on this machine.
- [Claude Code Desktop](https://code.claude.com/docs/en/desktop) documents
  parallel sessions, file/terminal/browser panels, diff review, permissions,
  connectors, and phone-to-Desktop dispatch. Its [extension reference](https://code.claude.com/docs/en/features-overview)
  describes automatically relevant skills, tools, and subagents.
- [Gemini Tasks](https://support.google.com/gemini/answer/17094507?co=GENIE.Platform%3DDesktop)
  documents visible plans/progress, files and connected apps. [Gemini Live](https://support.google.com/gemini/answer/15274899?hl=en-gb)
  supports camera and screen sharing on Android. [Google's Wear OS guide](https://support.google.com/wearos/answer/6056824?hl=en)
  lists watch voice activation and actions.

The June [Family Mode parity audit](../CONCIERGE-PARITY-AUDIT.md) is historical.
Its claims that Codex is web-only, Claude Code terminal-only, and Concierge
already has automatic service-skill parity are not valid current findings.

## The twelve tracked feature gaps

The [26 October feature-gap checkpoint](2026-10-26-feature-gaps.md) contains
**12** individually tracked items: **1 passed its recorded live acceptance**
and **11 remain open**. The open items vary from partial implementation to no
live proof; the number is a backlog count, not a measured parity percentage.

| Item | Current evidence | Assessment |
|---|---|---|
| Desktop summon | Restored a minimized app from Explorer and focused the composer | Passed for ordinary Desktop windows; elevated-window limitation recorded |
| Unified Library | Text navigation works; small inline media previews have automated coverage | Open: other storage/format coverage and live media review |
| Notebooks | Desktop source-grounded answer passed; PixieDust invented/ignored source facts | Open: mobile grounding and richer source/sync flow |
| Scheduling | While-open work, repeat, approval, stop, saved result were exercised | Open: app-closed execution and completion notification |
| Connected-app actions | Setup/action code exists in places; no accepted live journey | Open |
| File provenance | File-tool outcomes and command request/outcome recorded | Open: command and external-service path attribution |
| Browser operation | No accepted visible browser task/takeover journey | Open |
| Phone continuous voice | Live PixieDust capture and microphone cleanup passed | Open: transcription and sustained conversation not proved |
| Live camera video | Android service chat link reports text-only visual input | Open |
| Screen sharing | No live sharing assistance journey | Open |
| Watch wake phrase | Prior watch reply display is separate from hands-free start | Open; no current StarDust check |
| Watch connected-app actions | No accepted watch action journey | Open; no current StarDust check |

## Other product-level gaps the twelve do not fully capture

- **Answer quality:** the [five complex-task acceptance cases](2026-10-26-response-quality-tasks.md)
  have no complete pass. Tasks 1, 2, and 5 failed in the recorded runs (Task 1
  was only partly submitted); Tasks 3 and 4 were not run. The allowance task
  missed the correct $14 result on both Desktop and PixieDust. This is a more
  consequential gap than the existence of a chat box or Markdown renderer.
- **Current Desktop engine:** the [3.9.2 handoff](2026-10-27-windows-3.9.2-handoff.md)
  proved service restart recovery and a simple correct answer. A contextual
  apple-total answer was wrong (`8` rather than `7`), and a cold post-restart
  answer took 147.718 seconds end to end. The loopback contract did not expose
  multi-step agentic work to Desktop chat. These observations do not establish
  the service's full ability outside this client.
- **Skills and memory:** PixieDust's live panel shows 69 bundled skills and
  1,378 Circle AI library entries ([screenshot](2026-10-28-pixiedust-skills.png)).
  Current source says the service search returns names/IDs for discovery;
  enabled local skills supply instructions. Android chat recalls relevant
  long-term memory facts through the service, but the inspected enrichment path
  does not automatically select a Circle AI skill for a request. The installed
  phone screen says relevant library matches are passed into requests; that
  claim needs either an end-to-end trace or corrected wording. Desktop's 3.9.2
  loopback path has not proved the service SQLite skill or memory library in
  normal chat.
- **Work execution and continuity:** Chat, Projects, work records, permissions,
  and linked-device mechanisms exist. A user request has not yet been shown to
  become a complete plan, executed tools, reviewable artifacts, and follow-up
  across Desktop, Mobile, and Watch at the level demonstrated by the current
  competitor reference products. This is an inference from the acceptance
  inventory, not a claim that every possible workflow was attempted.

## Next acceptance gate

Run the same substantial, original task on Desktop and PixieDust: create a
small appointment-booking MVP for a one-person salon, with lunch/holiday
blocking, a five-hour weekly work limit, a two-week plan, files or other
reviewable output, and a corrected follow-up. Record the exact model/service,
tools invoked, sources/skills/memory used, progress, approvals, artifacts,
elapsed time, and full result. Then verify the project state from the other
device. A reply or a rendered plan alone does not pass this gate. Watch should
be judged on its own quick-input, notification, and decision journey when
StarDust is available.
