---
id: AI-012
title: Character Dialogue Examples
---

# Character Dialogue Examples

## Requirement

Per-character example-dialogue files must be injected into each NPC's session-start system instruction (the
`AgenticMind` `SystemInstruction` stack, AI-001 and AI-003) as a distinct prompt section. The section carries its own
static guidance, authored in a dedicated prompt file and referenced from the section, so the model imitates the
character's mannerism and speech pattern from those examples while treating their content as never having happened;
stacks without the section carry no dialogue instruction. Example content is data, not template source: apart from
one sanctioned character-ID token, it reaches the instruction exactly as authored, so malformed template syntax can
never break session start.

## Goal

Authors shape a character's voice through sample exchanges — the Marinara Engine / SillyTavern "example dialogue"
technique — without those examples becoming lore, knowledge, or memory. Characters without examples start unchanged,
and content packs can supply their own examples under the same resolution rules.

## User Requirements

1. Authors can provide per-character dialogue examples as a Markdown file inside that character's perspective folder,
   scoped by the same observer identity as perspective lore (AI-004). Speaker labels follow the event-history
   identity convention (`Heard char:vadim say: …`): the character's own lines are labelled with the literal token
   `{{ character.FullId }}`, which the section resolves to the character's full ID, and conversational partners carry
   valid-looking fictional full IDs such as `char:ally`.
2. A character's examples appear in that character's session-start system instruction under a distinct
   `Dialogue Examples` section, separate from the lore sections, exactly as authored except that every authored
   `{{ character.FullId }}` label reads as the character's resolved full ID.
3. Dialogue guidance travels with the section: when a stack includes it, the instruction explains how to treat
   examples — imitate the character's vocabulary, rhythm, and mannerisms from them, while knowing the exchanges
   never happened, are not lore, knowledge, or memory, and must never be recited or have their content treated as
   fact. Stacks without the section carry no dialogue instruction.
4. Characters without examples start sessions normally; the absence of examples is a normal authoring state, not an
   error.
5. Examples never surface through lore discovery or retrieval: they appear in no lore query result, in no
   session-start catalogue, and in no `read_lore` result.
6. Content packs can supply dialogue examples for their characters with identical resolution and behaviour.
7. Authors can write example content without template expertise: apart from the character-label token, any
   template-like text in a dialogue file is inert data — it reaches the instruction literally and can never break a
   session or alter other sections.

## Technical Requirements

1. Source path convention: `lore/perspectives/<observer-type>/<observer-id>/dialogue.md` under the active content
   root — `res://lore/...` for the `default` root and `res://content/<content-id>/lore/...` for packs, matching
   AI-004's lore-root mapping (AI-004 TR-2–TR-5). The section resolves the path from `buildContext.Character.FullId`
   plus `buildContext.Scene.Content`, splitting the observer `FullId` (CORE-009) into type and id segments without a
   colon in the path — the same mapping as the perspective root (AI-004 TR-4 and TR-6).
2. `DialogueExamplesPromptSection` is a Godot `GlobalClass` `PromptSection` resource like its sibling sections,
   runtime-backed through the async build contract (AI-003). Its section name is `Dialogue Examples`, so the shared
   `PseudoXmlPromptWriter` renders it as `<Dialogue Examples> ... </Dialogue Examples>`. The section owns its
   guidance: an exported guidance path on the section resource references the guidance file, defaulting to
   `res://prompts/dialogue.md`. When guidance is referenced, the section renders its guidance text before the example
   content inside the same section wrapper; an empty or absent reference yields example content alone.
3. The section reads the file and returns its content as authored: no parsing, no normalisation, and no format
   validation. The exchange format inside the file — SillyTavern-style `---`-separated exchanges with
   speaker-labelled lines — is a pure authoring convention; the runtime treats the file as opaque text. Exactly one
   substitution is sanctioned: the section itself replaces each authored `{{ character.FullId }}` occurrence with
   the resolved `buildContext.Character.FullId`. Everything else is data, not template source: no other template
   expression is substituted, malformed template syntax reaches the final rendered instruction literally, and it can
   never break session-start compilation.
4. The section reads the file directly; it is not lore access and must not route through the lore query abstraction
   (AI-004 TR-19). The file sits beside the lore collections, never inside `world/`, `locations/`, or `characters/`,
   and the lore backend reads only those three collections (AI-004), so `dialogue.md` is invisible to lore queries,
   the session-start catalogue, and `read_lore`. AI-004's contracts remain unchanged and normative for the
   collections.
5. A missing `dialogue.md` yields empty section content and the session still starts. Wrapper emission follows the
   existing `PseudoXmlPromptWriter` behaviour, which renders every non-empty-name section's wrapper even when its
   content is empty; the writer is not changed.
6. An invalid owner `FullId` fails session-start prompt building clearly. The section validates identity only when
   used, following the `EssentialLorePromptSection` precedent (AI-004 TR-18); it must not make dialogue-example
   identity a general prompt-stack requirement.
7. The section is added to `game/assets/characters/prompts/generic_npc_prompt_stack.tres` after the `Characters`
   section and before `Lore Catalogue`. It participates in AI-003's single session-start compile of
   `SystemInstruction`; its authored stack order remains movable like every other section's.
8. The dialogue guidance lives in `game/prompts/dialogue.md`, a dedicated static prompt file carrying the mandatory
   concepts of user requirement 3: the concepts are mandatory content, and the exact prose stays tunable. The
   guidance stays character-neutral and is project-authored static text carried through the same opaque path — no
   token substitution, not template source. It must not contradict AI-004's belief-vs-fact framing or AI-002's
   exchange contracts. `game/prompts/mind.md` carries no dialogue-examples passage: the shared instruction returns
   to its pre-AI-012 form.
9. Content packs supply their own `dialogue.md` files with identical resolution and behaviour. No per-mind or
   per-instance override paths exist.

## In Scope

- The `dialogue.md` path convention and its resolution from observer `FullId` and content context, for the default
  root and content packs.
- `DialogueExamplesPromptSection` with authored passthrough, single-token substitution, template opacity,
  missing-file empty behaviour, and invalid-identity failure.
- Stack wiring in the shared NPC prompt stack between `Characters` and `Lore Catalogue`.
- The section's exported guidance path and the dedicated `game/prompts/dialogue.md` guidance file with its mandatory
  concepts.
- The lore-exclusion contract keeping `dialogue.md` invisible to lore queries, the catalogue, and `read_lore`.

## Out Of Scope

- Parsing or validating the exchange format: the file is opaque text to the runtime, so no format grammar, linting,
  or rejection rules exist.
- Token-budget management for example content.
- Prompt-editor tooling and example-preview UI.
- Multiple example files per character and per-language example variants.
- Hot reload of example files during a session.
- Per-mind or per-instance path overrides.
- Changes to `PseudoXmlPromptWriter` empty-section behaviour.
- General template processing of example or guidance content beyond the single sanctioned token, including new
  render-context values for dialogue files.

## Acceptance Criteria

### User Requirements

1. A character with an authored `dialogue.md` receives that file's content in its session-start system instruction
   inside the distinct `Dialogue Examples` section, with every authored `{{ character.FullId }}` label rendered as
   the character's full ID and everything else unchanged (UR-1, UR-2).
2. A character without `dialogue.md` starts a session normally, with an empty section and no error (UR-4).
3. With the section present, the rendered instruction carries every mandatory concept of user requirement 3; a stack
   without the section renders no dialogue guidance anywhere in its instruction (UR-3).
4. With `dialogue.md` present, no lore query result, session-start catalogue, or `read_lore` result contains its
   content or lists the file (UR-5).
5. A content pack supplying `dialogue.md` for one of its characters behaves identically to a default-root example
   (UR-6).
6. A session whose dialogue file contains template-like text beyond the sanctioned token — including malformed
   `{{` and `{%` sequences — starts successfully with that text rendered literally and every other section unchanged
   (UR-7).

### Technical Requirements

1. Path tests verify `char:vadim` resolves to `lore/perspectives/char/vadim/dialogue.md` under the active content
   root, for both the `default` root and a content pack (TR-1).
2. Content tests verify section output equals the file content exactly except for sanctioned-token substitution:
   authored `{{ character.FullId }}` occurrences render as the resolved full ID, with no parsing, normalisation, or
   format-driven transformation and no other expression substituted (TR-3).
3. Missing-file tests verify empty section content, a successful session start, and unchanged wrapper emission
   (TR-5).
4. Invalid-identity tests verify a clear session-start failure and that identity validation happens only when the
   section is used (TR-6).
5. Stack tests verify the section renders between `Characters` and `Lore Catalogue` in the shared stack's compiled
   output, and that moving the authored section order moves the rendered section (TR-7).
6. Guidance tests verify the referenced guidance file covers each mandatory concept without asserting exact prose,
   that `mind.md` carries no dialogue-examples passage, that the guidance text reaches the rendered instruction with
   the section, and that an empty or absent guidance reference leaves the example content alone in the section
   (TR-2, TR-8).
7. Isolation tests verify the lore backend reads only the `world/`, `locations/`, and `characters/` collections —
   unchanged from AI-004 — so `dialogue.md` never enters lore entries, the catalogue, or retrieval (TR-4).
8. Opacity tests verify a dialogue file carrying malformed template syntax compiles and renders literally without
   failing session start, and that the sanctioned token is the only substituted expression in the rendered section
   (TR-3).

## References

- [AI System](../index.md)
- [AI-001: Mind Component](../001-mind/index.md)
- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [AI-004: Lore And Backstory Source Compilation](../004-lore-backstory/index.md)
- [CORE-008: Content Pack Resolution](../../core/008-content-pack-resolution/index.md)
- [CORE-009: Identifiable Identity](../../core/009-identifiable-identity/index.md)
- `game/assets/characters/prompts/generic_npc_prompt_stack.tres`
- `game/prompts/dialogue.md`
- `game/prompts/mind.md`
- `game/src/Mind/AI/Prompting/`
- `game/src/Mind/Observation/ObservedSpeech.cs`
