---
id: AI-006
title: Percept-Based Sensing And Attention
---

# Percept-Based Sensing And Attention

## Requirement

NPC senses must publish immutable percepts for Mind-owned interpretation, attention updates, and observation ingestion
without coupling Vision, Speech, Interaction, or Character production code to Mind.

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
7. After character installation or recomposition, NPCs continue to perceive eligible speech reliably, without duplicate
   observations.

## Technical Requirements

### Dependency Direction

1. Production source dependencies must follow these directions:
     - Sense owns neutral percept contracts and may depend on Core.
     - Vision, `Speech`, `Speech.Voice`, Interaction, and other modality or delivery domains may depend on Sense and
       Core as required by their contracts.
     - Mind's sensing and attention-production path may depend on Sense contracts, but not modality or delivery domains.
       AI-007's separately composed post-attention selector may depend only on the `IVision` capability contract to
       assign a target; Vision and other modality or delivery domains must not depend on Mind.
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

### Vision And Visual Survey

8. `IVision` must not expose `Scan()` publicly. EyesBehaviour owns periodic visual acquisition and publishes
   `VisualSurveyPercept` through `ISense`.
9. The scan interval must be finite and meet the exported minimum cadence. Invalid authored or runtime values must fail
   before activation.
10. EyesBehaviour performs at most one survey per frame. A delayed frame performs one survey without catch-up and
    starts the next interval from that survey.
11. `VisualSurveyPercept` contains only a producer-owned, immutable, ordered snapshot of canonical visible-subject
    `FullId` values. Snapshot membership and order cannot change after publication.
12. Survey acquisition preserves VISION-001 visibility, cue ownership, field-of-view, range, occlusion, and discovery
    behaviour. It does not call `VisualCue.Describe`, create observations, select gaze, change `LookTarget`, or alter
    saccades, blinking, or other eye presentation.

### Hearing And Speech

13. `SpeechPercept`, `Hearing`, `IHearing`, and `IHasHearing` live directly in `AlleyCat.Speech`.
    `Hearing : Node, IHearing` owns voice-listener subscription and teardown; `IHearing : ISense` declares exactly
    `SpeechPercept` and receives voice publications through `ReceiveVoice(string, IVoice)`.
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
21. When a live owning Character commits a replacement `Components` projection, Mind must synchronously revalidate and
    replace its sense bindings as one refresh operation. For a successful rebind, it must remove every previous sense
    handler before subscribing to current senses. Repeated equivalent refreshes must not duplicate delivery; node exit
    must unsubscribe both projection and sense handlers. Exact mapping validation remains mandatory on every refresh.
22. Each concrete faculty type owns one fixed, non-authorable initial semantic attention contribution in the inclusive
    range `0..1`. Generic `AttentionSettings` contains only maximum, decay, retention threshold, and context threshold
    values.

### Faculty Behaviour

23. `SpeechPerception` compares the percept's source voice `Id` with the observer's current voice `Id` using ordinal
    value equality. Equal values, including the installed character-owned voice ID, identify self speech and produce no
    attention effect or observation.
24. For non-self speech, `SpeechPerception` resolves current-scene characters whose composed voice `Id` ordinally equals
    the source voice `Id`. Blank configured candidate IDs do not match.
25. Other values follow ordinary attribution handling: zero matches produce exactly one unknown `ObservedSpeech`; one
    match reinforces that character's canonical `FullId` and produces exactly one recognised `ObservedSpeech` with that
    `ActorId`; and multiple matches fail without attention, timeline, pending-scheduling, or other effects.
26. Recognised and unknown observations retain the speech and raw local source voice `Id`. That ID remains operational
    attribution, not authenticated provenance.
27. `VisualSurveyPerception` returns one attention reinforcement for every subject `FullId` in percept order and no
    observations.

### Results, Attention, And Atomicity

28. `PerceptionResult` contains an ordered sequence of attention effects and an ordered sequence of zero or more
    `Observation` records. Both sequences are immutable after return.
29. Before any mutation, Mind validates the complete result, every attention effect, every observation, and every
    calculated observation importance. Any failure leaves attention, timeline, pending scheduling, and scheduling state
    unchanged.
30. After successful validation, Mind applies attention effects sequentially in declared order. Duplicate subject
    effects are valid and compound in that order.
31. Mind then atomically ingests all observations in result order through AI-001's existing timeline, pending FIFO, and
    scheduling path. Existing scheduling and interruption behaviour remains unchanged.
32. Attention is keyed by canonical `FullId` using ordinal comparison. Reinforcement applies
    `current + (maximum - current) * contribution` without exceeding maximum.
33. Attention decays lazily and linearly with elapsed game time on percept handling, queries, and snapshots. Entries
    below retention are evicted; every entry at or above the context threshold is eligible for context.
34. Maximum must be finite and positive; decay must be finite and non-negative; thresholds must be finite and satisfy
    `0 <= retention <= context <= maximum`. Settings validation must complete before activation or mutation.
35. Attention snapshots are immutable identity/value sequences ordered by `FullId` using ordinal comparison. Attention
    stores no live subject, percept, or observation reference.
36. Attention contracts, settings, snapshots, policies, and effects live in `AlleyCat.Mind.Attention`.
    `Mind.GetAttentionSnapshot()` remains the public read API. AI-007 is the separately composed post-attention consumer
    of that snapshot; this specification remains normative for attention production and mutation.

### Ownership And Composition

37. Mind owns incoming percept subscription, exact faculty dispatch, result validation, attention mutation, observation
    ingestion, and existing scheduling. It must not select or assign an IVision look target from a sense, survey,
    faculty, or attention effect.
38. AgenticMind owns only provider, prompt, render-context, and tool concerns. It must not interpret incoming percepts.
    The existing speech output tool and exactly-once self-action observation path remain unchanged.
39. `Character.Components` deliberately includes configured `ISense` components in deterministic holder order, in
    addition to its required embodied components. No `CharacterPerception` component or bespoke wiring remains.
40. AgenticMind foreground context contains self and each attention-eligible `FullId` that currently resolves through
    `ISceneContext.Find(FullId)` to an `IContextual` subject. It performs no additional visual survey.

## In Scope

- Immutable percept and synchronous sense contracts.
- EyesBehaviour-owned visual survey cadence and Hearing-owned speech acquisition.
- Mind-owned Resource faculties, exact type registry, attention, and atomic result handling.
- Attention contract namespace and immutable snapshot publication for AI-007's separately composed post-attention
  consumer; not gaze policy or target assignment.
- Speech interpretation and observation-free visual reinforcement.
- Sense projection through `Character.Components` and approved dependency direction.
- Post-commit projection refresh and Mind sense rebinding.

## Out Of Scope

- Focused visual inspection or additional visual percept types.
- Visual descriptions or observations.
- Gaze selection or direct look-target assignment by sensing, surveys, faculties, or attention mutation. AI-007 alone is
  the separately composed post-attention consumer that may assign a look target.
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
6. After character installation or recomposition, eligible speech still creates exactly one recognised or unknown
   memory per accepted non-self publication.

### Technical Requirements

1. Dependency checks verify that Sense remains the neutral percept-contract domain; modality and delivery domains
   depend on Sense as required; Mind sensing and attention production remain modality-neutral; AI-007 alone may consume
   `IVision`; and no modality or delivery domain depends on Mind.
2. Contract tests verify immutable behaviour-free `IPercept`, synchronous `ISense.Perceived`, deterministic exact-type
   metadata, sense-owned lifecycle, and no active/passive distinction.
3. Vision tests verify `IVision` has no public `Scan()`, `VisualSurveyPercept` lives in `AlleyCat.Vision`, cadence is
    finite and validated, at most one survey occurs per frame, there is no catch-up, and each survey has one
    producer-owned immutable ordered `FullId` snapshot.
4. Visual integration tests verify unchanged subject discovery, cue validation, field of view, distance, and occlusion,
   with no descriptions, observations, gaze selection, `LookTarget` change, saccade change, or blink change.
5. Hearing tests verify top-level `AlleyCat.Speech` ownership, `IHearing.ReceiveVoice(string, IVoice)` listener
    lifecycle, rejection of blank transport speech only, and synchronous immutable speech and raw source-ID snapshots
    without observer-voice or Mind knowledge.
6. Registry tests verify one exact typed faculty per exact type declared by configured senses and pre-activation failure
    for every missing, duplicate, incompatible, or undeclared mapping.
7. Live-composition tests verify a committed Character component refresh revalidates mappings and rebinds Mind exactly
   once against current senses without duplicate delivery, while invalid mappings fail and tree exit removes every
   projection and sense handler.
8. Speech tests verify ordinal source/observer ID self filtering, including the installed character-owned voice ID;
   ordinal zero, one, and ambiguous scene matching; ambiguity without effects; recognised `FullId` reinforcement; and
   exactly one recognised or unknown observation.
9. Visual faculty tests verify effects follow percept order, duplicate IDs remain ordered effects, and no observations
   are returned.
10. Settings tests verify generic settings contain only maximum, decay, retention, and context thresholds, while each
   concrete faculty supplies its fixed valid semantic contribution.
11. Atomicity tests verify complete result and calculated-importance validation precedes mutation; failure changes no
   attention, timeline, pending, or scheduling state; valid duplicate effects compound sequentially in order; and all
   observations ingest atomically in result order.
12. Attention tests verify ordinal canonical identity, the reinforcement formula, lazy decay, retention, context
   eligibility, immutable ordered snapshots, and absence of live object references.
13. Composition tests verify Mind owns interpretation, AgenticMind retains only provider/prompt/render/tool concerns,
   the speech tool's exactly-once self-action observation path remains, configured senses appear in deterministic
   `Character.Components`, and `CharacterPerception` and `MindStimulus` do not exist.
14. Foreground-context tests verify self inclusion, eligible `FullId` resolution, omission of unresolved or
   non-contextual subjects, no top-N selection, and no second visual survey.
15. Boundary tests verify sensing, surveys, faculties, and attention mutation never call `IVision.SetLookTarget` or
    `IVision.ClearLookTarget`; AI-007 alone consumes the published attention snapshot as the separately composed
    post-attention gaze consumer.

## References

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [AI-007: Attention-Driven Gaze Target Selection](../007-attention-gaze-target-selection/index.md)
- [VISION-001: Eyes](../../vision/001-eyes/index.md)
- [SPCH-006: Hearing Component](../../speech/006-hearing/index.md)
- [SPCH-005: Voice Component](../../speech/005-voice/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [CTX-001: Contextual Information API](../../context/001-contextual-information-api/index.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [CORE-009: Identifiable Identity](../../core/009-identifiable-identity/index.md)
- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
