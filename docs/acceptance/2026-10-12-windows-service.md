# Windows Circle AI service transition — 12 October 2026

Follow-up: [13 October owned storage and actual StarDust round trip](2026-10-13-owned-service-storage.md)
supersedes the installation/storage blockers below and records the remaining
answer-quality failure. This older report preserves the original failed attempts.

This report supersedes the earlier **Windows service not installed** finding.
It does not claim that a running service proves inference works.

## Measured state

- Released Circle AI 3.8.1 service installed as `CircleAI.Inference.Server`.
- SCM reports Running, automatic startup, virtual service account
  `NT SERVICE\CircleAI.Inference.Server` and configured restart recovery.
- Authenticated loopback `GET /v1/models`: HTTP 200. Without API key: HTTP 401.
- Windows Concierge registers the HTTP service runtime instead of its isolated
  model worker; the new running Desktop has no `Concierge.Model.Host` child.
- Latest Windows Debug build: 0 errors, 70 warnings. Package vulnerability
  warnings remain; these have not been suppressed.
- Cached model presence is checked separately from the SDK selector's download
  flag. Eligible cached candidates retain the SDK's ranking; no model download
  was started to establish this connection.

## Actual failure and pending repair

The new Desktop's capability response reported `canAnswer: false`. The service
model list was empty. Its Windows Application event at 07:24:08 named the real
cause: `UnauthorizedAccessException` at `C:\Users\user\AppData`, while the
released model factory called `Directory.CreateDirectory` on its model path.
The existing model-cache grant did not provide the required ancestor access.

The Concierge-owned installer now grants non-inherited directory access on the
cache ancestors. It does not grant inherited access to the user's profile.
The user approved the repair. The ancestor ACE was verified as non-inherited,
and the restarted service's authenticated `/v1/diagnostics` returned HTTP 200,
`server_version: 3.8.1.0`, and its selected CPU / Tier1_Small backend profile.
The client now uses those service-selected backend/tier values and reads the
actual `server_version` field.

The next measured blocker is memory: the Windows probe reported 332,849,152
available bytes (about 333 MB), while WSL's working set was 22,287,089,664 bytes.
The model selector found no eligible model under current resource pressure.
Concierge now retries readiness sequentially every minute without starting
model downloads. The user has been asked how to free memory; WSL has not been
stopped.

On the next restart, the selector found an eligible cached candidate and the
client attempted a model load. That request still failed with a profile-path
permission error, this time at `AppData\Local`. Successful diagnostics therefore
did **not** establish successful model-factory initialization. The installer
now also sets explicit `CircleAIServer__ModelStorageRoot` and
`CircleAIServer__RuntimeCacheRoot` SCM environment entries, with no broader
profile-content permissions. The user approved the second Windows dialog, the
entries were read back from SCM, and the restarted service was PID 36336.
An actual retry at 07:42:52 still failed in the model factory with the
`AppData\Local` access error. The environment-path repair is therefore **not
shown to fix loading**. Configuration binding and the effective path are being
investigated using the released binary, without accessing its source repository.
Model loading, a correct streamed answer, and the watch-to-service journey
remain **unverified**.

The released server's configuration binding was separately exercised with its
installed JSON files and actual `AddCircleAIInferenceServer` registration. It
resolved the intended model and runtime roots. This test ran as the current
user, not the service account, and therefore does not prove the account can
access the paths.

SCM `qsidtype` then reported `NONE`, and `showsid` reported `Inactive` for the
same service SID present in the cache ACL. The installer now sets
`sidtype unrestricted` on install and reconfiguration. Applying this change is
pending a third Windows administrator dialog. It is a measured configuration
gap, not yet a verified cause/fix of the model-load failure. The latest service
model list remains empty; Desktop reports `canAnswer: false`.

## Watch receipt regression

`Finished_answer_survives_the_watch_connection_dropping_after_acceptance`
failed before the fix: the completed receipt write used the disconnected HTTP
caller's cancellation token. The completion write now outlives that connection.
After the fix, all 7 `AwayRequestLedgerTests` passed (0 failed, 0 skipped).
This proves durable receipt behavior in tests, not physical watch acceptance.

Evidence logs are in `%TEMP%\ConciergeDeviceProof`:

- `windows-service-client-final-build.log`
- `watch-disconnected-receipt-red.log`
- `watch-disconnected-receipt-green.log`

Only Concierge source and released service installation material were inspected
for this transition. No other repository was accessed.
