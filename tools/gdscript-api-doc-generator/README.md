# GDScript API Documentation Generator

This tool generates structural Markdown references for GDScript. Its normative contract is
[GDScript API Documentation Generator](../../specs/tooling/gdscript-api-doc-generator/index.md).

## Commands

Run from the repository root. Generate the default source root to standard output:

```sh
tools/gdscript-api-doc-generator/commands/generate_gdscript_api_docs.sh
```

Generate a selected source root and write a file:

```sh
tools/gdscript-api-doc-generator/commands/generate_gdscript_api_docs.sh game/scripts \
  --output docs/gdscript-api.md
```

Optional flags are `--include-private`, `--include-all-vars`, and `--max-summary-chars <N>`. Use `--help` to inspect
the complete command interface.

## Usage Boundary

The command reads GDScript source and writes Markdown only. It does not validate GDScript execution, generate Godot
built-in documentation, or produce APIs for other languages.
