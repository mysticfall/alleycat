---
name: lore-graph-compiler
description: Use when compiling content-scoped Markdown wiki lore into graph artefacts or validating essential lore.
---

# Lore Graph Compiler

Use this skill for work under one active content lore root that validates Markdown lore wiki source, essential-lore
frontmatter, ontology files, compiled graph artefacts, or AI suggestions.

## Content Context Requirement

Every task must operate on exactly one active content context.

- Fallback committed content uses content id `default` and lore root `game/lore` (`res://lore`).
- Optional packs use content id `<id>` and lore root `game/content/<id>/lore` (`res://content/<id>/lore`).
- If a primary agent has not provided the active content id and exact lore root, stop and ask for them before
  proceeding.

## Lore Root Contract

The current AI-004 runtime slice loads observer perspective lore only:

```text
<lore-root>/
└── perspectives/  # Observer perspective Markdown lore; runtime source for essential world injection,
                   # the session-start catalogue, and entry-ID retrieval.
```

The canonical `wiki/` tree below is authoring-only at runtime: AI-004 never injects, catalogues, or retrieves
canonical pages for an observer perspective, and `essential` marks perspective world lore only.

Graph compilation work may add the extended directories below when that workflow is invoked. Do not require these
directories for the runtime `default` sample lore root used by the prompt injection slice.

```text
<lore-root>/
├── wiki/          # Canonical Markdown lore pages.
├── ontology/      # Canonical node and relation type definitions.
├── compiled/      # Deterministic derived graph artefacts.
└── suggestions/   # Advisory AI/compiler suggestions awaiting approval.
```

This skill is the normative source for the directory roles above when graph compilation is in scope. Example lore roots
may demonstrate the structure, but must not be treated as the schema authority.

## Wiki Page Convention

Markdown pages under `<root>/wiki` are canonical authoring source; runtime essential lore, the catalogue, and
entry-ID retrieval read the perspective trees per AI-004. Runtime-eligible pages require valid frontmatter, and every
`essential` value must be a boolean when present.

Markdown pages are graph node candidates when graph compilation is in scope and they include YAML frontmatter with these
fields:

Required fields:

- `id`: stable graph node ID, using a namespace-style value such as `character.alley`.
- `type`: node type ID defined by ontology.
- `title`: human-readable display title in Title Case, independent of `id` (the AI-004 display-title convention;
  known names or observer-known descriptive labels, never IDs).

Optional fields:

- `aliases`: list of alternate names that can resolve to the node.
- `tags`: list of loose authoring labels. Tags are not a substitute for typed graph relationships.
- `description`: optional single-line scope preview of the topics the page covers; not a fact summary or a
  when/why lookup trigger (the AI-004 description convention).
- `essential`: when `true`, marks world lore for baseline essential-lore prompt injection when read from a perspective
  tree. AI-004 loads observer perspective lore only, so an `essential: true` flag on a canonical `wiki/` page is
  preserved authoring metadata with no runtime injection effect.
- `links`: list of explicit typed graph relationships from this page's node.

Each `links` item must include:

- `type`: relation type ID defined by ontology.
- `target`: target node ID.

Each `links` item may include additional scalar metadata such as `strength`, `confidence`, `note`, or `source`, provided
the compiler preserves deterministic ordering and does not treat unknown metadata as a relation type.

Markdown body prose remains canonical authoring context. Wiki links in prose, such as `[[Mirror Room]]`, may inform
suggestions or validation, but explicit frontmatter `links` are the canonical typed graph edges.

## Ontology Convention

This convention applies when graph compilation or ontology validation is explicitly in scope. It is not mandatory for
the runtime `default` sample lore root used by essential-lore prompt injection.

Ontology files under `<root>/ontology` define the valid node and relation types for that content.

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

- Active content id: `default` or an optional pack id.
- Active lore root: `game/lore` for `default`, or `game/content/<id>/lore` for an optional pack.

Runtime perspective validation (essential-lore validation, catalogue, or entry-ID retrieval checks) requires the active
observer perspective tree `<lore-root>/perspectives/<observer-type>/<observer-id>/` instead of canonical wiki; AI-004
loads observer perspective lore only, so ordinary runtime validation must not require `<root>/wiki`.

Additional inputs are required only when canonical wiki, graph compilation, ontology validation, or suggestion work is
requested:

- Canonical wiki source: `<root>/wiki` (canonical wiki work and graph compilation).
- Canonical ontology source: `<root>/ontology`.
- Derived graph output: `<root>/compiled`.
- Advisory suggestions: `<root>/suggestions`.
- Normative spec: `specs/ai/004-lore-backstory/index.md`.

## Deterministic Sync Rules

Apply these rules when graph compilation or derived artefact sync is invoked. For the current AI-004 runtime slice,
validate Markdown source and essential frontmatter without requiring graph output.

1. Treat wiki and ontology files as source of truth.
2. Treat compiled artefacts as replaceable derived output, but avoid unnecessary rewrites.
3. Preserve stable IDs, stable ordering, stable formatting, and unchanged output when source input is unchanged.
4. Prefer small incremental edits over whole-file regeneration.
5. Sort generated node and edge collections by stable ID.
6. Keep inferred or uncertain changes in `suggestions`, not in canonical wiki or ontology files.

## Validation Checklist

- [ ] The active content id maps to `game/lore` (`res://lore`) for `default`, or to
      `game/content/<id>/lore` (`res://content/<id>/lore`) for an optional pack.
- [ ] Runtime lore resolves under `<lore-root>/perspectives/<observer-type>/<observer-id>/` per AI-004; the canonical
      `wiki/` tree is authoring-only at runtime.
- [ ] Every `essential` value, when present, is a boolean. Only perspective world `essential: true` entries mark lore
      for baseline prompt injection; canonical world `essential` flags are preserved authoring metadata with no
      runtime injection effect.
- [ ] Every `description`, when present, is a single-line scope preview of the page's covered topics, not a fact
      summary or a when/why lookup trigger.

Additional checks apply only when graph compilation or ontology validation is in scope:

- [ ] Every wiki page intended as a graph node has a stable `id`.
- [ ] Every graph node `type` is defined by ontology.
- [ ] Every typed relationship uses a defined relation type.
- [ ] Every typed relationship target resolves to a known node ID or accepted stub.
- [ ] Wiki links either resolve to known pages or appear in suggestions as unresolved stubs.
- [ ] Duplicate aliases or likely duplicate nodes are reported without being merged automatically.
- [ ] Derived output has a generated-file notice and deterministic ordering.

## Suggestion Policy

This policy applies only when AI suggestions are explicitly requested. The current essential-lore prompt injection slice
does not require a `suggestions/` directory or suggestion workflow.

Suggestions may include:

- missing stub pages,
- inferred aliases,
- candidate typed relationships,
- duplicate merge candidates,
- ontology extension candidates.

Do not apply these to canon unless the invoking agent says the user approved them.

## Response Requirement

Use this response shape when graph compilation or suggestion work is invoked. Essential-lore validation may return a
focused report that covers content context, the lore source validated (the observer perspective tree for runtime
validation; canonical wiki only when canonical or graph work is requested), essential-frontmatter validation, and
escalations.

Return the `lore-compiler` final report shape exactly:

1. **Content Context**
2. **Source Delta**
3. **Graph Delta**
4. **Validation**
5. **Determinism Check**
6. **Escalations**
