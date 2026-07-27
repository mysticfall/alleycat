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

## Technical Requirements

1. `AlleyCat.Core.IIdentifiable` replaces `IEntity` as the canonical game-object identity contract.
2. `Id` is a mutable, local identifier in lower `snake_case`.
3. Scene registration or installation must validate `Id` before the object becomes available to other scene systems.
4. `Type` is mandatory, read-only, and a lower `snake_case` identifier.
5. `FullId` is the canonical identity string in the exact form `Type:Id`.
6. All references from one object to another use `FullId`; local `Id` is not a cross-object reference.
7. This migration's type vocabulary includes `char` and `loc`.
8. `ICharacter` has the mandatory `Type` value `char`.
9. Voice ID is a separate local attribution contract. It is not derived from, substituted for, or used as object
   identity.

## In Scope

- The `IIdentifiable`, `Id`, `Type`, and `FullId` contracts.
- Validation at scene registration and installation boundaries.
- The `char` and `loc` type vocabulary for this migration.
- Normative identity integration for character, context, and lore specifications.

## Out Of Scope

- A global registry or persistence scheme for object identities.
- Additional object-type vocabulary beyond `char` and `loc`.
- Voice selection, voice assets, and speech-generation policy.
- Lore graph compilation or lore-content authoring beyond identity addressing.

## Acceptance Criteria

### User Requirements

1. A character can retain an asset-authored local ID while consumers use its unambiguous `char:<id>` identity.
2. Character and location references can be represented without relying on display labels or voice attribution.

### Technical Requirements

1. The public identity contract is `AlleyCat.Core.IIdentifiable`; `IEntity` is not the identity contract.
2. `Id` is mutable local lower `snake_case`, and registration or installation rejects an invalid ID before exposure.
3. `Type` is mandatory, read-only lower `snake_case`; `ICharacter.Type` is `char`.
4. `FullId` is exactly `Type:Id`, and cross-object references use it.
5. The migration accepts `char` and `loc` types, including `char:ally` and `loc:interrogation_room` as typed lore
   values.
6. Voice ID remains independently local and cannot satisfy an `IIdentifiable` identity reference.

## References

- [CORE-005: Scene Installer System](../005-scene-installer-system/index.md)
- [CHAR-001: Character Skeleton Profile](../../character/001-character-skeleton/index.md)
- [CTX-001: Contextual Information API](../../context/001-contextual-information-api/index.md)
- [AI-004: Lore And Backstory Source Compilation](../../ai/004-lore-backstory/index.md)
