# Concierge — implementation plan

**Purpose:** turn [Concierge’s product vision](CONCIERGE-PRODUCT-VISION.md) into staged, verifiable work, starting with Desktop. Concierge is the product: it owns the person’s experience, work, workflows, continuity, and control. Circle AI is the AI engine that supplies capabilities Concierge can surface and use. This is a plan, not a claim that the listed outcomes already exist.

## Product rules

- Concierge discovers and presents the capabilities Circle AI makes available. Circle AI handles model/runtime selection and AI execution; Concierge does not expose a provider/runtime selector.
- Concierge owns the durable work experience and coordinates the person’s context, workflows, connected clients, and action control around the engine.
- A request has one durable identity and one execution. Connected clients are interfaces to that work, not separate copies of it.
- The order is **route → execute → sync**. Synchronization follows execution and carries the result, events, progress, and artifacts.
- Chat, Workspace, Project, and Start-Up are progressively broader contexts for work. Do not force every request into a project workflow.
- People can understand what is happening and what needs their decision. Routine operation should not ask them to make model or runtime decisions.
- Build the Desktop experience first, while keeping contracts independent of any one UI head. Bring Mobile into the validated work flow after the Desktop foundation is coherent.

## Starting point in this repository

There are useful building blocks, but they are not yet the vision as a whole:

- `IDeviceCapability` describes actions available on a particular device. It is not yet a complete, runtime-discovered Circle AI capability catalogue for shaping the client UI.
- `Workflow` runs a fixed ordered sequence. It is not yet a persistent, user-facing Project plan with milestones, dependencies, artifacts, decisions, and recovery.
- The app has conversation storage, tool approvals, a model download path, isolated model hosting, and an Away/cross-device path. These need to be brought under one durable work/run model and verified against route → execute → sync.
- The vision calls for a Desktop-first implementation. Mobile and Wearable parity are later stages, not assumed outcomes of the existing shared UI.

## Delivery stages

### 0. Establish a trustworthy baseline

**Work**

- Map the current Desktop startup, chat, Workspace, project/workflow, download, and Away paths to this plan.
- Record which persisted stores and event types already exist, which are local-only, and which are currently sent across devices.
- Keep existing user changes intact; make the smallest additive changes needed for each stage.

**Exit criteria**

- A short architecture map names the current owners of capability discovery, routing, execution, persistence, sync, approvals, and downloads.
- Each later stage has a concrete baseline and a way to demonstrate its acceptance criteria.

### 1. Let Concierge discover what the engine makes available

**Work**

- Define a versioned capability response that Circle AI exposes and Concierge can discover: capability identifier, user-facing description, availability, requirements, and supported work contexts.
- Let Concierge provide live device resource/readiness facts to the engine for execution selection; do not send hardware details to the user as a model-selection chore.
- Keep device actions and approvals distinct from AI product capabilities. A phone camera is a device capability; project planning is a Circle AI capability.
- Have Concierge render the available catalogue and gracefully adapt when capabilities appear, disappear, or change. Concierge decides how each available capability fits its product experience.

**Exit criteria**

- Changing the Circle AI capability response changes the Concierge surface without hard-coded provider/runtime choices in the UI.
- An unavailable capability is not presented as ready, and its return does not require a client restart.

### 2. Make work durable before distributing it

**Work**

- Define a persistent work identity and event model covering conversation messages, run lifecycle, progress, decisions, approvals, agent activity, artifacts, and completion state.
- Give every submitted request an idempotency key so reconnects and retries cannot create a second execution.
- Persist events before acknowledging them; let clients resume from a cursor after reconnect or app restart.
- Represent honest outcomes: ready, needs a person, paused by a limit, failed with recovery options, or complete with remaining review clearly marked.

**Exit criteria**

- Restarting the Desktop client restores the same task, its latest event position, pending decisions, and artifacts.
- A repeated submission with the same request identity does not start duplicate work.
- Interrupted work resumes or reports exactly why it cannot resume.

### 3. Put Chat, Workspace, and Project on that work model (Desktop)

**Work**

- Keep Chat immediate and lightweight, with no project setup for ordinary questions.
- Make Workspace a focused home for one or two goals, their context, outputs, and pending actions.
- Make Project a goal with multiple named workflows, milestones, dependencies, decisions, budget/time assumptions, and deliverables.
- Start with a small number of useful templates, but let Circle AI adapt steps to the stated goal instead of forcing every task through an app-development checklist.
- Make movement from Chat to Workspace or Project preserve the same work identity and history.

**Exit criteria**

- A person can ask a simple question in Chat, develop a focused goal in Workspace, and promote a broader goal into a Project without re-entering context.
- A Project can show what is done, active, blocked, awaiting a decision, and next, with artifacts attached to the right work.

### 4. Add visible, accountable agents and Start-Up Mode

**Work**

- Let Circle AI assign sub-agents according to the resources and capabilities actually available. The user does not choose a provider or manually size a swarm.
- Show each agent's purpose, status, outputs, and resource constraint inside the owning Workspace or Project.
- Support pause, correction, review, and undo where the underlying action allows it. Keep approval behavior aligned with the person's selected control level.
- Add Start-Up Mode as a business-level journey that asks whether the business is new or existing, then creates or connects the Projects and workflows Circle AI determines are relevant.
- Make autonomous company operation an extension of observable Project workflows, not an unbounded hidden process.

**Exit criteria**

- A Project can run more than one useful workflow, report agent activity and outputs, recover from a constrained/failed step, and show which decisions need the person. Concierge owns the workflow and agent experience; the engine supplies the AI capabilities used within it.
- A new-business journey and an existing-business journey produce appropriately different starting plans without forcing a fixed industry template.

### 5. Connect Mobile to the same work (then Wearable)

**Work**

- Connect Mobile to the durable work/event service and Circle AI capability catalogue.
- Let Circle AI route each request to a connected device that has suitable live resources. A request runs once on the selected device.
- Deliver results to all clients after execution, including conversation events, progress, artifacts, and pending decisions. Keep Mobile's layout appropriate to the phone; do not make it a pixel mirror of Desktop.
- Handle offline periods, stale capability reports, device disappearance, and hand-back to Desktop without losing work.
- Add Wearable as a compact capture, monitor, and response surface after Mobile/Desktop continuity is reliable.

**Exit criteria**

- A task started on Mobile can execute on Desktop when Circle AI routes it there, and Mobile can monitor and guide the same run.
- A task started on Desktop is visible on Mobile with the same durable identity and current decisions.
- Reconnection synchronizes completed events and artifacts without replaying the execution.
- A Wearable can capture a request and surface meaningful status/decisions without pretending to run work it cannot support.

### 6. Make model downloads useful, visible, and non-blocking

**Work**

- Circle AI selects the model appropriate to each device automatically. Do not ask the person to choose a model or provider.
- Show clear download status on every affected client: what is being prepared, progress, approximate size/remaining work when known, and whether the download is active, paused, or ready. Do not make progress a blocking screen or imply an unverified completion time.
- **Give the person a useful activity while files download.** Offer a short first-run setup they can complete at their own pace—describe what they want Concierge to help with, prepare a first Chat/Workspace/Project, or explore a guided sample. Save this locally and let Circle AI pick it up automatically when it is ready; do not require them to watch a progress bar or make model/runtime decisions.
- Preserve partial download progress across app restarts and connected clients. Avoid restarting or duplicating transfers when Desktop and Mobile are both open.
- Keep model readiness and work readiness separate. A person can prepare their goal and inspect the interface while the model is being prepared; Circle AI begins execution once the chosen runtime is ready.
- If the network is unavailable or the download stalls, explain the state in plain language and preserve the person's setup and work. Do not silently discard or repeatedly restart it.

**Exit criteria**

- A first-time person can do something useful in Concierge while the chosen model downloads, leave and return, and continue without losing setup or restarting completed transfer work.
- Desktop and Mobile agree on the download state when both are connected.
- The user never needs to select a technical runtime to make progress.

## First implementation sequence

1. Complete Stage 0's architecture map and identify the existing source of truth for every store and endpoint.
2. Specify the engine capability contract and have Concierge render the available capabilities in its own experience.
3. Introduce Concierge's durable work/run identity and event cursor, preserving current conversation history.
4. Demonstrate one Desktop task end to end: submit in Concierge → engine selects a suitable execution path → Concierge streams/persists the run → restart/resume → honest completion.
5. Build the Workspace and Project surfaces over that durable model; only then wire Mobile into the same route → execute → sync flow.
6. Add the first-run download activity as part of the download lifecycle, before inviting new users to a clean install.

## Product-level acceptance journey

The vision is substantially realized when a person can say from any connected Concierge client, “Build an MVP for location-based product activation and BI,” then use Concierge to clarify material assumptions, coordinate suitable connected resources through Circle AI, follow one execution and its visible agents/workflows, preserve artifacts and progress across devices, and finish with a clear account of what is ready, what needs review, and what remains. During model preparation, the person can set up that goal or explore a useful sample instead of waiting idle. A simpler request should still feel like an ordinary conversation.

## Deliberately not implied by this plan

- No specific model/provider UI for users; Circle AI owns that choice.
- No automatic external publishing, purchases, or irreversible business actions without the configured action-control policy and clear visibility.
- No claim that a capability is implemented merely because a contract or design exists; each exit criterion must be demonstrated in the running product.
