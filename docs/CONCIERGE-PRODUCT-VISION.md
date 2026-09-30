# Concierge product vision

**Status:** Working product vision for Concierge, captured from product discussion. This is direction for the Desktop app, not a claim that these capabilities are implemented.

## Core principle

**Concierge — we work so you don’t have to.**

Concierge is the product described by this vision. It owns the person’s experience: Chat, Workspaces, Projects, Start-Up journeys, workflows, continuity, and control across devices. Circle AI is the AI engine Concierge reads capabilities from and uses to help get work done. Concierge should surface those capabilities in useful experiences without exposing technical model or runtime choices. Other clients can use Circle AI too and become different products, from an everyday assistant to a focused creative app such as a video editor.

The experience should feel like Concierge is helping the person get something done, with Circle AI powering the relevant capabilities behind the experience.

## Three ways to work

- **Chat:** A simple conversation with Circle AI, with no project setup required.
- **Workspace:** A focused place to work on one or two specific things, with the relevant context and tools close at hand.
- **Project:** A broader goal with one or more workflows, milestones, and workspaces. Project mode can coordinate work from the initial idea through delivery and ongoing operation.

Workspace and Project modes can manage sub-agents. The available environment resources determine how many agents can run. People should be able to see what agents are doing and manage their work from the Workspace or Project that owns it.

## Start-Up Mode for business journeys

Some goals are larger than a single Project. Starting an advertising business, for example, should be able to grow from Project Mode into **Start-Up Mode**, a business-level journey that can coordinate multiple connected projects and workflows.

Start-Up Mode should begin with a plain-language question: **“New business or existing business?”** Concierge uses Circle AI to understand the answer and shape the appropriate journey, then guides the person from there. This is a broader journey than one project checklist; the specific work should be shaped around the person's business and goals.

## Work follows the person across devices

Chat, Workspaces, Projects, workflows, and agent activity should be available across Desktop, Mobile, and Wearable clients. Mobile should be able to do the same work as Desktop when its resources allow. When a task exceeds mobile's available resources, Desktop can take over execution while the person continues to monitor and guide the work from Mobile. A Wearable can provide on-the-go access to work running on either Mobile or Desktop.

Execution should be able to move to a better-resourced device without losing the task, its context, progress, artifacts, or pending decisions. The device in the person's hand is an access point to the ongoing work; it should not determine what that work is allowed to become.

For example, during a 10:00–11:00 meeting, someone could dictate a request from Desktop, Mobile, or Wearable: “Build us an MVP of an app that can manage real-world location product activation and generate BI data flows.” Concierge should begin coordinating the work, using Circle AI, available agents, and device resources, and keep making progress while the person is away from the desk. Concierge should bring forward prompts when it needs a permission, correction, or decision. By the end of the meeting, the person should be able to review the progress and respond to whatever is blocking the next step.

### Cross-device execution order

When devices are already connected, a request follows this order:

1. **Route the input** to the device with suitable live resources. Circle AI owns that decision; the client should not expose a provider or runtime selector. Desktop can take heavy work, while a capable phone can handle lighter work locally.
2. **Execute and answer** on that selected device, using Circle AI available there. There is one execution for the request, not a second copy started by each connected client.
3. **Synchronize after the answer** so every connected device receives the resulting conversation events, run state, and artifacts and can continue from the same durable work.

This is route → execute → sync. Synchronization follows execution and carries its result; it does not race the answer or make the clients mirrors of one another. Each client keeps an interface suited to its device while sharing the work itself.

## Work the product should support

Project workflows should be useful for building and operating a business, as well as for personal and creative work. For example, an app project could track:

1. Idea research and validation, audience, and competing products.
2. Product requirements, milestones, decisions, and budget.
3. Product design, brand, and design assets.
4. Development, testing, accessibility, privacy, and security.
5. Store preparation and release to Google Play, Apple App Store, SleptOn, and other chosen channels.
6. Website, launch communications, marketing, and campaign assets for ads and social media.
7. Scheduled or autonomous publishing, with action visibility and the configured approval level.
8. User feedback, support, analytics, updates, costs, revenue, and ongoing operations.

The same flexible system should support homework, music, video, and other goals. It should present workflows and tools appropriate to the task rather than forcing every task into an app-development template.

## Product implications

- Circle AI capabilities should be discoverable by Concierge at runtime; Concierge should decide how to surface relevant abilities as they become available.
- Workspaces and Projects should track outcomes, steps, artifacts, decisions, progress, and agent activity together.
- Device changes should preserve one continuous task state; Desktop can provide execution capacity while Mobile or Wearable provides access and oversight.
- Project mode can grow to support agents coordinating company workflows autonomously, subject to environment capacity and the person's chosen level of control.
- Keep the interaction proportional to the task: a casual question belongs in Chat; focused work belongs in a Workspace; related workflows belong in a Project.

## Continuity, control, and dependable progress

- **One durable work state:** Chat, Workspaces, and Projects should build on the same task history, files, decisions, and agent activity. Switching devices should continue the work without a restart or manual reconciliation.
- **Clarify goals before substantial work:** Concierge, powered by Circle AI, should turn a broad request into a proposed result, plan, time and resource budget, and important assumptions. It should ask for decisions that materially affect the outcome before committing significant effort.
- **Respectful interruptions:** People should be able to choose when and where they receive prompts. Urgent approvals can be surfaced promptly; routine progress can be summarized for a convenient time.
- **Visible and accountable autonomy:** People should be able to see what agents are doing, what they changed, which permissions they used, and how to pause, correct, or undo their work.
- **Graceful limits and recovery:** If a device goes offline, runs low on power, or reaches a resource limit, Concierge should preserve progress, explain what paused, and offer a practical next step, using Circle AI where available.
- **An honest finish line:** Time-bound requests should clarify what counts as complete. Concierge should distinguish what is ready, what needs review, and what could not be completed, and communicate uncertainty plainly.

The product should feel ambitious and proactive while remaining honest about progress, constraints, and uncertainty.

## Desktop scope

This note records product direction for the Concierge Desktop app. Implementing this vision in other application heads is out of scope until the Desktop experience is established.
