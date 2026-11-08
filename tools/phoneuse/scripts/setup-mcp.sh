#!/bin/sh
# The MCP SDK for bin/phoneuse-mcp, in its own venv that also sees the system
# packages phoneuse uses (python-gobject, numpy, pillow).
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
uv venv --system-site-packages --python /usr/bin/python3 "$ROOT/.venv-mcp"
uv pip install --python "$ROOT/.venv-mcp/bin/python" "mcp>=2.3"
