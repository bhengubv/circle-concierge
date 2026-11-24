# CircleGPU and Concierge media — 24 November 2026

## Decision

CircleGPU belongs in Concierge's **local media path**, not only behind Circle AI inference. Its useful role is the device's source of truth for compute, codecs, display, heat, storage, camera and low-latency audio. The current CircleGPU service measures and reports those capabilities; it does **not** accept an image, video or audio job and return rendered media. Concierge must use a media renderer/encoder after choosing a supported path. Do not call a hardware capability report a media engine.

The existing CircleOS app contract is currently inaccessible to this separately signed Concierge APK. `ICircleGpu.getReport`, `subscribePose` and even `control` all call `enforceUse()` in `CircleGpuService.java`. The manifest declares `za.co.circleos.permission.USE_CIRCLEGPU` with `protectionLevel="signature"`; `enforceUse()` allows system/root/platform-signed callers or holders of that permission. The AIDL comment says reads are for any Circle OS app, but the implementation does not grant that access. `cmd circle.circlegpu` is a shell/system diagnostic, not a production app integration. No Concierge code should use shell commands, hidden `ServiceManager` reflection, privileged APK placement, or duplicated CircleGPU probing to bypass this boundary.

## Live AngelDust evidence

Read through the CircleGPU shell diagnostic on AngelDust, USB serial `3B221JEHN16653`. This proves what CircleGPU measured; it does not prove Concierge can call it yet.

| Media path | CircleGPU result | Concierge today | Missing integration |
| --- | --- | --- | --- |
| Images and visual scenes | Mali-G710, Vulkan 1.4.305, two compute queues, fp16 and int8; report is current (`stale=false`). | Image creation uses registered OpenAI/Stability cloud runtimes when configured; no local image-generation runtime is registered. Scene preview uses WebGL with a CSS fallback. | A local image engine or shader pipeline must execute work. CircleGPU can select a supported device/quality path and thermal budget, but has no image-generate/edit command. A GPU probe alone cannot create an image. |
| Video | 17 hardware codecs reported in total. Hardware encoders include H.264/AVC, H.265/HEVC, VP8 and VP9. Display supports 60/90 Hz and HDR types 2/3/4; HDR conversion reports unsupported. | Android uses FFmpegKit 8.1.7. `FfmpegMediaExport.VideoCodecAsync` chooses `libx264`, `libopenh264` or `mpeg4` from FFmpeg's encoder list; it does not select the measured hardware encoder. | Use a supported Android `MediaCodec` or verified FFmpegKit hardware-encoder path for rendered frames, selected from CircleGPU's codec report and validated with a real exported film. Preserve current FFmpeg fallback and quality checks. |
| Audio | `measure audio` opened an AAudio output stream, played silence for under one second and measured low-latency/exclusive mode at 48 kHz, 96 frames per burst, 192-frame buffer, zero underruns, 8.89 ms latency. Listed AAC, FLAC, Opus and AMR encoders are **software**, not hardware. | Speech uses the Circle AI audio link; media export and conversion use FFmpegKit. | Low-latency monitoring/preview can use the measured AAudio path if a supported app API is provided. Rendering and encoding stay with Circle AI/FFmpeg or another audio engine; CircleGPU's audio row is a capability and latency measurement, not a synthesizer or hardware audio encoder. |

The camera/ISP probe would open a RAW+YUV capture session. It was **not** run for this audit because it captures frames and is unnecessary to establish the image-generation contract. The GPU report contains no measured camera row until that explicit probe runs.

## Smallest service contract needed

The CircleOS owner should expose a **read-only, versioned media-capability API** to an approved Concierge signer, separate from system controls. `getReport(version)` can remain the source, or a narrower `getMediaReport(version)` can return GPU compute support, codec encode/decode flags, display, thermal, storage, audio and camera measurements with `measuredAgainst` and `stale`. A caller must not gain `control`, `revokeProfile`, display writes or privileged measurement verbs by receiving read access. The policy should work for a production-signed Concierge APK, not only the present Debug key. The app needs an ordinary supported binding mechanism; the current service-manager Binder alone is not one for a third-party MAUI app.

After that contract exists, Concierge can add one Android adapter and a platform-neutral media policy. It should choose a local media runtime from the report, decline stale/unsupported paths, and fall back to the existing renderer. Each of the three paths needs an actual output test: inspect image pixels, play video and inspect codec/frames, listen to audio and measure latency/quality. No work should be marked complete merely because `getReport` returns JSON.

## Current implementation boundary

- **No Concierge media implementation was changed in this audit.** The needed CircleGPU read contract is absent; wiring a fake client now would leave an uncallable path in the app.
- CircleAI's existing cross-app link contains chat, voice and setup verbs, not CircleGPU media reports or render jobs. Using CircleAI for speech remains valid; it does not substitute for CircleGPU's independent media capability service.
- CircleGPU v1 is a measured silicon adviser. Direct media rendering requires a media engine and a distinct job API. This separation keeps the source of hardware truth in CircleGPU without making it own every editor or codec implementation.
