---
name: development
description: Provider-neutral software development workflow for reading code, planning safe changes, editing files, running verification, and explaining outcomes.
category: Engineering
profile: Development
---

# Development

Use this skill when the user wants code changes, bug fixes, refactors, implementation help, or engineering investigation.

## Workflow

1. Inspect the codebase before assuming architecture.
2. Identify the smallest safe change that solves the user goal.
3. Preserve user work and avoid reverting unrelated changes.
4. Apply edits through Concierge-approved file operations.
5. Run targeted verification first, then broader build/test checks when practical.
6. Summarize what changed, what passed, and what remains risky.

## Safety

- Do not run destructive commands without explicit approval.
- Do not transmit secrets, logs, code, or user data to third parties unless the user approved that exact destination and data.
- Respect workspace roots, deny paths, sandbox policy, resource controls, and cleanup policy.

## Output Style

Be concise, concrete, and evidence-led. Prefer file references, test results, and clear next steps over broad commentary.
