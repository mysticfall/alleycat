---
name: writer-guide-lore
description: Writer-only guide for delegated perspective lore writing.
---

# Lore Writing Guide

Use this guide when `loremaster` delegates perspective lore writing under `game/lore/` or
`game/content/<content-id>/lore/`.

## Source Of Truth

- `loremaster` delegation packet.
- Approved source material supplied by `loremaster`.
- `@specs/ai/004-lore-backstory/index.md`.

## Invocation Requirements

If delegated lore writing lacks any item below, ask the invoker for clarification before editing:

1. active content id and lore root,
2. observer id and perspective path,
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
   - entry and subject identity live in the frontmatter `id` and `subject_id`; the display `title` never carries
     identity,
   - body prose references a subject entity by full ID where the name would appear; pronouns and purely descriptive
     references ("the room", "the table") remain natural,
   - a canonical entry states its subject's name once as an explicit fact (for example "His name is Vadim."),
   - a perspective entry either states a name by which the observer knows the subject, or states that the observer
     does not know the name.
5. Titles are readable display labels independent of IDs, written in Title Case:
   - use a known name the observer's knowledge supports (for example `Ally`, `Vadim`),
   - when the observer does not know a name, use an observer-known descriptive label (for example `The Detained
     Vesari`, `The Basement Interview Room`), never an invented name or a name the observer does not know,
   - canonical and perspective entries for the same subject may use different titles; do not copy the canonical title
     into a perspective entry when the observer's knowledge differs,
   - the prompt formatter renders titles as Markdown headings such as `# Ally`,
   - legacy ID-shaped titles (for example `char:vadim`) remain runtime-valid; never introduce one for a new or
     retitled entry.
6. Descriptions are concise scope previews: they name the meaningful topics or dimensions of information the entry
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
7. Do not use canonical lore as an automatic fallback. A missing perspective entry means no prompt-available contextual
   knowledge for that observer and subject.
8. Do not invent lore facts, relationships, memories, aliases, tags, links, or concrete prompt-usable facts.
9. If a concrete detail may affect dialogue or action, either state the supplied value, state that it is unknown,
   unavailable, or not prompt-relevant, or omit it.
10. Use `essential: true` only for world lore. Location and character entries must rely on contextual selection rather
    than essential marking.
11. For perspective-bound entries, mirror the canonical counterpart's authoring structure. Mirroring governs structure
    only; prose voice always follows the first-person monologue rule above, and titles follow the display-title rule:
    - keep the same collection/category and filename stem unless the invoker explicitly approves a remap,
    - keep the same Markdown heading outline, including section order and heading levels, under the H1-less
      convention where authored sections start at `#`,
    - preserve structural frontmatter needed to identify the same subject, such as `type` and `subject_id` where
      applicable, while using perspective-specific `id` values and observer-appropriate display titles,
    - keep perspective-specific prose inside the matching canonical sections instead of adding, removing, or reordering
      sections without approval.
12. Preserve valid frontmatter, aliases, tags, wiki links, typed links, and existing authored wording unless the request
    explicitly scopes a change.
13. Keep prose concise and perspective-safe: prefer direct statements the observer can use over meta-commentary about
    canon, tooling, or compilation.
14. Start entry body content directly after the frontmatter: do not author a title H1 duplicating the frontmatter
    `title`, and start authored sections at `#`. Hard-wrapping prose is fine; the prompt formatter reflows
    paragraphs at render time.

## Examples

Scope-preview descriptions (frontmatter `description`, sentence case with an initial capital):

- Good: `My background, professional identity, self-image, and attitudes towards the Office and those I investigate.`
  — user-approved `vadim.self` wording; previews topics without summarising facts.
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
- The entry remains reachable through the active lore root and AI-004 layout.
- Counterpart comparison is reported for perspective-bound entries, including canonical path, path/category result,
  frontmatter subject-identity result, heading-outline result, display-title appropriateness, and any approved
  divergence; titles may differ between canonical and perspective entries, so title equality is not compared.
- Perspective entries read as the observer's first-person internal monologue on the subject and convey all
  observer-available information so the topic is understandable without the canonical `wiki/` entry, with no omniscient
  asides or new concrete prompt-usable facts.
- Body prose references subjects by full ID where the name would appear; canonical entries state the subject's name
  once as an explicit fact, and perspective entries state a known name or explicitly state that the observer does not
  know it.
- Entry titles are readable Title Case display labels independent of IDs — known names where observer knowledge
  supports them, otherwise observer-known descriptive labels — with no invented or leaked unknown names and no newly
  authored ID-shaped titles.
- Descriptions preview the entry's supported topics so retrieval relevance is inferable: grounded in the body,
  observer-safe, in the entry's voice, sentence case with an initial capital, with no fact summaries, when/why-only
  triggers, stage directions, behavioural absolutes, or unjustified scenario narrowing.
- Entries start body content directly after frontmatter with no title H1 and authored sections starting at `#`;
  hard-wrapped prose is acceptable because the prompt formatter reflows paragraphs at render time.
- Prompt-usable concrete facts are stated, scoped as unknown/unavailable/not prompt-relevant, or omitted.
- The edit does not introduce canonical fallback, omniscient constraints, or unsupported graph/compiler workflow scope.

## Escalate Immediately When

- The request lacks an active content id, lore root, observer id, target collection, or intended perspective.
- A perspective-bound entry lacks a canonical counterpart path, or the requested target path/outline conflicts with that
  counterpart without explicit approval.
- Requested lore would promote AI-inferred concepts, relation changes, duplicate merges, ontology additions, or
  omniscient constraints into canon without user approval.
- Requested lore would force prompt consumers to infer unstated concrete facts such as names, ages, dates,
  registrations, employment history, or relationships.
- The task asks `writer` to decide canon, merge duplicates, select a lore root, or resolve source conflicts.
