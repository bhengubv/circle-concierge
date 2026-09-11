# Concierge — what is done and what is left

## Context

The "one dark workspace" redesign this file used to hold is finished and on
`main`. What replaces it is the working list: **142 boxes ticked, 21 open, eight
partial**, ordered so the next person can pick one up.

**Thirty-six of those forty arrived at once**, on 2026-09-11, when the six
inspiration repos were cloned again and their feature lists written out in full as
item 17. Nobody has agreed to any of them and nothing there is scheduled — the
list exists so the choice is made with the real scope visible rather than from a
feeling about it. Before that section the open count was four, and all four were
other heads.

That count is counted, not remembered — `grep -c '^- \[x\]'` and `'^- \[ \]'`
against this file. It read "17 items done, 23 open" for some time and matched
neither the boxes nor the sections. A number in a header nobody re-derives is
the same defect as an approvals badge that always said two, which this file
argues against roughly every third paragraph. Re-count it when you change it,
or delete it.

Everything checked below was verified on the running desktop app, not only by
tests. That distinction earns its place — five defects in one week were
invisible to bUnit and only appeared when the app was actually looked at: a
settings panel cut off by 166px, a status bar left MAUI purple, a top bar
hidden under the system bar, a sidebar clipped by a fifth group, and a model
host that never loaded because nothing ran its hosted service.

Desktop runs point until the product is complete. The handheld, wearable, web
and native Wear heads are built and stay in the tree, but they do not gate a
release.

Mirrored at `circle-concierge/TASKS.md`, which is version-controlled so every
concurrent session sees the same list. **The two are not automatically kept in
step, and copying one over the other loses whatever the target had and the
source did not** — this Context section was destroyed exactly that way and
restored by hand. Edit both, or diff them before you copy.

**And edit this file by anchor, not by span.** Two completed entries were
silently deleted by a script that replaced everything between one item's heading
and the next one's: whatever sat in between went with it. Nothing failed, the
file stayed valid markdown, and the only reason it was caught is that the
checkbox count came out two short of what the header claimed. Since this file is
usually uncommitted while it is being worked on, git cannot give those back. The
count at the top is the tripwire — re-derive it after every edit.

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

**"The local model is text-only and stays that way" was the sentence here, and it
is now out of date rather than wrong-at-the-time.** `CircleAI.Inference` 3.3.0
carries an image channel: `ChatMessage.ImageBytes`, fed through
`mnn_llm_generate_with_image_stream_ex` by `KimiVlGenerator`. Concierge used none
of it, so the image channel on `ChatTurn` reached the cloud runtimes only and
every picture on the device was refused with "this model cannot look at
pictures".

- [x] Pictures reach the local model. A turn's first image is carried through to
      `ChatMessage.ImageBytes`, and a vision family is loaded through the generator
      that can see rather than the text one.
- [x] **Whether it can see is read off the model, never off its filename.** The name
      only decides which generator to build; `KimiVlGenerator.IsVisionCapable` is
      the native runtime's own answer, and that is what fills
      `SupportedImageMediaTypes`. A file named like a vision model that is not one
      is never advertised as one.
- [x] The composer reads the list, not only the interface. The local runtime
      declares `IVisionCapableRuntime` because it *can* see, and lists nothing while
      a text model is loaded — so checking the interface alone would have sent a
      picture to a Qwen model and said nothing about it.

**No vision model is on this machine**, so the round trip is still unconfirmed,
exactly as it is for the cloud runtimes with no key. The wiring is tested; the
looking is not. Live confirmation needs a cloud key or a vision model.

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

### 7. ~~The device is not something Concierge can act on~~ — done bar the phone actions

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
- [x] Android-only actions — send a message, place a call. Written, and **the
      decision that made them safe enough to write is that neither of them sends
      anything.** `send_message` opens the messaging app with the message already
      written; `place_call` opens the dialler with the number already in it. A
      person presses send. A person presses call.

      The obvious build is the wrong one. MAUI *can* send an SMS silently on
      Android with the right permission, and a capability that did would be one
      approval card away from a model that misread a name texting the wrong
      person — with the card showing a phone number that looks like any other
      phone number. Composing means two gates, and the second is the operating
      system's own, in the app somebody already recognises, showing the message as
      it will actually arrive.

      A number is checked before it goes anywhere, and **the characters it refuses
      are the point**: `*` and `#` make a USSD code, and some Android diallers act
      on one the moment they receive it rather than waiting to be dialled. Codes
      exist that wipe a handset. A model writing a number out of a sentence
      somebody sent it must not be able to reach that.

      Both absent off a phone, like every other capability that cannot do its job
      here. OpenDroid's payment actions stay absent entirely.
- [x] **Run on a real Android runtime — done, on the emulator, and it found two
      things.** `concierge_phone` (Android 14, x86_64), deployed with
      `dotnet build -t:Run`, which is the part that matters: an APK installed by
      hand with `adb push` + `pm install` starts and then crashes, because a Debug
      build depends on fast-deployment pieces the MSBuild targets deliver and a
      hand install does not. That crash looked like an app defect and was not one.

      Verified on screen: the handheld layout renders, insets are right, Engineering
      lists 21 tools, and **`send_message` and `place_call` appear under "Not on
      this device"** — the emulator has no SIM, so `Available` is false and the
      model is never offered them. "Concierge is never offered these on this device,
      so it will not try them", in the room's own words. That is the capability
      design working end to end on a real runtime rather than in a container test.
- [x] **Every folding section in every room was unopenable, and nobody had noticed.**
      `RoomSection` bound `@onclick` *and* `@onpointerup` to the same Toggle, and one
      press fires both — so it opened and shut again in the same press. Thirty-five
      elements across seventeen files had the same pairing: saving a key twice,
      fetching a diagram twice, answering an approval twice. Found by tapping "Can
      change things" on a phone and watching the row light up and stay closed.

      The pairing existed for a comment repeated in three files — "MAUI's
      BlazorWebView drops touch-originated clicks on buttons intermittently" — with
      no measurement recorded anywhere. So it was measured: fourteen alternating
      taps on the permission-mode buttons in the Android head, state read back from
      the accessibility tree after each. **Fourteen of fourteen registered, none
      dropped.** The honest limit is that this is one emulator and one WebView
      build; it does not prove clicks are never dropped, but the workaround was
      costing a real reproducible defect to prevent one nobody had shown.

      And the measuring was wrong first, as usual: the first run reported 12 of 12
      dropped because the parser reading the accessibility tree matched nothing.
      The buttons had worked the whole time. Fifth time in this file's history that
      the measurement, not the thing measured, was the broken part.

      3 tests hold the rule: nothing answers one press twice, nothing responds to a
      pointer alone (that would be an Approve button a keyboard cannot press), and a
      folding section opens on one press.
- [ ] Run them on the handset itself. A Redmi 12 (`22101316UG`, Android 14,
      arm64-v8a) is paired and connected over wireless debugging, and the app builds
      and signs for it. Installing is refused with `INSTALL_FAILED_USER_RESTRICTED`
      by MIUI's own install policy — **Developer options → Install via USB** governs
      adb installs over any transport, wireless included. One toggle on the handset.
      That is the only step left, and with a SIM in it the two capabilities move from
      "Not on this device" to "Asks first".

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

### 9. ~~Design — say it, see it, point at it~~ — built; three open, two need a model

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

- [x] One surface: a canvas and the composer. No files, no timeline, no
      properties panel, no code view — and it takes the middle of the workspace
      rather than opening a tab, so the sidebar and composer stay put
- [x] Describe → see → correct. Clicking a thing on the canvas names it ("The
      title"), and the next sentence has a subject
- [x] Undo as pictures of how it looked. Every state is kept whole, so going back
      is choosing one rather than reversing anything
- [x] Design acts freely — no prompt, no "are you sure", no save button
- [x] Look chosen from six pictures. No colour picker, no font name, no file
- [x] Fits at every size. Measured: in a 479px window the canvas was getting 147px
      against 111px of strips, so the strips shrink and the canvas keeps the room
- [x] Works with no engine at all — the ordinary sentences are understood without
      a model, which is what makes the surface usable on this machine today
- [x] Verified on the running desktop app: said three things, pointed at the
      title, made it bigger, changed the look, went back by picking a picture
- [x] The composer stops lying while the canvas is open — placeholder, permission
      modes, engine name and paperclip were all describing a conversation that was
      not happening
- [x] Closing the canvas puts it away instead of throwing the design out
- [x] And closing the *app* no longer does either. `FileDesignStore` writes the
      design as it changes and puts it back when the canvas opens. Built because
      `DesignDocumentFormat` — added the same morning, precisely to write a design
      down — was referenced by nothing: a seventh instance of this file's
      signature defect, and the first one that was mine.
      Two decisions stated rather than left to be discovered. **Only the design
      comes back, not its thirty moments of history** — writing all of them on
      every keystroke would make a canvas a disk benchmark, so undo stops at the
      moment the app opened. And **the file is written beside and moved into
      place**, because a save interrupted by the app closing would otherwise leave
      half a file where the design used to be; losing the last change is
      recoverable, losing the design is not. A design that cannot be read never
      blocks the canvas — it opens blank and says why, since refusing to open
      leaves somebody with no route back to a working surface and the bad file is
      still on disk either way. 10 tests.
- [x] The handheld can reach it. Every other piece was wired there — placeholder,
      hidden paperclip, suppressed permission modes — and there was no control that
      could ever open it, so all of it was dead code that looked finished
- [x] Pictures on the page. The paperclip was hidden in Design because it silently
      did nothing; it puts a picture on the canvas now, carried as a data URI so
      the design is genuinely portable rather than a reference to one machine
- [x] Verified on a handheld at 504px: canvas 393 of 680, nothing overflows,
      drawer closes behind you
- [x] What a watch shows instead of a canvas: **nothing, and the reason is not the
      screen.** Decided and written down as `WatchSurfaces.Design`, with a test, so
      adding it later is a deliberate act rather than something that happens
      because nothing said not to.

      A watch could carry the useful half of designing — "no, not like that" —
      without carrying the canvas at all. Glancing at your wrist, seeing what
      changed and saying no fits the product's actual claim better than any attempt
      to draw on a 192dp face. That is the screen to build: the last change, plus
      undo.

      It is not built because the correction has no way home. Answering on the
      wrist needs approvals to ride the mesh, which is held behind packet-signature
      verification — an unauthenticated channel would let anyone on the café wifi
      say "allowed". So a design screen today is a button that appears to work and
      does not, and `WatchFace` already refuses to ship exactly that: its decisions
      are held per session rather than persisted, for this same reason.
- [x] A model driving the canvas — the same edits as agent tools, so "make it feel
      like a school newsletter" reaches it. **Done, and it was the seam the whole
      product turns on rather than one more feature.**

      What was actually there: `SendAsync` returned early whenever the canvas was
      open, so a sentence went to `DesignSpeech` and never to a model. No tools, no
      plan, no skills, no approvals. The comment above it read "a model still
      handles everything this cannot" — and nothing did. There was no
      fall-through and there never had been. Fifth comment in this file's history
      found describing behaviour that did not exist, and the one that kept Design
      outside the harness entirely.

      `DesignToolSource` publishes the canvas as seven tools — describe, add,
      change, remove, look, making, go back — and `DesignWorkbench` is the seam
      that hands the live session over when the canvas opens and takes it back
      when it closes. Nothing is published while no canvas is open, because a
      model offered `design_add` against a chat would use it and report a heading
      added to something nobody can see. An ordinary sentence is still applied
      instantly and locally, which is what makes the surface feel like a pen;
      everything else now falls through to the model.

      **These tools do not ask permission, deliberately.** Everywhere else acting
      asks first, because acting on files is hard to reverse. On a canvas, asking
      is what makes the tool unusable for the people it is for, and going back is
      free because every state is kept. Applying the file rule here would be
      importing a rule from a different problem.

      This was blocked on "a working runtime" this morning and was unblocked by
      the prefix-cache fix earlier the same day. 16 tests. Registered for the
      desktop head in `MauiProgram`, **not yet verified by a desktop build** — the
      running app holds the binaries.

      **A trap worth writing down, because it cost 76 tests here.** A nullable
      `[Inject]` property is *not* optional. Blazor throws when the service is
      missing whether or not the type says `?`, so `[Inject] Workbench? ...` with
      a comment calling it optional made every workspace fail to render on every
      host that had not registered it. Optional means
      `Services.GetService(typeof(T)) as T`, which returns null. The irony is on
      the record: the comment asserted behaviour nobody had checked, inside the
      change whose whole purpose was removing a comment that asserted behaviour
      nobody had checked.
- [x] The plan in plain language for a multi-step design change. One is driving it
      now, so this stopped being hypothetical the moment the canvas joined the
      agent loop — and it immediately exposed a hole I had put there the same day.

      `StepExpectation` knew about files and commands and nothing else, so a design
      step could state itself but could not say what it would leave behind. The
      strip therefore fell back to ticking on any success — the exact behaviour
      `Judge` was written to stop, walking back in through a medium it had never
      been taught about. "Make it feel like a school newsletter" is a look, a
      heading and three sizes, and all three would have ticked off the moment the
      model did anything at all.

      `StepOutcome.CanvasChanged` closes it, and excludes `design_describe` on
      purpose: looking at the canvas does not keep a promise to change it, for the
      same reason reading a file does not keep a promise to write one. 22 tests.

**The rule this surface must not break:** every defect worth fixing in Concierge
so far has been the same one — a screen asserting something untrue. An approvals
badge that always said two. A room listing tools that did not exist. "On device"
under a cloud provider. A plan ticking off steps that failed. A flashlight
offered on a desktop. A canvas has far more room to lie than a sidebar does, and
the whole value of the product is that you can see what it did and stop it.

### 10. ~~The mesh had no radio~~ — radio attached; approvals still cannot ride it

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
- [ ] Approvals as a bundle type, so the wrist can actually answer. **Still blocked,
      and now checked rather than inherited.** This sat here as "held behind packet-
      signature verification, which is in the pipeline elsewhere" — a claim nobody
      had tested against the package. Reflected over `Aether.Core` 1.0.1, the version
      actually restored:

      * `MeshPacket.Signature` and `MeshPacket.PacketNonce` exist as **fields with a
        getter and a setter and nothing else**. No public API signs a packet, and
        none verifies one.
      * `AetherTag.Verify(string, byte[])` binds a UHID to a public key. That proves
        an id was derived from a key — not that a packet came from whoever holds it.
      * `IRouteReplyVerifier` exists, and the only implementation the package ships
        is `AcceptAllRouteReplyVerifier`, which says what it does in its name.
      * `AetherTelemetry` counts `SignaturesValidated` and `SignaturesRejected`, so
        signing is coming upstream — it is simply not here at 1.0.1.

      And `LanMeshSender.AcceptLoopAsync` deserialises a `MeshPacket` straight off
      the socket and raises `PacketReceived` with no check at all, which is exactly
      what the boundary note below says.

      So the blocker is real and is **stronger** than it was written: it is not that
      signatures go unverified, it is that there is no way to sign or verify one.
      Putting approvals on this would let anyone on the same wifi say "allowed".
      The mesh is a NuGet package and signing belongs upstream in it, not
      reimplemented here.
- [ ] BLE or Wi-Fi Direct, for when there is no shared network

**The boundary, stated so it is not forgotten:** this transport does not
authenticate anything. Aether packets carry a signature and a nonce and
verifying them is the protocol's job. Anything on the same wifi can send bytes
to this port, so an approval must not ride on it until that signature is
checked — which is why the last two boxes are unticked rather than rushed.

**Not built further on purpose.** The mesh is a NuGet package and an upgrade to
it is in the pipeline elsewhere. This implements the published `IMeshSender` and
stops there.

### 11. ~~run_command had no boundary~~ — confined on Windows, with one named gap

`Concierge.Shared.Sandboxing` held a complete Windows job-object sandbox — memory
cap, process cap, and KILL_ON_JOB_CLOSE — referenced nowhere, while the one
method in the product that starts a process started it with nothing at all. The
Engineering room says "what Concierge can do to your machine" above a list that
includes `run_command`, and the answer was: whatever it likes.

- [x] Every command runs inside a fresh job object — capped memory, capped
      process count, killed when the command ends
- [x] Per command rather than per app, so one command finishing cannot kill
      another command's processes
- [x] A sandbox that refuses to build fails the call rather than running the
      command unconfined after deciding it should not be
- [x] Engineering reports what confines a command, in the platform's own words
- [x] The process is created inside the job rather than assigned to one after it
      starts — CreateProcess with PROC_THREAD_ATTRIBUTE_JOB_LIST, which removes the
      window rather than narrowing it
- [x] Measure why `cmd`'s `start /b` gets out, instead of theorising about it a
      third time. Querying the job for its process list the moment the parent exits:
      `in-job survivors=0`. The job is not failing to kill the child — the child was
      never in the job. So the fix belongs in how the process is created, not in the
      job's lifetime, and every explanation offered so far looked at the wrong half
- [x] **There is no escape. There never was.** Four rounds of measurement, and the
      fault was in the measuring every time.

      Two early versions of the test were discarded for measuring their own
      vehicle — `timeout` fails instantly with redirected handles, an unquoted `&`
      binds to the outer shell. The third fixed the vehicle, waited nine seconds,
      found the child's marker file and concluded a detached child had outlived
      the job. That conclusion stood in this file as a known gap.

      It was wrong. Timing the call: `RunAsync` takes **7,141ms** for a child that
      sleeps seven seconds, and enumerating **every process on the machine** two
      seconds later finds nothing new alive. `start /b` asks for a child with no
      new window, but the command is created with `CREATE_NO_WINDOW` and has no
      console to share, so cmd runs it **synchronously**. No detached process is
      created at all. The marker the test kept finding was written during the call
      it believed had already returned, while the job was still open.

      The test now asserts what is true — nothing a command starts outlives it —
      and carries the whole history, because the lesson is not about job objects.
      A marker file proves something ran. It proves nothing about *when*, and four
      rounds of confident reasoning were built on it. Whether some other route
      genuinely detaches is unknown and unmeasured; if one is found it gets its own
      test.

      Also killed on the way: a speculative fix. Believing the pipe was holding the
      call open, I bounded the drain after process exit — a change that would have
      risked truncating real output to solve a problem that did not exist. Reverted
      before it went anywhere near the suite.

- [x] **Linux is confined now.** `LinuxNamespaceSandbox` wraps a command in
      `unshare --user --map-root-user --pid --fork --kill-child` and `prlimit`, so
      it gets the same two guarantees the Windows job object gives, at the same
      numbers: nothing it starts outlives it (`--kill-child` is the direct
      equivalent of KILL_ON_JOB_CLOSE), and it is capped at 512MB and four
      processes. No package to install — both tools are already on an Ubuntu.

      **Measured on Linux before being claimed**, because this file's history is
      four rounds of confident sandbox reasoning that were all wrong. Under WSL
      Ubuntu-24.04, kernel 6.18: the wrapped command runs; a child started with
      `nohup … & disown` does **not** survive it; a 900MB allocation under the
      512MB cap dies with MemoryError; and an ordinary pipeline still works, so the
      four-process cap does not break real commands. 15 tests check that the
      command line which produced those results is the one this builds.

      **It does not restrict the filesystem, and does not claim to.** That is
      Landlock, which the child has to ask for itself between fork and exec —
      somewhere managed code cannot reach without a helper binary per
      architecture. So it reports `Process`, exactly as Windows does. Android is
      Linux and is deliberately excluded: a child there runs under the app's own
      uid, and wrapping it would report a boundary the platform does not give.
- [ ] macOS, Android and iOS confinement. Still "not confined", still honestly —
      and iOS forbids child processes at all, so that one is a fact rather than a
      gap.
- [x] Scrub the child's environment. Done: `ConfinedProcess` built the child's
      environment instead of passing `nint.Zero`, which had been handing every
      command everything this process holds — on a developer's machine that
      routinely means a GitHub token, NuGet credentials, cloud keys. Concierge's
      own keys live in a file rather than the environment, so this was leaking the
      operator's secrets rather than ours, which makes it no less real.
      **A denylist, not an allowlist, and that is the weaker choice made
      deliberately.** An allowlist would have to name every variable a build tool
      needs — PATH, TEMP, USERPROFILE, VSINSTALLDIR, half of MSBuild's surface —
      and the first one missed turns a working command into a mysterious failure.
      The denylist fails the other way: it can miss a secret nobody thought of. So
      this reduces blast radius, it does not guarantee anything, and the comment in
      the code says so. Two tests: a token is invisible, and `PATH` still is not.
- [~] Cap what a command may write. **Done on Linux. On Windows there is no route
      left, and that is now a decision rather than an absence.**

      Two ways existed. A container is one, and **containers are ruled out for this
      project entirely** — said plainly on 2026-09-11, so it is not an option to be
      weighed again. The other is a filesystem filter driver, which means writing,
      signing and installing a kernel driver on somebody's machine to stop a command
      filling a disk. That is wildly out of proportion to the problem.

      So this stays open only in the sense that Windows cannot do it, not in the
      sense that somebody should try. Whoever reads this next should not go looking
      for a third way; there is not one that is worth what it costs.
      `prlimit --fsize` sets RLIMIT_FSIZE and the kernel enforces it: writing 400MB
      under a 256MB cap stops at exactly 268,435,456 bytes, measured. It caps any
      one file rather than everything written in total, so a command determined to
      fill a disk can still write many files — it stops the ordinary accident, a
      runaway log or a bad download, which is what this was ever going to catch.

      Worth noting how this was missed: the entry said "not doable", which was true
      of job objects and then got quietly generalised to every platform without
      anybody checking. Linux had it all along. The original reasoning follows,
      and it still stands for Windows. **Not doable with a job object, and the entry
      was written without checking.** Job objects cap memory and process count;
      Windows has no per-process disk quota to set. OpenSandbox gets its
      `UpperMaxBytes` from an overlay filesystem under bubblewrap — the cap is a
      property of the overlay, not of the process — and there is no overlay here.
      Doing it properly on Windows means a filesystem filter driver or running the
      command in a container, both of which are far larger decisions than the line
      that asked for it. Left open with the reason, rather than closed with
      something that looks like a cap and is not.

**Read against OpenSandbox (Alibaba, Apache-2.0), and a claim of mine corrected.**
I said three times that it does not confine local processes. It does —
`components/execd/pkg/isolation/` uses bubblewrap, seccomp, Landlock and
capability-dropping, with no Docker. Docker is a requirement of their *server*,
not their isolation layer, and I had read the README and the SDK rather than the
code. The same mistake as item 15, made again while item 15 was open on screen.

Most of theirs does not apply — multi-tenancy, Kubernetes, pooling, egress
proxying — because ours confines one command, for one user, on one machine. What
does apply is the two boxes above, plus the shape: their hardening, Landlock,
seccomp and eBPF layers each switch on independently and each default to off,
which is how the four platform gaps could ship a floor before the filesystem
work. Their Landlock implementation is a usable reference for the Linux box.

That last gap is a passing test rather than a failing one: it asserts the escape
happens, so the day somebody closes the race the test goes red and says to assert
containment instead. A limitation nobody can read is the same as silence.

### 12. ~~Two more things written and never reached~~ — done, hooks included

`FileTodoStore` was registered in DI and nothing resolved it. `ProcessHookBridge`
was complete and never constructed, because what it was missing was
configuration. Same seam as MCP, `edit_file` and the sandbox.

- [x] `todo_read` and `todo_write` — a working list that outlives the turn. The
      plan strip says what is happening now and is cleared when the turn ends;
      this says what is still outstanding on Monday
- [x] Neither interrupts. Reading your own notes is not an act, and writing them
      changes nothing anybody else can see
- [x] Hooks read from `%LOCALAPPDATA%/Concierge/hooks.json`, off unless somebody
      wrote one. A `preToolUse` hook can refuse a call, which is a safety control
      a person writes for themselves — they know what their machine holds and
      Concierge does not
- [x] A broken hooks file runs nothing and says why. Failing closed matters more
      here than for MCP: half-running would give somebody a control they believe
      is in force and is not
- [x] Registered hooks shown in Engineering, beside the MCP servers — including
      when there are none, because "no hooks" and "hooks I have forgotten about"
      look identical from outside and this is the room where you check which

**Two deliberately left unwired, and why.**

`ITerminalRuntime` keeps a shell alive across calls, which is genuinely useful
and directly undoes the confinement added in tranche 11: a long-lived shell is
exactly the thing a per-command job object cannot contain. Wiring it the week
after adding the boundary would be taking it away again quietly. It needs its own
answer to containment first.

`PluginHost` loads assemblies into the process with full trust. It is well built
— collectible load context, cooperative unload — and "any DLL on disk can add
tools" is a decision to make deliberately rather than by finishing a wiring job.

### 14. Five renderers, one document

**The strategy line this section opened with was wrong, and it was wrong for
months.** It read: *"Matching all six inspirations feature for feature, which is
the strategy: parity is table stakes, and the UX is what wins on top of it."*

That was never achievable and was never measured. The six were cloned again on
2026-09-11 and counted:

| Repo | Lines | What it actually is |
| --- | --- | --- |
| Pascal | 516,762 | Architectural 3D editor — walls with mitering and CSG cutouts, slabs, ceilings, roofs, levels, zones, 3D scans, spatial grid, plugin registry, MCP server, CLI |
| Diffusion Studio | 79,403 | Agent-native video editor — cuts, filler-word removal, word-level subtitles, colour grading, motion graphics, generative video and voiceover, scene search, transcription |
| OpenMontage | 152,995 | 12 pipelines, 52 tools, 500+ skills, Piper TTS, Remotion and HyperFrames compositions, archival footage providers |
| Antra | 39,182 | Music library manager — seven streaming sources, ISRC exact matching, hi-res awareness, dedup, discography download, scheduled sync, audio analyzer |
| AniGen | 25,951 | SIGGRAPH 2026 research — one image to a rigged mesh with skeleton and skinning weights |
| open-design | 1,702,351 | Six artifact types, desktop app, Docker, 16+ CLI integrations, 248 skill files, 298 design briefs |

**2,516,644 lines against Concierge's 61,232.** The file also said the six were
"248,000 lines", which is out by a factor of ten and is the only number here
anybody had written down.

So the honest strategy, replacing the one above: **this is not a smaller version
of any of them, and matching them feature for feature is not the goal.** What
Concierge has that none of the six has is one document across five media on one
agent loop, with approvals, local-first, on five heads. Pascal is an editor.
Diffusion Studio is a runtime. open-design is an agent shell with no document
model at all. The thing to protect is the harness and the bar — a five-year-old
and a ninety-seven-year-old — not a feature count.

**And AniGen is now answered rather than open.** Nothing was ever taken from it
and no reason was recorded, which sat here as a hole. It is a machine-learning
research pipeline that turns a photograph into a rigged 3D asset. There was
nothing applicable to take short of shipping a model. That is a finding, not a
gap.

**What each of these five renderers is, said plainly, so nobody reads "parity"
into them again:** a page, a deck, a slideshow with timing, a room of primitives,
and a running order. Each is the thing a person can use without learning a
cockpit. None is a competitor to the product it was inspired by.

**One thing worth taking, found by cloning them rather than remembering them.**

- [x] **Let a model look inside a media file.** Done. Diffusion Studio ships media
      inspection as a first-class idea — `probe`, `grab`, `filmstrip`, `waveform`,
      `transcribe`, `listen` — framed in its own README as "the inspection tools an
      agent needs to work with media it cannot watch". That sentence is the whole
      argument, and it named a hole here exactly.

      `MediaLook` and three tools, all read-only, so none of them asks — the same
      rule `read_file` follows. `media_facts` reads how long a file runs and what
      streams are in it, from the encoder's own report rather than a second parser
      of ours. `media_is_silent` answers the question that actually comes up — a
      track that downloaded wrong, a shot recorded with the microphone muted, both
      of which look entirely normal in a file listing. `media_frames` takes frames
      from across a video and hands them over as **one** strip rather than several
      pictures: a model given eight separate frames spends eight times the context
      and still has to work out the order. It rides the same `CapturedImages`
      channel `screenshot` uses, and is absent on a head that keeps none, because a
      tool that produces a picture nothing can carry reports success and shows
      nobody anything.

      A number a model asks for is bounded rather than trusted — two hundred frames
      becomes twelve. Absent entirely where there is no encoder.

      16 tests, all real runs against files the encoder makes, and **one of them is
      a tripwire that goes red when there is no encoder** — applied before the
      mistake could repeat, because four export tests stood down on exactly that
      and reported success for weeks.

      Concierge has vision input, an encoder already wired, and **no way for a
      model to learn anything about an audio or video file at all.** Told "the
      second shot is too long", it cannot check. Handed a track, it cannot tell
      whether the file is silence. It can put media into a design and export it,
      and it is blind to everything in between.

      What is cheap here, because `FfmpegMediaExport` already finds and runs the
      encoder: how long it is, what is in it, a frame or a row of frames as
      pictures the vision channel can already carry, and whether an audio file is
      silent. Read-only, every one of them — the same reason `read_file` does not
      ask and `write_file` does.

      What is **not** cheap and should not be assumed into scope: transcription and
      "ask a model about this footage" both need a model that can hear, which is a
      different decision. Named here so the small half does not quietly drag the
      large half in behind it.

The document knows nothing about how it is drawn, so five media is five files.

**A correction.** This was justified by a claim that each inspiration welds its
renderer to its document, so none can become another. Having since cloned and
read them rather than their READMEs: false. Pascal's core carries an architecture
test that fails the build on a runtime `three` import — "core is pure logic, no
Three.js, no rendering". Diffusion Studio's runtime says of itself "headless, no
DOM, no solid-js", with encoding and reconciling in separate packages. They do
exactly what this does.

So the separation is not a differentiator; it is the ordinary right answer, and
the argument is the bar, not the architecture.

- [x] `DesignMedium` on the document, and frames — the sequence a deck, a video, a
      space and a running order all are
- [x] **Slides.** One filling the frame, arrow keys, and it prints one slide to a
      sheet — so a deck becomes a handout with no export step
- [x] **Video.** Shots with durations that play and loop, sound tied to its shot.
      No timeline: a progress bar that says where you are and cannot be dragged
- [x] **Space.** A room of solids on a floor, turned with arrow keys. CSS
      transforms, so no dependency and nothing to install
- [x] **Sound.** Tracks with real playback and a play-everything button. No
      waveform, no faders, no stacked tracks
- [x] Changing what you are making throws nothing away — the same words and
      pictures, drawn differently
- [x] The composer's invitation follows the medium: on the slide, in the shot,
      in the room, to hear
- [x] **Every medium can be handed to somebody now.** `design_save` wrote only
      `.m4a` and `.mp4` until 2026-09-11 and refused a page, a deck and a room on
      the grounds that they were "printed from the page itself" — so three of the
      five media could not be given to anybody at all except through a browser
      print dialog, by hand, on a head that has one.

      Cloning the six made the cost plain: open-design's entire pitch is "real
      files, HTML/PDF/PPTX/MP4 export", and Concierge could produce a file for two
      media out of five.

      A page, a deck and a room now save as one HTML file, and it costs nothing —
      which is the point rather than an excuse. The renderer already emits a
      complete standalone document with its look, fonts and pictures inlined as
      data URIs, because it has to stand alone inside a srcdoc frame. **The file
      that opens in a browser is the file that was on screen** — not an export of
      it, the same bytes. It needs no encoder, so a machine without ffmpeg can
      still hand somebody a page, which is most of what a person makes.

- [x] **PDF and PowerPoint.** Both written by hand, no dependency added for either.

      A `.pptx` is a zip of XML, so `DeckExport` writes the smallest set of parts
      PowerPoint will open: content types, a presentation, a master, a layout, a
      theme carrying the design's own colours, and a slide each. The reason it is
      worth the XML rather than exporting a picture: **a deck that leaves as a
      picture is a deck nobody can change**, and somebody who needs to fix one word
      has to come back and ask.

      `PdfExport` lays the document out itself from the same tree the renderers
      read. The obvious build is to drive WebView2 headlessly and get an exact copy
      of the canvas; this does not, and the reason is that it would work on Windows
      on the desktop and nowhere else. Type is one of the fourteen standard fonts so
      nothing is embedded, and wrapping uses Helvetica's real character widths —
      guessing an average gives lines that overrun on capitals and stop short on
      narrow letters, which shows up on exactly the words people notice. Pictures
      ride as JPEG, which a PDF carries as-is; a PNG is converted by the encoder
      when there is one and **left out when there is not** rather than embedded as
      bytes a reader will show as noise.

      **What it costs, and it is not hidden:** a PDF here is not a picture of the
      canvas. Words, colours, pictures and order carry; exact spacing and anything
      CSS does that this does not know about do not.

      Verified outside our own code: Windows' own OPC reader — the one Office uses
      — opens the .pptx and enumerates all fourteen parts, and Chromium's PDF viewer
      renders all three pages of a test deck. Two defects the first real file
      showed: a start-of-frame marker at the very end of a JPEG was skipped by an
      off-by-one, and "Concierge — Q3" came out as "Concierge  Q3" because the em
      dash was dropped, leaving a double space that reads as a typo. Typographic
      characters become their plain equivalents now — the reader loses the
      typography and keeps the sentence. 28 tests.
- [x] Encoding. Done, and it produces real files: `FfmpegMediaExport` concatenates
      a running order into one `.m4a`, and turns shots into an `.mp4` with each
      shot held for its own length, the design's own colours behind it and its
      words on it. Timing comes from `DesignTiming`, so a delay and a rate are in
      the file rather than only in the preview. `design_save` puts it in Music or
      Videos and never writes over anything — a name already taken gets a number,
      which is how a tool that leaves something on a real disk keeps the same
      promise the undoable ones keep by being undoable.

      **The honest limit:** this is a slideshow with timing, not a recording of
      the canvas. The canvas is HTML, and turning HTML into frames needs a browser
      driven frame by frame — a much larger piece of work. So the words and the
      ground are right and the layout is the encoder's.

      **And the tests were green while proving nothing.** Four end-to-end exports
      returned early on `if (!EncoderHere) return;` and reported success, because
      installing the encoder prints "restart your shell to use the new value" and
      the test host still had the old path. Nine passes were five real tests and
      four no-ops. Two fixes, and the second matters more: `Find()` now also looks
      where an installer actually puts one, and a single test goes **red** when
      there is no encoder, so a run says which it was instead of looking identical
      either way. Eighth instance of this file's signature defect and the first
      where the thing asserting something untrue was the test suite.

      Two real defects surfaced the moment they genuinely ran. `CardOf` discarded
      the encoder's result, so a card it refused to draw came back as a path to a
      file that was never written and failed several steps later as "No such file
      or directory" — the symptom, three calls away from the cause. The cause was
      that `drawtext` asks fontconfig for a default face and Windows has none, so
      every card with words on it failed while blank ones came out fine. A font is
      named outright now, and a shot whose font cannot be found keeps its ground
      and loses its words rather than losing the film. Escaping the words by hand
      was tried and is not winnable — they cross two parsers, and `Act 1: "the
      fall" \ 50% off` beat it — so the words go to the encoder in a file, where
      there is no syntax to get wrong. 20 tests.
- [x] A real 3D engine. Done: three.js, MIT, **carried in the repository** rather
      than fetched from a CDN, because a design surface that needs the internet to
      draw a box is not a local-first product. 2.1MB of vendored source, which is
      the price.

      **Drawn twice, and the ordering is the design.** The room is written out
      first as the CSS version it has always been, and the engine then draws the
      same room over the top and hides the flat one only once it has actually
      succeeded. So the fallback is the default and the upgrade is what has to
      work — no WebGL, no GPU, a webview that will not run modules, the file
      missing, and somebody is looking at the room this always drew. The obvious
      build is the other way round, and it makes the failure path the one nobody
      ever runs and the one that greets somebody on an old laptop with an empty
      rectangle.

      Meshes, two lights (one that casts and one that fills — a single hard light
      makes every unlit face pure black, which reads as a hole), soft shadows on a
      floor that receives them, and four shapes: box, sphere, cylinder, cone.
      Picking is a raycast that writes the same `data-node` attribute every other
      thing on every other surface carries, during capture, so the click handler
      that already existed needed no changes at all — one contract, five surfaces.
      **Still no orbit-by-dragging.** An engine makes that gesture trivially
      available, which is exactly why it is worth refusing again.

      **Verified on the running app, and it found four things tests had not.**
      One: the room went over as `Things` and the script read `things` —
      undefined is not an error in JavaScript until you ask it for a length, and a
      single `.catch` on the promise chain then made our own bug look exactly like
      a machine without a GPU. The rejection handler is now the second argument to
      `then`, so it sees only the import failing, and a build that throws says so
      out loud. Two: **there was no sentence that put anything in a room at all** —
      no word anywhere mapped to a solid, so the entire medium was reachable only
      by a model calling a tool, on a surface whose whole claim is that you say
      what you want. Three: everything a person added landed in the middle of the
      floor, so the second thing was inside the first and looked like nothing had
      happened. Four: pointing at a block said **"The page"** — the component kept
      its own list of names with no case for a solid, a sound or a frame, so
      pointing at a track or a slide had been saying the same thing all along. It
      uses `DesignSpeech.NameFor` now: one vocabulary, which is the rule the tools
      were written under and the component was not.

      All four were invisible to 1,353 passing tests and took one look at the
      screen. 33 tests now, most of them on the fallback and on the two files
      agreeing about spellings.
- [x] Antra's half of Sound: tagged files out, artwork and lyrics. Done — **and
      "Antra's half" overstates it, which was checked on 2026-09-11 by cloning
      Antra and reading its own feature list.** Antra is a music library manager:
      seven streaming sources, ISRC exact matching, explicit-version preference,
      hi-res awareness, deduplication, artist discography download, scheduled
      sync, parallel downloads, a failed-downloads viewer, source health checks,
      library history and an audio analyzer, across FLAC, ALAC, AAC and MP3.
      39,182 lines of it.

      What is here is tagging, artwork and lyrics on export, and one approval-gated
      fetch of a direct audio URL. That is a useful slice of one of its twenty-odd
      features. It is not its half and it is not parity, and saying so here stops
      the next person reading this list as though it were.

      The entry it replaces was a different problem. It read "that is a library, not a design
      surface", **which was my decision, taken in my own commit, and then quoted
      back in a parity answer as though it had been agreed.** It had not been. Asked
      "declined by whom?", the answer was me.

      The line was never as clean as that sentence made it sound. Artwork and
      lyrics are things you *look at*, and a running order that showed neither was
      showing less than the file it exported already carried. `SoundDetails` reads
      title, artist, album, year, artwork and lyrics off a track — or off the
      design, so an album name said once covers the whole running order and a
      person is not filling in a form twelve times. A track that names its own
      artist is naming a guest and wins for that track. `FfmpegMediaExport` writes
      them as tags and attaches the cover as a still stream marked `attached_pic`,
      so the file knows what it is when it lands on a phone instead of showing a
      grey square and the word Unknown. The canvas shows the cover and who made it,
      with the words folded away — lyrics are long, and something that pushes the
      next track off the screen has stopped being a running order. Sayable, not
      just settable: "the artist is Nina Simone", "the album is called Wild Is The
      Wind", "it came out in 1965".

      A cover follows the rule the paperclip already follows: a data URI travels
      with the document, an address does not. And a picture that cannot be decoded
      costs the picture, not the export — nobody loses forty minutes of audio over
      half-pasted artwork.

      One test earned its place immediately: a track that knows only its own name
      drew an empty box under every row, because the guard asked whether there was
      anything *at all* rather than anything **not already on screen**. A title is
      already the caption above the player. 24 tests.

- [x] Antra's other half: **links in.** Your call, taken, and gated the way
      `web_fetch` already is rather than opened up in the export path.

      `design_bring_in_sound` fetches an audio file and adds it to the running
      order. **It is the one design tool that asks first, and the exception is the
      point.** Everything else on this canvas acts without asking, because going
      back is free and asking is what makes the surface unusable for the people it
      is for. That argument holds exactly as far as the edge of the device: this
      one makes a request to an address a model may have written, and no amount of
      picking an earlier picture un-makes it — the request happened, and whoever is
      at the other end knows it. The **address** is on the card, not a summary of
      it, because "fetch a sound" is not something anybody can decide about.

      The fetch goes through the same `IWebAccess` the web tools use — one set of
      rules about what this program may reach, rather than a second fetcher with
      its own idea of them, which is how a guard comes to cover one path and miss
      the newer one. http and https only, no loopback or private address **on any
      hop** (a redirect to 169.254.169.254 is the whole trick, and a guard that
      runs once at the start does not see it), a redirect limit, a timeout, and
      `audio/` or nothing.

      Two decisions worth stating. A file over the cap is **refused, not
      truncated**: half a page is still readable, half an audio file is a broken
      file embedded in somebody's design that fails much later somewhere that does
      not mention downloading. And the cap is 10MB because the design is written to
      disk whole on every change — a cap that allowed an album would make editing a
      heading cost a hundred megabytes of writing.

      What arrives is carried as a data URI, the way the paperclip carries a
      picture, so the design still travels rather than pointing at somebody else's
      server that may be gone next week. Absent entirely on a head with no web
      access or no way to ask. 21 tests.

      Still not here, and worth naming: this is reachable by a model, not by a
      sentence. `DesignSpeech` is synchronous and this needs to ask and wait, so
      "add the track at https://…" typed into a canvas with no model does nothing.

**What every renderer refuses, because it is the whole point:** no timeline, no
track stack, no waveform, no node graph, no orbit-by-dragging, no keyframes. Each
is the thing that makes its category's software feel like a cockpit, and each is
what a person who has never used one cannot get past.

---

### 15. What reading the six actually showed

Cloned and read, after asserting things about them from summaries of their
READMEs. Coverage stated honestly: their architecture-defining code — Pascal's
`core` schema and its architecture test, Diffusion Studio's runtime traits,
OpenMontage's pipeline manifests and checkpoint layer, Antra's source adapters
and resolver, AniGen's representations and licences, open-design's packages and
design systems.

**"Not all 248,000 lines" was the figure written here, and it is wrong.** Counted
on 2026-09-11 after cloning them again: **2,516,644 lines**, of which open-design
alone is 1.7 million. So the coverage was an order of magnitude thinner than this
paragraph claimed — a fraction of a percent, not a slice of a quarter-million.
What was read is still what is written below; what was *not* read is ten times
larger than stated. A number nobody re-derived, sitting in the section about
claims nobody checked.

**Two claims of mine were wrong.**

The renderer-is-welded-to-the-document claim is false. Pascal's `core` has a
test that fails the build on a runtime `three` import; Diffusion Studio's
`runtime` is "headless… No DOM, no solid-js" with encoding and reconciling in
separate packages. Both separate exactly as we do. Corrected in the code.

open-design has **no document model at all** — it is an agent harness (daemon,
desktop shell, launcher, plugin runtime) driving coding agents with 154 prose
design-system briefs. So it genuinely cannot become Pascal, but not for the
reason I gave.

**And a licence I never checked:** Antra is Elastic License 2.0 — source
available, not open source. It forbids offering the software as a managed
service. It is yours, so it changes nothing here, but it does not meet the
"MIT/Apache/BSD/OFL/PD only" rule the rest of this repo is held to.

**Four things worth taking, each better than what we have.**

- [x] **Schema-validated stage artifacts** (OpenMontage). Done, and it turned up
      a live defect rather than an absence: `PlanProgress.Round` ticked a step off
      whenever *any* call in the round succeeded. A plan whose step was "write the
      config file" ticked when the model instead listed a directory — the strip
      advanced, whoever was watching believed the file existed, and nothing had
      been written. The same defect as an approvals badge that always said two,
      sitting on the one surface whose entire job is showing what is happening.
      `StepExpectation` states what a step will leave behind — a file written, a
      file read, a command run, optionally against a named target — and
      `PlanProgress.Judge` only ticks when the promise was kept. A round that
      worked but did something else still counts as progress (the failure counter
      resets) and simply does not tick, because withholding a tick must not also
      stop a turn that is going fine.
      **Their JSON schema is declined**: what a step here promises is small and
      nameable, and the plan strip has to stay something a person can read.
      `Unstated` is the default, so a plan that names no expectations behaves
      exactly as every plan did before. 17 tests.
- [x] **A resolver, not a list** (Antra). Done: `RuntimeResolver` decides the
      order, `RuntimeFailover` keeps deciding who may be asked at all. Failover
      answered "may this one be asked" and said nothing about order, so the chain
      was whatever order the alternatives arrived in — every turn, forever. A
      provider that fell over thirty seconds ago was tried first again while the
      one answering all afternoon waited behind it.
      Recently-failed providers are **demoted to the back, never removed** — that
      is Antra's actual insight, and it keeps a bad thirty seconds from costing a
      provider for the session; healthy ones are rotated round-robin so the same
      one is not hammered; when everything is cooling, the one that broke longest
      ago goes first because it is the likeliest to have recovered. Wired into
      `WorkspaceBase`, which records the outcome of every turn, rather than
      registered and never reached like `McpClient` and `FileTodoStore` were.
      **Per-source accept thresholds are declined on purpose:** Antra scores a
      candidate and rejects a poor match, but a chat reply has no such score, and
      inventing one would mean deciding an answer was not good enough and quietly
      asking somebody else — the shopping-for-a-yes that `RuntimeFailover`
      exists to forbid. 9 tests.
- [x] **Schema migrations** (Pascal — seven migration test files). Done:
      `DesignDocumentFormat` writes a `SchemaVersion` and reads one back,
      upgrading older documents one step at a time through a migration chain that
      is empty today and exists so version 2 is an entry and a test rather than a
      mechanism invented while somebody's saved work depends on it.
      A **newer** document is refused with a sentence a person can read, because
      opening one by ignoring what this build does not understand hands back a
      design that silently lost half of itself — which then gets saved over the
      good copy. Insertion order is written too: children live in a list private
      to the document, and a serialiser that wrote only the node dictionary would
      reopen a deck with its slides shuffled, first noticed by somebody standing
      in front of an audience. 13 tests.
- [x] **Timing that means something** (Diffusion Studio). Done: `DesignTiming`
      reads `seconds`, `delay`, `trim` and `rate` off a node, so "start this a
      beat after the last one", "skip the first four seconds" and "play it at half
      speed" are sayable — none of which one number can express. Two of their five
      are declined on purpose: **Workarea** is a timeline slice and there is no
      timeline, it being first on the list of things every renderer refuses;
      **SourceFrameRate** is for frame-accurate cutting against source material
      and Concierge does not decode video, so it would be stored, never read, and
      eventually believed. Read off properties rather than a new node shape, so
      nothing migrates and a document carrying only `seconds` behaves exactly as
      before. 19 tests, most of them about a sentence a model got slightly wrong —
      "a couple", "two point five", a rate of zero — falling back rather than
      breaking the canvas.

**And a convergence worth noting rather than claiming credit for.** Pascal's
`BaseNode` is `id / type / parentId / visible / metadata` — the shape we arrived
at independently. Diffusion Studio's `Scene` trait is "a clipped, playable
frame", which is our `Frame`. Where two serious projects and this one land in the
same place without conferring, the shape is probably right and nobody involved
was being clever.

---

### 16. What the second pass through open-design added

Where the first pass read the architecture, this one read the design corpus:
every `DESIGN.md`, the whole `design-templates` tree, all 162 skills, and about
sixty per cent of the plugin skills. Four things are worth having; the rest was
repetition, and saying so is part of the finding.

- [x] **A design system as prose, not tokens.** Unblocked by the canvas joining
      the agent loop, and done the same day: `DesignLook.Brief` carries the
      argument behind each of the six looks — what it is for, what it refuses,
      where it goes wrong — beside the hex values that were all it had before.
      `design_describe` hands the brief for the current look to the model, and
      `design_look` hands back the brief for the one it just chose, so what gets
      added next is added *in* the look rather than merely alongside it. That
      wiring is the whole point: told only "Warm", a model adds a hard-edged banner
      and four accent colours and produces something wearing the name and none of
      the intent. Written prose rather than more tokens, from open-design's 154
      briefs — theirs read as atmosphere → palette → type → components → motion
      with the reason beside every rule, which is why somebody who is not a
      designer can read them. Same bar this product sets for everything else.

- [x] **Rules written as corrections to a known offender.** The three built-in
      personas in `AgentPreset` were ideals — "Be warm and plain-spoken. Ask before
      doing anything that changes a file" — which is advice a model already agrees
      with and goes on ignoring. They now name the specific ways it fails, and the
      failures named are this product's own rather than generic: acting and then
      mentioning it; reporting a success it never checked (every defect worth
      fixing here has been a screen asserting something untrue); padding a one-line
      answer. The operator persona adds the two that matter when there is nothing
      left to ask permission for — reaching for the broad command, and stating a
      cause nobody measured. Kept short on purpose: this text is in every message
      of every conversation and spends the same budget the skills do.

- [x] **Diversification against a log.** Also unblocked the same day, and the
      failure it prevents is specific rather than theoretical: a model driving a
      canvas picks the same look every time — not because it is right, but because
      it is first in the list and the safest-sounding word. Six looks with one used
      forever is the same product as one look, and nobody notices until every page
      anybody made is identical.
      `FileDesignLog` keeps the last five (look, medium) pairs; `design_describe`
      tells the model what the last three wore and why it is being mentioned; and
      choosing a look records it. **It informs, it does not enforce** — a person
      who asks for Calm twice is right, and a canvas that argued with them would be
      worse than a repetitive one. Kept short deliberately: a long history lets a
      model reason about trends nobody asked it to notice. An unreadable log is an
      empty one, because a memory aid must never break the thing it is helping
      with. 10 tests.

- [x] **Load discipline as a first-class rule.** Another written-and-never-reached:
      `SkillActivation` has computed an `EstimatedTokens` since the day it was
      written and nothing ever read it, so there was no budget at all. Switch on
      twenty skills and twenty full bodies went into every message, all conversation
      long, whether or not any applied. `ComposeSystemPrompt` now spends a budget in
      the order the skills are shown — predictable from the list in front of you
      rather than a ranking you cannot see — and anything past it is **named rather
      than dropped**, because a skill that vanished silently leaves somebody who
      switched it on unable to tell it from one that simply did not help. The first
      is always quoted whatever it costs: a budget that can reject everything
      produces a prompt that mentions skills and contains none. 8,000 tokens, which
      two or three skills never reach — it exists for the pathological case, not to
      police ordinary use. 7 tests.

**One rule of theirs we should adopt outright, unchanged:** *invented metrics are
slop the moment they are invented.* "10× faster", "trusted by 50,000+ teams",
"99.9% uptime" — the fix is a labelled blank, never a plausible number. That is
the same defect as an approvals badge that always said two, and we have now hit
it twice.

**The honest limit on all of this.** I read considerably more than is written
above, and what is not written down is gone — my context compacts between
sessions, so reading without recording produces coverage and not knowledge. Four
items here and four in item 15 are what survived because they were written down.
That is the argument for reading less and recording more, and it applies to
whatever is read next.


---

### 17. Feature parity with the six, listed out

> **What is needed and not here.** Asked directly on 2026-09-11 — *"do you have
> everything you need?"* — and the answer is no. Four things, and nothing else on
> this list waits on them. Everything else is being built.
>
> | Missing | Blocks | What it would take |
> | --- | --- | --- |
> | **Speech model files** | Transcription, narration, and stripping "um" | `CircleAI.Voice` ships the code — `WhisperTranscriber`, `OnnxTtsEngine` — and the models folder holds only the three Qwen language models. Whisper weights and a TTS voice need to arrive. |
> | **Image generation** | open-design's generated images | No CircleAI package does it. `mnn_llm_image_*` is images going *in*, not pictures coming out. Either a cloud key on the `IImageRuntime` seam that already exists, or a local model. |
> | **Music service credentials** | Antra's seven sources | Spotify, Tidal, Qobuz, Deezer, Apple, Amazon and YouTube Music each need an account and a key. |
> | **A trained model for AniGen** | Photo to rigged 3D asset | Plus a Python machine-learning stack and a GPU. It is a research pipeline, not a feature. |
>
> **Everything is here for the rest**: ffmpeg, three.js, the CircleAI packages
> including image *input* on Inference 3.3.0, the six repos cloned and readable,
> and the desktop head verified. Roughly four to six of the thirty-six wait; the
> other thirty do not.

Asked for in full on 2026-09-11, after the six were cloned again and read rather
than remembered. Everything below is **theirs**, taken from their own feature
lists and READMEs, written as what Concierge would have to build to match it.

**Read the size before reading the list.** These six are 2,516,644 lines against
Concierge's 61,232. Real parity means building a building editor, a video editor,
a video production studio, a music download service and a machine-learning
research model — five separate products. Nothing here is scheduled, nobody has
agreed to any of it, and the list exists so the choice is made with the actual
scope in view instead of from a feeling about it.

**The useful question is not whether to do all of this.** It is which two or
three of these lines would change what a person can actually do with Concierge.
The rest can stay written down and undone without that being a failure.

Links, which this file never recorded and should have — finding them cost an
afternoon digging through an old session transcript:

| Repo | Where it is |
| --- | --- |
| Pascal | https://github.com/pascalorg/editor |
| Diffusion Studio | https://github.com/diffusionstudio/editor |
| OpenMontage | https://github.com/calesthio/OpenMontage (ours: `bhengubv/OpenMontage`) |
| Antra | https://github.com/bhengubv/Antra |
| AniGen | https://github.com/VAST-AI-Research/AniGen (ours: `bhengubv/AniGen`) |
| open-design | https://github.com/nexu-io/open-design |

#### Pascal — a 3D building editor

Space today is four shapes on a floor, turned with arrow keys. Pascal is
architecture.

- [x] Walls, floors, ceilings and roofs as things you draw, not blocks you place — `design_build`; a wall runs between two points because that is how somebody describes one
- [x] Doors and windows cut into walls properly, so a wall has a hole in it — `design_opening`; **no CSG library**, because a rectangle can be left rather than cut
- [x] Building levels that stack, pull apart, or show one at a time — `design_floors` and `design_floor_of`; pulled apart is the view no physical model gives you
- [x] Furniture that attaches to a wall or a ceiling and sits at the right height — `design_hang`; the pushing-out is worked out from the wall's thickness rather than asked for
- [x] Trace over a photo — `design_plan` lays a floor plan flat at its real size to
      build over, laid under everything by a hair so a wall drawn on the line covers
      it rather than fighting it. **Bringing in a 3D scan is not done**: that is a
      mesh format reader and a real decision, not a line of this.
- [x] Add-ons other people can write — `shapes.json` names new things a room can be
      furnished with, and `design_furnish` puts them in. **Pascal's plugin idea with
      the dangerous half left out**: theirs loads real code, and `PluginHost` here
      does the equivalent and stays deliberately unwired, because "any DLL on disk
      can add tools" is a decision somebody makes on purpose. What people actually
      want from those add-ons is more things to put in the room, and a file naming a
      desk as three boxes cannot execute anything or break the app.

#### Diffusion Studio — video

Motion today is shots held for a length, exported as a slideshow of cards. Its
own comment says so. Diffusion Studio edits video.

- [x] Cut and join real video clips — `design_add_footage`, `design_cut`, and an export that normalises every shot then joins by copying
- [~] Strip out "um" and "er" — **same blocker, and it is the timings rather than the
      words.** Finding a filler word in a transcript is easy; cutting it needs to know
      where it is in the file, and the package hands back no timings at all. `design_cut`
      can already cut to a start and a length, so the cutting half is done and waiting on
      something that can say where.
- [~] Subtitles timed to each word — **blocked in the package, measured rather than
      assumed.** `CircleAI.Voice` 1.2.0's `TranscriptionResult` carries the text, a
      confidence and a language code, and nothing about *when* anything was said.
      Whisper itself has the timings — `whisper_full_get_segment_t0` / `_t1` — but
      `WhisperInterop` is **internal**, checked by compiling against it rather than
      guessed at: "'WhisperInterop' is inaccessible due to its protection level".

      So the honest position is that this needs segment timings out of the package, and
      the only way to have them today is to reimplement whisper's interop here — which
      is duplicating a NuGet package's internals, the thing the mesh entry refuses for
      the same reason. Spreading the words evenly across the running time would produce
      subtitles that look right and drift, which is an invented metric with a timestamp
      on it.
- [x] Colour correction and filters — `design_colour`: warm, cool, bright, dark, grey, faded, vivid, graded before the shot is shaped so the letterbox bars are not graded too
- [x] Animation — `design_blend` fades one shot into the next; a cut by default, because a dissolve on every join is what a first attempt looks like
- [ ] Generate images, video and voiceover
- [~] Write out what is said in a recording — **wired, waiting on a model file.**
      `CircleAI.Voice` sat in the package cache with no caller at all: `WhisperTranscriber`
      for listening, `OnnxTtsEngine` for speaking, both complete, both unreachable. Both are
      now behind the `IVoiceRuntime` seam the cloud runtime already sits behind, so whatever
      uses voice does not care which answered, and `media_transcribe` is a read-only tool
      beside `media_facts`.

      **Present when the model files are, absent otherwise** — the rule every device
      capability follows. No key, no account, nothing to configure: a whisper model (.bin)
      and a voice model (.onnx) in the models folder, and it works on the device. With
      neither there the tool is not offered at all rather than offered and always failing,
      and the status says which half is missing and names the folder.

      Listening needs the encoder too, and says so: whisper wants 16kHz mono samples and a
      person has an .m4a, so the file goes through ffmpeg first. A model file with no encoder
      is speaking only, stated rather than discovered.

      **The model files are not here and nobody is pretending otherwise.** That is one of the
      four things named as missing at the top of this section. 26 tests, none of which need a
      model, because what they check is that the absence is honest.
- [x] Watch footage and answer questions about it. Every piece existed separately and
      the join was missing: `media_frames` hands a strip to `CapturedImages`, a turn can
      carry pictures, and the runtimes can look at them. A strip a tool produced now
      reaches a model that can see, rides exactly one turn, and — the half that matters
      more — **a model that cannot see is told a picture was produced rather than handed
      one it will ignore and then be asked about.** That case was silent before. 3 tests,
      driven through the real workspace rather than around it.

The inspection half of this is **done** — `media_facts`, `media_is_silent` and
`media_frames`, in item 14. That was the piece that cost little and closed a real
hole. The rest is a video editor.

#### OpenMontage — video production

- [ ] Production routines that run end to end, rather than one tool at a time
- [~] Computer-generated narration — **wired, waiting on a voice model.** `design_narrate`
      says written words out loud and adds them to the running order as a track, carried
      inside the design as a data URI the way a picture already is, so the design still
      travels. It does not ask, like everything else on this canvas: nothing about it leaves
      the device, and going back is free. Nothing coming back is a failure rather than a
      silent track — a silent track looks exactly like one that worked.
- [ ] Built-in free stock footage
- [ ] A composition engine for motion graphics
- [~] Automatic captions — waits on the same timings as word-timed subtitles above.

#### Antra — a music library

Sound today is a running order that exports one file, with tags, artwork and
lyrics, and one approval-gated fetch from a link. Antra is a library.

- [ ] Pull from the seven music services it supports
- [ ] Match the exact recording rather than the right title
- [ ] Choose between clean and explicit versions
- [ ] Notice high-quality audio and prefer it
- [ ] File everything into artist and album folders
- [ ] Spot duplicates already in the library
- [ ] Download an artist's whole catalogue
- [ ] Downloads on a schedule
- [ ] An audio analyser, podcasts, and peer-to-peer

#### AniGen — research

- [ ] Turn one photograph into a 3D model with a skeleton that can be animated
- [ ] Ship or reach the trained model that does it

Nothing was ever taken from AniGen and for a long time this file called that a
hole in the record. It is not: this is a machine-learning research pipeline, and
there is nothing to take from it short of the model itself.

#### open-design

- [x] Mobile app designs as their own medium — `Handheld`. A phone screen at 390×844
      with the two strips nothing may go under drawn as hatched bands, and the line a
      thumb reaches drawn across it. **Its own medium rather than a page drawn narrow**,
      because the difference that matters is not the width: a design that looks fine at
      1200px and falls apart at 390 is the commonest thing to get wrong, and nobody
      finds out until it is built. No device frame, no bezel, no fake battery — those
      make a mock-up look finished and tell nobody anything.
- [ ] Image generation
- [ ] Animated motion graphics
- [x] Live dashboards that update themselves — `Board`. A small name, an enormous
      number, and which way it is moving as an arrow (▲▼▬) rather than a colour alone,
      because a board is read at a glance by whoever is walking past and colour says
      nothing to somebody who cannot tell red from green. **No chart, and that is the
      decision this renderer turns on**: a line going up is what every dashboard reaches
      for and almost nobody reads. A panel with no number shows a labelled blank —
      open-design's own rule, adopted unchanged, that an invented metric is slop the
      moment it is invented.

      What makes it *live* is where the numbers come from, which is the agent's job and
      not the renderer's: `design_panel` sets a panel, and writes only the fields
      actually given, so updating the number does not wipe the note.
- [x] Design guides. **The count is not matched and saying so is the point.** open-design
      ships 298; this ships 21 that were written rather than counted, plus `guides.json` —
      a file anybody can add to with no code, which is the answer `shapes.json` already
      gives for furniture. Claiming a number would be claiming something nobody has
      written.

      What they are is the half the six looks do not answer: the looks say how a thing
      appears, a guide says **what goes on it and in what order** — a poster read from a
      corridor, a deck whose slide titles are sentences, a dashboard read in two seconds,
      a form where every field is a reason to give up. That is the part somebody who is not
      a designer has no way to know, and the part a model gets wrong by producing something
      competently laid out that says nothing.

      Prose with the reason beside every rule, which is open-design's own shape: a rule
      with no reason gets applied where it does not belong, and a rule nobody understands
      gets ignored the first time it is inconvenient. `design_guide` is read-only and takes
      what somebody said in their own words — nobody says "artifact type: presentation",
      they say "a deck for Thursday". A guide somebody wrote wins over one of ours with the
      same name; a broken file costs the file and never the built-ins. 22 tests.
- [x] Plug into the other coding tools. **Their shape inverted, which is the honest
      version of it.** open-design is a harness that drives sixteen coding assistants;
      Concierge *is* the assistant. The useful half survives the inversion: somebody with
      Claude Code, Codex or Aider already installed has already chosen their tool for large
      code changes, and an assistant that cannot acknowledge that is one they leave in
      order to go and use it.

      `coding_tools` says which of nine are actually on this machine — claude, codex,
      gemini, cursor-agent, aider, opencode, crush, goose, qwen — and nothing is installed,
      downloaded or mentioned when it is not there. `ask_coding_tool` hands one a job.

      **It asks every time and the card carries the tool and the job in full**, because
      this is further from reversible than anything else here: it starts another agent, in
      the workspace, that edits files on its own judgement and not ours. Nobody can decide
      about "run a coding tool"; everybody can decide about "aider — rewrite the export to
      stream". With no way to ask, the listing is still offered and the handing over is
      not.

      Confined like every other command — job object on Windows, `unshare` and `prlimit` on
      Linux — and given twenty minutes, because an agent that has misunderstood its job can
      run for an afternoon. Each entry carries the way that tool takes a job with nobody
      watching, and one whose non-interactive form nobody here is sure of is **left out
      rather than guessed at**: a wrong flag opens an interactive session nothing is
      sitting in front of and hangs until the time limit. 13 tests.

**What Concierge has that none of the six has**, kept here so the list above is
not read as a scoreboard: one document across five media on one agent loop, with
approvals, working on the device, on five heads. Pascal is an editor. Diffusion
Studio is a runtime. open-design is an agent shell with no document model at all.
Matching their features is not the same as being better than them at anything,
and the bar this product is actually held to — a five-year-old and a
ninety-seven-year-old — rules several of the lines above out on purpose.

---

## What is actually next

Everything above is done or done-bar-a-named-remainder. Nine items are open and
they are not equal: three can be done today, and six cannot, for reasons worth
being explicit about rather than rediscovering.

**This list had gone stale, and two of its three items were already done.** Worth
saying rather than quietly deleting: a next-actions list that disagrees with the
ticked list above it is the same defect as a badge that always says two.

- ~~Show registered hooks in Engineering~~ — **done.** `Engineering.razor:156–199`
  reads `HookStatus` and renders the list, the empty state, the problem and the
  file path. Item 12 had it ticked; only this section still asked for it.
- ~~Verify Design on a handheld~~ — **done**, at 504px, per item 9. What is still
  open is the other half of that line: **what a watch shows instead of a canvas.**

**Can be done now, and needs no model.**

1. **Decide what a watch shows instead of a canvas.** A 192dp face is not a design
   surface and pretending otherwise would be the fourth-tab mistake in miniature.
   The decision is the work; the rendering is small.
2. **Close the sandbox escape**, now that the failing half is known rather than
   guessed — see item 11. The measurement is done; the fix is not.

**Blocked, and by what.**

- **Android actions; BLE or Wi-Fi Direct** — need a second device in the room.
- **Confinement on Linux, macOS, Android, iOS** — need those platforms.
- **Approvals over the mesh** — needs packet signatures verified, and the mesh
  upgrade is in the pipeline elsewhere. Putting approvals on an unauthenticated
  channel would hand anyone on the café wifi the ability to say "allowed".
- **A plan for a multi-step design change** — the canvas is on the agent loop now,
  so this is no longer blocked on a runtime; it is blocked on wanting it. Nothing
  to plan until somebody asks for a change big enough to need one.

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

- ~~**The native model crash.**~~ **Not a package bug, and not "not doing" — it
  was a switch of ours, and it is off.** Recorded here for weeks as an access
  violation inside `mnn_llm_generate_stream_text`, owned by the `CircleAI.Inference`
  package and upgraded elsewhere. That attribution was wrong.

  `GenerationOptions.UsePrefixCache` was set to `true` in
  `CircleAiChatRuntime.StreamAsync`. The prefill KV cache is keyed on
  (modelId, systemPrompt), so it engages only once a system turn is present, and
  that path faults: the host prints `Fatal error.` and dies mid-reply. Every real
  turn carries a system prompt, so it was every turn. It read as a package fault
  for weeks because running `Concierge.Model.Host` by hand — the obvious way to
  check — sends no system prompt and so never touches the cache. It looked
  healthy every single time anyone looked.

  Measured through the host rather than reasoned about, and the three payloads
  are worth keeping because they are what makes the diagnosis falsifiable:
  a 3,075-char **user** prompt completes (`cache=off`); a **16-char system turn**
  faults at 121 chars fed (`cache=on`); the same 6,882-char payload faults as a
  system turn and completes as a user turn. Length is irrelevant. The system role
  is the trigger.

  With the cache off, that same 6,882-char system payload streams and completes.
  The cost is the sub-200ms first-token latency of RT-06, which is a fair price
  for a product that answers. It is a named option now, defaulted off, with a
  test holding the default, so switching it back on is a decision with evidence
  rather than a literal nobody re-examines.

  **What this cost, and the lesson worth more than the fix:** the entry said
  "no amount of work in this repo changes that", and one line in this repo did.
  Third misattribution in this file — after the `start /b` race and two broken
  sandbox measurements — and all three shared a shape: a confident cause written
  down before anything was measured, then inherited by everyone who read it.

- **`MainLayout`, `NavMenu`, `BottomNavLayout`, `AppMenu`, `Bell`.** These are
  the navigation model the redesign removed. Kept as shells for possible reuse
  rather than deleted.

---

## Standing checks

Boxes that are re-checked per change rather than ticked once. Each carries the
commit it was last actually checked at, because "all four hold as of `<ref>`"
was one date covering four checks made at different times — and it had drifted
two commits behind `HEAD` before anyone noticed.

- [x] **1599 tests pass, and the suite is deterministic again.** It was not: four
      consecutive runs each failed *one different test*, which meant it could not
      tell a regression from noise — the same defect as a screen asserting
      something untrue, sitting on the check everything else is measured by.

      Three separate causes, none of them the code under test.

      `SandboxedCommandTests.Nothing_a_command_starts_outlives_it` listed **every
      process on the machine** and failed if a new one was called `cmd` — so it
      failed whenever anything else opened a shell during those seven seconds:
      another test, a build, a person opening a terminal. It now writes a marker
      *after* its wait and checks the timestamp against the moment the command
      returned, which measures the actual claim. Note the shape: the previous
      version of this test was itself the fix for reading a marker's existence as
      proof of *when*; this one reads its timestamp, which is the thing that was
      missing all along.

      `WorkspaceThreadTests.A_chip_is_collapsed_until_it_is_asked` waited for the
      first render and then read the state after a click straight away — holding
      for a race it had already been fixed for once, three lines further down.

      `WorkspaceHandoffTests` polled, *then* rendered, then asserted. If the
      handoff needs a render to fire, the poll could only ever time out, the render
      then started the work, and the assertion read the store before it finished.
      Rendering inside the wait fixes it.

      Verified the only way this can be: **six consecutive full runs, all green.**

      A fourth surfaced the moment the branch merged — `WorkspaceSkillsTests`
      asserting on the line after a `Click()`, passing ten times alone and failing
      inside the full suite. Same shape as the other two bUnit ones, and worth
      naming as a shape rather than three separate fixes: **an assertion on the
      line after a Click is a race.** Blazor re-renders after the event; a loaded
      machine gets there second. If a fifth turns up, that is where to look before
      anything else.

      **Stop the web head before running it** — a running `Concierge.Web` holds the
      shared DLLs and the build fails with MSB3027 rather than anything about tests. One of them is
      a tripwire rather than a test of this code — it goes red when there is no
      encoder on the machine, because the four export tests stand down without one
      and four silent greens look exactly like four real ones. **Run it from the repo root with an absolute
      project path** — a backgrounded run starts elsewhere, fails with MSB1009,
      and a filtered pipe reports that as success
- [x] Verified on the running desktop app, not only in tests — Engineering lists
      the real catalogue, `todo_read` and `todo_write` included, and says what
      confines a command. Last checked at `a00191b`; **not re-run since**
- [x] **The 3D engine works on the desktop head. Measured, not assumed.**

      The only genuinely head-specific thing about it was whether WebView2 loads an
      ES module by absolute path from inside a srcdoc frame, on the origin MAUI
      serves the app from. Everything else is shared code.

      Settled by reproducing exactly those conditions rather than by reasoning about
      them: a WebView2 host with `SetVirtualHostNameToFolderMapping("0.0.0.0", ...)`
      — which is what `BlazorWebView` does on Windows — serving the real
      `SceneRenderer` output inside a srcdoc iframe with the same sandbox
      attributes, on WebView2 runtime 152.0.4191.66.

      The answer: `deep: true`, `report: {drew: true}`, `canvasHidden: false`,
      `stageHidden: true`. The engine loaded, drew, and put the flat room away.

      The self-report built for this stays, and earns its place either way: it is
      what tells somebody on a machine that *cannot* run it why their room looks
      flat, which is a question a person actually has.
- [~] **Superseded — kept for the reasoning.** The entry below said this was
      unverified and explained why that mattered.
      Everything about it is shared code — one `SceneRenderer`, one set of
      components — so there is nothing to port. What is genuinely head-specific is
      whether the WebView loads an ES module by absolute path from inside a srcdoc
      frame. Checked what could be checked without eyes on the screen: the engine
      **does** ship in the desktop app's `wwwroot/_content/Concierge.Shared.Components/lib/three/`,
      at exactly the path the renderer asks for. Whether it then loads is not
      known.

      Rather than guess, the app answers it. `SceneEngineReport` records how the
      last room was drawn and Engineering shows it under **Rooms in three
      dimensions** — "With a 3D engine", "Flat" with the reason, or "Not tried
      yet", which is a third answer on purpose because *not tried* and *tried and
      failed* are different things. The reason comes from whichever way out was
      taken: no engine, no WebGL, an unreadable room, or a fault in ours.

      This exists because the fallback is silent by design. A machine that cannot
      run the engine shows a room that works perfectly and never mentions it — the
      desktop head could have been falling back for weeks with every test passing.
      To check: open Design, choose **Space**, say "add a room", then open
      Engineering. 10 tests.
- [x] `[skip ci]` in the HEAD commit before any push — a rule, not a check
- [x] The app actually starts. Added after a Razor comment inside an element's
      attribute list compiled, passed 1141 tests, and threw on every render in
      the real host — the whole sidebar went dead and no test noticed. Last
      checked at `a00191b`; **not re-run since**
