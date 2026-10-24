---
name: writer-guide-lore
description: Writer-only guide for delegated perspective lore writing.
---

# Lore Writing Guide

Use this guide when `loremaster` delegates perspective lore writing under `game/lore/` or
`game/content/<content-id>/lore/`. The formatting conventions below apply equally to both roots; applying them
within a content pack is that pack's authors' responsibility, and delegated lore edits do not retrofit existing
pack content to conform.

## Source Of Truth

- `loremaster` delegation packet.
- Approved source material supplied by `loremaster`.
- `@specs/ai/004-lore-backstory/index.md`.

## Invocation Requirements

If delegated lore writing lacks any item below, ask the invoker for clarification before editing:

1. active content id and lore root,
2. observer full ID and perspective path (for example `char:vadim` and `perspectives/char/vadim/`),
3. target entry path or collection (`world/`, `locations/`, or `characters/`),
4. canonical counterpart path when the perspective entry is bound to an existing canonical entry,
5. source material approved for canon use,
6. intended reader/consumer of the entry, especially whether it is prompt-available.

When a perspective entry is bound to a canonical counterpart, the canonical path must be supplied or unambiguously
derivable from the target entry path. If it is not, stop and ask the invoker which canonical entry owns the structure.

## Writing Rules

1. Treat `loremaster` as the canon decision owner. Write only within the active content id, lore root, source paths,
   observer id, and intended perspective supplied by the invocation.
2. Write character perspective entries as observer-available knowledge: beliefs, memories, assumptions, or available
   context, not omniscient canon plus a belief overlay.
3. Write perspective entries in the observer character's first-person voice, as an internal monologue on the subject:
   - the observer speaks as "I" throughout the entry,
   - the entry conveys all observer-available information about the subject so the topic is understandable without the
      canonical `wiki/` entry,
   - the prose is embellished with the observer's personality, attitudes, and judgements rather than being a mechanical
      pronoun flip of third-person text,
   - external narrator observations become the observer's own self-perception or rationalisations, with no omniscient
      asides and no new concrete prompt-usable facts.
4. Reference subjects by full ID, not by name, in prompt-facing lore. Full IDs (`[type]:[id]`, with types `char`,
   `loc`, and `item`) are identity trackers, not names:
   - entry and subject identity derive from the entry's path — restated on wiki pages by the path-matching
     frontmatter `id` and `type` — never from the display title and never from a `subject_id` field, which no longer
     exists: a page carrying one fails load validation,
   - body prose references a subject entity by its full ID wrapped in backticks (for example `` `char:ally` ``) where
     the name would appear; pronouns and purely descriptive references ("the room", "the table") remain natural,
   - a canonical entry states its subject's name once as an explicit fact (for example "His name is Vadim."),
   - a perspective entry either states a name by which the observer knows the subject, or states that the observer
     does not know the name.
5. Address lore entries by canonical full ID of the form `[type]:[wiki-relative path]`, with types `char`, `loc`, and
   `world` (for example `char:vadim`, `loc:interrogation_room`, `world:charter`, and `world:notes/nested` for a
   nested page):
   - hyphens and underscores are equivalent within path segments — `locations/interrogation-room.md` is addressed
     as `loc:interrogation_room` — so never author two files in one directory whose stems collide under that
     equivalence,
   - canonical IDs resolve against the active observer's perspective: the same ID may be available to one observer
     and unavailable to another,
   - `scenario:` pages exist for wiki authoring only — they have no perspective counterpart and are never available
     through the LLM-facing tool layer — so never reference them from prompt-facing prose,
   - authoring dot-handle ids (for example `vadim.self`) are authoring-only and never appear in prompt-facing prose,
     descriptions, or link labels.
6. Titles are readable display labels independent of IDs, written in Title Case:
   - use a known name the observer's knowledge supports (for example `Ally`, `Vadim`),
   - when the observer does not know a name, use an observer-known descriptive label (for example `The Detained
     Vesari`, `The Basement Interview Room`), never an invented name or a name the observer does not know,
   - canonical and perspective entries for the same subject may use different titles; do not copy the canonical title
     into a perspective entry when the observer's knowledge differs,
   - the prompt formatter renders titles as Markdown headings such as `# Ally`,
   - legacy ID-shaped titles (for example `char:vadim`) remain runtime-valid; never introduce one for a new or
     retitled entry.
7. Descriptions are concise scope previews: they name the meaningful topics or dimensions of information the entry
   covers, so a catalogue reader can infer retrieval relevance without reading the body:
   - ground every advertised topic in the entry body and keep it observer-safe; advertise nothing the body does not
     cover,
   - write in the entry's voice — first person for perspective entries, third person for canonical `wiki/` entries,
   - do not summarise the entry's facts, and avoid when/why-only triggers, imperative or mandatory-retrieval
     framing, stage directions, and behavioural absolutes that would restrict roleplay,
   - name a specific scenario only when it is intrinsic to the entry (a scenario entry); never narrow a broader
     character, location, or world profile to one situation,
   - use sentence case with an initial capital, on a single line, with no authoring notes or source paths,
   - existing lowercase metadata keys, `type` values, and boolean tokens stay lowercase.
8. Do not use canonical lore as an automatic fallback. A missing perspective entry means no prompt-available contextual
   knowledge for that observer and subject.
9. Do not invent lore facts, relationships, memories, aliases, tags, links, or concrete prompt-usable facts.
10. If a concrete detail may affect dialogue or action, either state the supplied value, state that it is unknown,
    unavailable, or not prompt-relevant, or omit it.
11. Use `essential: true` only for world lore. Location and character entries must rely on contextual selection rather
    than essential marking.
12. For perspective-bound entries, mirror the canonical counterpart's authoring structure at the canonical path in
    the observer's tree. Mirroring governs structure only; prose voice always follows the first-person monologue
    rule above, and titles follow the display-title rule:
    - place the entry under `perspectives/<observer-type>/<observer-id>/` at the canonical path mirroring the wiki
      layout — `characters/<subject-id>.md` and `locations/<subject-id>.md` sit directly in their collections, named
      after the subject they bind, and `world/**/*.md` nests at any depth — so the kind and subject binding derive
      from the tree position, never from frontmatter; `dialogue.md` (AI-012 dialogue examples) is opaque to lore
      parsing and follows its own speaker-label convention,
    - keep the same collection/category and filename stem unless the invoker explicitly approves a remap,
    - keep the same Markdown heading outline, including section order and heading levels, under the H1-less
      convention where authored sections start at `#`,
    - keep perspective-specific prose inside the matching canonical sections instead of adding, removing, or reordering
      sections without approval.
13. Author frontmatter per base — identity never comes from frontmatter restatement:
    - perspective pages carry `title` (required on runtime-eligible pages), optional `description`, `essential`, and
      `priority`, an optional authoring dot-handle `id` (for example `vadim.self`) that is authoring-only and unique
      within its observer/content scope, and an optional `type` that must match the collection directory when
      present — a mismatch fails load validation; a page whose frontmatter carries a `title` but no `id` still loads,
      with an identity derived from its path,
    - wiki pages carry a bare `id` plus load-bearing `type` (for example `id: vadim` + `type: char`; world pages
      `id: charter` + `type: world`), both validated against the page's path — `type` against the mapped top-level
      directory and `id` against the wiki-relative path segments, hyphen/underscore-equivalent — and may
      additionally include `title`, `description`, `aliases`, `tags`, `essential`, `priority`, and typed `links`,
    - `subject_id` exists on neither base, and composed full IDs (`type:id`) appear in prose references and labels,
      never as frontmatter values.
14. Preserve valid frontmatter, aliases, tags, wiki links, typed links, and existing authored wording unless the request
    explicitly scopes a change.
15. Keep prose concise and perspective-safe: prefer direct statements the observer can use over meta-commentary about
    canon, tooling, or compilation.
16. Start entry body content directly after the frontmatter: do not author a title H1 duplicating the frontmatter
    `title`, and start authored sections at `#`. Author each paragraph on a single line, with no linebreaks inside
    sentences and exactly one blank line between paragraphs; never hard-wrap prose.
17. Reference another lore entry with a Markdown link whose target is the target entry's path relative to the lore
    content root (for example `wiki/characters/vadim.md`, or `perspectives/char/vadim/world/charter.md` for a
    perspective entry), and whose label is the display name followed by the target entry's composed canonical full
    ID in backticks inside parentheses — for example ``[Vadim(`char:vadim`)](wiki/characters/vadim.md)`` and
    ``[The Charter(`world:charter`)](perspectives/char/vadim/world/charter.md)``. Rendered prompts keep only the
    label text, so link syntax never reaches the model.
18. Author annotated pipe wiki references as ``[[path|Title]](`target_id`)`` with the target's canonical full ID as the
    annotated ID, preferring the canonical full ID for subject-bound targets — for example
    ``[[characters/vadim|Vadim]](`char:vadim`)``: parse-time reduction emits ``Title(`target_id`)`` textually, and the
    reduced ID must stay a valid `read_lore` handle.
19. Wrap IDs and tool names in backticks in all prose that reaches prompts — body text, `description` values, link
    labels, annotated reference IDs, dialogue example text, and speaker labels (`` `char:visitor`: `` or the literal
    token `` `{{ character.FullId }}`: `` with the placeholder inside its authored backticks). Frontmatter values and
    tool-call argument values stay bare: backticks are a rendering convention, not data syntax.

## Examples

Scope-preview descriptions (frontmatter `description`, sentence case with an initial capital):

- Good: `My background, professional identity, self-image, and attitudes towards the Office and those I investigate.`
  — user-approved wording for Vadim's self-profile entry; previews topics without summarising facts.
- Good: `The setup, participants, objectives, and procedure of the detention-interview scenario.` — a scenario entry
  names its intrinsic scenario scope.
- Bad: `A young Vesari woman with conditional standing` — a fact summary duplicating the body's answers.
- Bad: `When my manner in an interview matters — calm, direct, thorough, never raising my voice` — narrows a broad
  self-profile to interviews, and `never raising my voice` is a stage direction prescribing behaviour.
- Bad: `When to weigh her clean record and flagged temper against the expectation of cooperation` — a when/why
  lookup trigger, not a topic preview.

Display titles (frontmatter `title`, Title Case, independent of IDs):

- Good: `Ally` — the observer knows her name from her file.
- Good: `The Detained Vesari` — the observer never learned the name; the label uses only observer-known grounding.
- Bad: `char:ally` — an ID, not a readable label; legacy pages may carry it, new authoring must not.
- Bad: `Alina Petrova` — an invented name the observer does not know.

## Consistency Checks

- Active content id, lore root, observer perspective, and target collection are explicit.
- The entry remains reachable through the active lore root and AI-004 layout: perspective entries sit at
  `perspectives/<observer-type>/<observer-id>/<canonical path>` mirroring the wiki tree, with flat subject-named
  `characters/` and `locations/` pages and `world/` nesting at any depth.
- Counterpart comparison is reported for perspective-bound entries, including canonical path, path/category result,
  path-derived subject-identity result, heading-outline result, display-title appropriateness, and any approved
  divergence; titles may differ between canonical and perspective entries, so title equality is not compared.
- Perspective entries read as the observer's first-person internal monologue on the subject and convey all
  observer-available information so the topic is understandable without the canonical `wiki/` entry, with no omniscient
  asides or new concrete prompt-usable facts.
- Body prose references subjects by full ID wrapped in backticks where the name would appear; canonical entries state
  the subject's name once as an explicit fact, and perspective entries state a known name or explicitly state that
  the observer does not know it.
- Frontmatter matches the base contract: perspective pages carry no `subject_id`, any optional `type` matches the
  collection directory, and any dot-handle `id` is unique within its observer/content scope; wiki pages carry a bare
  path-matching `id` plus load-bearing `type`.
- Every ID and tool name written in prose, `description` values, link labels, and dialogue speaker labels is
  backticked — including template placeholders that render IDs (`` `{{ character.FullId }}` ``) — while frontmatter
  values stay bare; authoring dot-handle ids never appear in prompt-facing text.
- Entry titles are readable Title Case display labels independent of IDs — known names where observer knowledge
  supports them, otherwise observer-known descriptive labels — with no invented or leaked unknown names and no newly
  authored ID-shaped titles.
- Descriptions preview the entry's supported topics so retrieval relevance is inferable: grounded in the body,
  observer-safe, in the entry's voice, sentence case with an initial capital, with no fact summaries, when/why-only
  triggers, stage directions, behavioural absolutes, or unjustified scenario narrowing.
- Entries start body content directly after frontmatter with no title H1 and authored sections starting at `#`;
  each paragraph is a single line with no linebreaks inside sentences, separated by exactly one blank line.
- Cross-references to other lore entries use a Markdown link with the target path relative to the lore content root
  and a label of display name plus the target entry's backticked composed canonical full ID in parentheses; annotated
  pipe references carry the target's canonical full ID as their annotated ID.
- Prompt-usable concrete facts are stated, scoped as unknown/unavailable/not prompt-relevant, or omitted.
- The edit does not introduce canonical fallback, omniscient constraints, or unsupported graph/compiler workflow scope.

## Escalate Immediately When

- The request lacks an active content id, lore root, observer id, target collection, or intended perspective.
- A perspective-bound entry lacks a canonical counterpart path, or the requested target path/outline conflicts with that
  counterpart without explicit approval.
- A requested edit would introduce `subject_id`, a composed full ID as a frontmatter value, or any other frontmatter
  restatement of path-derived identity.
- Requested lore would promote AI-inferred concepts, relation changes, duplicate merges, ontology additions, or
  omniscient constraints into canon without user approval.
- Requested lore would force prompt consumers to infer unstated concrete facts such as names, ages, dates,
  registrations, employment history, or relationships.
- The task asks `writer` to decide canon, merge duplicates, select a lore root, or resolve source conflicts.
