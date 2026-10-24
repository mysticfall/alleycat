---
id: AI-004
title: Lore And Backstory Source Compilation
---

# Lore And Backstory Source Compilation

## Requirement

The current implementation slice must support content-scoped Markdown lore roots — per-observer perspective trees as
the human source of truth for what each character believes or knows, alongside a canonical wiki base for authoring
consistency — with automatic prompt injection, a discoverable catalogue, and read-only canonical-ID retrieval for the
active AgenticMind perspective.

## Goal

Authors should be able to write ordinary Markdown lore from a canonical character perspective so AI prompts receive
stable, deterministic context for that character without requiring graph compilation, memory mutation, or broader
lore-management workflows in this slice. A lore entry's canonical identity derives from its path, so identity is
authored once and every model-visible surface presents it in the same backticked canonical form.

## User Requirements

1. Authors can write lore/backstory as local Markdown pages with prose, frontmatter, aliases, tags, and wiki links.
2. Authors can organise lore by observer perspective so a character prompt receives that character's beliefs and
   knowledge, not an omniscient canonical fact list.
3. Authors can mark perspective world lore as essential so baseline world context is injected for the active character.
4. Essential world and scene-character lore bodies remain automatically available. Contextual character and location
   queries remain available; this slice does not automatically inject location lore.
5. Prompt consumers receive deterministic lore text with each entry clearly demarcated by its title and its
   backticked canonical ID, so an entry can be correlated with full-ID references elsewhere in the model's context —
   event history, scene status, and lore prose.
6. Authoritative lore remains controlled by the human-authored perspective wiki, not by generated graph artefacts.
7. Authors do not need to duplicate every canonical subject for every character perspective.
8. Characters receive lore only for subjects the author has made available to that observer perspective.
9. Canonical lore remains useful for authoring consistency, service-layer reads, and future generation tooling,
   without entering LLM-visible context as a substitute for observer knowledge in this slice.
10. Perspective entries do not force prompt consumers to invent unstated facts when they claim the observer knows a
    concrete detail that may affect dialogue or action.
11. An NPC can discover all remaining available lore and request one or more exact canonical IDs, then read the bodies
    on a subsequent reasoning request before choosing an action. Retrieval is encouraged when relevant, not mandatory.
12. Authors write perspective entries in the observer character's first-person subjective voice, as an internal
    monologue on the subject: the entry conveys all observer-available information about the subject so the topic is
    understandable without the canonical `wiki/` entry, the prose reflects the observer's personality, attitudes, and
    judgements rather than a mechanical pronoun flip of third-person text, and external narrator observations become
    the observer's own self-perception or rationalisations without omniscient asides or new concrete prompt-usable
    facts.
13. Subjects in prompt-facing lore are referenced by full ID (`[type]:[id]`, with types `char`, `loc`, and `item`)
    rather than by name: body prose references subjects by full ID where the name would appear, names are carried as
    explicit lore facts (a canonical entry states the name once; a perspective entry states a name the observer knows
    or states that the name is unknown), and natural dialogue or speech uses known names rather than full IDs. Entry
    and subject identity derive from the entry's path — restated on wiki pages by the path-matching frontmatter `id`
    and `type` — never from the display title and never from a per-entry `subject_id` field, which no longer exists.
14. Titles are readable display labels independent of IDs, written in Title Case. Use a known name (for example
    **Ally**) when the observer's knowledge supports it; otherwise use an observer-known descriptive label (for
    example **The Detained Vesari**), never an invented name or a name the observer does not know. Canonical and
    perspective entries for the same subject may use different titles without changing their identity relationship.
    The lore formatter renders titles as Markdown headings in prompts. Existing lowercase metadata keys, `type`
    values, and boolean tokens remain lowercase.
15. Authors write entries without a title heading duplicating the frontmatter `title`: body content starts directly
    after the frontmatter and authored sections start at `#` at authoring time, with the entry title rendered into
    the prompt by the lore formatter instead.
16. Authors can co-locate authoring-time material with runtime lore — scratch pages, note sections, HTML comments, and
    source link syntax — without it reaching prompts or breaking runtime queries.
17. The session-start catalogue groups remaining entries under World, Characters, and Locations, omits empty groups,
    and lists each entry's exact canonical full ID (backticked), title, and optional description. Missing
    descriptions need no generated fallback. Authoring dot-handle ids (for example `vadim.self`) appear in no
    model-visible surface.
18. Model-visible discovery and retrieval expose neither canonical wiki content, other observers' knowledge,
    authoring-only material, authoring dot-handle ids, nor source paths. Unknown IDs produce explicit unavailable
    results, never fabricated knowledge; canonical entries remain readable behind the lore service for non-LLM
    consumers without ever entering model-visible context.
19. Descriptions are concise scope previews: they identify the meaningful topics or dimensions of information the
    entry covers, so retrieval relevance can be inferred without reading the body. Advertised topics are grounded in
    the entry body, observer-safe, and written in sentence case with an initial capital. A description is neither a
    summary of the entry's facts nor a lookup instruction: it avoids when/why-only triggers, imperative or
    mandatory-retrieval framing, stage directions, and behavioural absolutes, and it narrows the entry to a specific
    scenario only when that scenario is intrinsic to the entry itself (for example a scenario entry), never to
    narrow a broader character, location, or world profile.
20. All 15 existing default lore pages receive scope-preview descriptions without changing facts or essential flags;
    canonical descriptions remain authoring-only for LLM-visible context.
21. Annotated pipe wiki references authored in lore bodies reach prompt consumers and `read_lore` results as
    ``Title(`target_id`)`` — the reference title plus the exact backtick-annotated target ID, with no source path —
    so consumers can retrieve the referenced entry by that exact ID. The annotated target ID prefers the canonical
    full ID for subject-bound targets (for example `char:vadim`), keeping the reduced form a valid `read_lore`
    handle.
22. Lore entries carry path-derived canonical IDs of the form `[type]:[wiki-relative path]`, with types `char`,
    `loc`, and `world` — for example `char:vadim`, `loc:interrogation_room`, `world:charter`, and `world:notes/nested`
    for a nested page. An NPC addresses lore through `read_lore` by these canonical IDs alone, and the same IDs
    identify entries in formatted bodies and the catalogue. Canonical IDs resolve against the active observer's
    perspective: the same ID may be present for one observer and unavailable for another, and scenario pages —
    which exist in wiki authoring only — are always unavailable through the tool layer.
23. A `read_lore` request that cannot be resolved — an unknown canonical ID, an unsupported type such as
    `scenario:`, an authoring dot-handle id, or a malformed string — returns an explicit unavailable result for that
    requested ID, never an error and never fabricated knowledge. When the requested string matches an in-scope
    entry's authoring dot-handle id, the unavailable result appends a hint pointing at the canonical form.
24. Authors place lore entries at their canonical path: the entry's kind and subject binding derive from the tree
    position (`world/`, `locations/`, `characters/`), perspective entries mirror the wiki layout under the
    observer's root, and wiki pages carry a bare `id` plus load-bearing `type` that the loader validates against the
    path. No frontmatter field restates the path's identity decisions: perspective `subject_id` does not exist, and
    perspective `type` is optional.
25. In all model-visible text — prompts, formatted lore, tool descriptions and results, event history, scene
    snapshots, and dialogue examples — IDs (canonical full IDs) and tool names (for example `read_lore`) are
    enclosed in backticks, so identity tokens read unambiguously as handles rather than prose. Tool-call argument
    values remain exact bare strings, and frontmatter values remain bare: backticks are a rendering convention, not
    data syntax.

## Technical Requirements

1. Lore roots are resolved from the current CORE content context.
2. The fallback `default` content id has lore root `res://lore`, committed as `game/lore/`.
3. Optional content packs use lore root `res://content/<content-id>/lore`, committed under
   `game/content/<content-id>/lore/` when present.
4. Lore for the default content root uses these bases and layout:
    - Canonical wiki: `game/lore/wiki/<collection>/<path>.md`, with collections `world/`, `locations/`,
      `characters/`, and `scenarios/`. The `scenarios/` collection exists for wiki authoring only: it has no
      perspective counterpart and is never available through the LLM-facing tool layer.
    - Perspectives: `game/lore/perspectives/<observer-type>/<observer-id>/<canonical-path>.md`, where the canonical
      path mirrors the wiki layout — `world/**/*.md` at any nesting depth, and `locations/<subject-id>.md` plus
      `characters/<subject-id>.md` directly in their collections, named after the subject they bind. Nested pages
      under `world/` are part of the read contract as multi-segment canonical IDs (for example
      `world:notes/nested`).
    - Observer identity remains canonical `FullId` (for example `char:vadim`), while type-scoped directories avoid
      colons (for example `perspectives/char/vadim/`).
5. Perspective lore for content packs uses the same layout under
    `game/content/<content-id>/lore/perspectives/<observer-type>/<observer-id>/`.
6. Lore observers are CORE-009 `FullId` values; the directory layout separates the `Type` and `Id` segments without
   changing their identity, and `Id` and `Type` are validated at scene registration or installation. Subject
   identity is path-authoritative: the loader derives each runtime entry's kind, canonical subject binding
   (`LoreEntry.Kind`/`SubjectID`), and canonical full ID from the collection directory and path segments, exposes
   the canonical ID on the query result for formatter and catalogue use, and no frontmatter field can override or
   restate those decisions.
7. Runtime lore access must go through an asynchronous query service that accepts content context, an observer
   `FullId` for perspective-scoped queries (absent for canonical-base reads, requirement 52), and query intent.
8. AgenticMind lore prompt consumption must query lore for its associated character perspective and remain read-only.
9. Frontmatter is split by base, and identity is path-authoritative. Perspective pages may include `title`
   (required on runtime-eligible pages), optional `description`, `essential`, `priority`, an optional authoring
   dot-handle `id` (for example `vadim.self`), and an optional `type` that must match the collection directory when
   present — a mismatch fails load validation. Wiki pages carry a bare `id` plus load-bearing `type` (for example
   `id: vadim` + `type: char`; world pages `id: charter` + `type: world`), both validated against the page's path —
   `type` against the mapped top-level directory and `id` against the wiki-relative path segments,
   hyphen/underscore-equivalent — and may additionally include `title`, `description`, `aliases`, `tags`,
   `essential`, `priority`, and typed `links`. Runtime inclusion requires a frontmatter block carrying an `id` or a
   `title`: a page with no frontmatter block at all, or whose frontmatter carries neither field, is authoring-time
   only and is excluded from runtime queries, while a page whose frontmatter carries a `title` but no `id` loads
   with an authoring identity derived from its source path and is addressed canonically like any other entry.
   `subject_id` exists on neither base; a page carrying it fails load validation. Composed full IDs (`type:id`)
   appear in prose references and labels, never as frontmatter values.
10. A top-level frontmatter field `essential: true` marks only world lore for baseline prompt injection.
11. `essential`, when present on a runtime-eligible page, must be parsed and validated as a boolean.
12. Location and character entries are selected by contextual relevance, not by `essential`.
13. `priority`, when present on a runtime-eligible page, must be parsed as an ordering value used by the lore API to
    sort retrieved entries before formatting. Fixture/sample content should either set explicit priorities
    consistently where order is meaningful or test without relying on source-file ordering.
14. Sorting must be deterministic: sort by priority first, then entry authoring identity — the authored dot-handle
     `id` or its path-derived fallback — then title, then the backend's source path as the final tie-breaker.
     Runtime entries always carry an authoring identity because pages declaring no runtime intent are skipped at
     read time (requirement 36); the Markdown backend retains title and source path as deterministic tie-breakers.
15. Lore prompt injection must keep selection separate from presentation by querying lore through the lore query
    abstraction and delegating output shape to a lore formatter.
16. `EssentialLorePromptSection` must be runtime-backed through the `PromptSection` async build contract in AI-003.
17. `EssentialLorePromptSection` must construct its own essential world query from
    `buildContext.Character.FullId` and query through the lore abstraction rather than hardcoding source paths.
18. `EssentialLorePromptSection` remains world-only and validates the owning character `FullId` only when used. This
    validation must not make lore identity a general prompt-stack requirement.
19. Lore query state, factories, and resolver methods must not leak into the general `PromptSectionBuildContext` API.
20. `LoreEntry` must not expose `SourcePath` publicly. The Markdown backend retains source paths privately only for
    diagnostics and the final deterministic sorting tie-breaker.
21. The default lore formatter (`MarkdownLorePromptFormatter`, the `ILorePromptFormatter` default) renders each entry
    as Markdown: a `# {title}` heading from the verbatim, unsanitised entry title, one blank line, the entry's
    backticked canonical full ID on its own line, one blank line, then the normalised body:
    - the ID line is uniform for world, character, and location entries, and always shows the canonical ID — never
      an authoring dot-handle id,
    - the shallowest body heading is demoted as a block, preserving relative depth and never promoting headings, so
      body headings start at exactly `##`,
    - consecutive non-blank prose lines within a paragraph are trimmed and joined with single spaces, removing
      mid-sentence hard-wrap line breaks,
    - runs of two or more blank lines collapse to a single blank line,
    - consecutive entries are separated by exactly one blank line, and fenced code blocks pass through verbatim.
22. The initial query service may read perspective and wiki Markdown source directly behind the query abstraction.
23. Lore source remains Markdown, and graph/compiler artefacts remain future derived outputs.
24. AgenticMind prompt lore must treat perspective entries as the active character's beliefs, not canonical facts plus
    supplemental belief overlays.
25. When a perspective entry exists for an observer and subject, it replaces any canonical entry for LLM-visible
    prompt and tool use by default.
26. Missing perspective entries mean the observer has no LLM-visible contextual knowledge for that subject.
27. The AgenticMind lore pipeline — prompt sections and `read_lore` composition — must never substitute canonical
    wiki content: canonical entries are readable only through the lore service layer by non-LLM consumers
    (requirement 52), and no channel may inject them into LLM-visible context unless a future omniscient or system
    context contract explicitly adds that behaviour.
28. Omniscient constraints that the LLM must obey must live in system/developer rules or a future narrator/game-master
    channel, not in character belief lore.
29. Perspective authoring and validation must flag claims that imply concrete prompt-usable knowledge without including
    the value or explicitly scoping it as unknown, unavailable, or not prompt-relevant.
30. Catalogue and batch canonical-ID retrieval must extend the existing asynchronous query service with query intents
    or filters, preserving contextual queries rather than introducing a separate retrieval pathway.
31. Character and location lore-subject requests accept canonical `FullId` values only: `char:<id>` and `loc:<id>`.
    They validate the required type and reject bare or differently typed values, resolving the subject's entry at
    `<collection>/<subject-id>.md` in the observer's perspective (hyphen/underscore-equivalent).
32. `CharacterLorePromptSection` must query every character in `ISceneContext.Characters` from the owning character's
    observer perspective. It orders the owner first and all others by ordinal exact `Character.FullId`.
33. `CharacterLorePromptSection` must fail for invalid or duplicate scene character `FullId` values; it must not
    silently merge their lore.
34. The shared NPC prompt stack includes essential world lore before character lore, preserving authored section order.
35. Lore prompt sections keep their pseudo-XML outer wrappers rendered by the shared `PseudoXmlPromptWriter` /
    `PseudoXmlFormatter` utilities (for example `<Essential Lore> ... </Essential Lore>`), with the formatted
    Markdown entry batch as the section content.
36. Read-time page triage in the Markdown lore backend is normative and applies to both bases:
    - A file with no frontmatter block is authoring-time only and is skipped silently.
    - A file whose frontmatter parses but carries neither an `id` nor a `title` is authoring-time only and is
      skipped silently.
    - A file whose frontmatter carries a `title` but no `id` loads, with an authoring identity derived from its
      source path; it is addressed canonically like any other entry (requirement 9).
    - A file whose frontmatter block never closes after its opening `---` is skipped with a logged warning: a stray
      `---` in a scratch file must not break runtime, but dropping a possibly runtime-eligible entry deserves a
      signal.
    - Frontmatter field validation (`title`, `essential`, `priority`, and optional `type` consistency) applies to
      runtime-eligible pages — any page carrying an `id` or a `title`; invalid values must throw, keeping the
      existing fail-hard contract. A `subject_id` field is rejected outright because identity is path-authoritative.
37. Body cleaning happens at parse time in the Markdown lore backend, before entries are constructed, and never inside
    fenced code blocks, which pass through verbatim. The lore formatter's output contract (requirement 21) is
    unchanged.
38. A block from a line that is exactly `<!-- lore:ignore -->` to the next line that is exactly
    `<!-- /lore:ignore -->` (both marker lines inclusive) is removed from the body. Markers must sit on their own
    lines.
39. All remaining bare HTML comments are stripped from bodies everywhere outside fenced code blocks.
40. Link syntax is reduced at parse time, outside fenced code blocks:
    - Inline links: `[label](target)` becomes `label`.
    - Reference links: `[label][ref]` becomes `label`, and `[ref]: …` definition lines are dropped.
    - Collapsed references: `[label][]` becomes `label`.
    - Autolinks: `<url>` becomes `url`.
    - Annotated pipe wiki references: ``[[path|Title]](`target_id`)`` becomes ``Title(`target_id`)``. The
      reduction is textual: the source path is dropped and the title plus exact backtick-annotated target ID are
      preserved verbatim, with no link resolution or content lookup. The annotated target ID prefers the canonical
      full ID for subject-bound targets (user requirement 21), keeping the reduced form a valid `read_lore` handle.
    - Bare `[label]` with no following `(` or `[`, image syntax, and unannotated `[[…]]` wiki links are left
      untouched.
41. A runtime-eligible page whose body is empty after parse-time cleaning is excluded from query results with a
    logged warning, not an error.

### Discovery and Canonical-ID Retrieval

42. Parse optional `description` as a single-line frontmatter value using the existing simple frontmatter format, not
    general YAML. Absent or blank values mean no description; do not derive excerpts or model summaries from bodies.
    Descriptions are runtime-facing authored metadata and must not contain authoring-only notes or source paths.
    Description quality (user requirement 19) and title readability (user requirement 14) are authoring conventions,
    not parser rules: the parser accepts any nonempty `title` value, so readable display titles and legacy ID-shaped
    titles are equally valid and no new rejection rule is introduced.
43. Lore entries are addressed by canonical full IDs of the form `[type]:[wiki-relative path]`, with the
    type→directory map `char`→`characters`, `loc`→`locations`, and `world`→`world`. Match canonical IDs by exact
    ordinal equality of the type and each path segment, with hyphens and underscores equivalent within segments —
    `locations/interrogation-room.md` resolves from `loc:interrogation_room` — without case folding, trimming
    requested values, subject-ID interpretation, or alias lookup. Load-time validation fails when two files in one
    directory collide under the hyphen/underscore equivalence. One canonical ID maps to at most one file per
    resolution base — the canonical wiki or one observer's perspective — so canonical lookups are unambiguous by
    construction. The loader separately validates per-scope uniqueness of authoring identities (authored
    dot-handle ids and path-derived fallbacks) across all runtime-eligible world, character, and location entries
    within one observer/content scope, including nested pages. Diagnostics identify the conflicting sources
    internally; model-facing failures expose no source paths. Reuse across observers or content roots is valid.
44. Catalogue selection includes every runtime-eligible entry in the bound observer/content scope except entries
    actually selected for the shared stack's automatic essential-world and scene-character injection. Share selection
    logic with those sections rather than duplicating their rules. Exclude by canonical ID, not subject binding or
    category.
    Location entries remain discoverable; no current-location property or automatic location-selection system is needed.
45. The catalogue formatter emits groups in World, Characters, Locations order and applies requirement 14's
    deterministic ordering within each group. Every selected entry appears once with its backticked canonical full
    ID, title, and optional description — for example `` - `world:charter` — The Charter — {description} `` — but
    no body, source path, or authoring dot-handle id. Omit empty groups and omit catalogue content when all groups
    are empty.
46. Render the catalogue once as part of AI-003's session-start system instruction, after the automatic lore sections.
    Later retrieval does not rebuild it. Ordinary asynchronous reads require no new cache or snapshot subsystem.
47. The `read_lore` input is a non-empty list of canonical-ID strings; a single lookup uses a one-element list.
    Validate the complete list before lookup: reject missing, null, non-list, empty-list, non-string, or blank-ID
    inputs as invalid, with no partial lore result. Non-blank strings are exact canonical lookup keys resolved
    against the active observer's perspective through the type→directory map and segment path
    (hyphen/underscore-equivalent) — never paths into the wiki tree, authoring dot handles, or aliases. Deduplicate
    repeated IDs by ordinal equality, preserving first-request order; return exactly one result per distinct ID in
    that order.
48. Each successful batch result explicitly associates its requested canonical ID with either its formatted lore
    body or an unavailable status, using the envelope ``Entry `{requestedID}`:`` followed by the formatted body, or
    ``Entry `{requestedID}`: unavailable.`` Unknown IDs — including IDs available only outside the bound scope —
    unsupported types such as `scenario:`, authoring dot-handle ids, and malformed strings are unavailable without
    revealing whether another scope contains them; when the requested string equals an in-scope entry's authored
    dot-handle id, the unavailable result appends a hint pointing at the canonical form, for example
    ``unavailable — request canonical IDs such as `char:vadim`.`` The tool never reads the canonical wiki as content
    or fallback. Mixed found/missing batches succeed with both result kinds. Automatically injected entries remain
    retrievable. Use the existing cleaned entries and lore body formatter (requirements 21 and 36–41); the result
    envelope adds ID association without changing body formatting.
49. Catalogue and canonical-ID queries accept and propagate cancellation through asynchronous reads. Cancellation
    before completion cancels the whole operation: do not return a successful partial batch or translate
    cancellation into unavailable lore. Source validation failures remain errors, not missing knowledge. No
    arbitrary batch limit is set.
50. AI-002 normatively owns `read_lore` composition, trusted observer/content binding, no-observation execution, and
    default exchange retention. AI-003 normatively owns static catalogue composition and guidance to retrieve
    relevant lore before acting, including the canonical-reference and backtick presentation teaching. Neither
    integration may bypass the isolation and cleaning contracts here.

### Canonical Presentation And Layered Retrieval

51. In all model-visible text — prompts, formatted lore, tool descriptions and results, event history, scene
    snapshots, and dialogue examples — canonical IDs and tool names (for example `read_lore`) are enclosed in
    backticks; authoring dot-handle ids appear nowhere. Tool-call argument values remain exact bare strings, and
    frontmatter values remain bare: backticks are a display convention over data, not part of any value. This
    rendering contract applies to the model-facing surfaces owned by AI-001 (observation canonical text), AI-002
    (tool descriptions and results), AI-003 (scene status and shared guidance), and AI-012 (dialogue speaker
    labels); those specs' rendering contracts comply with it.
52. The lore service layer reads both bases: canonical wiki entries (no observer) and perspective entries
    (observer-scoped). Retrieval accepts a full canonical ID, or a short id with an explicit kind, and selects its
    base explicitly or through the service's configured scope; the exact API shape is an implementation concern.
    Canonical reads without an observer are first-class service capability for non-LLM consumers — tests, tooling,
    and future features — while every LLM-facing path (prompt sections and `read_lore` composition through
    `AgentMind`) resolves only against the active observer's perspective and never serves canonical wiki content.

## In Scope

- Content-scoped lore repositories rooted at `game/lore/` for `default` and `game/content/<content-id>/lore/` for packs.
- Canonical wiki base under `wiki/` — including the authoring-only `scenarios/` collection — read by the lore
  service layer for non-LLM consumers and never entering LLM-visible context.
- Perspective Markdown authoring conventions under `perspectives/<observer-type>/<observer-id>/`, mirroring the
  canonical wiki layout.
- `world/`, `locations/`, and `characters/` perspective lore collections, with nested subdirectories under `world/`
  at any depth as multi-segment canonical IDs.
- Canonical ID grammar and addressing: `[type]:[wiki-relative path]` with the type→directory map,
  hyphen/underscore segment equivalence with load-time collision validation, and canonical `read_lore` requests
  with unavailable results and dot-handle hints.
- Path-authoritative identity: loader-derived kind, subject binding, and canonical ID; optional authoring
  dot-handle ids that are never model-visible; wiki bare `id` plus `type` validated against the path; removal of
  `subject_id`.
- Backticked canonical-ID and tool-name presentation across model-visible surfaces.
- Top-level `essential: true` frontmatter for baseline world-lore prompt injection.
- Contextual retrieval for location and character lore needed by the current prompt context.
- Optional `priority` frontmatter and deterministic ordering for retrieved entries.
- Fixture/sample perspective content uses canonical `FullId` values, including typed values `char:ally` and
  `loc:interrogation_room`, and consistent `priority` usage in ordering-sensitive cases.
- Async runtime lore query and presentation-agnostic formatting contracts for perspective lore.
- Essential world-query construction from the typed owning character supplied by AI-003.
- Character-lore query construction for all scene characters from the owning character's perspective.
- Canonical `FullId` character and location lore-subject requests with type validation.
- Read-only AgenticMind prompt consumption of perspective lore.
- Perspective entries as the default replacement for canonical lore in LLM-visible prompt and tool consumption.
- Missing perspective entries as absent observer knowledge with no canonical content entering LLM-visible context.
- Service-layer retrieval over both bases — canonical wiki reads without an observer and observer-scoped
  perspective reads — for non-LLM consumers.
- Authoring guidance that prevents perspective entries from implying unstated concrete prompt-usable facts.
- First-person subjective voice authoring convention for perspective entries as the observer's internal monologue on
  the subject.
- Full-ID subject referencing in prompt-facing lore: identity carried by the entry path (restated on wiki pages by
  the path-matching `id`/`type`) and full-ID subject references in body prose, names carried as explicit lore facts
  under the canonical/perspective name rule, and readable Title Case observer-safe display titles independent of
  IDs.
- H1-less entry authoring: body content starts directly after frontmatter and authored sections start at `#`, with
  entry titles rendered into prompts by the Markdown lore formatter.
- Read-time page triage and parse-time body cleaning in the Markdown lore backend: runtime-intent-based inclusion,
  `lore:ignore` omission, bare HTML comment stripping, and link-label reduction, including annotated pipe wiki
  references, before entry construction.
- Optional descriptions, grouped session-start discovery, scoped unique authoring identities, and asynchronous
  canonical-ID batch retrieval.
- Scope-preview descriptions on all 15 default pages, preserving facts, perspective boundaries, and essential
  flags.
- Deterministic query, prompt, and tool-flow validation with the AI-002 and AI-003 integrations.

## Out Of Scope

- Graph compiler artefacts, including `compiled/` output and deterministic graph sync.
- Ontology authoring or validation, including required `ontology/` files for the `default` runtime sample root.
- AI suggestions workflow, including required `suggestions/` files or agent classification of suggestions.
- Full content-pack lore authoring workflow beyond keeping the content-root mapping stable.
- Optional lore fragment search, dynamic retrieval ranking, token-budgeted prompt projection, or vector indexes beyond
  the first-slice query contract.
- Omniscient/system lore channels, narrator/game-master lore channels, or any pathway that injects canonical wiki
  content into LLM-visible context; the service-layer canonical reads for non-LLM consumers (requirement 52) are
  not such a pathway.
- Multiple separately-addressable entries per subject (one canonical ID maps to one file per base), `scenario:`
  addressing through the tool layer, and `item:` typed lore subjects or collections.
- General wiki-link resolution or wiki-link rewriting beyond the annotated pipe reference reduction in technical
  requirement 40, including any canonical content fallback for wiki references.
- Episodic memory, relationship state, or model-directed lore mutation during gameplay.
- Dynamic writes, save snapshots, memory curator/auditor workflows, and automated canonical-to-perspective conversion
  tooling.
- Mandatory external graph databases or embedding stores.
- Final production lore content beyond the small example set.
- Automatic location injection or new location-selection infrastructure, catalogue hot reload, automatic summarisation,
  and live-model behavioural experiments. Deterministic validation of availability and execution flow remains required.

## Acceptance Criteria

1. Runtime loading resolves `default` to `game/lore` / `res://lore` and optional content id `<id>` to
   `game/content/<id>/lore` / `res://content/<id>/lore`.
2. Runtime loading reads perspective lore from `perspectives/<observer-type>/<observer-id>/world/`, `locations/`,
   and `characters/` collections — the canonical path mirroring the wiki layout, with nested `world/`
   subdirectories at any depth — through the asynchronous query service using canonical observer and subject
   `FullId` values; `char:vadim` resolves to `perspectives/char/vadim/` without a colon in the path.
3. AgenticMind prompt consumption requests lore for its associated character observer `FullId` and does not consume
   canonical facts as a substitute for that perspective.
4. AgenticMind prompt lore presents perspective entries as the character's beliefs, not as canonical facts plus
   supplemental belief overlays.
5. Missing perspective entries do not trigger canonical fallback in LLM-visible prompt or tool lore.
6. The implementation does not require every canonical entry to have a matching perspective entry for every character.
7. Omniscient constraints required for LLM behaviour are kept out of character belief lore and represented only through
   system/developer rules or a future narrator/game-master channel.
8. World entries with `essential: true` are included in baseline prompt lore for the active perspective.
9. Contextual location and character queries select by subject, not by `essential`; automatic injection remains limited
   to essential world and scene-character bodies, with no automatic location section.
10. Any present `essential` value on a runtime-eligible page is validated as a boolean, and any present `priority`
    value participates in deterministic API ordering.
11. Entries are ordered deterministically by priority and exact authoring identity — the authored dot-handle `id` or
    its path-derived fallback; title and backend-internal source path remain final sort keys without permitting
    duplicate runtime-eligible authoring identities within an observer/content scope.
12. `LoreEntry` does not expose source path as public result data; the Markdown backend retains it privately for
    diagnostics and the final sorting tie-breaker.
13. Runtime prompt sections query the lore abstraction, not hardcoded prompt-section paths, and do not write lore data.
14. The default lore formatter produces deterministic Markdown prompt output: each entry renders an injected
    `# {title}` heading from the verbatim title, one blank line, the entry's backticked canonical full ID on its
    own line, one blank line, and the body with headings demoted to start at `##` or deeper, reflowed prose
    paragraphs, blank-line runs collapsed to one, and exactly one blank line between entries, with no per-entry
    XML tags. The ID line is uniform across world, character, and location entries and never shows a dot handle.
    Lore prompt sections still wrap the entry batch in pseudo-XML (for example
    `<Essential Lore> ... </Essential Lore>`).
15. `EssentialLorePromptSection` constructs its essential world query from `buildContext.Character.FullId` and validates
    that lore identity only when used before querying the lore abstraction.
16. The general prompt build context contains no lore query, factory, or resolver, and lore-free stacks do not require a
    non-empty lore identity.
17. The implementation does not require ontology files, compiled graph artefacts, suggestions directories, lore fragment
    search, dynamic writes, save snapshots, memory curator/auditor workflows, automated canonical-to-perspective
    conversion tooling, token-budget projection, vector indexes, or full content-pack lore authoring workflow for this
    slice.
18. Perspective lore reviews flag entries that claim an observer knows a concrete prompt-usable fact without stating the
    value or scoping it as unknown, unavailable, or not prompt-relevant.
19. This spec is linked from the AI specification index and the project specification index.
20. `EssentialLorePromptSection` returns only essential world lore.
21. `CharacterLorePromptSection` queries every scene character from the owner's canonical `FullId` perspective, with
    the owner first and remaining characters ordered by ordinal exact `FullId`.
22. Invalid or duplicate scene character `FullId` values fail during character-lore construction.
23. Character and location subject requests accept only canonical `char:<id>` and `loc:<id>` values, validate their
    type, and reject bare or differently typed values.
24. Typed lore values include `char:ally` and `loc:interrogation_room`; the corresponding type-scoped directory layout
    contains no colon.
25. Perspective entries are written in the observer character's first-person subjective voice and remain
    information-complete without the canonical `wiki/` entry, conveying all observer-available information about the
    subject.
26. Subject-bound lore entries, canonical `wiki/` entries and perspective entries alike, reference subjects by full
    ID in body prose where the name would appear and handle names per the full-ID rule: canonical entries state the
    subject's name once as an explicit fact, and perspective entries state a known name or state that the observer
    does not know it. Their `title` values are readable Title Case display labels independent of IDs — a known name
    where the observer's knowledge supports it, otherwise an observer-known descriptive label, never an invented or
    leaked unknown name — and canonical and perspective titles for the same subject may differ. Legacy ID-shaped
    titles remain runtime-valid; reviews flag newly authored ID-shaped titles as convention issues, not parser
    errors.
27. Authored lore entries omit a heading that duplicates the frontmatter `title`: body content starts directly after
    the frontmatter and authored sections start at `#` at authoring time.
28. Authoring-time content never reaches prompts: pages with no frontmatter block, pages whose frontmatter carries
    neither an `id` nor a `title`, `lore:ignore` blocks, bare HTML comments, and original link syntax are absent
    from formatted prompt lore, and reduced links appear as their label text, with annotated pipe wiki references
    appearing as ``Title(`target_id`)`` — title plus the exact backtick-annotated target ID, no source path,
    preferring the canonical full ID for subject-bound targets.
29. Co-located authoring files cannot break runtime queries: files with no frontmatter block, frontmatter carrying
    neither an `id` nor a `title`, and unterminated frontmatter blocks never cause query failures, and the
    unterminated and empty-body cases log warnings.
30. Unit or integration tests exercise every read-time triage outcome and every parse-time cleaning form: silent
    skips, warned skips, `lore:ignore` omission, bare-comment stripping, inline, reference, collapsed, and autolink
    reduction, annotated pipe wiki reference reduction to ``Title(`target_id`)``, the untouched forms (bare
    `[label]`, image syntax, unannotated `[[…]]` wiki links), fenced-code-block pass-through, and the empty-body
    exclusion.
31. Frontmatter validation remains fail-hard on runtime-eligible pages: invalid `title`, `essential`, or `priority`
    values throw rather than skip, an optional `type` that contradicts its collection directory fails load
    validation, and a `subject_id` field is rejected outright because identity is path-authoritative.
32. The default lore formatter's output contract is unchanged by parse-time cleaning: the requirement 21 formatting
    behaviour, including the canonical ID line, and its tests hold without modification.

### Discovery User Requirements

33. The NPC retains the same automatic lore bodies and sees every other eligible entry exactly once in the grouped
    catalogue, including locations and fixture-authored non-essential world lore. Every listing carries the entry's
    backticked canonical full ID, and authoring dot-handle ids appear nowhere model-visible. Empty groups are
    absent; descriptions are optional, with no generated fallback. This verifies user requirements 4, 11, 17, and
    25.
34. Deterministic provider fixtures show an NPC requesting relevant canonical IDs, receiving their bodies on the
    next reasoning request, and then choosing an action. Retrieval is not a prerequisite for every action. No
    canonical, other-observer, authoring-only, or source-path information leaks; unavailable entries yield no
    fabricated knowledge. This verifies user requirements 11 and 18, not a live model's ability to choose the
    right entry.
35. Content checks confirm scope-preview descriptions on all 15 default pages — topic lists grounded in each entry
    body, in sentence case with an initial capital, with no fact summaries, when/why-only lookup triggers, stage
    directions, behavioural absolutes, or unjustified scenario narrowing — plus unchanged lore facts and essential
    flags, and no LLM-visible exposure of canonical descriptions (user requirements 19 and 20).

### Discovery Technical Requirements

36. Metadata tests cover absent, blank, and populated descriptions while retaining page triage and cleaning behaviour
    (TR-42), and confirm the parser accepts readable display titles and legacy ID-shaped titles alike without new
    rejection rules (TR-42, user requirement 14). Query tests cover recursive world collections, all three
    categories, canonical resolution through the type→directory map with hyphen/underscore equivalence,
    per-scope authoring-identity uniqueness, allowed reuse across scopes, observer/content isolation, and no
    canonical content in LLM-facing results (TR-30, TR-43, TR-52).
37. Prompt tests prove shared automatic-selection exclusion, unchanged automatic bodies, exact-once listings with
    backticked canonical IDs, category and within-category order, optional descriptions, coherent empty output,
    and session-start-only rendering (TR-44–TR-46). Use non-essential-world fixtures rather than changing default
    essential flags.
38. Batch tests cover single and multiple canonical IDs, invalid whole-list inputs, repeated requested IDs,
    first-request ordering, mixed found/missing results, automatic-entry retrieval, envelope-associated bodies,
    unavailable results for unknown IDs, unsupported `scenario:` types, malformed strings, and authoring dot
    handles — including the dot-handle hint — plus scoped duplicate failures with internal diagnostics only, and
    cancellation without partial success (TR-43, TR-47–TR-49).
39. AI-002 and AI-003 acceptance verifies trusted tool binding, retained retrieval reaching the next provider
    request, no observations, unchanged action disposal, and shared retrieval guidance with canonical-reference
    and backtick teaching (TR-50).
40. An end-to-end regression proves an authored annotated pipe wiki reference renders as ``Title(`target_id`)``
    with a canonical full ID for subject-bound targets, no source path, and no residual wiki brackets in both the
    automatically injected opening request payload and the retrieved `read_lore` result replayed to the next
    provider request; the same reference inside a fenced code block passes through verbatim. This verifies user
    requirement 21 and TR-40.

### Canonical Addressing, Layering, And Presentation

41. Loader tests prove path-authoritative identity: kind, canonical subject binding, and canonical full ID derive
    from the collection directory and path segments; a `subject_id` field is rejected; an optional `type` that
    contradicts its directory fails load validation; a page whose frontmatter carries a `title` but no `id` loads
    with its path-derived authoring identity; and two files in one directory that collide under
    hyphen/underscore equivalence fail load validation (TR-4, TR-6, TR-9, TR-36, TR-43).
42. Canonical addressing resolves per observer: the same canonical ID retrieves an entry for one observer and
    returns unavailable for another; `world:` IDs resolve including multi-segment paths; `scenario:` IDs are
    always unavailable; the found envelope associates the requested canonical ID with its formatted body; and the
    unavailable envelope carries the dot-handle hint when the request matched an in-scope authoring id
    (TR-43, TR-47, TR-48).
43. Layered-retrieval tests prove the service reads both bases — canonical wiki entries without an observer and
    perspective entries scoped to an observer — through the shared retrieval surface, while canonical wiki
    content appears in no LLM-visible prompt or tool result; the existing fixture-wiki isolation assertions
    remain valid as LLM-facing pipeline checks (TR-25–TR-27, TR-52).
44. Formatted lore, catalogue rows, and the `read_lore` envelope enclose canonical IDs and tool names in
    backticks, and the owning specs verify the same presentation for event history (AI-001/AI-003), scene status
    (AI-003), and dialogue speaker labels (AI-012); tool-call argument values and frontmatter values remain bare
    strings (user requirements 22–25, TR-45, TR-48, TR-51).

## References

- [AI System](../index.md)
- [AI-001: Mind Component](../001-mind/index.md)
- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [CORE-008: Content Pack Resolution](../../core/008-content-pack-resolution/index.md)
- [CORE-009: Identifiable Identity](../../core/009-identifiable-identity/index.md)
- `game/lore`
- `game/content/<content-id>/lore`
