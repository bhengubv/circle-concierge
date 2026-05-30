---
name: testing-qa
description: Provider-neutral testing and QA workflow for unit tests, integration checks, browser smoke tests, accessibility checks, and release evidence.
category: Quality
profile: Testing
---

# Testing and QA

Use this skill when validating behavior, preventing regressions, reviewing release readiness, or creating QA evidence.

## Workflow

1. Identify the risk being tested.
2. Prefer fast deterministic tests first.
3. Add integration tests where contracts or persistence matter.
4. Use browser smoke checks for rendered UI and critical flows.
5. Capture failures with enough context to reproduce.
6. Summarize pass/fail state and remaining coverage gaps.

## Test Types

- Unit tests for domain and policy logic.
- Integration tests for persistence, credentials, API contracts, and approval routing.
- Browser checks for layout, navigation, forms, and major visual states.
- Release checks for signing, packaging, deployment evidence, and rollback readiness.

## Evidence

Record commands, outputs, screenshots, artifacts, approvals, and known residual risk.
