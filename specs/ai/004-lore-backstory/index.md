---
id: AI-004
title: Lore And Backstory Source Compilation
---

# Lore And Backstory Source Compilation

## Requirement

The current implementation slice must support content-scoped perspective Markdown lore roots as the human source of
truth for what each character believes or knows, with automatic prompt injection, a discoverable catalogue, and
read-only entry-ID retrieval for the active AgenticMind perspective.

## Goal

Authors should be able to write ordinary Markdown lore from a canonical character perspective so AI prompts receive
stable, deterministic context for that character without requiring graph compilation, memory mutation, or broader
lore-management workflows in this slice.

## User Requirements

1. Authors can write lore/backstory as local Markdown pages with prose, frontmatter, aliases, tags, and wiki links.
2. Authors can organise lore by observer perspective so a character prompt receives that character's beliefs and
   knowledge, not an omniscient canonical fact list.
3. Authors can mark perspective world lore as essential so baseline world context is injected for the active character.
4. Essential world and scene-character lore bodies remain automatically available. Contextual character and location
   queries remain available; this slice does not automatically inject location lore.
5. Prompt consumers receive deterministic lore text with each entry clearly demarcated by its title.
6. Authoritative lore remains controlled by the human-authored perspective wiki, not by generated graph artefacts.
7. Authors do not need to duplicate every canonical subject for every character perspective.
8. Characters receive lore only for subjects the author has made available to that observer perspective.
9. Canonical lore remains useful for authoring consistency and future generation tooling, without acting as a runtime
   substitute for observer knowledge in this slice.
10. Perspective entries do not force prompt consumers to invent unstated facts when they claim the observer knows a
    concrete detail that may affect dialogue or action.
11. An NPC can discover all remaining available lore and request one or more exact entry IDs, then read the bodies on a
    subsequent reasoning request before choosing an action. Retrieval is encouraged when relevant, not mandatory.
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
    and subject identity live in the frontmatter `id` and `subject_id`, never in the display title.
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
    and lists each entry's exact ID, title, and optional description. Missing descriptions need no generated fallback.
18. Discovery and retrieval expose neither canonical pages, other observers' knowledge, authoring-only material, nor
    source paths. Unknown IDs produce explicit unavailable results, never fabricated knowledge.
19. Descriptions are concise scope previews: they identify the meaningful topics or dimensions of information the
    entry covers, so retrieval relevance can be inferred without reading the body. Advertised topics are grounded in
    the entry body, observer-safe, and written in sentence case with an initial capital. A description is neither a
    summary of the entry's facts nor a lookup instruction: it avoids when/why-only triggers, imperative or
    mandatory-retrieval framing, stage directions, and behavioural absolutes, and it narrows the entry to a specific
    scenario only when that scenario is intrinsic to the entry itself (for example a scenario entry), never to
    narrow a broader character, location, or world profile.
20. All 15 existing default lore pages receive scope-preview descriptions without changing facts or essential flags;
    canonical descriptions remain authoring-only at runtime.

## Technical Requirements

1. Lore roots are resolved from the current CORE content context.
2. The fallback `default` content id has lore root `res://lore`, committed as `game/lore/`.
3. Optional content packs use lore root `res://content/<content-id>/lore`, committed under
   `game/content/<content-id>/lore/` when present.
4. Perspective lore for the default content root uses this layout:
    - `game/lore/perspectives/<observer-type>/<observer-id>/world/**/*.md`
    - `game/lore/perspectives/<observer-type>/<observer-id>/locations/**/*.md`
    - `game/lore/perspectives/<observer-type>/<observer-id>/characters/**/*.md`
    - Subdirectories under each collection are included at any depth; nested pages are part of the read contract.
    - Observer identity remains canonical `FullId` (for example `char:vadim`), while type-scoped directories avoid
      colons (for example `perspectives/char/vadim/`).
5. Perspective lore for content packs uses the same layout under
    `game/content/<content-id>/lore/perspectives/<observer-type>/<observer-id>/`.
6. Lore observers and subjects are CORE-009 `FullId` values. The directory layout separates the `Type` and `Id` segments
   without changing their identity; `Id` and `Type` are validated at scene registration or installation.
7. Runtime lore access must go through an asynchronous query service that accepts content context, observer `FullId`,
   and query intent.
8. AgenticMind lore prompt consumption must query lore for its associated character perspective and remain read-only.
9. Wiki pages may include frontmatter fields such as `id`, `title`, `description`, `aliases`, `tags`, `essential`,
   `priority`, and typed `links`. The `id` field controls runtime inclusion: a page without an `id` (including
   a page with no frontmatter block at all) is authoring-time only and is excluded from runtime queries.
10. A top-level frontmatter field `essential: true` marks only world lore for baseline prompt injection.
11. `essential`, when present on an `id`-bearing page, must be parsed and validated as a boolean.
12. Location and character entries are selected by contextual relevance, not by `essential`.
13. `priority`, when present on an `id`-bearing page, must be parsed as an ordering value used by the lore API to sort
    retrieved entries before formatting. Fixture/sample content should either set explicit priorities consistently
    where order is meaningful or test without relying on source-file ordering.
14. Sorting must be deterministic: sort by priority first, then entry `id`, then title, then the backend's source path
    as the final tie-breaker. Runtime entries always carry an `id` because pages without one are skipped at read time
    (requirement 36); the Markdown backend retains title and source path as deterministic tie-breakers.
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
    as Markdown: a `# {title}` heading from the verbatim, unsanitised entry title, one blank line, then the normalised
    body:
    - the shallowest body heading is demoted as a block, preserving relative depth and never promoting headings, so
      body headings start at exactly `##`,
    - consecutive non-blank prose lines within a paragraph are trimmed and joined with single spaces, removing
      mid-sentence hard-wrap line breaks,
    - runs of two or more blank lines collapse to a single blank line,
    - consecutive entries are separated by exactly one blank line, and fenced code blocks pass through verbatim.
22. The initial query service may read perspective Markdown source directly behind the query abstraction.
23. Lore source remains Markdown, and graph/compiler artefacts remain future derived outputs.
24. AgenticMind prompt lore must treat perspective entries as the active character's beliefs, not canonical facts plus
    supplemental belief overlays.
25. When a perspective entry exists for an observer and subject, it replaces any canonical entry for AgenticMind prompt
    use by default.
26. Missing perspective entries mean the observer has no prompt-available contextual knowledge for that subject.
27. The AgenticMind lore query path must not fall back to canonical entries unless a future omniscient or system context
    channel explicitly adds that behaviour.
28. Omniscient constraints that the LLM must obey must live in system/developer rules or a future narrator/game-master
    channel, not in character belief lore.
29. Perspective authoring and validation must flag claims that imply concrete prompt-usable knowledge without including
    the value or explicitly scoping it as unknown, unavailable, or not prompt-relevant.
30. Catalogue and batch entry-ID retrieval must extend the existing asynchronous query service with query intents or
    filters, preserving contextual queries rather than introducing a separate retrieval pathway.
31. Character and location lore-subject requests accept canonical `FullId` values only: `char:<id>` and `loc:<id>`.
    They validate the required type and reject bare or differently typed values.
32. `CharacterLorePromptSection` must query every character in `ISceneContext.Characters` from the owning character's
    observer perspective. It orders the owner first and all others by ordinal exact `Character.FullId`.
33. `CharacterLorePromptSection` must fail for invalid or duplicate scene character `FullId` values; it must not
    silently merge their lore.
34. The shared NPC prompt stack includes essential world lore before character lore, preserving authored section order.
35. Lore prompt sections keep their pseudo-XML outer wrappers rendered by the shared `PseudoXmlPromptWriter` /
    `PseudoXmlFormatter` utilities (for example `<Essential Lore> ... </Essential Lore>`), with the formatted
    Markdown entry batch as the section content.
36. Read-time page triage in the Markdown lore backend is normative:
    - A file with no frontmatter block is authoring-time only and is skipped silently.
    - A file whose frontmatter parses but carries no `id` is authoring-time only and is skipped silently.
    - A file whose frontmatter block never closes after its opening `---` is skipped with a logged warning: a stray
      `---` in a scratch file must not break runtime, but dropping a possibly `id`-bearing entry deserves a signal.
    - Frontmatter field validation (`title`, `subject_id`, `essential`, `priority`) applies only to `id`-bearing pages;
      invalid values on an `id`-bearing page must throw, keeping the existing fail-hard contract.
37. Body cleaning happens at parse time in the Markdown lore backend, before entries are constructed, and never inside
    fenced code blocks, which pass through verbatim. The lore formatter's output contract (requirement 21) is
    unchanged.
38. A block from a line that is exactly `<!-- lore:ignore -->` to the next line that is exactly
    `<!-- /lore:ignore -->` (both marker lines inclusive) is removed from the body. Markers must sit on their own
    lines.
39. All remaining bare HTML comments are stripped from bodies everywhere outside fenced code blocks.
40. Link syntax is reduced to its label text at parse time, outside fenced code blocks:
    - Inline links: `[label](target)` becomes `label`.
    - Reference links: `[label][ref]` becomes `label`, and `[ref]: …` definition lines are dropped.
    - Collapsed references: `[label][]` becomes `label`.
    - Autolinks: `<url>` becomes `url`.
    - Bare `[label]` with no following `(` or `[`, image syntax, and `[[…]]` wiki links are left untouched.
41. An `id`-bearing page whose body is empty after parse-time cleaning is excluded from query results with a logged
     warning, not an error.

### Discovery and Entry-ID Retrieval

42. Parse optional `description` as a single-line frontmatter value using the existing simple frontmatter format, not
    general YAML. Absent or blank values mean no description; do not derive excerpts or model summaries from bodies.
    Descriptions are runtime-facing authored metadata and must not contain authoring-only notes or source paths.
    Description quality (user requirement 19) and title readability (user requirement 14) are authoring conventions,
    not parser rules: the parser accepts any nonempty `title` value, so readable display titles and legacy ID-shaped
    titles are equally valid and no new rejection rule is introduced.
43. Lore entry IDs are distinct from subject `FullId` values. Match entry IDs by exact ordinal equality, without case
    folding, trimming requested values, subject-ID interpretation, or alias lookup. Require uniqueness across all
    runtime-eligible world, character, and location entries within one observer/content scope, including nested pages.
    Catalogue and ID queries validate this scope before returning results: ambiguous IDs fail the query rather than
    selecting arbitrarily. Diagnostics identify the conflicting sources internally; model-facing failures expose no
    source paths. Reuse across observers or content roots is valid.
44. Catalogue selection includes every runtime-eligible entry in the bound observer/content scope except entries
    actually selected for the shared stack's automatic essential-world and scene-character injection. Share selection
    logic with those sections rather than duplicating their rules. Exclude by entry ID, not subject ID or category.
    Location entries remain discoverable; no current-location property or automatic location-selection system is needed.
45. The catalogue formatter emits groups in World, Characters, Locations order and applies requirement 14's
    deterministic ordering within each group. Every selected entry appears once with exact ID, title, and optional
    description, but no body or source path. Omit empty groups and omit catalogue content when all groups are empty.
46. Render the catalogue once as part of AI-003's session-start system instruction, after the automatic lore sections.
    Later retrieval does not rebuild it. Ordinary asynchronous reads require no new cache or snapshot subsystem.
47. The `read_lore` input is a non-empty list of entry-ID strings; a single lookup uses a one-element list. Validate the
    complete list before lookup: reject missing, null, non-list, empty-list, non-string, or blank-ID inputs as invalid,
    with no partial lore result. Non-blank strings are exact lookup keys, not paths. Deduplicate repeated IDs by ordinal
    equality, preserving first-request order; return exactly one result per distinct ID in that order.
48. Each successful batch result explicitly associates its requested ID with either its formatted lore body or an
    unavailable status. Unknown IDs, including IDs available only outside the bound scope, are unavailable without
    revealing whether another scope contains them. Mixed found/missing batches succeed with both result kinds.
    Automatically injected entries remain retrievable. Use the existing cleaned entries and lore body formatter
    (requirements 21 and 36–41); the result envelope adds ID association without changing body formatting.
49. Catalogue and ID queries accept and propagate cancellation through asynchronous reads. Cancellation before
    completion cancels the whole operation: do not return a successful partial batch or translate cancellation into
    unavailable lore. Source validation failures remain errors, not missing knowledge. No arbitrary batch limit is set.
50. AI-002 normatively owns `read_lore` composition, trusted observer/content binding, no-observation execution, and
    default exchange retention. AI-003 normatively owns static catalogue composition and guidance to retrieve relevant
    lore before acting. Neither integration may bypass the isolation and cleaning contracts here.

## In Scope

- Content-scoped lore repositories rooted at `game/lore/` for `default` and `game/content/<content-id>/lore/` for packs.
- Perspective Markdown wiki authoring conventions under `perspectives/<observer-type>/<observer-id>/`.
- `world/`, `locations/`, and `characters/` perspective lore collections, including nested subdirectories at any
  depth.
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
- Perspective entries as the default replacement for canonical lore in AgenticMind prompt consumption.
- Missing perspective entries as absent observer knowledge with no automatic canonical fallback.
- Authoring guidance that prevents perspective entries from implying unstated concrete prompt-usable facts.
- First-person subjective voice authoring convention for perspective entries as the observer's internal monologue on
  the subject.
- Full-ID subject referencing in prompt-facing lore: identity carried by `id`/`subject_id` and full-ID subject
  references in body prose, names carried as explicit lore facts under the canonical/perspective name rule, and
  readable Title Case observer-safe display titles independent of IDs.
- H1-less entry authoring: body content starts directly after frontmatter and authored sections start at `#`, with
  entry titles rendered into prompts by the Markdown lore formatter.
- Read-time page triage and parse-time body cleaning in the Markdown lore backend: `id`-based inclusion,
  `lore:ignore` omission, bare HTML comment stripping, and link-label reduction before entry construction.
- Optional descriptions, grouped session-start discovery, scoped unique entry IDs, and asynchronous batch retrieval.
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
- Omniscient/system lore channels, narrator/game-master lore channels, or canonical prompt fallback pathways beyond the
  explicit no-fallback contract for this slice.
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
   and `characters/` collections, including nested subdirectories at any depth, through the asynchronous query
   service using canonical observer and subject `FullId` values; `char:vadim` resolves to
   `perspectives/char/vadim/` without a colon in the path.
3. AgenticMind prompt consumption requests lore for its associated character observer `FullId` and does not consume
   canonical facts as a substitute for that perspective.
4. AgenticMind prompt lore presents perspective entries as the character's beliefs, not as canonical facts plus
   supplemental belief overlays.
5. Missing perspective entries do not trigger automatic canonical fallback in AgenticMind prompt lore.
6. The implementation does not require every canonical entry to have a matching perspective entry for every character.
7. Omniscient constraints required for LLM behaviour are kept out of character belief lore and represented only through
   system/developer rules or a future narrator/game-master channel.
8. World entries with `essential: true` are included in baseline prompt lore for the active perspective.
9. Contextual location and character queries select by subject, not by `essential`; automatic injection remains limited
   to essential world and scene-character bodies, with no automatic location section.
10. Any present `essential` value on an `id`-bearing page is validated as a boolean, and any present `priority` value
    participates in deterministic API ordering.
11. Entries are ordered deterministically by priority and exact entry `id`; title and backend-internal source path
    remain final sort keys without permitting duplicate runtime-eligible IDs within an observer/content scope.
12. `LoreEntry` does not expose source path as public result data; the Markdown backend retains it privately for
    diagnostics and the final sorting tie-breaker.
13. Runtime prompt sections query the lore abstraction, not hardcoded prompt-section paths, and do not write lore data.
14. The default lore formatter produces deterministic Markdown prompt output: each entry renders an injected
    `# {title}` heading from the verbatim title, one blank line, and the body with headings demoted to start at `##`
    or deeper, reflowed prose paragraphs, blank-line runs collapsed to one, and exactly one blank line between
    entries, with no per-entry XML tags. Lore prompt sections still wrap the entry batch in pseudo-XML (for example
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
28. Authoring-time content never reaches prompts: pages without a frontmatter `id` (including files with no frontmatter
    block), `lore:ignore` blocks, bare HTML comments, and original link syntax are absent from formatted prompt lore,
    and reduced links appear as their label text.
29. Co-located authoring files cannot break runtime queries: files with no frontmatter block, frontmatter without an
    `id`, and unterminated frontmatter blocks never cause query failures, and the unterminated and empty-body cases
    log warnings.
30. Unit or integration tests exercise every read-time triage outcome and every parse-time cleaning form: silent
    skips, warned skips, `lore:ignore` omission, bare-comment stripping, inline, reference, collapsed, and autolink
    reduction, the untouched forms (bare `[label]`, image syntax, `[[…]]` wiki links), fenced-code-block
    pass-through, and the empty-body exclusion.
31. Frontmatter validation on `id`-bearing pages remains fail-hard: invalid `title`, `subject_id`, `essential`, or
    `priority` values throw rather than skip.
32. The default lore formatter's output contract is unchanged by parse-time cleaning: the requirement 21 formatting
     behaviour and its tests hold without modification.

### Discovery User Requirements

33. The NPC retains the same automatic lore bodies and sees every other eligible entry exactly once in the grouped
    catalogue, including locations and fixture-authored non-essential world lore. Empty groups are absent; descriptions
    are optional, with no generated fallback. This verifies user requirements 4, 11, and 17.
34. Deterministic provider fixtures show an NPC requesting relevant IDs, receiving their bodies on the next reasoning
    request, and then choosing an action. Retrieval is not a prerequisite for every action. No canonical,
    other-observer, authoring-only, or source-path information leaks; unavailable entries yield no fabricated knowledge.
    This verifies user requirements 11 and 18, not a live model's ability to choose the right entry.
35. Content checks confirm scope-preview descriptions on all 15 default pages — topic lists grounded in each entry
    body, in sentence case with an initial capital, with no fact summaries, when/why-only lookup triggers, stage
    directions, behavioural absolutes, or unjustified scenario narrowing — plus unchanged lore facts and essential
    flags, and no runtime exposure of canonical descriptions (user requirements 19 and 20).

### Discovery Technical Requirements

36. Metadata tests cover absent, blank, and populated descriptions while retaining page triage and cleaning behaviour
    (TR-42), and confirm the parser accepts readable display titles and legacy ID-shaped titles alike without new
    rejection rules (TR-42, user requirement 14). Query tests cover recursive collections, all three categories, exact
    ordinal IDs, duplicates across categories, allowed reuse across scopes, observer/content isolation, and no
    canonical fallback (TR-30, TR-43).
37. Prompt tests prove shared automatic-selection exclusion, unchanged automatic bodies, exact-once listings, category
    and within-category order, optional descriptions, coherent empty output, and session-start-only rendering
    (TR-44–TR-46). Use non-essential-world fixtures rather than changing default essential flags.
38. Batch tests cover single and multiple IDs, invalid whole-list inputs, repeated requested IDs, first-request
    ordering, mixed found/missing results, automatic-entry retrieval, ID-associated bodies, scoped duplicate failures
    with internal diagnostics only, and cancellation without partial success (TR-43, TR-47–TR-49).
39. AI-002 and AI-003 acceptance verifies trusted tool binding, retained retrieval reaching the next provider request,
    no observations, unchanged action disposal, and shared retrieval guidance (TR-50).

## References

- [AI System](../index.md)
- [AI-001: Mind Component](../001-mind/index.md)
- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [CORE-008: Content Pack Resolution](../../core/008-content-pack-resolution/index.md)
- [CORE-009: Identifiable Identity](../../core/009-identifiable-identity/index.md)
- `game/lore`
- `game/content/<content-id>/lore`
