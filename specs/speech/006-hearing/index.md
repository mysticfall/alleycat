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
Completed automatic speech arrives as pause-delimited segments carrying generic grouping metadata; transient
onset/resume lifecycle signalling is not speech and never enters the hearing path.

## Goal

Provide a reusable hearing sense that receives completed voice publications — including grouped automatic segments —
and leaves self filtering, attribution, and observation creation to AI-006.

## User Requirements

1. NPCs can receive completed speech for later perception.
2. Blank transport speech is ignored.
3. Hearing does not itself change attention, memories, or speaker attribution.
4. Completed automatic segments carry generic grouping metadata — an optional speech-group identity, segment index,
   and continuation flag — so downstream perception can treat one speech group's segments as one logical utterance.
   Manual and AI speech remain ungrouped.
5. Onset and resumed speech activity are transient: they produce no percept, no observation, and no Mind timeline
   entry (AI-001).
6. Blank, failed, and abandoned automatic segment settlements are transient and textless: they produce no percept,
   observation, attention change, wait or freshness effect, Mind timeline entry, or transcript.

## Technical Requirements

1. `SpeechPercept`, `IHearing`, `IHasHearing`, and `Hearing` live directly in `AlleyCat.Speech`, not in a hearing
   subnamespace.
2. `IHearing : ISense` defines `ReceiveVoice(string speech, IVoice source)` and is the Voice listener contract.
   `PerceptTypes` declares exactly `SpeechPercept`.
3. `IHasHearing : IComponentHolder` exposes `TryGetHearing(out IHearing? hearing)` and `RequireHearing()`.
4. `Hearing : Node, IHearing` owns listener lifecycle and implements `ReceiveVoice`.
5. Voice dispatches completed speech to grouped `IHearing` implementations through
   `ReceiveVoice(string speech, IVoice source)`. The group constant remains owned by `IHearing` as
   `"voice_listeners"`.
6. Hearing rejects only null, empty, or whitespace-only transport speech.
7. For each accepted publication, Hearing snapshots the speech, the source's raw local `Id`, and any grouping
   metadata into one immutable `SpeechPercept` and publishes it synchronously.
8. Hearing must not know its observer's voice, filter self speech, attribute a character, create an observation, or
   reference Mind. AI-006 assigns those interpretation responsibilities to `SpeechPerception`.
9. `SpeechPercept` carries optional generic grouping metadata: nullable `SpeechGroupID`, `SegmentIndex` (default `0`),
   and `Continued` (default `false`). Values are fixed at construction and the percept remains immutable. Manual and
   AI speech are ungrouped — `SpeechGroupID` is null. The metadata is generic identity only and implies no draft,
   speculative, or adoption semantics (SPCH-008).
10. Transient onset/resume lifecycle signalling (SPCH-005, SPCH-008) must not reach `IHearing`: Hearing creates no
     percept, no observation, and no Mind timeline entry for it. Its forwarding and downstream consumption are owned
     by SPCH-005 and the AI specs (AI-001, AI-002).
11. `Blank`, `Failed`, and `Abandoned` automatic terminal settlements (SPCH-005 TR-36) must not reach `IHearing`.
    Hearing creates no `SpeechPercept` for them and has no terminal-settlement, draft, streaming, or transcript API.

## In Scope

- The top-level `AlleyCat.Speech` hearing contracts and component.
- Voice-to-`IHearing.ReceiveVoice(string, IVoice)` listener lifecycle and completed-speech acquisition.
- Immutable synchronous `SpeechPercept` publication, including optional generic grouping metadata on completed
  automatic segments.
- The boundary between sensory acquisition and AI-006 interpretation.

## Out Of Scope

- Decoupling Voice from the `IHearing.ReceiveVoice(string, IVoice)` mechanism; that remains a later change.
- Spatial hearing, acoustic propagation, distance attenuation, and directional filtering.
- Self-speech filtering, character attribution, attention, and observation creation.
- Speech generation, playback, and voice submission, which SPCH-005 owns.
- Onset/resume lifecycle signal forwarding and consumption, which SPCH-005 and the AI specs own; hearing only
  guarantees no percept is created for it.
- Terminal-settlement routing, which SPCH-005, AI-001, and AI-002 own; Hearing only guarantees no percept is created.

## Acceptance Criteria

### User Requirements

1. Completed nonblank speech is available to NPC perception without direct attention or memory mutation.
2. Blank transport speech produces no percept.
3. Completed automatic segments carry their grouping metadata into perception, while manual and AI speech remain
   ungrouped.
4. Onset and resumed speech activity produce no percept; only completed published speech becomes one.
5. Blank, failed, and abandoned automatic segment settlements produce no percept or transcript.

### Technical Requirements

1. Contract tests verify `SpeechPercept`, `IHearing`, `IHasHearing`, and `Hearing` are directly in
   `AlleyCat.Speech`, while Voice implementations remain in `AlleyCat.Speech.Voice`.
2. Tests verify `IHearing : ISense`, `IHasHearing` resolution, Hearing listener lifecycle, and dispatch through
   `ReceiveVoice(string, IVoice)`.
3. Tests verify `PerceptTypes` is exactly `SpeechPercept` and each accepted publication produces one immutable
   synchronous speech/raw-source-ID snapshot.
4. Tests verify no observer-voice, attribution, observation, or Mind dependency. AI-006 owns self filtering.
5. Tests verify `SpeechPercept` snapshots any grouping metadata immutably — nullable `SpeechGroupID` null for manual
   and AI speech, `SegmentIndex` defaulting to `0`, `Continued` defaulting to `false` — with values fixed at
   construction (TR-9).
6. Tests verify onset-only and resume lifecycle signalling create no percept, no observation, and no `IHearing`
   dispatch (TR-10).
7. Tests verify blank, failed, and abandoned terminal settlements create no `SpeechPercept`, observation, transcript,
   or `IHearing` dispatch (TR-11).

## References

### Implementation

- `@game/src/Speech/SpeechPercept.cs`
- `@game/src/Speech/IHearing.cs`
- `@game/src/Speech/IHasHearing.cs`
- `@game/src/Speech/Hearing.cs`

### Related Specifications

- [SPCH-005: Voice Component](../005-voice/index.md)
- [SPCH-008: Automatic Voice Detection](../008-automatic-voice-detection/index.md)
- [AI-006: Percept-Based Sensing And Attention](../../ai/006-character-perception-and-attention/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
