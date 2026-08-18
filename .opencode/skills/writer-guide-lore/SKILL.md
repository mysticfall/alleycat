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
   - subject-bound entries, canonical `wiki/` entries and perspective entries alike, use the subject's full ID as the
     frontmatter `title` and H1 heading (for example `char:vadim`, `loc:interrogation_room`); the prompt formatter
     renders these titles as tags such as `<char:vadim>`,
   - body prose references a subject entity by full ID where the name would appear; pronouns and purely descriptive
     references ("the room", "the table") remain natural,
   - a canonical entry states its subject's name once as an explicit fact (for example "His name is Vadim."),
   - a perspective entry either states a name by which the observer knows the subject, or states that the observer
     does not know the name.
5. Do not use canonical lore as an automatic fallback. A missing perspective entry means no prompt-available contextual
   knowledge for that observer and subject.
6. Do not invent lore facts, relationships, memories, aliases, tags, links, or concrete prompt-usable facts.
7. If a concrete detail may affect dialogue or action, either state the supplied value, state that it is unknown,
   unavailable, or not prompt-relevant, or omit it.
8. Use `essential: true` only for world lore. Location and character entries must rely on contextual selection rather
   than essential marking.
9. For perspective-bound entries, mirror the canonical counterpart's authoring structure. Mirroring governs structure
   only; prose voice always follows the first-person monologue rule above:
   - keep the same collection/category and filename stem unless the invoker explicitly approves a remap,
   - keep the same top-level title and Markdown heading outline, including section order and heading levels,
   - preserve structural frontmatter needed to identify the same subject, such as `title`, `type`, and `subject_id`
     where applicable, while using perspective-specific `id` values,
   - keep perspective-specific prose inside the matching canonical sections instead of adding, removing, or reordering
     sections without approval.
10. Preserve valid frontmatter, aliases, tags, wiki links, typed links, and existing authored wording unless the request
    explicitly scopes a change.
11. Keep prose concise and perspective-safe: prefer direct statements the observer can use over meta-commentary about
    canon, tooling, or compilation.

## Consistency Checks

- Active content id, lore root, observer perspective, and target collection are explicit.
- The entry remains reachable through the active lore root and AI-004 layout.
- Counterpart comparison is reported for perspective-bound entries, including canonical path, path/category result,
  frontmatter subject-identity result, title result, heading-outline result, and any approved divergence.
- Perspective-bound entries were compared with their canonical counterparts for collection/path, frontmatter subject
  identity, title, and Markdown heading outline.
- Perspective entries read as the observer's first-person internal monologue on the subject and convey all
  observer-available information so the topic is understandable without the canonical `wiki/` entry, with no omniscient
  asides or new concrete prompt-usable facts.
- Subject-bound entry titles and H1 headings use the subject's full ID, body prose references subjects by full ID
  where the name would appear, canonical entries state the subject's name once as an explicit fact, and perspective
  entries state a known name or explicitly state that the observer does not know it.
- Prompt-usable concrete facts are stated, scoped as unknown/unavailable/not prompt-relevant, or omitted.
- The edit does not introduce canonical fallback, omniscient constraints, or unsupported graph/compiler workflow scope.

## Escalate Immediately When

- The request lacks an active content id, lore root, observer id, target collection, or intended perspective.
- A perspective-bound entry lacks a canonical counterpart path, or the requested target path/outline conflicts with that
  counterpart without explicit approval.
- Requested lore would promote AI-inferred concepts, relation changes, duplicate merges, ontology additions, or omniscient
  constraints into canon without user approval.
- Requested lore would force prompt consumers to infer unstated concrete facts such as names, ages, dates, registrations,
  employment history, or relationships.
- The task asks `writer` to decide canon, merge duplicates, select a lore root, or resolve source conflicts.
