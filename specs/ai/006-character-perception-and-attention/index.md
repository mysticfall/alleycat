---
id: AI-006
title: Percept-Based Sensing And Attention
---

# Percept-Based Sensing And Attention

## Requirement

NPC senses must publish immutable percepts for Mind-owned interpretation, attention updates, and observation ingestion
without coupling Body or Character production code to Mind.

## Goal

Separate sensory acquisition from semantic interpretation while preserving synchronous gameplay, deterministic
attention, existing speech history, and existing eye visibility and presentation behaviour.

## User Requirements

1. NPCs notice non-self speech, including speech from an unknown speaker.
2. NPCs periodically notice visible subjects and retain relevant subjects in attention as that relevance decays.
3. Recognised speakers and visible subjects can enter foreground context when their attention reaches the configured
   threshold.
4. Non-self speech becomes exactly one durable recognised or unknown speech memory; routine visual surveys create no
   memories.
5. Sensing must not redirect gaze, alter eye presentation, or delay normal gameplay with asynchronous processing.
6. Invalid sensing or perception configuration must fail before sensory processing activates.

## Technical Requirements

### Dependency Direction

1. Production source dependencies must follow these directions:
   - Sense may depend on Core.
   - Body may depend on Sense.
   - Character may depend on Body.
   - Mind may depend on Sense and may depend transitively on Character and Body.
   - Body and Character must not depend on Mind.
2. Scene composition may place a Mind node beneath a Character node without creating a Character-to-Mind source
   dependency.
3. The refactor must remove `CharacterPerception`, `MindStimulus`, and their bespoke production wiring.

### Percept And Sense Contracts

4. `IPercept` is an immutable sensory-data marker. It is not an `Observation`, exposes no behaviour, and does not own
   semantic interpretation.
5. `ISense : IComponent` exposes a synchronous `Perceived(IPercept)` event and deterministic metadata declaring the
   exact percept runtime types it can publish.
6. Declared percept metadata must contain no duplicate exact type and must not use assignability or fallback matching.
   A sense must publish only declared exact runtime types.
7. There is no active or passive sense distinction. Each sense owns its acquisition, polling, activation, and teardown
   lifecycle.

### Eyes And Visual Survey

8. `IEyes` must not expose `Scan()` publicly. Eyes owns periodic visual acquisition and publishes
   `VisualSurveyPercept` through `ISense`.
9. The scan interval must be finite and meet the exported minimum cadence. Invalid authored or runtime values must fail
   before activation.
10. Eyes performs at most one survey per frame. A delayed frame performs one survey without catch-up and starts the
    next interval from that survey.
11. `VisualSurveyPercept` contains only a producer-owned, immutable, ordered snapshot of canonical visible-subject
    `FullId` values. Snapshot membership and order cannot change after publication.
12. Survey acquisition preserves BODY-004 visibility, cue ownership, field-of-view, range, occlusion, and discovery
    behaviour. It does not call `VisualCue.Describe`, create observations, select gaze, change `LookTarget`, or alter
    saccades, blinking, or other eye presentation.

### Hearing And Speech

13. Hearing is a Body component, an `ISense`, and an `IVoiceListener`. It owns voice-listener subscription and teardown
    and declares exactly `SpeechPercept`.
14. Hearing rejects only null, empty, or whitespace-only transport speech publications. It must not filter publications
    by observer voice or source identity.
15. For each accepted publication, Hearing snapshots the speech and raw local source voice `Id` into one immutable
    `SpeechPercept` and publishes it synchronously.
16. Hearing must not know the observer's voice, attribute a speaker, create an `Observation`, or depend on Mind.

### Perception Faculties

17. Mind obtains its configured senses from the owning Character's `Components` projection and owns an authorable array
    of Godot `Resource` faculties implementing non-generic `IPerception` and typed `IPerception<TPercept>` contracts.
18. Mind builds an exact runtime-type registry from its configured faculties and configured senses before activation.
    Every exact percept type declared by a configured sense must map to exactly one faculty.
19. Missing mappings, duplicate mappings, duplicate declared types, incompatible generic mappings, and senses that
    publish undeclared types must fail clearly before activation or publication, as applicable.
20. On `ISense.Perceived`, Mind synchronously selects the one exact faculty and receives one `PerceptionResult`. It must
    not use inheritance dispatch, fallback handlers, queues, tasks, Rx, or background processing.
21. Each concrete faculty type owns one fixed, non-authorable initial semantic attention contribution in the inclusive
    range `0..1`. Generic `AttentionSettings` contains only maximum, decay, retention threshold, and context threshold
    values.

### Faculty Behaviour

22. `SpeechPerception` compares the percept's source voice `Id` with the observer's current voice `Id` using ordinal
    value equality. Equal values, including the installed character-owned voice ID, identify self speech and produce no
    attention effect or observation.
23. For non-self speech, `SpeechPerception` resolves current-scene characters whose composed voice `Id` ordinally equals
    the source voice `Id`. Blank configured candidate IDs do not match.
24. Other values follow ordinary attribution handling: zero matches produce exactly one unknown `ObservedSpeech`; one
    match reinforces that character's canonical `FullId` and produces exactly one recognised `ObservedSpeech` with that
    `ActorId`; and multiple matches fail without attention, timeline, pending-scheduling, or other effects.
25. Recognised and unknown observations retain the speech and raw local source voice `Id`. That ID remains operational
    attribution, not authenticated provenance.
26. `VisualSurveyPerception` returns one attention reinforcement for every subject `FullId` in percept order and no
    observations.

### Results, Attention, And Atomicity

27. `PerceptionResult` contains an ordered sequence of attention effects and an ordered sequence of zero or more
    `Observation` records. Both sequences are immutable after return.
28. Before any mutation, Mind validates the complete result, every attention effect, every observation, and every
    calculated observation importance. Any failure leaves attention, timeline, pending scheduling, and scheduling state
    unchanged.
29. After successful validation, Mind applies attention effects sequentially in declared order. Duplicate subject
    effects are valid and compound in that order.
30. Mind then atomically ingests all observations in result order through AI-001's existing timeline, pending FIFO, and
    scheduling path. Existing scheduling and interruption behaviour remains unchanged.
31. Attention is keyed by canonical `FullId` using ordinal comparison. Reinforcement applies
    `current + (maximum - current) * contribution` without exceeding maximum.
32. Attention decays lazily and linearly with elapsed game time on percept handling, queries, and snapshots. Entries
    below retention are evicted; every entry at or above the context threshold is eligible for context.
33. Maximum must be finite and positive; decay must be finite and non-negative; thresholds must be finite and satisfy
    `0 <= retention <= context <= maximum`. Settings validation must complete before activation or mutation.
34. Attention snapshots are immutable identity/value sequences ordered by `FullId` using ordinal comparison. Attention
    stores no live subject, percept, or observation reference.

### Ownership And Composition

35. Mind owns incoming percept subscription, exact faculty dispatch, result validation, attention mutation, observation
    ingestion, and existing scheduling.
36. AgenticMind owns only provider, prompt, render-context, and tool concerns. It must not interpret incoming percepts.
    The existing speech output tool and exactly-once self-action observation path remain unchanged.
37. `Character.Components` deliberately includes configured `ISense` components in deterministic holder order, in
    addition to its required embodied components. No `CharacterPerception` component or bespoke wiring remains.
38. AgenticMind foreground context contains self and each attention-eligible `FullId` that currently resolves through
    `ISceneContext.Find(FullId)` to an `IContextual` subject. It performs no additional visual survey.

## In Scope

- Immutable percept and synchronous sense contracts.
- Eyes-owned visual survey cadence and Hearing-owned speech acquisition.
- Mind-owned Resource faculties, exact type registry, attention, and atomic result handling.
- Speech interpretation and observation-free visual reinforcement.
- Sense projection through `Character.Components` and approved dependency direction.

## Out Of Scope

- Focused visual inspection or additional visual percept types.
- Visual descriptions or observations.
- Gaze selection or perception-driven eye presentation.
- Spatial hearing, acoustics, distance attenuation, or directionality.
- Non-sensory stimuli.
- Asynchronous dispatch, queues, Reactive Extensions, or background processing.
- Separate assemblies for the dependency layers.
- Replacing the current Mind owner with a narrower owner abstraction.
- Top-N attention selection or context budgets.
- Removal of the current render-context fallback behaviour.
- Final tuning values for cadence, contributions, decay, and thresholds.

## Acceptance Criteria

### User Requirements

1. NPCs record exactly one recognised or unknown speech memory for each accepted non-self speech publication and none
   for self speech.
2. Periodic visual surveys reinforce every visible subject in survey order without producing visual memories.
3. Foreground context contains self and every currently resolvable attention-eligible contextual subject.
4. Visual sensing preserves all existing visibility and eye presentation behaviour, including gaze, saccades, and
   blinking.
5. Invalid sense, faculty, cadence, or attention authoring fails before sensing activates.

### Technical Requirements

1. Dependency checks verify Sense may depend only on Core within this slice, Body depends on Sense, Character depends on
   Body, Mind may consume Sense and Character/Body, and Body and Character production code do not depend on Mind.
2. Contract tests verify immutable behaviour-free `IPercept`, synchronous `ISense.Perceived`, deterministic exact-type
   metadata, sense-owned lifecycle, and no active/passive distinction.
3. Eyes tests verify no public `IEyes.Scan()`, finite validated cadence, at most one survey per frame, no catch-up, and
   one producer-owned immutable ordered `FullId` snapshot per survey.
4. Visual integration tests verify unchanged subject discovery, cue validation, field of view, distance, and occlusion,
   with no descriptions, observations, gaze selection, `LookTarget` change, saccade change, or blink change.
5. Hearing tests verify Body composition and voice-listener lifecycle, rejection of blank transport speech only, and
   synchronous immutable speech and raw source-ID snapshots without observer-voice or Mind knowledge.
6. Registry tests verify one exact typed faculty per exact type declared by configured senses and pre-activation failure
   for every missing, duplicate, incompatible, or undeclared mapping.
7. Speech tests verify ordinal source/observer ID self filtering, including the installed character-owned voice ID;
   ordinal zero, one, and ambiguous scene matching; ambiguity without effects; recognised `FullId` reinforcement; and
   exactly one recognised or unknown observation.
8. Visual faculty tests verify effects follow percept order, duplicate IDs remain ordered effects, and no observations
   are returned.
9. Settings tests verify generic settings contain only maximum, decay, retention, and context thresholds, while each
   concrete faculty supplies its fixed valid semantic contribution.
10. Atomicity tests verify complete result and calculated-importance validation precedes mutation; failure changes no
    attention, timeline, pending, or scheduling state; valid duplicate effects compound sequentially in order; and all
    observations ingest atomically in result order.
11. Attention tests verify ordinal canonical identity, the reinforcement formula, lazy decay, retention, context
    eligibility, immutable ordered snapshots, and absence of live object references.
12. Composition tests verify Mind owns interpretation, AgenticMind retains only provider/prompt/render/tool concerns,
    the speech tool's exactly-once self-action observation path remains, configured senses appear in deterministic
    `Character.Components`, and `CharacterPerception` and `MindStimulus` do not exist.
13. Foreground-context tests verify self inclusion, eligible `FullId` resolution, omission of unresolved or
    non-contextual subjects, no top-N selection, and no second visual survey.

## References

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [BODY-004: Eyes](../../body/004-eyes/index.md)
- [BODY-006: Voice Component](../../body/006-voice/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [CTX-001: Contextual Information API](../../context/001-contextual-information-api/index.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [CORE-009: Identifiable Identity](../../core/009-identifiable-identity/index.md)
- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
