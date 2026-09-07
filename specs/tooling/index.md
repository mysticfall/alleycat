# Developer Tool Layout

## Requirement

Define the authoritative ownership, layout, and execution contracts for manually invoked developer tools.

## Goal

Developers and technical artists can find each tool, its supported commands, and its operating environment without
searching unrelated project directories.

## User Requirements

1. Developers can find every manually invoked developer tool under the top-level `tools/` directory.
2. Each tool provides a concise operational README that identifies supported commands, prerequisites, inputs, outputs,
   and verification boundaries.
3. Technical artists can run Kimodo-owned external GDScript while `game` remains the Godot `res://` project root.

## Technical Requirements

1. The authoritative tool roots are:
   - `tools/character-generator/`;
   - `tools/kimodo-pipeline/`; and
   - `tools/gdscript-api-doc-generator/`.
2. Each tool owns its manually invoked commands, command implementation, configuration where required, tests, fixtures,
   and operational README. Commands are the supported user-facing entry points; implementation files are not.
3. Every tool README is normatively linked from its ownership specification and documents commands that work from the
   repository root using actual paths and arguments.
4. Kimodo's external GDScript commands and libraries remain under its tool root. They run with
   `godot-mono --path game --script <absolute-script-path>`, preserving `game` as `res://`. External tooling retains no
   `.gd.uid` sidecars.
5. Physical tool, test, configuration, and asset names must not contain `anim002`. Compatibility-sensitive persisted
   schema, property, provenance, and package-identity identifiers may retain that token and must be documented as such.

## In Scope

- Ownership, command boundaries, README requirements, and Kimodo's external-GDScript environment.
- Navigation to each tool specification and operational README.

## Out Of Scope

- Tool behaviour owned by the individual tool and content specifications.
- Renaming compatibility-sensitive persisted identifiers.

These exclusions do not defer required command, ownership, README, environment, or validation contracts.

## Acceptance Criteria

### User Requirement Acceptance

1. A developer can navigate from [Project Specifications](../index.md) to this page, a tool specification, and its
   operational README.
2. A technical artist can identify the Kimodo external-GDScript invocation and preserved `game` `res://` root.

### Technical Requirement Acceptance

1. The three tool roots and their ownership boundaries are unambiguous.
2. Each tool specification normatively links its actual operational README and supported command locations.
3. Kimodo's external scripts use an absolute tool-owned path with `godot-mono --path game --script` and no `.gd.uid`
   sidecar requirement.
4. Physical names exclude `anim002`; retained compatibility identifiers are explicitly distinguished from physical
   names.

## References

- [Character Generator](character-generator/index.md)
- [Kimodo Locomotive Animation-Set Pipeline](kimodo-pipeline/locomotive-animation-set.md)
- [GDScript API Documentation Generator](gdscript-api-doc-generator/index.md)
