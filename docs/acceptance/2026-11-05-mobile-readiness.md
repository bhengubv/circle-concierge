# Mobile readiness display, 5 November 2026

REDMI12 answered a local question through Circle AI Service while Concierge still showed an indeterminate “preparing” bar and said text readiness was not confirmed. The service's live status contained no text-readiness phase. Concierge had been mapping every unreported phase to UI preparation, even when neither a model load nor a download was reported.

The client now distinguishes an active load or download from an unknown readiness report. The latter keeps queued questions available, displays no preparation bar, and, after a successful local answer, says the last question was answered on this device without claiming the model is currently ready. It does not invent a percentage or override the service's readiness decision.

The focused readiness, service-runtime and composer tests passed: 65 passed, 0 failed, 0 skipped. The Android Debug APK built with 0 errors and 17 warnings. The existing 16 KB native-library warnings remain. REDMI12 moved into SleptON before installation, so the new display has **not** yet been checked on the phone. No response-quality improvement is claimed; the salon planning task remains failed.
