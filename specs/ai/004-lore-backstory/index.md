---
id: AI-004
title: Lore And Backstory Source Compilation
---

# Lore And Backstory Source Compilation

## Requirement

The project must support per-game local Markdown lore roots as the human source of truth for lore and backstory, with
deterministic incremental compilation into graph-compatible derived artefacts under `lore/<game>`.

## Goal

Authors should be able to brainstorm and curate lore in ordinary Markdown while giving AI tooling enough structure to
validate concepts, relationships, aliases, and unresolved suggestions without taking over canon decisions.

## User Requirements

1. Authors can write lore/backstory as local Markdown pages with prose, frontmatter, aliases, tags, and wiki links.
2. Authors can start with unstructured notes and receive suggestions for concept pages, relationships, duplicate merges,
   and missing stubs before those suggestions become canon.
3. Authors can define game-specific node and relation types without changing runtime code or OpenCode instructions.
4. Re-running compilation against unchanged source must keep derived graph output stable and reviewable.
5. Canon remains controlled by the human-authored wiki and approved ontology files, not by generated graph artefacts.
6. Lore-management agents must always know which game lore root they are working on before reading or changing lore.

## Technical Requirements

1. Lore roots live under `lore/<game>`, where `<game>` is a subdirectory name selected for the current task.
2. If the game context is not specified, lore-management agents must enumerate subdirectories under `lore` and ask the
   user to choose with the `question` tool before continuing.
3. The `lore-graph-compiler` skill is the normative location for lore-root directory roles and source file conventions.
4. Wiki pages may include frontmatter fields such as `id`, `type`, `title`, `aliases`, `tags`, and typed `links`.
5. The compiler must preserve stable IDs, stable ordering, and unchanged output for unchanged source and ontology input.
6. Compilation must be incremental by default: update only artefacts affected by changed source, ontology, or dependency
   metadata rather than regenerating the entire graph.
7. The compiler must validate duplicate IDs, missing link targets, invalid relation types, invalid node types, and
   unresolved wiki links.
8. AI extraction is advisory: inferred nodes, relation changes, duplicate merges, and ontology additions must stay as
   suggestions unless the user explicitly approves canonical edits.
9. OpenCode lore-management agents must classify compiler output as `accepted`, `follow-up`, or `escalated` before
    continuing with further delegated work.

## In Scope

- Per-game lore repositories rooted at `lore/<game>`, including the example game at `lore/example`.
- Markdown wiki authoring conventions for graph-compilable lore pages.
- Extensible ontology files for node and relation types.
- Deterministic, incremental graph-compatible derived artefacts.
- Validation and suggestion flows for AI-assisted extraction.
- OpenCode harness instructions for a `loremaster` primary agent and lore compilation delegation.

## Out Of Scope

- Runtime querying, retrieval ranking, token-budgeted prompt projection, or vector indexes.
- Episodic memory, relationship state, or model-directed lore mutation during gameplay.
- Mandatory external graph databases or embedding stores.
- Final production lore content beyond the small example set.
- A complete compiler implementation; this spec defines the source/derived-data contract and workflow first.

## Acceptance Criteria

1. `lore/example` contains sample content conforming to the lore-root contract in the `lore-graph-compiler` skill.
2. At least one wiki page demonstrates frontmatter metadata, aliases, tags, and a typed relationship link.
3. At least one ontology file defines extensible node or relation types used by the example wiki pages.
4. Compiled artefacts state that they are derived and are ordered deterministically by stable ID.
5. Suggestions are separated from canonical wiki and compiled graph data.
6. OpenCode agent instructions require explicit game context and use the `question` tool if it is missing.
7. OpenCode agent instructions identify the Markdown wiki as canonical, require incremental sync, and forbid broad
   regeneration when source input did not change.
8. OpenCode agent instructions require compiler responses to be classified as `accepted`, `follow-up`, or `escalated`.
9. This spec is linked from the AI specification index and the project specification index.

## References

- [AI System](../index.md)
- [AI-001: Mind Component](../001-mind/index.md)
- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- `lore/example`
- `.opencode/agents/loremaster.md`
- `.opencode/agents/lore-compiler.md`
- `.opencode/skills/lore-graph-compiler/SKILL.md`
