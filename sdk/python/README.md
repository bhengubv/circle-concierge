# Concierge Python SDK

Talks to a Concierge agent over JSON-RPC. Standard library only — no packages to install and
nothing that can be withdrawn.

```python
from concierge import ConciergeClient

client = ConciergeClient.spawn(["dotnet", "run", "--project", "Concierge.Cli", "--", "--serve"])
client.initialize()

session = client.new_session(system_prompt="You are Bell.")
print(client.prompt(session, "What shall we do today?"))
```

The transport is one JSON object per line, matching the C# `LineFraming`, and the methods are
the same ones `AcpServer` registers: `initialize`, `session/new`, `session/prompt`,
`session/history`.
