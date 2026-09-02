"""Concierge client.

Talks to a Concierge agent over JSON-RPC on a pipe — the same surface the Agent Client
Protocol server exposes. Written against the protocol rather than a package, so it depends on
nothing but the Python standard library and cannot be taken away.
"""

from .client import ConciergeClient, ConciergeError

__all__ = ["ConciergeClient", "ConciergeError"]
