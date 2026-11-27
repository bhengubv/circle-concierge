# Concierge security boundary — 27 November 2026

This review covers the Concierge Web host and its shared Desktop/Mobile dependency graph. It is not a penetration test or a public Web release sign-off.

## Checks completed

The complete Concierge test project passed on this branch: 2,788 passed, 0 failed, 0 skipped. Its coverage gate passed at 77.67% line, 66.54% branch, and 82.29% method coverage. The secured Concierge Code test project passed separately: 9 passed, 0 failed, 0 skipped. These are code checks; the embedded Desktop launch has not been visually accepted in this review.

| Boundary | Enforced behavior | Verification |
| --- | --- | --- |
| Anonymous Development Web access | Only a loopback peer requesting a loopback host may enter. Foreign `Origin` values, including cross-port origins, are rejected. A LAN caller cannot use Development mode as an anonymous bypass. | `ApiKeyAuthTests` includes allowed loopback, remote peer, forged Host, and foreign Origin cases. |
| Keyed remote Web access | Direct non-loopback HTTP is rejected even with the correct key; HTTPS is accepted. A separate reverse proxy must terminate TLS before forwarding over its trusted local connection. | `ApiKeyAuthTests` exercises both transports with the same key and remote peer. |
| Published Web access | Startup still requires `CONCIERGE_API_KEY` or `Auth:ApiKey`. `/metrics` now requires the key; `/healthz` and static assets remain public. | `ApiKeyAuthTests` checks startup and exact path exceptions. |
| Host and browser boundaries | Default allowed hosts are loopback and `studio.circleos.co.za`. The host sends `frame-ancestors 'none'`, `X-Frame-Options: DENY`, `nosniff`, and `Referrer-Policy: no-referrer`. A deployment with another hostname must explicitly configure `AllowedHosts`. | `WebSecurityHeadersTests`; `Concierge.Web/appsettings.json` reviewed. [ASP.NET Core host filtering](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/host-filtering?view=aspnetcore-10.0). |
| Voice API resource use | Transcription is capped at a 6 MiB HTTP body and 4 MiB decoded audio. Speech synthesis is capped at a 32 KiB HTTP body and 4,000 text characters. Uploaded file names lose directory components. | `VoiceEndpointSecurityTests` checks route size metadata and path stripping. [ASP.NET Core request limits](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.requestsizelimitattribute?view=aspnetcore-10.0). |
| Package advisories | `dotnet list package --vulnerable --include-transitive` reported no vulnerable packages for `Concierge.Web` or `Concierge` from the configured NuGet sources on this date. | Command output from this review; this is a point-in-time advisory check, not a code audit. |

## Release boundaries still open

- A global API key is a host gate, not a person's sign-in. Ordinary browser navigation cannot supply the custom header. The public Web app still needs the planned phone-approved QR session and person-bound authorization on every conversation, project, artifact, and Away endpoint. `2026-11-03-auth-boundary-review.md` records the missing contract. Do not expose a shared Web store as a multi-person service yet.
- A Desktop/Mobile address that answers over LAN is only reachable. It is not paired. Remote mobile access needs authenticated pairing and a protected transport before private work crosses that link. The new gate deliberately rejects anonymous LAN access and keyed cleartext HTTP.
- Local SQLite content and the JSON secret store rely on OS account permissions. They are not encrypted by these changes. Device-level encryption, backups, export, and loss-of-device behavior need separate acceptance.
- The HTTP middleware tests prove their decisions, but a deployed reverse proxy, TLS termination, phone QR flow, and two-person isolation have not been tested end to end here.
- Concierge Code uses a one-time URL fragment for local launch and a scoped HttpOnly session cookie after exchange. The shared Desktop route builder and ready contract were updated for that protocol. A person using the Desktop window interrupted the visual acceptance, so the integrated WebView2 flow remains unverified here.
