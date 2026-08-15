---
id: SPCH-006
title: Hearing Component
legacy_id: HEAR-001
legacy_body_id: BODY-006
---

# Hearing Component

> **Historical Traceability:** HEAR-001 and BODY-006 are superseded identifiers only. SPCH-006 is authoritative.

## Requirement

Characters must acquire completed speech as immutable sensory data without coupling acquisition to interpretation.

## Goal

Provide a reusable hearing sense that receives completed voice publications and leaves self filtering, attribution, and
observation creation to AI-006.

## User Requirements

1. NPCs can receive completed speech for later perception.
2. Blank transport speech is ignored.
3. Hearing does not itself change attention, memories, or speaker attribution.

## Technical Requirements

1. `SpeechPercept`, `IHearing`, `IHasHearing`, and `Hearing` live directly in `AlleyCat.Speech`, not in a hearing
   subnamespace.
2. `IHearing : ISense` defines `ReceiveVoice(string speech, IVoice source)`. It declares exactly `SpeechPercept` and
   is the Voice listener contract.
3. `IHasHearing : IComponentHolder` exposes `TryGetHearing(out IHearing? hearing)` and `RequireHearing()`.
4. `Hearing : Node, IHearing` owns listener lifecycle and implements `ReceiveVoice`.
5. Voice dispatches completed speech to grouped `IHearing` implementations through
   `ReceiveVoice(string speech, IVoice source)`. The group constant remains owned by `IHearing` as
   `"voice_listeners"`.
6. Hearing rejects only null, empty, or whitespace-only transport speech.
7. For each accepted publication, Hearing snapshots speech and the source's raw local `Id` into one immutable
   `SpeechPercept` and publishes it synchronously.
8. Hearing must not know its observer's voice, filter self speech, attribute a character, create an observation, or
   reference Mind. AI-006 assigns those interpretation responsibilities to `SpeechPerception`.

## In Scope

- The top-level `AlleyCat.Speech` hearing contracts and component.
- Voice-to-`IHearing.ReceiveVoice(string, IVoice)` listener lifecycle and completed-speech acquisition.
- Immutable synchronous `SpeechPercept` publication.
- The boundary between sensory acquisition and AI-006 interpretation.

## Out Of Scope

- Decoupling Voice from the `IHearing.ReceiveVoice(string, IVoice)` mechanism; that remains a later change.
- Spatial hearing, acoustic propagation, distance attenuation, and directional filtering.
- Self-speech filtering, character attribution, attention, and observation creation.
- Speech generation, playback, and voice submission, which SPCH-005 owns.

## Acceptance Criteria

### User Requirements

1. Completed nonblank speech is available to NPC perception without direct attention or memory mutation.
2. Blank transport speech produces no percept.

### Technical Requirements

1. Contract tests verify `SpeechPercept`, `IHearing`, `IHasHearing`, and `Hearing` are directly in
   `AlleyCat.Speech`, while Voice implementations remain in `AlleyCat.Speech.Voice`.
2. Tests verify `IHearing : ISense`, `IHasHearing` resolution, Hearing listener lifecycle, and dispatch through
   `ReceiveVoice(string, IVoice)`.
3. Tests verify exactly `SpeechPercept` and one immutable synchronous speech/raw-source-ID snapshot.
4. Tests verify no observer-voice, attribution, observation, or Mind dependency. AI-006 owns self filtering.

## References

### Implementation

- `@game/src/Speech/SpeechPercept.cs`
- `@game/src/Speech/IHearing.cs`
- `@game/src/Speech/IHasHearing.cs`
- `@game/src/Speech/Hearing.cs`

### Related Specifications

- [SPCH-005: Voice Component](../005-voice/index.md)
- [AI-006: Percept-Based Sensing And Attention](../../ai/006-character-perception-and-attention/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
