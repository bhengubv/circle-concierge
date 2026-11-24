# CircleOS native Circle AI and CircleGPU integration — 24 November 2026

## Integration contract found in CircleOS

- CircleOS installs native Circle AI as the system package `za.co.circleos.circleaiservice`. Its exported `CircleNeuronLinkService` accepts `com.bhengubv.circleai.action.LINK`, descriptor `com.bhengubv.circleai.link.v1`, protocol 2. `LinkConsentActivity` accepts `com.bhengubv.circleai.action.LINK_CONSENT`. This is the cross-app contract that Concierge's existing CircleAI link client can use. The CircleOS implementation is in `modules/circleos-circleai/lib/AndroidManifest.xml` and `modules/circleos-circleai/lib/CircleAI.Linking/src/.../LinkIpc.java`.
- The CircleAI 3.9.1 SDK still names the standalone package `com.bhengubv.circleai.service`. Concierge now checks for the native **system** package and its exported link service, chooses it on CircleOS, and otherwise keeps the standalone host. It uses one host for the app process lifetime. Permission approval and all verbs target that same selected package.
- CircleOS also publishes `circle.circleai` (`ICircleAI`), but its separate host service is protected by a signature bind permission. The exported link is the appropriate cross-app route for Concierge.
- CircleGPU publishes `circle.circlegpu` (`ICircleGpu`). Its `USE_CIRCLEGPU` permission is signature-only, so this separately signed Concierge Debug APK cannot call it directly. CircleAI's `Engine.java` queries CircleGPU for capabilities and backend choice. Concierge should send work to CircleAI and let CircleAI choose the backend. If Concierge needs direct GPU telemetry later, CircleOS needs a supported read-only client contract and permission policy.

## Device evidence

- AngelDust USB serial `3B221JEHN16653` runs CircleOS. The native CircleAI package is version name `16`, code `36`, and native CircleGPU is installed. Service manager lists `circle.circleai`, `circle.circleai.inference`, and `circle.circlegpu`.
- `cmd circle.circleai status` reported `hostBound=true`, `modelLoaded=true`, model `Ternary-Bonsai-2-27B-PQ2_0`, `validated=false`.
- `cmd circle.circlegpu report 1` reported a Vulkan decision for MNN on Mali-G710, while llama.cpp was CPU. This is capability reporting, not evidence that the observed answer used GPU.
- Rebuilt Concierge Android arm64 Debug APK twice after the final source change: second build passed with **0 errors, 13 warnings**, including 16 KB native-library warnings from CircleAI 3.9.1 and ONNX Runtime. Installed it on AngelDust with `adb install -r`.
- The actual Concierge screen changed from the previous “Circle AI service could not respond” to **“Conversation — Ready”** and **“Circle AI can answer on this phone”**. [Status screenshot](images/angel-native-status.png).
- Submitted the original-game question through Concierge: “Do you think you can help me create a Donkey Kong knock-off?” The UI showed **“This phone · Waiting for reply”**, and the request was observed Running on `Ternary-Bonsai-2-27B-PQ2_0`.
- The service marked the request Succeeded after `TotalMs=120053`, but its timing says `Finish="deadline"`, `TtftMs=94247`; the only displayed reply was **“Implottie”**. [Result screenshot](images/angel-native-reply2.png). This is a failed user outcome despite a working client link. The model/service team's request-completion semantics and model validation need attention; Concierge should not present a deadline fragment as a satisfactory answer.
- Concierge's queued-chat client was then changed to reject terminal results whose finish reason is `deadline`, `length`, `cancel`, or `error`. The four new cases passed, and the focused queued-chat class passed **17/17**. This guards the user-facing result; it does not repair the model's answer. The first live screenshot above predates this guard.
- Rebuilt and installed the final Debug APK with that guard (0 errors, 4 existing native-library warnings). Submitted a second, shorter original-game prompt. The service returned `state=Failed` this time; Concierge displayed **“This run failed”** and no new assistant answer. [Final-build screenshot](images/angel-final-terminal.png). This verifies failure display and persistence behavior in the final build. The `deadline` branch itself was exercised by focused tests, not by this second live request because the service returned Failed instead.

## What is verified

Native package discovery, cross-app authorization, link transport, status read, queued request submission, status polling, and return of a terminal result all worked from Concierge on AngelDust. A useful answer, successful GPU execution of this request, and the full four-turn original-game task are **not verified**. No CircleOS source or installed service was changed.
