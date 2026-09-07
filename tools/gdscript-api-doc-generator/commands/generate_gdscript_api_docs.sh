#!/usr/bin/env bash
set -euo pipefail

COMMANDS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TOOL_ROOT="$(cd "$COMMANDS_DIR/.." && pwd)"

exec python3 "$TOOL_ROOT/implementation/gdscript_api_docs.py" "$@"
