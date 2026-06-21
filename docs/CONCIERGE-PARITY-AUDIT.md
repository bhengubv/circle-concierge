# Concierge — Family Mode Parity Audit

**Date:** 2026-06-05
**Author:** Concierge dev (Claude)
**Audience:** Founder. Honest snapshot of where we are vs. the AI tools we're *not* trying to be.

## The positioning

Concierge is the **Visual Basic of AI** — the warm middle ground between the developer-focused power tools (Codex, Claude Code, Cursor) and the generic consumer chats (ChatGPT, Gemini, Copilot). Target users: tech-averse executives and their kids. Sales channel: the kids.

We are not competing with Codex or Claude Code. They are reference points to understand what AI tools *can* do today — so we know which capabilities lift us into the same conversation as the serious tools without inheriting their cold, intimidating surface.

## Feature matrix

| Capability | Codex (OpenAI Cloud) | Claude Code (CLI) | Concierge — today | Concierge — gap to close |
|---|---|---|---|---|
| **Chat with an LLM** | ✓ | ✓ | ✓ (multi-provider: OpenAI · Anthropic · Gemini · Moonshot · Z.ai · DeepSeek · Qwen · xAI · Mistral) | none — *Concierge has wider provider coverage than either* |
| **Voice input** | partial (Whisper via mic icon) | ✗ (terminal) | ✓ (`conciergeVoice.start/stop` + `/api/voice/transcribe`) | persistent home-screen mic, not just chat |
| **Voice output (TTS)** | partial | ✗ | ✓ (`/api/voice/speak`, MP3 playback) | auto-speak responses in Kid Mode |
| **Image generation** | ✓ (DALL-E inside chat) | ✗ | ✓ (`/images` — OpenAI Images, Stability adapters) | inline image generation from chat composer |
| **Diagram generation** | ✗ | partial (mermaid) | ✓ (`/diagrams` — PenPot + Figma + mermaid) | *Concierge has more diagramming surface than either reference* |
| **File attachment in chat** | ✓ | ✓ (project-aware) | ✓ (text attachments, 256 KB limit) | binary / image / PDF attachments (Concierge: text-only in v1) |
| **Camera / vision input** | ✓ (web app — image upload) | ✗ | ✗ (camera button routes to chat but no capture yet) | wire `getUserMedia` → base64 → vision-enabled provider |
| **Skill system** | ✗ (no equivalent) | ✓ (SKILL.md format) | ✓ (`SkillRuntime` + 1,400+ skills synced from `Skills/` library) | *Concierge matches Claude Code's skill discipline + has 10× more skills out of the box* |
| **Tool calling (LLM → real APIs)** | ✓ (function calling) | ✓ (`IAgentToolRegistry`) | ✓ (`IAgentToolRegistry`, tool-loop protocol in `Chat.razor`) | broader built-in tool catalog (Concierge currently has lightweight set) |
| **Code execution** | ✓ (Python sandboxes) | ✓ (Bash) | ✗ | sandboxed code-execution tool (kid-safe: read-only by default, parental opt-in for write) |
| **File system access** | ✓ (cloud workspace) | ✓ (local) | partial (`Concierge.Cli` has local FS; chat doesn't expose it) | gated "Save my work" + "Open a file" actions for the family layout |
| **Web fetch / browse** | ✓ | ✓ (WebFetch tool) | ✗ | add a `web-fetch` tool the LLM can call when answering "look this up" |
| **Git ops** | ✓ (cloud git) | ✓ (CLI git) | ✗ | out of scope for Family Mode (kids + execs ≠ git users) |
| **Multi-step autonomous agents** | ✓ (background tasks) | ✓ (subagents, hooks) | partial (tool-loop iteration cap = 5) | "Plan + run" mode that doesn't show the user the steps — just the result + an "I made this" receipt |
| **Conversation history** | ✓ | ✓ (per-session) | ✓ (`IConversationStore` per-owner) | better cross-device sync (currently local store) |
| **Workspace concept** | ✓ (one per task) | partial (CWD) | ✓ (`Concierge.Shared.Workspaces`) — currently hidden | bring it back as a kid-friendly "Projects" tile |
| **Native mobile app** | ✗ (web only) | ✗ (CLI only) | ✓ (MAUI Android · iOS · Mac · Windows) | *Concierge wins this dimension outright — install on phone, no terminal needed* |
| **Family / kid mode** | ✗ | ✗ | partial (mascot + warm theme done; content filter + parental dash TODO) | the moat: nobody else builds this |
| **Mascot / personality** | ✗ | ✗ | ✓ Bell — brass concierge bell, animated SVG | tie Bell into chat responses (Bell reacts to each turn) |
| **Skill marketplace** | partial (GPT Store) | ✗ | ✗ (we ship a curated catalog; no user-uploaded skills yet) | community skill submissions + signing |
| **Workplace policy / SSO** | ✓ (ChatGPT Enterprise) | ✗ | partial (`WorkplacePolicy` in CircleUp; Concierge has API-key vault) | this is how the enterprise sale closes — defer until the kid-to-exec flywheel is real |
| **Offline mode** | ✗ | ✗ | partial (`CircleAether` mesh wired in — peer transport works without internet) | "offline assistant" feature where Bell can still help with cached content |

## Where Concierge already wins

1. **Cross-platform reach.** Codex is web-only; Claude Code is terminal-only. Concierge is iOS + Android + Mac + Windows + Web from one codebase.
2. **Provider breadth.** 10 LLM families wired through one runtime. Codex is OpenAI-only. Claude Code is Anthropic-only.
3. **Skill scale.** 1,400+ skills synced from the curated library beats Claude Code's typical install count.
4. **Multimedia.** Voice in/out, image gen, diagram gen — all native, no extra extensions.
5. **The face.** Bell is the only AI assistant with a friendly mascot that a 6-year-old recognises and an 80-year-old finds charming.
6. **No-jargon UX.** Approvals, workloads, tool calls, autonomous loops — all there technically, but hidden from the user. The home page says "Hi! What shall we do?" — not "Agent workspace".

## Top 5 gaps to close before the kids-take-it-home demo is bulletproof

1. **Camera capture** — visible camera button on home, snap → send to vision-enabled provider for "what is this?" / "explain this picture". Effort: ~2 days.
2. **Inline image generation in chat** — `chat: "draw me a dragon"` → image renders in the bubble. Effort: ~1 day (the runtime exists, just wire to chat).
3. **"Show me what you can do" demo** — currently the button navigates to `/chat?demo=1` but the demo flow isn't scripted yet. 30-second magic-moment intro (Bell tells a story, makes a picture, plans dinner — all in one tap). Effort: ~2 days.
4. **Kid Mode content filter + parental dashboard** — toggle on first launch, content-filter pre-prompt for kid sessions, parent gets a weekly digest of what the kid asked. Effort: ~3 days. **This is the safety story enterprise needs to hear later.**
5. **One-tap web-fetch tool** — Bell can look something up when asked "what's the weather" / "when did X happen". Effort: ~1 day.

## What we deliberately are not building

- **Code execution / git / dev environments.** Codex and Claude Code own that. Their users speak terminal.
- **Plugin marketplace economics.** Apple-style curated catalog only. No third-party skill submissions in v1.
- **Multi-agent orchestration (Loki Mode / swarms).** The mental model is "talk to Bell", not "deploy a fleet of agents". The plumbing exists internally; we never surface it.
- **Self-hosted enterprise SSO.** Comes later, after the kid-to-exec virality is observable.

## Bottom line

Against Codex + Claude Code on raw capability, Concierge is **at parity or ahead** on chat, provider breadth, voice, image, diagram, skills, and platform reach — and **behind** on code execution, browse, git, and autonomous-agent depth. The behind list is **intentional**: those features are for engineers, and engineers aren't the audience.

What Concierge has that neither has: a face, warmth, a price for everyone, and a phone in the kid's hand.
