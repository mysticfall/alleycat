---
name: lore-graph-compiler
description: Use when compiling per-game lore/<game> Markdown wiki lore into graph artefacts.
---

# Lore Graph Compiler

Use this skill for work under a selected `lore/<game>` root that validates lore wiki source, ontology files, compiled
graph artefacts, or AI suggestions.

## Game Context Requirement

Every task must operate on exactly one active game context.

- Lore roots live at `lore/<game>`.
- The active game is the `<game>` directory name, for example `example` for `lore/example`.
- If a primary agent has not provided the active game and exact root, stop and ask for it before proceeding.

## Lore Root Contract

A lore root is expected to use this structure:

```text
lore/<game>/
├── wiki/          # Canonical Markdown lore pages.
├── ontology/      # Canonical node and relation type definitions.
├── compiled/      # Deterministic derived graph artefacts.
└── suggestions/   # Advisory AI/compiler suggestions awaiting approval.
```

This skill is the normative source for the directory roles above. Example lore roots may demonstrate the structure, but
must not be treated as the schema authority.

## Wiki Page Convention

Markdown pages under `<root>/wiki` are graph node candidates when they include YAML frontmatter with these fields:

Required fields:

- `id`: stable graph node ID, using a namespace-style value such as `character.alley`.
- `type`: node type ID defined by ontology.
- `title`: human-readable display title.

Optional fields:

- `aliases`: list of alternate names that can resolve to the node.
- `tags`: list of loose authoring labels. Tags are not a substitute for typed graph relationships.
- `links`: list of explicit typed graph relationships from this page's node.

Each `links` item must include:

- `type`: relation type ID defined by ontology.
- `target`: target node ID.

Each `links` item may include additional scalar metadata such as `strength`, `confidence`, `note`, or `source`, provided
the compiler preserves deterministic ordering and does not treat unknown metadata as a relation type.

Markdown body prose remains canonical authoring context. Wiki links in prose, such as `[[Mirror Room]]`, may inform
suggestions or validation, but explicit frontmatter `links` are the canonical typed graph edges.

## Ontology Convention

Ontology files under `<root>/ontology` define the valid node and relation types for that game.

Node type definitions are expected in YAML using this top-level key:

```yaml
nodeTypes:
  - id: character
    description: A person, creature, or agent with identity and perspective.
```

Required node type fields:

- `id`: stable node type ID referenced by wiki page `type` values.
- `description`: concise author-facing meaning of the node type.

Relation type definitions are expected in YAML using this top-level key:

```yaml
relationTypes:
  - id: lives_in
    description: Connects a character to the location where they usually reside.
    directed: true
    sourceTypes: [character]
    targetTypes: [location]
```

Required relation type fields:

- `id`: stable relation type ID referenced by wiki page `links[].type` values.
- `description`: concise author-facing meaning of the relationship.
- `directed`: whether edge direction is semantically meaningful.
- `sourceTypes`: allowed source node type IDs.
- `targetTypes`: allowed target node type IDs.

Optional relation type fields:

- `inverse`: inverse relation type ID, only valid when that inverse relation type is also defined.
- additional scalar metadata that does not change validation semantics unless the compiler explicitly supports it.

## Required Inputs

- Active lore root: `lore/<game>`.
- Canonical wiki source: `<root>/wiki`.
- Canonical ontology source: `<root>/ontology`.
- Derived graph output: `<root>/compiled`.
- Advisory suggestions: `<root>/suggestions`.
- Normative spec: `specs/ai/004-lore-backstory/index.md`.

## Deterministic Sync Rules

1. Treat wiki and ontology files as source of truth.
2. Treat compiled artefacts as replaceable derived output, but avoid unnecessary rewrites.
3. Preserve stable IDs, stable ordering, stable formatting, and unchanged output when source input is unchanged.
4. Prefer small incremental edits over whole-file regeneration.
5. Sort generated node and edge collections by stable ID.
6. Keep inferred or uncertain changes in `suggestions`, not in canonical wiki or ontology files.

## Validation Checklist

- [ ] Every wiki page intended as a graph node has a stable `id`.
- [ ] Every graph node `type` is defined by ontology.
- [ ] Every typed relationship uses a defined relation type.
- [ ] Every typed relationship target resolves to a known node ID or accepted stub.
- [ ] Wiki links either resolve to known pages or appear in suggestions as unresolved stubs.
- [ ] Duplicate aliases or likely duplicate nodes are reported without being merged automatically.
- [ ] Derived output has a generated-file notice and deterministic ordering.

## Suggestion Policy

Suggestions may include:

- missing stub pages,
- inferred aliases,
- candidate typed relationships,
- duplicate merge candidates,
- ontology extension candidates.

Do not apply these to canon unless the invoking agent says the user approved them.

## Response Requirement

Return the `lore-compiler` final report shape exactly:

1. **Game Context**
2. **Source Delta**
3. **Graph Delta**
4. **Validation**
5. **Determinism Check**
6. **Escalations**
