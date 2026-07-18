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
3. Do not use canonical lore as an automatic fallback. A missing perspective entry means no prompt-available contextual
   knowledge for that observer and subject.
4. Do not invent lore facts, relationships, memories, aliases, tags, links, or concrete prompt-usable facts.
5. If a concrete detail may affect dialogue or action, either state the supplied value, state that it is unknown,
   unavailable, or not prompt-relevant, or omit it.
6. Use `essential: true` only for world lore. Location and character entries must rely on contextual selection rather
   than essential marking.
7. For perspective-bound entries, mirror the canonical counterpart's authoring structure:
   - keep the same collection/category and filename stem unless the invoker explicitly approves a remap,
   - keep the same top-level title and Markdown heading outline, including section order and heading levels,
   - preserve structural frontmatter needed to identify the same subject, such as `title`, `type`, and `subject_id`
     where applicable, while using perspective-specific `id` values,
   - keep perspective-specific prose inside the matching canonical sections instead of adding, removing, or reordering
     sections without approval.
8. Preserve valid frontmatter, aliases, tags, wiki links, typed links, and existing authored wording unless the request
   explicitly scopes a change.
9. Keep prose concise and perspective-safe: prefer direct statements the observer can use over meta-commentary about
   canon, tooling, or compilation.

## Consistency Checks

- Active content id, lore root, observer perspective, and target collection are explicit.
- The entry remains reachable through the active lore root and AI-004 layout.
- Counterpart comparison is reported for perspective-bound entries, including canonical path, path/category result,
  frontmatter subject-identity result, title result, heading-outline result, and any approved divergence.
- Perspective-bound entries were compared with their canonical counterparts for collection/path, frontmatter subject
  identity, title, and Markdown heading outline.
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
