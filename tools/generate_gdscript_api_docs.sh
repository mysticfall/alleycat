#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$ROOT_DIR/.." && pwd)"

SOURCE_ROOT_INPUT="${1:-game/scripts}"
OUTPUT_PATH_INPUT="${2:-temp/gdscript-api.md}"
MAX_SUMMARY_CHARS="${MAX_SUMMARY_CHARS:-120}"

if [[ "$SOURCE_ROOT_INPUT" = /* ]]; then
  SOURCE_ROOT="$SOURCE_ROOT_INPUT"
else
  SOURCE_ROOT="$REPO_ROOT/$SOURCE_ROOT_INPUT"
fi

if [[ "$OUTPUT_PATH_INPUT" = /* ]]; then
  OUTPUT_PATH="$OUTPUT_PATH_INPUT"
else
  OUTPUT_PATH="$REPO_ROOT/$OUTPUT_PATH_INPUT"
fi

python3 "$ROOT_DIR/generate_gdscript_api_docs.py" \
  "$SOURCE_ROOT" \
  --output "$OUTPUT_PATH" \
  --max-summary-chars "$MAX_SUMMARY_CHARS"

printf 'Generated: %s\n' "$OUTPUT_PATH"
