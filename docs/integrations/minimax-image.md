# MiniMax H3 Image in Concierge

Concierge's shared Images room can create a picture or edit up to four selected photos. The optional MiniMax adapter uses the official `/v1/images/generations` and `/v1/images/edits` API routes. A creator's image instructions and selected photos leave the device only when they press the action button. On Mobile, keys saved in Settings are loaded at the next app start. No model weights are bundled.

Set `MiniMaxImages:ApiKey` in Settings → Keys, then restart Concierge. Without a key, the adapter reports that it is unavailable and sends no request. The provider returns image bytes, which the room shows in the current session. Persistent image history and a reviewed release path to SleptON! are still open work.

The official BF16 H3-Image checkpoint is 68.65 GB and also requires a separate 66.7 GB text encoder. The claimed 6 GB **system RAM** requirement is not established by the official model documentation. Local Desktop inference needs a measured machine, a supported quantized/offloaded runtime, and an actual generation benchmark before Concierge can offer it. This adapter is a cloud API path, not proof of local inference. H3-Image handles still images; MiniMax video generation is a separate model and workflow.

The provider contract and shared UI have focused tests for creation, ordered reference editing, invalid or missing credentials, per-image errors, upload gating, and result display. The Android and Windows apps build. A live API call, image persistence across app restarts, PixieDust visual acceptance, and SleptON! publishing are not yet verified. Review the [MiniMax H3-Image license](https://github.com/MiniMax-AI/MiniMax-H3-Image/blob/main/LICENSE) and API terms before offering a commercial or hosted image service.

Primary references: [model and API examples](https://github.com/MiniMax-AI/MiniMax-H3-Image), [API reference script](https://github.com/MiniMax-AI/MiniMax-H3-Image/blob/main/examples/api_generate.py).
