---
name: reviewer-checklist-lore
description: Reviewer-only checklist for lore Markdown and graph-artefact reviews.
---

# Lore Review Checklist

Use this checklist with `reviewer-checklist-markdown` when reviewing lore Markdown or graph-compatible lore artefacts.

## Source Of Truth

- User-requested lore scope, active content id, and active lore root.
- @specs/ai/004-lore-backstory/index.md
- Canonical counterpart Markdown under the same active lore root's `wiki/` tree for every changed perspective-bound
  entry, when such a counterpart exists or is supplied by the invoker.
- `writer-guide-lore` when `writer` drafted prose or frontmatter.
- `loremaster` delegation packet and any `writer` response when prose/frontmatter drafting was delegated.

## Checks

- [ ] The review stays within the requested lore scope and perspective.
- [ ] The active content id and lore root are explicit: `game/lore/` for `default`, or
  `game/content/<content-id>/lore/` for packs.
- [ ] Any delegated writer output was classified by the invoking agent as `accepted`, `follow-up`, or `escalated`
  before review handoff.
- [ ] Perspective Markdown remains the human source of truth under `perspectives/<observer-id>/`.
- [ ] New or edited entries are under the correct observer id and collection (`world/`, `locations/`, or `characters/`).
- [ ] Every changed perspective-bound entry was compared against its canonical counterpart under `<lore-root>/wiki/`, or
  the absence of a counterpart was explicitly justified by the invoker.
- [ ] Perspective-bound entries mirror canonical counterpart structure: matching collection/category and filename stem
  unless an approved remap is documented, matching top-level title, compatible `type`, matching subject identity using
  `subject_id` where that field is present or required by local convention, and the same Markdown heading outline with
  section order and heading levels preserved.
- [ ] World, location, and character entries follow the AI-004 perspective layout and frontmatter rules.
- [ ] `essential: true` is used only for world lore, and location/character selection is contextual rather than essential.
- [ ] Perspective entries represent observer beliefs, memories, and available context rather than omniscient canon plus a
  belief overlay.
- [ ] Canon decisions, duplicate handling, ontology additions, and omniscient/system constraints were not delegated to
  `writer` or silently resolved during drafting.
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
- The observer id, target collection, or delegated writer scope is missing or ambiguous.
- A changed perspective-bound entry has no identified canonical counterpart and the invoker has not explicitly scoped it
  as perspective-only lore.
- A changed perspective-bound entry diverges from its canonical counterpart's collection/path, subject identity, title,
  or Markdown outline without explicit approval from the invoker/user.
- Canon meaning is ambiguous, or a duplicate merge would change authored meaning.
- The request asks to promote AI-inferred concepts, relation changes, duplicate merges, or ontology additions into canon
  without user approval.
- The request asks to blend omniscient canonical constraints into character belief lore.
- Ontology, compiled graph, suggestions, broad regeneration, dynamic retrieval, save snapshots, auditor workflows, or
  gameplay-time lore mutation are introduced without explicit scope or an updated AI-004 contract.
- Validation cannot distinguish source error from compiler-output drift.
