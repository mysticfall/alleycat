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
- [ ] Perspective Markdown remains the human source of truth under `perspectives/<observer-type>/<observer-id>/`.
- [ ] Perspective files sit at their canonical paths, mirroring the wiki layout: `characters/` and `locations/` hold
  flat subject-named pages (`characters/<subject-id>.md`, `locations/<subject-id>.md`), `world/` nests at any depth,
  and no two files in one directory collide under hyphen/underscore stem equivalence.
- [ ] New or edited entries are under the correct observer id and collection (`world/`, `locations/`, or `characters/`).
- [ ] Every changed perspective-bound entry was compared against its canonical counterpart under `<lore-root>/wiki/`, or
  the absence of a counterpart was explicitly justified by the invoker.
- [ ] Perspective-bound entries mirror canonical counterpart structure: matching collection/category and filename stem
  unless an approved remap is documented, an optional `type` that matches the collection directory when present,
  matching path-derived subject identity (the canonical path binds the same subject — never a `subject_id` field,
  which no longer exists), and the same Markdown heading outline with
  section order and heading levels preserved under the H1-less convention (authored sections start at `#`). Titles may
  differ between canonical and perspective entries because each follows the observer-appropriate display-title rule.
- [ ] World, location, and character entries follow the AI-004 perspective layout and frontmatter rules.
- [ ] No page carries `subject_id` on either base: identity is path-authoritative, and a page carrying it fails load
  validation.
- [ ] Perspective frontmatter carries only valid fields — `title` (required on runtime-eligible pages), optional
  `description`, `essential`, `priority`, authoring dot-handle `id`, and `type` — with any `type` matching its
  collection directory and any dot-handle `id` unique within its observer/content scope.
- [ ] Wiki pages carry a bare path-matching `id` plus load-bearing `type` (for example `id: vadim` + `type: char`;
  `id: charter` + `type: world`): a typed or dot-handle wiki `id` value, a missing `type`, or an `id` that does not
  match the page's path segments (hyphen/underscore-equivalent) is flagged as a load-validation failure.
- [ ] `essential: true` is used only for world lore, and location/character selection is contextual rather than
  essential.
- [ ] Perspective entries represent observer beliefs, memories, and available context rather than omniscient canon
  plus a belief overlay.
- [ ] Changed perspective entries read as the observer's first-person subjective internal monologue (the observer speaks
  as "I") and convey all observer-available information about the subject so the topic is understandable without the
  canonical `wiki/` entry; third-person narration of perspective entries is flagged as an issue.
- [ ] Subject-bound entries, canonical `wiki/` entries and perspective entries alike, reference subjects by
  backticked full ID in body prose where the name would appear and handle names per the full-ID rule: canonical
  entries state the subject's name once as an explicit fact, and perspective entries state a known name or state
  that the observer does not know it.
- [ ] Every model-visible ID and tool name is backticked in prose, `description` values, link labels, annotated pipe
  reference IDs, and dialogue speaker labels (`` `char:visitor`: ``, `` `{{ character.FullId }}`: `` with the
  placeholder inside its authored backticks); tool-call argument values and frontmatter values remain bare strings.
- [ ] Authoring dot-handle ids (for example `vadim.self`) appear in no model-visible surface: not in prose,
  descriptions, link labels, or dialogue text.
- [ ] Link labels carry the target entry's composed canonical full ID (for example
  ``[Vadim(`char:vadim`)](wiki/characters/vadim.md)``), and annotated pipe references prefer canonical full IDs for
  subject-bound targets so the reduced ``Title(`target_id`)`` form stays a valid `read_lore` handle; `scenario:`
  pages are never referenced from prompt-facing prose because they are never LLM-addressable.
- [ ] Entry titles are readable Title Case display labels independent of IDs: a known name where the observer's
  knowledge supports it, otherwise an observer-known descriptive label. Newly authored ID-shaped titles, invented
  names, and titles leaking names the observer does not know are flagged as convention issues; legacy ID-shaped titles
  remain runtime-valid and are not flagged as parser errors.
- [ ] Descriptions are scope previews of the topics the entry supports: grounded in the entry body, observer-safe, in
  the entry's voice, sentence case with an initial capital, and free of authoring notes and source paths.
  Fact-summary descriptions, when/why-only lookup triggers, stage directions, behavioural absolutes, and scenario
  narrowing a broad profile does not justify are flagged as issues; a scenario entry may name its intrinsic scenario
  scope.
- [ ] Entries start body content directly after frontmatter with no title H1; authored sections start at `#`.
  Authored headings duplicating the frontmatter `title` are flagged as an issue.
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
- A changed perspective-bound entry diverges from its canonical counterpart's collection/path, path-derived subject
  identity, or Markdown outline without explicit approval from the invoker/user. Title differences alone are not
  divergence.
- Canon meaning is ambiguous, or a duplicate merge would change authored meaning.
- The request asks to promote AI-inferred concepts, relation changes, duplicate merges, or ontology additions into canon
  without user approval.
- The request asks to blend omniscient canonical constraints into character belief lore.
- Ontology, compiled graph, suggestions, broad regeneration, dynamic retrieval, save snapshots, auditor workflows, or
  gameplay-time lore mutation are introduced without explicit scope or an updated AI-004 contract.
- Validation cannot distinguish source error from compiler-output drift.
