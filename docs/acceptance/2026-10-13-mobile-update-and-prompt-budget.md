# Mobile update and prompt measurement — 13 October 2026

## Mobile deployment

Built `Concierge/Concierge.csproj`, net10.0-android, android-arm64, Debug: zero errors, 153 warnings. Installed the resulting signed debug APK in place on both devices; both ADB installs reported Success. Existing app data was retained.

- AngelDust / CircleOS: package lastUpdateTime 02:39:11; app launched, retained its previous conversation and displayed a Desktop connection.
- PixieDust / AndroidOS: package lastUpdateTime 02:40:08; app launched to the starting-point screen and displayed Desktop connected.

Screenshots: `2026-10-13-angeldust-update.png`, `2026-10-13-pixiedust-update.png`. The incorrect 1+1 = 3 on AngelDust is an old saved answer, not a new passing test.

Desktop's live presence response at 02:43:26 listed all four endpoints connected: Desktop, AngelDust, PixieDust, StarDust. This proves foreground LAN presence, not remote Internet access, mobile local inference, or complete functional parity.

## Real Desktop prompt measurement

Added content-free diagnostics to the Windows service client and Debug LAN bridge. These retain counts only, not messages or API keys. Heuristic token estimates are not exact tokenizer measurements or a proven backend context limit.

Actual fresh Desktop conversation `93400adf-3296-4f51-b332-3256f7d3bade`, prompt: What is eight plus five? Answer with the number only.

- Selected resident model: Qwen3.5-0.8B-MNN.
- Outgoing message count: 2.
- Characters: 15,476.
- Estimated prompt tokens: 3,919; estimated system tokens: 3,901.
- Concierge's configured compaction budget: 4,096. Compaction currently runs BEFORE identity, skills, plan instructions and tool catalogue are inserted.
- Recorded start: 02:38:05.020; recorded end: 02:38:44.194. Difference: 39.174 seconds.
- Actual answer: **10.** — incorrect. Receipt says completed; this marks transport completion, not answer correctness.

Seven service-client tests passed, including the new diagnostics test. Windows build: zero errors, 70 warnings. Android build completed while this test was running, so latency is an observation under concurrent build load, not a controlled benchmark.

## Next work

Account for the complete outgoing prompt and response reserve; keep tool discovery usable without spending almost the entire configured budget on unsolicited schemas. Verify the service's intended inference configuration and automatic suitable-model selection. Do not remove capabilities, add question-specific answer rules, or call the accuracy defect fixed based on payload-size evidence alone.

Logs and raw measurements: `%TEMP%/ConciergeDeviceProof/prompt-metrics-tests.log`, `prompt-metrics-build.log`, `prompt-measured-20261013.json`, `mobile-update-20261013.log`, `all-devices-20261013.json`.
