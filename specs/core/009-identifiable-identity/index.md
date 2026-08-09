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

## Technical Requirements

1. `AlleyCat.Core.IIdentifiable` replaces `IEntity` as the canonical game-object identity contract.
2. `Id` is a mutable, local identifier in lower `snake_case`.
3. Scene registration or installation must validate `Id` before the object becomes available to other scene systems.
4. `Type` is mandatory, read-only, and a lower `snake_case` identifier.
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
    exposure to scene consumers. Template placeholder voice IDs must be valid lower `snake_case` and are replaced at
    that final installation boundary.
12. `IIdentifiable` is the shared subject and optional-observer input boundary for CTX-001 context sources. This does
    not make every identifiable object contextual or a visual subject.

## In Scope

- The `IIdentifiable`, `Id`, `Type`, and `FullId` contracts.
- Validation at scene registration and installation boundaries.
- The `char`, `loc`, and `voice` type vocabulary for this migration.
- Normative identity integration for character, context, and lore specifications.
- Character-owned voice-ID installation after target-scene precedence.
- Identifiable inputs for CTX-001 sources and BODY-004 visual subjects.

## Out Of Scope

- A global registry or persistence scheme for object identities.
- Additional object-type vocabulary beyond `char`, `loc`, and `voice`.
- Voice selection, voice assets, and speech-generation policy.
- Lore graph compilation or lore-content authoring beyond identity addressing.

## Acceptance Criteria

### User Requirements

1. A character can retain an asset-authored local ID while consumers use its unambiguous `char:<id>` identity.
2. Character and location references can be represented without relying on display labels or voice attribution.
3. Voice identity remains canonical without presenting operational attribution as authenticated speaker provenance.

### Technical Requirements

1. The public identity contract is `AlleyCat.Core.IIdentifiable`; `IEntity` is not the identity contract.
2. `Id` is mutable local lower `snake_case`, and registration or installation rejects an invalid ID before exposure.
3. `Type` is mandatory, read-only lower `snake_case`; `ICharacter.Type` is `char`.
4. `FullId` is exactly `Type:Id`, and cross-object references use it.
5. The migration accepts `char`, `loc`, and `voice`, including `char:ally`, `loc:interrogation_room`, and
   `voice:ally`.
6. `IVoice` is both an `IComponent` and `IIdentifiable`; it retains mutable local `Id`, exact Type `voice`, and
   canonical `voice:<id>` `FullId`.
7. Tests verify ordinal voice-ID value comparison without object-reference identity and preserve raw local voice ID as
   operational attribution rather than authenticated provenance.
8. Installation tests verify each character-owned voice receives the final precedence-resolved `Character.Id`, valid
   lower-`snake_case` template placeholder IDs are replaced, and `voice:<character-id>` validates before exposure.
9. CTX-001 sources accept identifiable subject and optional-observer inputs without requiring `IIdentifiable` to
   implement contextual or visual-subject contracts.

## References

- [CORE-005: Scene Installer System](../005-scene-installer-system/index.md)
- [CHAR-001: Character Skeleton Profile](../../character/001-character-skeleton/index.md)
- [CTX-001: Contextual Information API](../../context/001-contextual-information-api/index.md)
- [AI-004: Lore And Backstory Source Compilation](../../ai/004-lore-backstory/index.md)
- [BODY-006: Voice Component](../../body/006-voice/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [AI-006: Percept-Based Sensing And Attention](../../ai/006-character-perception-and-attention/index.md)
