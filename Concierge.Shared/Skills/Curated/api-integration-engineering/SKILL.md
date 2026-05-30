---
name: api-integration-engineering
description: Provider-neutral API and integration engineering workflow for API design, client integrations, auth, webhooks, retries, rate limits, contracts, data mapping, testing, and operations.
category: Software and IT
profile: API and Integration Engineering
---

# API and Integration Engineering

Use this skill when the user is designing APIs, integrating third-party systems, building webhooks, handling auth, mapping data, or operating integrations.

## Workflow

1. Identify systems, data direction, auth method, rate limits, webhooks, ownership, SLAs, and failure impact.
2. Define contracts: endpoints, schemas, idempotency, pagination, errors, retries, timeouts, versioning, and compatibility.
3. Plan security: secrets, OAuth/PAT/API keys, scopes, token refresh, signature validation, least privilege, and audit logs.
4. Build resilience: queues, dedupe, backoff, circuit breakers, replay, dead-letter handling, and observability.
5. Verify with contract tests, sandbox environments, fixtures, webhook tests, and operational runbooks.

## Coverage

- REST, GraphQL, webhooks, SSE, gRPC, MCP-style tools, OAuth, API keys, HMAC signatures, SDKs, and third-party adapters.
- Data mapping, sync jobs, conflict handling, eventual consistency, rate limits, retries, idempotency, and error taxonomy.
- Integration QA, sandbox/live credentials, audit logs, monitoring, support tooling, and disconnect/revoke flows.

## Guardrails

- Do not call live APIs, store credentials, trigger webhooks, sync production data, or change scopes without approval.
- Flag privacy, overbroad permissions, destructive endpoints, data drift, vendor lock-in, and hidden billing.
- Preserve request/response evidence with secret redaction.

## Output

Produce API designs, integration plans, data maps, auth reviews, webhook specs, test plans, runbooks, and approval requests.
