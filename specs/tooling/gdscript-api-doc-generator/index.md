# GDScript API Documentation Generator

## Requirement

Generate concise Markdown API documentation for GDScript source files.

## Goal

Developers can generate a structural GDScript reference through one supported, repository-root command.

## User Requirements

1. Developers can generate documentation for GDScript classes with configurable filtering and summary length.
2. Output can be written to standard output or a requested file.

## Technical Requirements

1. `tools/gdscript-api-doc-generator/` owns this workflow. Its sole supported entry point is
   `tools/gdscript-api-doc-generator/commands/generate_gdscript_api_docs.sh`.
2. The command delegates to the tool-owned implementation. Consumers must not invoke the implementation file directly.
3. [The operational README][readme] is normative for invocation, inputs, output, flags, and verification.
4. The command accepts an optional source root, defaults to `game/scripts`, and supports `--output`,
   `--include-private`, `--include-all-vars`, and `--max-summary-chars`.
5. The generated Markdown reports class name, inheritance, constants, signals, exported variables, functions, and
   `##` documentation comments. A missing or non-directory source root fails.

## In Scope

- GDScript structural parsing, command-line options, and Markdown generation.
- Canonical command ownership and operational documentation.

## Out Of Scope

- HTML output, cross-class dependency analysis, Godot built-in documentation integration, and non-GDScript APIs.

These exclusions do not defer the command, output, or invalid-input contracts.

## Acceptance Criteria

### User Requirement Acceptance

1. The README gives a repository-root command for standard output and file output.
2. Optional private-member, all-variable, and summary-length controls are discoverable.

### Technical Requirement Acceptance

1. The canonical command delegates to the tool-owned implementation and accepts the documented flags.
2. Valid output is structured Markdown; a missing or non-directory source root fails.
3. The tool specification links normatively to its operational README and command location.

## References

- [Developer Tool Layout](../index.md)
- [Operational README][readme]

[readme]: ../../../tools/gdscript-api-doc-generator/README.md
