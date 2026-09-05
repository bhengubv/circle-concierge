# Concierge — what is done and what is left

Supersedes the redesign plan, which is finished. Every unchecked item below is
something a person could pick up; every checked one was verified on the running
desktop app, not only by tests.

Desktop runs point until the product is complete. The handheld, wearable, web
and native Wear heads are built and stay in the tree, but they do not gate a
release.

---

## Done

### The redesign
- [x] One dark workspace — sidebar, thread, composer. No tab bar, no `⋯` menu
- [x] Settings as a panel over the work, not a page you travel to
- [x] Skills as a picker you switch on, not a catalogue you read
- [x] The seven rooms — Product, Engineering, Beyond Code, Business APIs, Roadmap, Release, Pricing
- [x] History, Diagrams, Images, Who is chatting, Help, About, Not found
- [x] API key editor, inside Settings under Keys
- [x] Every route reachable, every page fits its window, no horizontal overflow

### Form factors
- [x] Three recipes — desktop/web, handheld, wearable — as separate component sets
- [x] `WorkspaceBase` holds all behaviour; recipes are markup only
- [x] Form factor read from the viewport, not the OS, and switched live on resize
- [x] Mobile navigation — the sidebar as a drawer below 760px
- [x] Native Wear OS head (`Concierge.Wear`), because Wear OS ships no WebView
- [x] Android phone: status bar, top bar and composer insets

### The four priorities
- [x] **Web fetch and search** — approval-gated, SSRF-guarded, content labelled untrusted
- [x] **Session resume** — the thread, unsent text and active skills survive a restart
- [x] **Vision input** — image channel on `ChatTurn`, and attachments no longer decoded as UTF-8
- [x] **Process isolation** — the model runs in a child process; a native fault costs the sentence, not the app

---

## Next

Ordered. The first is a correctness problem; the rest are absences.

### 1. ~~The approvals queue is fake~~ — done
- [x] Wire the sidebar Approvals group to `InteractiveToolApprovalService`
- [x] Wire the `/approvals` room to the same source
- [x] Delete the hardcoded `ApprovalRequest` seed data in `ConciergeStateService`
- [x] Test: with nothing pending, the count is 0 and the room says so

> There are two approval systems and only one is real. Tool approvals render
> inline in the thread through `ApprovalPrompt`, which works. The sidebar and
> the room read two hardcoded entries and always show **2** — so a person can
> press Allow on something that never existed. In a product whose pitch is
> "it asks before it acts", that is the worst place to be wrong.

### 2. ~~MCP is a class, not a capability~~ — done
- [x] Somewhere to configure a server — `%LOCALAPPDATA%/Concierge/mcp.json` (Settings, under Keys or its own group)
- [x] Connect on start-up and surface failures the way a runtime does
- [x] Register its tools into `IAgentToolRegistry` so the model can call them
- [x] Show connected servers and their tools in Engineering

> `McpClient` is 153 lines and is never instantiated outside its own file.

### 3. ~~Custom skills need a way in~~ — done
- [x] Add a skill from the Skills panel — paste a folder, not an env var
- [x] Show where each skill came from, curated or yours
- [x] Test: a skill added at runtime reaches the catalogue

> 70 curated skills, and the only way to add your own is
> `CONCIERGE_SKILLS_ROOT` before launch.

### 4. ~~Long work has no spine~~ — done
- [x] Background tasks — a run outlives the screen that started it, and the sidebar says so
- [x] A visible plan for multi-step work — stated up front, ticked off per round
- [x] Permission modes set up front, not only per action — Plan only / Ask first / Act freely

### 5. Vision has nothing to see with
- [ ] A vision-capable runtime — the local model is text-only
- [ ] End-to-end test once one exists

---

## Known gaps, not scheduled

- [ ] `Concierge.Wear` has no tests — bUnit cannot render an Android UI
- [ ] iOS and macOS heads have never been built or run (Windows machine)
- [ ] Mobile and wearable are not release-gated, by decision
- [ ] Notebook support

---

## Not doing

- **The native model crash.** `mnn_llm_generate_stream_text` faults with an
  access violation. It is in the `CircleAI.Inference` package, upgraded
  separately. Concierge now survives it; it does not fix it.
- **`MainLayout`, `NavMenu`, `BottomNavLayout`, `AppMenu`, `Bell`.** These are
  the navigation model the redesign removed. Kept as shells for possible reuse
  rather than deleted.

---

## Standing checks

- [ ] 891 tests pass — `dotnet test Concierge.Tests/Concierge.Tests.csproj`
- [ ] Verified on the running desktop app, not only in tests
- [ ] `[skip ci]` in the HEAD commit before any push
