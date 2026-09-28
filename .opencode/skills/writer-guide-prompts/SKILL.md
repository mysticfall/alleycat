---
name: writer-guide-prompts
description: Writer-only guide for authoring or editing shared prompt files under game/prompts/.
---

# Prompt File Writing Guide

Use this guide when writing, rewriting, or editing authored prompt files under `game/prompts/`.

## Scope

- These conventions apply only to authored prompt files (`game/prompts/*.md`).
- They do not apply to `specs/`, tests, code, or other documentation.
- Lore pages (`game/lore/**`, including content-pack lore roots) follow `.opencode/skills/writer-guide-lore/SKILL.md`.

## Source Of Truth

- User request and intended prompt-file scope.
- `@specs/ai/003-prompt-api/index.md` TR-12–TR-18 for the mandatory guidance topics of `game/prompts/mind.md`.

## Formatting Rules

1. Write each paragraph on a single line, with no linebreaks inside sentences, and separate paragraphs with exactly
   one blank line; never hard-wrap prose.
2. Wrap every subject full ID written in prose in backticks. Full IDs take the form `[type]:[id]` with types `char`,
   `loc`, and `item` (for example `` `char:vadim` ``).
3. Wrap every lore entry ID written in prose in backticks (for example `` `vadim.charter` ``).
4. Template placeholders such as `{{ character.FullId }}` are template expressions, not IDs: leave them unbackticked.
5. Tool names are not IDs: leave them unbackticked.

## Verbatim Rendering Rationale

`FilePromptSection` returns a prompt file's text as-is, with no render-time reflow, so the authored lines are exactly
what the model sees. Unlike lore pages, which the formatter reflows at render time, a prompt file's formatting reaches
the model unchanged.

## Content Ownership

- The guidance content of `game/prompts/mind.md` is spec-owned: AI-003 TR-12–TR-18 define its mandatory topics
  (event-history interpretation, current-scene interpretation, action selection, available watch tools, and lore
  discovery/retrieval); exact prose stays tunable.
- This guide licenses formatting changes only; substantive guidance changes in `mind.md` need AI-003-backed scope.
- Preserve the distinction taught in `mind.md`'s `# Subject References` section: prose references wrap IDs in
  backticks, while tool arguments take the exact bare ID string without backticks.

## Consistency Checks

- Formatting Audit: pass/fail — each paragraph is a single line with no linebreaks inside sentences, separated by
  exactly one blank line, and no prose is hard-wrapped.
- ID Backticking Audit: pass/fail — subject full IDs and lore entry IDs in prose are backticked, while template
  placeholders and tool names are not.
- Subject-Reference Distinction: pass/fail — prose ID references remain backticked and tool-argument ID references
  remain bare.
- Scope Audit: pass/fail — only files under `game/prompts/` changed, and `mind.md` guidance content changed only
  under an AI-003-backed request.

If any item is `fail` or uncertain, escalate instead of guessing.

## Escalate Immediately When

- A request changes `mind.md` guidance content beyond formatting without AI-003 backing.
- A requested edit would break the backticked-prose versus bare-tool-argument distinction in `# Subject References`.
- The task targets lore pages, `specs/`, tests, or code; use the matching guide or stop.
