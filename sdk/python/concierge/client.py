"""A JSON-RPC client for a Concierge agent.

Standard library only. The transport is a pair of streams — typically a subprocess running
``Concierge.Cli`` — and messages are one JSON object per line, matching the C# ``LineFraming``.
"""

from __future__ import annotations

import json
import subprocess
import threading
from typing import Any, IO


class ConciergeError(Exception):
    """The agent answered with an error."""

    def __init__(self, code: int, message: str) -> None:
        super().__init__(f"{message} (code {code})")
        self.code = code


class ConciergeClient:
    """Drives one Concierge session.

    Replies are matched by id rather than by arrival order, because a server may answer out of
    order and two callers must never receive each other's answers.
    """

    def __init__(self, stdin: IO[bytes], stdout: IO[bytes]) -> None:
        self._stdin = stdin
        self._stdout = stdout
        self._next_id = 0
        self._lock = threading.Lock()

    @classmethod
    def spawn(cls, command: list[str]) -> "ConciergeClient":
        """Start an agent as a subprocess and talk to it over its pipes."""
        process = subprocess.Popen(
            command,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
        )
        if process.stdin is None or process.stdout is None:
            raise ConciergeError(-1, "The agent process exposed no pipes.")
        client = cls(process.stdin, process.stdout)
        client._process = process
        return client

    def initialize(self) -> dict[str, Any]:
        """Complete the handshake and learn what is on the other end."""
        return self._invoke("initialize", None)

    def new_session(self, owner_id: str = "local", system_prompt: str | None = None) -> str:
        """Start a conversation and return its id."""
        params: dict[str, Any] = {"ownerId": owner_id}
        if system_prompt is not None:
            params["systemPrompt"] = system_prompt
        return self._invoke("session/new", params)["sessionId"]

    def prompt(self, session_id: str, text: str) -> str:
        """Ask something and return the answer."""
        result = self._invoke("session/prompt", {"sessionId": session_id, "prompt": text})
        return result["output"]

    def history(self, session_id: str) -> list[dict[str, str]]:
        """Everything said in a conversation so far."""
        return self._invoke("session/history", {"sessionId": session_id})["turns"]

    def _invoke(self, method: str, params: dict[str, Any] | None) -> dict[str, Any]:
        with self._lock:
            self._next_id += 1
            request_id = self._next_id

            message: dict[str, Any] = {"jsonrpc": "2.0", "id": request_id, "method": method}
            if params is not None:
                message["params"] = params

            self._stdin.write((json.dumps(message) + "\n").encode("utf-8"))
            self._stdin.flush()

            # Read until the reply carrying this id arrives. Anything else on the pipe is a
            # reply to another call or a notification, and neither is ours to consume.
            while True:
                line = self._stdout.readline()
                if not line:
                    raise ConciergeError(-1, "The agent closed the connection.")

                try:
                    reply = json.loads(line.decode("utf-8"))
                except json.JSONDecodeError:
                    # A peer can send anything; one bad frame is not the end of the session.
                    continue

                if reply.get("id") != request_id:
                    continue

                if "error" in reply:
                    error = reply["error"]
                    raise ConciergeError(error.get("code", -1), error.get("message", "unknown"))

                return reply.get("result", {})
