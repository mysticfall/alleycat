---
id: AI-004
title: Lore And Backstory Source Compilation
---

# Lore And Backstory Source Compilation

## Requirement

The current implementation slice must support content-scoped perspective Markdown lore roots as the human source of
truth for what each character believes or knows, with runtime loading and prompt injection for the active AgenticMind
perspective.

## Goal

Authors should be able to write ordinary Markdown lore from a named character perspective so AI prompts receive
stable, deterministic context for that character without requiring graph compilation, memory mutation, or broader
lore-management workflows in this slice.

## User Requirements

1. Authors can write lore/backstory as local Markdown pages with prose, frontmatter, aliases, tags, and wiki links.
2. Authors can organise lore by observer perspective so a character prompt receives that character's beliefs and
   knowledge, not an omniscient canonical fact list.
3. Authors can mark perspective world lore as essential so baseline world context is injected for the active character.
4. Location and character lore is selected when it is contextually relevant, such as the current location, the character
   themself, scene participants, or conversation participants.
5. Prompt consumers receive deterministic lore text with each entry clearly demarcated by its title.
6. Authoritative lore remains controlled by the human-authored perspective wiki, not by generated graph artefacts.
7. Authors do not need to duplicate every canonical subject for every character perspective.
8. Characters receive lore only for subjects the author has made available to that observer perspective.
9. Canonical lore remains useful for authoring consistency and future generation tooling, without acting as a runtime
   substitute for observer knowledge in this slice.
10. Perspective entries do not force prompt consumers to invent unstated facts when they claim the observer knows a
    concrete detail that may affect dialogue or action.
11. Future dynamic lore retrieval should use the same query abstraction as the first-slice prompt injection path.

## Technical Requirements

1. Lore roots are resolved from the current CORE content context.
2. The fallback `default` content id has lore root `res://lore`, committed as `game/lore/`.
3. Optional content packs use lore root `res://content/<content-id>/lore`, committed under
   `game/content/<content-id>/lore/` when present.
4. Perspective lore for the default content root uses this layout:
   - `game/lore/perspectives/<observer-id>/world/*.md`
   - `game/lore/perspectives/<observer-id>/locations/*.md`
   - `game/lore/perspectives/<observer-id>/characters/*.md`
5. Perspective lore for content packs uses the same layout under
   `game/content/<content-id>/lore/perspectives/<observer-id>/`.
6. The observer key is the stable entity id associated with the observing character.
7. Runtime lore access must go through an asynchronous query service that accepts content context, observer id, and
   query intent.
8. AgenticMind lore prompt consumption must query lore for its associated character perspective and remain read-only.
9. Wiki pages may include frontmatter fields such as `id`, `title`, `aliases`, `tags`, `essential`, `priority`, and
   typed `links`.
10. A top-level frontmatter field `essential: true` marks only world lore for baseline prompt injection.
11. `essential`, when present, must be parsed and validated as a boolean.
12. Location and character entries are selected by contextual relevance, not by `essential`.
13. `priority`, when present, must be parsed as an ordering value used by the lore API to sort retrieved entries before
    formatting.
14. Sorting must be deterministic: sort by priority first, then by stable entry id when present, then title, then source
    path.
15. Lore prompt injection must keep selection separate from presentation by querying lore through the lore query
    abstraction and delegating output shape to a lore formatter.
16. `EssentialLorePromptSection` must be runtime-backed through the PromptSection async build contract in AI-003 and
    must query through the lore abstraction rather than hardcoding paths in prompt code.
17. The current default lore formatter emits pseudo-XML-compatible entry blocks using each entry title as the tag and
    the trimmed body as the tag content, delegating block formatting to the shared pseudo-XML utility used by the
    prompt writer.
18. The initial query service may read perspective Markdown source directly behind the query abstraction.
19. Lore source remains Markdown, and graph/compiler artefacts remain future derived outputs.
20. AgenticMind prompt lore must treat perspective entries as the active character's beliefs, not canonical facts plus
    supplemental belief overlays.
21. When a perspective entry exists for an observer and subject, it replaces any canonical entry for AgenticMind prompt
    use by default.
22. Missing perspective entries mean the observer has no prompt-available contextual knowledge for that subject.
23. The AgenticMind lore query path must not fall back to canonical entries unless a future omniscient or system context
    channel explicitly adds that behaviour.
24. Omniscient constraints that the LLM must obey must live in system/developer rules or a future narrator/game-master
    channel, not in character belief lore.
25. Perspective authoring and validation must flag claims that imply concrete prompt-usable knowledge without including
    the value or explicitly scoping it as unknown, unavailable, or not prompt-relevant.
26. Future dynamic lore retrieval must use the same asynchronous query service abstraction, adding query intents or
    filters rather than introducing a separate retrieval pathway.

## In Scope

- Content-scoped lore repositories rooted at `game/lore/` for `default` and `game/content/<content-id>/lore/` for packs.
- Perspective Markdown wiki authoring conventions under `perspectives/<observer-id>/`.
- `world/`, `locations/`, and `characters/` perspective lore collections.
- Top-level `essential: true` frontmatter for baseline world-lore prompt injection.
- Contextual retrieval for location and character lore needed by the current prompt context.
- Optional `priority` frontmatter and deterministic ordering for retrieved entries.
- Async runtime lore query and presentation-agnostic formatting contracts for perspective lore.
- Read-only AgenticMind prompt consumption of perspective lore.
- Perspective entries as the default replacement for canonical lore in AgenticMind prompt consumption.
- Missing perspective entries as absent observer knowledge with no automatic canonical fallback.
- Authoring guidance that prevents perspective entries from implying unstated concrete prompt-usable facts.

## Out Of Scope

- Graph compiler artefacts, including `compiled/` output and deterministic graph sync.
- Ontology authoring or validation, including required `ontology/` files for the `default` runtime sample root.
- AI suggestions workflow, including required `suggestions/` files or agent classification of suggestions.
- Full content-pack lore authoring workflow beyond keeping the content-root mapping stable.
- Optional lore fragment search, dynamic retrieval ranking, token-budgeted prompt projection, or vector indexes beyond
  the first-slice query contract.
- Omniscient/system lore channels, narrator/game-master lore channels, or canonical prompt fallback pathways beyond the
  explicit no-fallback contract for this slice.
- Episodic memory, relationship state, or model-directed lore mutation during gameplay.
- Dynamic writes, save snapshots, memory curator/auditor workflows, and automated canonical-to-perspective conversion
  tooling.
- Mandatory external graph databases or embedding stores.
- Final production lore content beyond the small example set.

## Acceptance Criteria

1. Runtime loading resolves `default` to `game/lore` / `res://lore` and optional content id `<id>` to
   `game/content/<id>/lore` / `res://content/<id>/lore`.
2. Runtime loading reads perspective lore from `perspectives/<observer-id>/world/`, `locations/`, and `characters/`
   collections through the asynchronous query service.
3. AgenticMind prompt consumption requests lore for its associated character observer id and does not consume canonical
   facts as a substitute for that perspective.
4. AgenticMind prompt lore presents perspective entries as the character's beliefs, not as canonical facts plus
   supplemental belief overlays.
5. Missing perspective entries do not trigger automatic canonical fallback in AgenticMind prompt lore.
6. The implementation does not require every canonical entry to have a matching perspective entry for every character.
7. Omniscient constraints required for LLM behaviour are kept out of character belief lore and represented only through
   system/developer rules or a future narrator/game-master channel.
8. World entries with `essential: true` are included in baseline prompt lore for the active perspective.
9. Location and character entries are included only when contextually relevant, not because they are marked essential.
10. Any present `essential` value is validated as a boolean, and any present `priority` value participates in
   deterministic API ordering.
11. Entries with equal priority are ordered deterministically by stable entry id when present, then title, then source
   path.
12. Runtime prompt sections query the lore abstraction, not hardcoded prompt-section paths, and do not write lore data.
13. The default lore formatter produces deterministic pseudo-XML-compatible prompt blocks using each loaded page title
    as the tag and body as the content, without wrapper or child title/body/source tags.
14. `EssentialLorePromptSection` uses the AI-003 async prompt-section build contract and queries the lore abstraction.
15. The implementation does not require ontology files, compiled graph artefacts, suggestions directories, lore fragment
    search, dynamic writes, save snapshots, memory curator/auditor workflows, automated canonical-to-perspective
    conversion tooling, token-budget projection, vector indexes, or full content-pack lore authoring workflow for this
    slice.
16. Perspective lore reviews flag entries that claim an observer knows a concrete prompt-usable fact without stating the
    value or scoping it as unknown, unavailable, or not prompt-relevant.
17. This spec is linked from the AI specification index and the project specification index.

## References

- [AI System](../index.md)
- [AI-001: Mind Component](../001-mind/index.md)
- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [CORE-008: Content Pack Resolution](../../core/008-content-pack-resolution/index.md)
- `game/lore`
- `game/content/<content-id>/lore`
