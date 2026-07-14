---
id: AI-004
title: Lore And Backstory Source Compilation
---

# Lore And Backstory Source Compilation

## Requirement

The current implementation slice must support content-scoped Markdown lore roots as the human source of truth for
essential lore, with runtime loading and prompt injection for pages marked as essential.

## Goal

Authors should be able to mark baseline canon in ordinary Markdown so AI prompts receive stable essential context for
the active content, without requiring graph compilation or broader lore-management workflows in this slice.

## User Requirements

1. Authors can write lore/backstory as local Markdown pages with prose, frontmatter, aliases, tags, and wiki links.
2. Authors can mark lore pages as essential so static baseline canon is injected into AI prompts for the active content.
3. Prompt consumers receive deterministic essential-lore text with each entry clearly demarcated by its title.
4. Canon remains controlled by the human-authored wiki, not by generated graph artefacts.
5. Future dynamic lore retrieval should use the same query abstraction as static essential-lore injection.

## Technical Requirements

1. Lore roots are resolved from the current CORE content context.
2. The fallback `default` content id has lore root `res://lore`, committed as `game/lore/`.
3. Optional content packs use lore root `res://content/<content-id>/lore`, committed under
   `game/content/<content-id>/lore/` when present.
4. Runtime lore access must go through an asynchronous query service that accepts the current content context and query
   intent, including an essential-lore query for pages marked `essential: true`.
5. Wiki pages may include frontmatter fields such as `id`, `type`, `title`, `aliases`, `tags`, `essential`, and typed
   `links`, but only `essential` is required by the current runtime slice.
6. A top-level frontmatter field `essential: true` marks a lore page for static essential-lore prompt injection.
7. `essential`, when present, must be parsed and validated as a boolean.
8. Essential-lore injection must keep selection separate from presentation by querying essential lore through the lore
   query abstraction and delegating output shape to a lore formatter.
9. `EssentialLorePromptSection` must be runtime-backed through the PromptSection async build contract in AI-003 and must
    query through the lore abstraction rather than hardcoding paths in prompt code.
10. The current default lore formatter emits pseudo-XML-compatible entry blocks using each entry title as the tag and
    the trimmed body as the tag content, delegating block formatting to the shared pseudo-XML utility used by the prompt
    writer.
11. The initial query service may read canonical Markdown source directly behind the query abstraction.
12. Canonical lore source remains Markdown, and graph/compiler artefacts remain future derived outputs.
13. Future dynamic lore retrieval must use the same asynchronous query service abstraction, adding query intents or
     filters rather than introducing a separate retrieval pathway.

## In Scope

- Content-scoped lore repositories rooted at `game/lore/` for `default` and `game/content/<content-id>/lore/` for packs.
- Markdown wiki authoring conventions needed by essential-lore runtime loading.
- Top-level `essential: true` frontmatter for static essential-lore prompt injection.
- Async runtime lore query and presentation-agnostic formatting contracts for essential lore.
- A fallback sample wiki page under `game/lore/wiki/` for content id `default`.

## Out Of Scope

- Graph compiler artefacts, including `compiled/` output and deterministic graph sync.
- Ontology authoring or validation, including required `ontology/` files for the `default` runtime sample root.
- AI suggestions workflow, including required `suggestions/` files or agent classification of suggestions.
- Full content-pack lore authoring workflow beyond keeping the content-root mapping stable.
- Dynamic retrieval ranking, token-budgeted prompt projection, or vector indexes beyond essential-lore formatting.
- Episodic memory, relationship state, or model-directed lore mutation during gameplay.
- Mandatory external graph databases or embedding stores.
- Final production lore content beyond the small example set.

## Acceptance Criteria

1. `game/lore/wiki/` contains at least one fallback sample Markdown page for content id `default`.
2. The fallback sample page demonstrates frontmatter metadata, aliases, tags, wiki prose, and top-level
   `essential: true`.
3. Runtime loading resolves `default` to `game/lore` / `res://lore` and optional content id `<id>` to
   `game/content/<id>/lore` / `res://content/<id>/lore`.
4. Runtime loading reads essential lore from Markdown through the asynchronous query service, not from hardcoded
   prompt-section paths.
5. The default lore formatter produces deterministic pseudo-XML-compatible prompt blocks using each loaded page title as
   the tag and body as the content, without wrapper or child title/body/source tags.
6. `EssentialLorePromptSection` uses the AI-003 async prompt-section build contract and queries the lore abstraction.
7. Frontmatter parsing accepts `essential: true` and validates any present `essential` value as a boolean.
8. The implementation does not require ontology files, compiled graph artefacts, suggestions directories, dynamic
   retrieval ranking, token-budget projection, or full content-pack lore authoring workflow for this slice.
9. This spec is linked from the AI specification index and the project specification index.

## References

- [AI System](../index.md)
- [AI-001: Mind Component](../001-mind/index.md)
- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [CORE-008: Content Pack Resolution](../../core/008-content-pack-resolution/index.md)
- `game/lore`
- `game/content/<content-id>/lore`
- `.opencode/agents/loremaster.md`
- `.opencode/agents/lore-compiler.md`
- `.opencode/skills/lore-graph-compiler/SKILL.md`
