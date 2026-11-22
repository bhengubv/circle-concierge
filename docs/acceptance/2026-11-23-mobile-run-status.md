# Mobile run status — 23 November 2026

Desktop already showed whether a turn was waiting for its first response or receiving an answer, with elapsed time. The Handheld view showed only the runtime's generic status text. During a multi-minute Circle AI wait, that gave the person less useful feedback on Mobile.

The Handheld active-run banner now shows **This phone**, **Waiting for reply** or **Receiving reply**, and elapsed time. The runtime's detailed status remains below it, so preparation or failure information is not discarded. This is a client UI change; it does not make the service answer sooner or prove that a model is loading.

The existing three-second workspace sync loop skipped rendering when the conversation event sequence did not change. That made an elapsed label freeze during a silent service wait. It now redraws only while the active conversation has a running turn, without re-reading the thread or polling the model more often. A focused bUnit test held a runtime silent with no new conversation events and observed the clock advance.

The complete Concierge test gate compiled the shared Razor UI and passed **2,745/2,745**, with zero failures and zero skipped tests. The reported coverage for instrumented assemblies was 80.21% line, 68.02% branch, and 85.63% method. PixieDust was absent from existing ADB connections and mDNS at the time of this check. The rebuilt Mobile UI has **not** been visually accepted on the phone.
