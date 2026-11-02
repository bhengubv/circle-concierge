# Concierge sign-in and pairing boundary — 3 November 2026

This is a source audit against the SDPKT sign-in handoff, not a live sign-in acceptance test. No device, SMS account, payment, or external service was exercised.

| Boundary | Concierge today | Required outcome |
| --- | --- | --- |
| Local phone access | Android starts the MAUI workspace; no Concierge-specific local identity/lock gate was found in its routes or `App.xaml.cs`. Circle AI Binder authorization is service access, not a person signing in. | Decide whether the phone's OS unlock is sufficient for Concierge's private conversations or add an app lock. Do not call local unlock an online account session. |
| Online account | `ButlerAccountSession` signs in to TGN AuthAPI with email/mobile and password, handles a second factor, stores rotating tokens in MAUI SecureStorage, and refreshes access tokens. This is Butler integration, not a Concierge identity. Web deliberately disables it. | Keep this provider link separate from Concierge sign-in and from transaction approval. Do not copy SDPKT's wallet SMS endpoint into Concierge without its own account contract. |
| Desktop/Web Concierge authorization | Desktop MAUI opens directly. Web has an optional global `X-Concierge-Key`; with no key the middleware passes requests through. Neither host has a QR challenge, phone approval, person-bound session, revocation, or device list. | Web/Desktop show a short-lived QR challenge. The corresponding signed-in phone scans and approves it. Issue a device-bound session only after approval; support expiry, rejection, revocation, and restart. |
| Data ownership | Web registers one SQLite conversation store at a process-wide local path, with a singleton cache. The conversation model has `OwnerId`, but the workspace hardcodes `"local"` and several reads accept only a conversation ID. The optional API key is global rather than person-bound. | Bind every conversation, project, artifact, and Away operation to the approved person and device before exposing a multi-user web host. A QR screen alone would not make the shared store private. |
| Phone-to-Desktop link | The Desktop `DEBUG` bridge on port 5198 is explicitly unauthenticated. Mobile saves a reachable address and checks capabilities; reachability was labelled “Desktop connected.” | Keep this as proof networking. Production handoff needs paired TLS and authorization on every read/write endpoint. Reachability alone must not be presented as QR authorization. |
| Session retention | The UI `SessionSnapshot` persists drafts and preferences. It is not an authentication session. Butler tokens persist in SecureStorage and refresh, but there is no Concierge 20-minute background grace or explicit app lock contract. | Specify and test local and online retention separately, including background/resume, manual lock, token renewal, and cold restart. Never treat a cached UI draft as proof of identity. |
| Transaction confirmation | No Concierge transaction authorization flow was found in this scope. Butler account linking explicitly does not start an operation. | If Concierge invokes financial actions, require a distinct approval immediately before the request; cancellation must send nothing. |

## First safe correction

The handheld workspace now says **Desktop reachable · test link** and states that the address link is not QR-authorized. The ready message also says QR authorization is not set up. This changes presentation only; it does not claim to secure the bridge.

## Implementation order

1. Define a person-owned identity and storage boundary for Concierge. Keep Butler's separate account link separate.
2. Define an expiring QR challenge and phone approval protocol, including a single-use challenge, device identity, session renewal, revocation, and audit events.
3. Enforce the resulting session on Web and Desktop/Away reads and writes, and isolate stores by person. Do not replace the global API key with a static QR-encoded secret.
4. Implement the phone scanner/approval surface, then Web/Desktop waiting, approved, rejected, and expired states.
5. Prove two-person isolation, cancel/replay/expiry/revocation, background/resume, cold restart, and cross-network reconnect in running apps. No release claim until these pass.

The SDPKT reference's 20-minute background retention is built but not phone-verified. Its wallet-specific SMS and transaction contracts are not Concierge contracts.

The shared TGN `IAuthApiClient` reference already specifies mobile login initialization, six-digit verification, additional-factor handling, current-user validation, refresh, and a push-to-app web-auth challenge. Concierge does not currently reference that shared client. The existing push-to-app screen displays a matching code rather than a QR; Concierge must not claim that this is the requested QR flow. Any phone approval endpoint must validate the phone's current TGN bearer identity with AuthAPI before it can approve a Concierge challenge. The browser's private verifier must not appear in the QR payload.

`QrPairingChallenges` is a small in-memory foundation for two-minute, single-use challenge state. `TgnCurrentUserVerifier` checks a phone bearer with AuthAPI's existing `GET /api/auth/me` contract before that bearer can establish an owner. Seven focused tests pass for private browser proof, replay rejection, denial, expiry, and accepted/rejected account identity. Neither component is registered as a sign-in endpoint or issues a session; this is deliberately not counted as a working QR sign-in.
