# Circle AI settings in Concierge

## Delivered

Six groups appear in Concierge Settings on Desktop and Mobile: Language and replies, Voice and wake, Readiness and capabilities, Permissions and setup, Storage and housekeeping, and Trusted-device assistance.

The Android adapter consumes the published CircleAI 3.8.1 Link verbs `ResidentStatus`, `SetupReadiness`, `Abilities`, `Permissions`, and `Footprint`. It preserves service-reported states rather than converting a supported capability into readiness. Binder work runs off the UI thread, with only one native transaction outstanding; a timeout does not start another blocked transaction.

Mobile provides working actions to open the installed Circle AI controls and Android app info for **the service package**, plus separate service-owned connection approval for Chat and Voice. Neither connection button grants microphone permission or starts listening. Language/reply policy, custom wake phrases, retention/cache limits/cleanup, and borrowing remain managed in Circle AI's own screens: this released cross-app link has no general settings read/write verb. The local SDK `ISettings` interface is not a third-party IPC setter.

Windows displays its service connection, version, and reported conversation model. It labels settings not exposed by the installed Windows API. It does not fabricate phone settings, directly alter the service's files, choose/load/download models, or clean models.

The earlier Stream answers/Agentic work preferences remain default On. Streaming controls the real chat-completion request and supports full non-streamed answers when Off. Agentic work is **not operational through Concierge**: the installed Companion endpoint returned `[Companion offline — AI service not available]`; its contract also lacks the existing conversation-history and action-approval import needed for a safe handoff. The UI labels this limitation. No model-selection workaround was added.

## Measured checks

- Focused test run: **39 passed, 0 failed**. Filter: `CircleAiDeviceSettingsTests`, `CircleAiServiceSettingsTests`, `CircleAiWindowsServiceRuntimeTests`, `WorkspaceSettingsTests`. Includes malformed/unknown/denied reports, preference persistence and failed writes, serialized inference, owner-control actions, and declined Chat/Voice authorization. This is not a full-suite pass.
- A regression test for service-formatted storage sizes was first observed failing (**1 failed**) against the numeric-only parser. After correction it passes and live storage displays the returned text, without inventing units.
- Final Android build: **0 errors, 163 warnings**, 1m20.39s. Final Windows build: **0 errors, 70 warnings**, 26.29s. Dependency warnings are not treated as fixed by this change.

## Live tests

**AngelDust, Circle OS, USB `3B221JEHN16653`:** installed the updated Debug client preserving app data. Opened all six groups. Service reported conversation readiness and seven On capabilities plus Waking Ready; these are reported states, not inference execution tests. Permission rows correctly showed microphone and notifications Not allowed. Footprint displayed downloaded models 23.1 GB, memory 392 KB, skills 19 MB, voice data 23 MB, scratch/audio 356 KB; these are service-formatted values, not a new filesystem byte count.

The controls button actually opened `com.bhengubv.circleai.hybrid`. The permissions button actually opened Android app info labelled Circle AI Service, showing no runtime permissions granted. Voice settings initially returned `not linked for Voice — approve in Circle AI first`. The voice connection button opened the service's own consent screen for Concierge. Back cancelled it; Concierge displayed “Voice connection was not approved. Your settings were not changed.” No Voice grant or microphone/notification permission was given. The final installed build also includes visible token-styled action buttons.

**PixieDust, Pixel 7a, `192.168.0.140:35035`:** updated Debug client, preserving app data. Settings rendered all groups; readiness/capability reports returned `not linked for Chat — approve in Circle AI first`. The added Connect Circle AI button provides the missing approval entry point; it does not falsely report the device ready. Tapping it reached the device lock screen; the phone was not unlocked and no grant was completed. Approval and successful reports after approval remain unverified on this device.

**Desktop:** launched directly from the build output, with no Visual Studio IDE. Settings showed service Responding, version 3.8.1.0, and service-reported `Qwen3.5-0.8B-MNN`. Stream answers and Agentic work were both On after restoring temporary test choices. Unsupported Windows controls remain explicitly labelled. This is not evidence of answer accuracy or working agentic execution.

## Remaining limits

- Native editing of language, retention/cache, and borrowing inside Concierge requires a service settings contract; current Mobile actions open the owner app.
- Voice resident reports after approval are not verified; microphone remains denied on AngelDust.
- Windows has no equivalent general settings/voice/storage/borrowing endpoint in the installed contract.
- No cleanup, downloads, borrowing, voice capture, or destructive profile operations were started by this settings test.
- No Circle AI repository was read or modified; the published SDK, installed application UI, and service contract were inspected.

Screenshots in this directory show Desktop settings and AngelDust's storage panel. The service approval screen prevents screenshots; its UI hierarchy and actual cancellation result were used as evidence instead.
