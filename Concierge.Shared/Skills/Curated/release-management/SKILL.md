---
name: release-management
description: Provider-neutral release workflow for preflight checks, compliance, signing, packaging, deployment adapters, store submissions, and evidence bundles.
category: Release
profile: Release Management
---

# Release Management

Use this skill when preparing web, desktop, mobile, or backend releases across Apple, Android, Linux, Windows, web, and cloud targets.

## Workflow

1. Select release target and channel.
2. Run preflight checks: restore, build, test, lint, format, smoke, accessibility, and packaging.
3. Validate signing and credentials.
4. Check store or repository compliance.
5. Require approval before uploads, submissions, financial actions, or public communication.
6. Capture artifacts, release notes, screenshots, audit trail, and rollback plan.

## Platform Coverage

- Web: TLS, CSP, hosting, rollback, monitoring.
- Android: keystore, Play signing, target SDK, permissions.
- Apple: bundle id, provisioning, certificates, notarization/TestFlight.
- Windows: MSIX/installer signing, Store/winget metadata.
- Linux: Flatpak, Snap, AppImage, repository credentials, checksums.

## Done Means

The release has build artifacts, signing status, QA evidence, approval history, deployment action plan, and known risks documented.
