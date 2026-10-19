---
id: CORE-009
title: Identifiable Identity
---

# Identifiable Identity

## Requirement

Game objects require one canonical, typed identity contract so systems can safely refer to objects across scenes,
context, and lore without relying on ambiguous local IDs.

## Goal

Give contributors and runtime systems a compact identity vocabulary that preserves asset-local authoring while making
cross-object references unambiguous.

## User Requirements

1. Object identities remain stable and readable when shown or used by game systems.
2. A character's local identity can be authored per asset, while references to that character remain unambiguous.
3. Lore and context can identify characters and locations consistently.
4. Voice components have canonical object identity while retaining local IDs for operational speaker attribution.
   A character-owned voice receives its character's final installed local ID without making that attribution
   authenticated character provenance.
5. Local IDs may be authored in accepted non-ASCII scripts — for example Korean — as well as ASCII lower
   `snake_case`, and such characters install, resolve by exact identity, and inherit voice identities without
   transliteration or renaming.

## Technical Requirements

1. `AlleyCat.Core.IIdentifiable` replaces `IEntity` as the canonical game-object identity contract.
2. `Id` is a mutable, local identifier. A valid `Id` is one or more non-empty segments separated by single
   underscores. The first segment starts with a Unicode letter; later segments may start with a Unicode letter or
   decimal digit. Segments may continue with Unicode lowercase letters, uncased letters, decimal digits, and
   combining marks; a combining mark must follow a letter or digit, never start a
   segment, and mark sequences after that valid base are allowed. Cased letters must be lowercase — uncased scripts
   such as Hangul are accepted — and existing ASCII lower `snake_case` IDs remain valid unchanged.
3. Scene registration or installation must validate `Id` before the object becomes available to other scene systems.
4. `Type` is mandatory, read-only, and an ASCII lower `snake_case` identifier.
5. `FullId` is the canonical identity string in the exact form `Type:Id`.
6. All references from one object to another use `FullId`; local `Id` is not a cross-object reference.
7. This migration's type vocabulary includes `char`, `loc`, and `voice`.
8. `ICharacter` has the mandatory `Type` value `char`.
9. `IVoice : IComponent, IIdentifiable` has mandatory Type `voice`, mutable authored local `Id`, and canonical
   `FullId` `voice:<id>`.
10. Raw local voice `Id` remains operational attribution rather than authenticated provenance. Voice semantic identity
    comparisons use ordinal ID values rather than object-reference equality.
11. After CORE-005 target-scene precedence resolves a character's final local ID, character installation must assign
    each character-owned voice the same local ID and validate its resulting `voice:<character-id>` identity before
    exposure to scene consumers. Template placeholder voice IDs must be valid under the same local-ID grammar as any
    other local `Id` — ASCII lower `snake_case` or an accepted Unicode local ID — and are replaced at that final
    installation boundary.
12. `IIdentifiable` is the shared identity boundary for AI-003 render contexts and VISION-001 visual subjects. This
    does not make every identifiable object a renderable character or a visual subject.
13. Local `Id` validation is purely lexical and rejects empty IDs; uppercase cased letters; whitespace; control or
    format characters; path punctuation and colons; and leading, trailing, or repeated underscores. Validation and
    identity comparison perform no normalisation, case folding, transliteration, or confusable detection: `Id` and
    `FullId` remain unique and compare as exact ordinal strings, and canonically equivalent Unicode sequences remain
    distinct identities. The rejected classes keep local IDs usable as single path components on the supported
    filesystem; no broader filesystem portability is promised.

## In Scope

- The `IIdentifiable`, `Id`, `Type`, and `FullId` contracts.
- The local `Id` character grammar: Unicode letters, decimal digits, and combining marks with ASCII lower
  `snake_case` compatibility, lexical rejection rules, and exact ordinal identity comparison.
- Validation at scene registration and installation boundaries.
- The `char`, `loc`, and `voice` type vocabulary for this migration.
- Normative identity integration for character, context, and lore specifications.
- Character-owned voice-ID installation after target-scene precedence.
- Identifiable identities for AI-003 render contexts and VISION-001 visual subjects.

## Out Of Scope

- A global registry or persistence scheme for object identities.
- Additional object-type vocabulary beyond `char`, `loc`, and `voice`.
- Voice selection, voice assets, and speech-generation policy.
- Lore graph compilation or lore-content authoring beyond identity addressing.
- Confusable or look-alike detection, and arbitrary display-name acceptance for local IDs.
- A Unicode normalisation, case-folding, or transliteration layer, or a folder-name portability guarantee on every
  filesystem: identity comparison remains the exact ordinal contract defined in the Technical Requirements.

## Acceptance Criteria

### User Requirements

1. A character can retain an asset-authored local ID while consumers use its unambiguous `char:<id>` identity.
2. Character and location references can be represented without relying on display labels or voice attribution.
3. Voice identity remains canonical without presenting operational attribution as authenticated speaker provenance.
4. A character authored with a non-ASCII local ID (for example the Korean `강주영`) passes the same real runtime
   window as an unchanged ASCII control: registration and installation validate it, character-owned voices inherit
   the exact `voice:<id>` identity, an exact `char:<id>` lookup resolves it, and one scripted Mind request/tool cycle
   completes with turn and index files written under the exact local-ID directory. Readability of the JSON inside
   those files is AI-011's contract.

### Technical Requirements

1. The public identity contract is `AlleyCat.Core.IIdentifiable`; `IEntity` is not the identity contract.
2. `Id` is mutable and validates against the local-ID grammar — Unicode letters, uncased letters, decimal digits,
   combining marks after a valid base, single-underscore segments, ASCII lower `snake_case` compatible — and
   registration or installation rejects an invalid ID before exposure.
3. `Type` is mandatory, read-only ASCII lower `snake_case`; `ICharacter.Type` is `char`.
4. `FullId` is exactly `Type:Id`, and cross-object references use it.
5. The migration accepts `char`, `loc`, and `voice`, including `char:ally`, `loc:interrogation_room`, and
   `voice:ally`.
6. `IVoice` is both an `IComponent` and `IIdentifiable`; it retains mutable local `Id`, exact Type `voice`, and
   canonical `voice:<id>` `FullId`.
7. Tests verify ordinal voice-ID value comparison without object-reference identity and preserve raw local voice ID as
   operational attribution rather than authenticated provenance.
8. Installation tests verify each character-owned voice receives the final precedence-resolved `Character.Id`, only
   template placeholder IDs valid under the shared local-ID grammar — ASCII lower `snake_case` or an accepted
   Unicode local ID — are replaced, and `voice:<character-id>` validates before exposure.
9. AI-003 render contexts expose characters through the
   [TMPL-001](../../templating/001-templating-system/index.md) curated member-access policy — `IIdentifiable.FullId`
   is the curated member — without requiring `IIdentifiable` to implement render or visual-subject contracts.
10. Tests verify the local-ID grammar: accepted Korean and other uncased-script IDs, lowercase accented letters,
    combining marks following a valid base, mixed decimal digits, and unchanged valid ASCII IDs; rejected empty IDs,
    uppercase cased letters, whitespace, control and format characters, path punctuation, colons, and leading,
    trailing, or repeated underscores; and `Type` remaining ASCII-only lower `snake_case`.
11. Tests verify exact ordinal identity: canonically equivalent composed and decomposed sequences remain distinct
    identities with no normalisation, case folding, or transliteration, and installation inherits a non-ASCII
    `Character.Id` into a validating `voice:<character-id>` before exposure.

## References

- [CORE-005: Scene Installer System](../005-scene-installer-system/index.md)
- [CHAR-001: Character Skeleton Profile](../../character/001-character-skeleton/index.md)
- [AI-003: Prompt API](../../ai/003-prompt-api/index.md)
- [AI-004: Lore And Backstory Source Compilation](../../ai/004-lore-backstory/index.md)
- [SPCH-005: Voice Component](../../speech/005-voice/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [AI-006: Percept-Based Sensing And Attention](../../ai/006-character-perception-and-attention/index.md)
