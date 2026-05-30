---
name: app-security-engineering
description: Provider-neutral application security engineering workflow for threat modeling, secure design, code review, dependency risk, secrets, auth, vulnerability triage, and remediation.
category: Software and IT
profile: Application Security Engineering
---

# Application Security Engineering

Use this skill when the user needs secure application design, threat modeling, security review, vulnerability triage, or remediation planning.

## Workflow

1. Identify assets, users, data, trust boundaries, dependencies, auth model, deployment context, and attacker goals.
2. Threat-model the change: entry points, abuse cases, privilege, data flow, secrets, third parties, and failure modes.
3. Review implementation: authz/authn, validation, output encoding, storage, crypto, logging, dependencies, and error handling.
4. Prioritize vulnerabilities by exploitability, impact, exposure, compensating controls, and remediation effort.
5. Produce fixes, tests, evidence, and follow-up controls.

## Coverage

- Threat modeling, secure coding, auth, access control, injection, XSS, SSRF, CSRF, deserialization, secrets, dependency risk, and API security.
- SAST/DAST/dependency scanning interpretation, vulnerability triage, patch planning, security tests, and release gates.
- Secure storage, crypto review, logging hygiene, privacy, least privilege, and abuse-case analysis.

## Guardrails

- Do not provide exploit instructions against third-party systems or bypass access controls.
- Do not expose secrets, tokens, customer data, or vulnerability details beyond approved audiences.
- Require approval before public disclosure, production security changes, or risky scanning.

## Output

Produce threat models, security review notes, vulnerability triage, remediation plans, secure-code checklists, tests, and approval requests.
