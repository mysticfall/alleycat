# Lore Review Checklist

Use this checklist with [Markdown Checklist](./checklist-markdown.md) when reviewing lore Markdown or graph-compatible
lore artefacts.

## Source Of Truth

- User-requested lore scope, active content id, and active lore root.
- @specs/ai/004-lore-backstory/index.md

## Checks

- [ ] The review stays within the requested lore scope and perspective.
- [ ] The active content id and lore root are explicit: `game/lore/` for `default`, or
  `game/content/<content-id>/lore/` for packs.
- [ ] Perspective Markdown remains the human source of truth under `perspectives/<observer-id>/`.
- [ ] World, location, and character entries follow the AI-004 perspective layout and frontmatter rules.
- [ ] `essential: true` is used only for world lore, and location/character selection is contextual rather than essential.
- [ ] Perspective entries represent observer beliefs, memories, and available context rather than omniscient canon plus a
  belief overlay.
- [ ] Missing perspective entries are treated as absent prompt-available knowledge, not as a cue for canonical fallback.
- [ ] Concrete prompt-usable facts are stated, scoped as unknown/unavailable/not prompt-relevant, or omitted rather than
  left for the LLM to infer.
- [ ] Markdown structure passes the Markdown checklist and remains valid for the relevant lore compiler or consumer.
- [ ] Required graph-compatible metadata, links, or identifiers are present and consistent when graph artefacts are in
  scope.
- [ ] Cross-references do not introduce stale, orphaned, or contradictory lore facts.
- [ ] Missing source material or unresolved canon conflicts are escalated.

## Escalate Immediately When

- The active content context or lore root is missing or ambiguous.
- Canon meaning is ambiguous, or a duplicate merge would change authored meaning.
- The request asks to promote AI-inferred concepts, relation changes, duplicate merges, or ontology additions into canon
  without user approval.
- The request asks to blend omniscient canonical constraints into character belief lore.
- Ontology, compiled graph, suggestions, broad regeneration, dynamic retrieval, save snapshots, auditor workflows, or
  gameplay-time lore mutation are introduced without explicit scope or an updated AI-004 contract.
- Validation cannot distinguish source error from compiler-output drift.
