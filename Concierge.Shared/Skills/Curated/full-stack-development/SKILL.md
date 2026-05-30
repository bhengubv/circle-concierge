---
name: full-stack-development
description: Provider-neutral full-stack delivery workflow covering UX, API, domain logic, persistence, tests, deployment readiness, and observability.
category: Engineering
profile: Full Stack Development
---

# Full Stack Development

Use this skill when a feature crosses UI, backend services, persistence, integrations, release checks, or business workflows.

## Workflow

1. Map the feature from user action to data model to persistence to external effects.
2. Define trust boundaries: local-only, workspace-write, network, credentials, approvals, and audit evidence.
3. Implement vertical slices that can be built and tested independently.
4. Add or update tests at the lowest useful level.
5. Verify the UI path and the service/API path.
6. Capture operational evidence: logs, errors, release impact, and rollback notes.

## Design Principles

- Keep provider adapters behind interfaces.
- Keep secrets in protected storage.
- Treat integrations as approval-gated until proven safe.
- Prefer durable state and resumable workloads for long-running tasks.

## Done Means

Build passes, tests pass, user-facing path renders, and the feature has clear audit or evidence output where applicable.
