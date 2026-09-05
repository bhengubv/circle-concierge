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

### 5. ~~Vision has nothing to see with~~ — done
- [x] A vision-capable runtime — the three cloud runtimes declare `IVisionCapableRuntime`
- [x] Per-provider image encoding — Anthropic base64, OpenAI data URI, Gemini inline_data
- [x] Test: all three shapes, and a text-only turn keeps its plain string content

The local model is text-only and stays that way; the check the image channel
added had nothing to find, so every picture was refused. Live confirmation
needs a cloud key — the wire shapes are tested, the round trip is not.

### 6. ~~The model cannot find anything~~ — done

- [x] `list_files` — walks the workspace, skipping what the path guard would refuse
- [x] `search_text` — literal, with file and line number, binaries skipped
- [x] `edit_file` — one exact replacement, diff previewed, ambiguity refused
- [x] `read_file_lines` — a range of lines, counted from 1 as a person counts them
- [x] Test: 22 covering the four, plus the count and the names Engineering shows

Found by fixing the Engineering room. It listed `list_files` and `grep` as
things Concierge could run; neither exists. The model is given `read_file`
and can only use it if it already knows the path — there is no way to find
a file, and no way to search inside one. For an assistant whose whole job is
working in a repository, that is the largest single gap left.

Two of these are already written and simply never exposed:
`IAgentHarnessService.EditFileAsync` has a find/replace with a diff preview,
and `ReadFileWindowAsync` reads a range of lines. Both are unreachable from
a conversation. `write_file` is the only way to change anything, so the model
must reproduce a whole file to alter one line — the same fault the notebook
tools were built to avoid, sitting on every other file type in the repo.

### 7. The device is not something Concierge can act on

Every tool it has works on files, the web, or a notebook. Nothing reaches the
machine it is running on — no clipboard, no notification, no "open this", no
battery or network state. On a phone that gap is the whole product.

Prompted by OpenDroid (Apache-2.0, Kotlin/Compose): one agent loop and sixty
Android action executors. The code is not reusable here and the licence is not
the reason — it is Kotlin, and Concierge is .NET across five heads. What
transfers is the shape.

The translation, and the part that makes it work everywhere: **a device action
is another tool source**, the same seam MCP already uses. Each head contributes
what it can actually do; a head that cannot do a thing does not advertise it.
OpenDroid can assume Android. Concierge cannot, so absence has to be honest
rather than a call that fails.

- [x] `IDeviceCapability` — an action a head provides, with a runtime availability check
- [x] `DeviceCapabilitySource : IAgentToolSource` — publishes only what this device can do
- [x] Every acting capability approval-gated, with its arguments on the card
- [x] `device_info` and `network_state` — portable, true everywhere .NET runs
- [x] The MAUI set — clipboard read/write, battery, connection type, open link, flashlight.
      Written once, true on Windows, Android, iOS and Mac Catalyst
- [x] Engineering shows them, and shows what this device *cannot* do
- [x] Test: 18, including that the container can actually be built
- [ ] Android-only actions — send a message, place a call. Deliberately last: these are
      where "it asks first" has to be exactly right, and they need a device to test on

The thing worth keeping from the comparison is the thing OpenDroid does not do.
Its README describes no confirmation workflow for sending a message or making a
payment. Concierge asks before it acts, and that has to survive contact with a
catalogue of actions that touch the real world — which is where an assistant
that acts is most easily made dangerous.

### 8. ~~Three things taken from OpenDroid that were left behind~~ — done

The device capability layer took the shape and left most of the ideas. These
three are the ones worth having, all verifiable without a working runtime.

**Replanning.** `PlanProtocol` states a plan and nothing ever revises it. If
step two fails, steps three to six run anyway against an assumption that is no
longer true — or the loop stops at its round cap with no explanation. OpenDroid's
AgentLoop re-evaluates after each step. The plan strip already exists and ticks
steps off; what is missing is what happens when a step does not go the way it
said.

- [x] A failed step asks for a rethink rather than continuing past it — on the first
      failure, not the third
- [x] The revision is visible — the thread records it and the strip resets honestly
- [x] A plan that keeps failing stops: two failed rounds in a row, or four rewrites
- [x] And the strip stopped claiming progress nobody made — a round where every call
      failed used to tick a step off anyway

**Provider failover.** Twelve providers with automatic chaining there; here,
three cloud runtimes and a local one with no chaining at all, so a provider
having a bad afternoon is a dead turn. This is small and it is the difference
between an answer and nothing.

- [x] Try the next configured runtime when one fails
- [x] Say which one answered — the stored engine label credits whoever spoke
- [x] Never past a refusal, never after the first token, never off the device
- [x] And the sidebar stopped saying "on device" under a cloud provider

**Screenshot.** Concierge has vision input and cannot take a picture of its own
screen; there is not one reference to screen capture in the repository. It fits
the capability layer exactly, works on the desktop today, and makes the vision
work reachable without a person finding a file to attach.

- [x] `screenshot` as a device capability, approval-gated — it changes nothing, and
      what it produces goes to whatever is answering, which may be somewhere else
- [x] `CapturedImages` — a tool result is a string, so the picture rides on the next
      turn through the channel a person attaching a file already uses
- [x] Absent where a head cannot capture, like every other capability

Two ideas from OpenDroid are deliberately **not** on this list: driving other
apps through an accessibility service, and the payment actions. Both are where
an assistant that acts becomes genuinely dangerous, and both are things
OpenDroid documents no confirmation step for.

### 9. Design — say it, see it, point at it

Ours. Inspired by a handful of open projects that prove an agent can make
visual things; nothing taken from any of them. No components, no licences to
worry about, no per-repo shopping list.

**The bar, which is the spec:** a five-year-old and a ninety-seven-year-old can
both use it. That rules out, before anything is designed: syntax, modes,
timelines, node graphs, file names, panels, and every word a person would have
to look up. What both of them do naturally is say what they want, look at it,
and say "no, not like that". That is the entire interface.

**Not in scope:** models. CircleAI handles that, and on the right device one
model does everything. Nothing in this tranche is about inference, providers or
prompts. It is about the surface — which is the product.

- [ ] One surface: a canvas and the composer. No files, no timeline, no
      properties panel, no code view
- [ ] Describe → see → correct. Pointing at a thing and saying what is wrong
      with it is a first-class way to edit, not a fallback
- [ ] Undo that always works, shown as pictures of how it looked — not a list of
      changes. Every state you have seen is one tap from coming back
- [ ] Design acts freely. Being asked permission to try something is what makes a
      tool unusable for the people this is for; the approvals that stay are the
      ones that leave the canvas or leave the device
- [ ] Look and feel chosen from pictures, never from a document
- [ ] The plan in plain language, each step refusable before it happens — the
      readable version of a node graph, and it already exists
- [ ] Fits its window at every size, like every other panel. Scrollbars are hidden
      app-wide, so a surface that overflows loses content silently
- [ ] Degrades honestly: what a handheld shows, and what a watch shows instead
- [ ] Verified on the running desktop app, not only in tests

**The rule this surface must not break:** every defect worth fixing in Concierge
so far has been the same one — a screen asserting something untrue. An approvals
badge that always said two. A room listing tools that did not exist. "On device"
under a cloud provider. A plan ticking off steps that failed. A flashlight
offered on a desktop. A canvas has far more room to lie than a sidebar does, and
the whole value of the product is that you can see what it did and stop it.

### 10. The mesh had no radio

`NullMeshSender` reports no peers and refuses every send. Everything above it —
Aether identity, route store, DTN bundles with custody transfer, opportunistic
delivery — was complete and had nothing to talk over, so bundles were created
correctly and queued forever. Its own summary said it was waiting for "BLE,
Wi-Fi Direct, internet relay" to attach.

- [x] `LanMeshSender` — multicast discovery, length-prefixed TCP delivery
- [x] Peers expire; an unreachable peer is "not now", not an exception, so DTN
      keeps the bundle instead of losing it
- [x] Opt-in — attaching a radio opens a port on somebody's machine and puts
      traffic on their network, which a host should decide out loud
- [x] Test: a packet reaches another node, and an answer comes back
- [ ] Approvals as a bundle type, so the wrist can actually answer
- [ ] BLE or Wi-Fi Direct, for when there is no shared network

**The boundary, stated so it is not forgotten:** this transport does not
authenticate anything. Aether packets carry a signature and a nonce and
verifying them is the protocol's job. Anything on the same wifi can send bytes
to this port, so an approval must not ride on it until that signature is
checked — which is why the last two boxes are unticked rather than rushed.

**Not built further on purpose.** The mesh is a NuGet package and an upgrade to
it is in the pipeline elsewhere. This implements the published `IMeshSender` and
stops there.

---

## Known gaps, not scheduled

- [x] `Concierge.Wear` — the decision it makes is now `WatchFace` in Shared, and tested;
      the view building stays untested, because bUnit cannot render an Activity and never will
- [~] iOS and Mac Catalyst — both compile from this Windows machine
      (`dotnet build -f net10.0-ios`, `-f net10.0-maccatalyst`, both clean). Neither has been
      *run*: that needs Apple hardware, so nothing about how they look or fit is verified
- [x] Mobile and wearable are not release-gated — your decision, made and standing.
      Desktop runs point until the product is complete; the other heads stay in the tree
- [x] Notebooks — `read_notebook` renders cells rather than JSON, `edit_notebook` changes one cell
      and leaves the rest byte-for-byte. No kernel; running one stays `run_command`

---

## Not doing

- **The native model crash.** `mnn_llm_generate_stream_text` faults with an
  access violation. It is in the `CircleAI.Inference` package, upgraded
  separately. Concierge now survives it; it does not fix it.

  Checked again on 6 Sep 2026, twice in a row on the running desktop app.
  Every turn ends the same way: *"The model stopped unexpectedly. Concierge is
  still running — send the message again to try once more."* So **Concierge
  cannot yet hold a conversation**, and no amount of work in this repo changes
  that. What the run does confirm is the isolation: the parent process kept
  its PID and its window across both crashes, the child was replaced each time
  (9672 → 14260 → 5544), the thread was saved, and the composer came back
  ready. Before isolation this was an application-wide crash.

  Two smaller things it also showed: the send button is briefly disabled while
  the replacement child starts, which is correct but undocumented; and the
  retry the message invites does work — it fails the same way.
- **`MainLayout`, `NavMenu`, `BottomNavLayout`, `AppMenu`, `Bell`.** These are
  the navigation model the redesign removed. Kept as shells for possible reuse
  rather than deleted.

---

## Standing checks

Boxes that are re-checked per change rather than ticked once. All three hold as
of `80cda9c`.

- [x] 1089 tests pass — `dotnet test Concierge.Tests/Concierge.Tests.csproj`
- [x] Verified on the running desktop app, not only in tests — Engineering shows
      11 tools under their real names, no page overflow
- [x] `[skip ci]` in the HEAD commit before any push
