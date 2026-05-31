#!/usr/bin/env bash
set -euo pipefail

usage() {
  printf 'Usage: %s <character-config.json>\n' "${0##*/}" >&2
  printf '\n' >&2
  printf 'Runs Blender in background mode with tools/generate_character.py.\n' >&2
  printf 'Set BLENDER_BIN to override the Blender executable (default: blender).\n' >&2
}

if [[ $# -ne 1 ]]; then
  usage
  exit 2
fi

SOURCE="${BASH_SOURCE[0]}"
while [[ -L "$SOURCE" ]]; do
  SCRIPT_DIR="$(cd -P "$(dirname "$SOURCE")" && pwd)"
  SOURCE="$(readlink "$SOURCE")"
  if [[ "$SOURCE" != /* ]]; then
    SOURCE="$SCRIPT_DIR/$SOURCE"
  fi
done

SCRIPT_DIR="$(cd -P "$(dirname "$SOURCE")" && pwd)"
GENERATOR_PATH="$SCRIPT_DIR/generate_character.py"
BLENDER_EXECUTABLE="${BLENDER_BIN:-blender}"
CONFIG_PATH="$1"

exec "$BLENDER_EXECUTABLE" --background --python "$GENERATOR_PATH" -- "$CONFIG_PATH"
