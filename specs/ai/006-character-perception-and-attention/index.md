---
id: AI-006
title: Percept-Based Sensing And Attention
---

# Percept-Based Sensing And Attention

## Requirement

NPC senses must publish immutable percepts for Mind-owned interpretation, attention updates, and observation ingestion
without coupling Vision, Speech, Interaction, or Character production code to Mind.

## Goal

Separate sensory acquisition from asynchronous semantic interpretation while preserving immediate publication,
deterministic attention, ordered memory, existing speech history, and eye visibility and presentation behaviour.

## User Requirements

1. NPCs notice non-self speech, including speech from an unknown speaker.
2. NPCs periodically notice visible subjects and retain relevant subjects in attention as that relevance decays.
3. Recognised speakers and visible subjects can enter session prompt context when their attention reaches the
   configured threshold.
4. Non-self speech becomes exactly one durable recognised or unknown speech memory; routine visual surveys create no
   memories, while focusing a valid visual cue can produce a durable subject description.
5. Sensing and semantic interpretation must not choose gaze or alter eye presentation. Asynchronous interpretation must
   preserve percept publication order without blocking sensory publication or normal gameplay.
6. Invalid sensing or perception configuration must fail before sensory processing activates.
7. After character installation or recomposition, NPCs continue to perceive eligible speech reliably, without duplicate
   observations.
8. Repeated equivalent focused descriptions of the same subject are suppressed, while changed descriptions and
   descriptions of different subjects remain memorable.

## Technical Requirements

### Dependency Direction

1. Production source dependencies must follow these directions:
     - Sense owns neutral percept contracts and may depend on Core.
     - Vision, `Speech`, `Speech.Voice`, Interaction, and other modality or delivery domains may depend on Sense and
       Core as required by their contracts.
     - Mind's sensing and attention-production path may depend on Sense contracts, but not modality or delivery domains.
       AI-007's separately composed post-attention selector may depend only on the `IVision` capability contract to
       assign a target; Vision and other modality or delivery domains must not depend on Mind.
        Mind's attended-speaker resolution — which voices block `speak` and wake `wait` — resolves voice activity
        through current-scene characters' composed `IVoice` via `ICharacter.TryGetVoice()`, mirroring the established
        `SpeechPerception` attribution precedent (TR-23–TR-26).
       This exception is acknowledged explicitly rather than presenting strict separation; it adds no dependency beyond
       the existing precedent.
2. Scene composition may place a Mind node beneath a Character node without creating a Character-to-Mind source
    dependency.
3. The refactor must remove `CharacterPerception`, `MindStimulus`, and their bespoke production wiring.

### Percept And Sense Contracts

4. `IPercept` is an immutable sensory-data marker. It is not an `Observation`, exposes no behaviour, and does not own
   semantic interpretation.
5. `ISense : IComponent` exposes a synchronous `Perceived(IPercept)` event and deterministic metadata declaring the
    exact percept runtime types it can publish.
6. `ISense<out TPercept> : ISense` is a covariant family marker that retains the non-generic event bridge. Declared
   `PerceptTypes` metadata must contain no duplicate exact type, and a sense must publish only declared exact concrete
   runtime types. Faculty matching uses assignability separately from this publisher validation.
7. There is no active or passive sense distinction. Each sense owns its acquisition, polling, activation, and teardown
   lifecycle.

### Vision And Visual Survey

8. `IVisualPercept : IPercept` marks the visual family. `IVision : ISense<IVisualPercept>` must not expose `Scan()`
   publicly and must declare exact concrete `PerceptTypes` for `VisualSurveyPercept` and
   `LookTargetChangedPercept`; both percepts implement `IVisualPercept`.
9. The scan interval must be finite and meet the exported minimum cadence. Invalid authored or runtime values must fail
   before activation.
10. EyesBehaviour performs at most one survey per frame. A delayed frame performs one survey without catch-up and
    starts the next interval from that survey.
11. `VisualSurveyPercept` contains only a producer-owned, immutable, ordered snapshot of canonical visible-subject
    `FullId` values. Snapshot membership and order cannot change after publication.
12. Survey acquisition preserves VISION-001 visibility, cue ownership, field-of-view, range, occlusion, and discovery
    behaviour. It does not call `VisualCue.Describe`, create observations, select gaze, change `LookTarget`, or alter
    saccades, blinking, or other eye presentation.
13. An effective `IVision` look-target transition publishes one immutable
    `LookTargetChangedPercept(previous, current)`. Assigning the same cue publishes none; clearing an active cue
    publishes `current -> null`. Transition sensing reports applied state and remains separate from gaze-selection
    policy.

### Hearing And Speech

14. `SpeechPercept`, `Hearing`, `IHearing`, and `IHasHearing` live directly in `AlleyCat.Speech`.
    `Hearing : Node, IHearing` owns voice-listener subscription and teardown; `IHearing : ISense` declares exactly
    `SpeechPercept` and receives voice publications through `ReceiveVoice(string, IVoice)`.
15. Hearing rejects only null, empty, or whitespace-only transport speech publications. It must not filter publications
    by observer voice or source identity.
16. For each accepted publication, Hearing snapshots the speech and raw local source voice `Id` into one immutable
    `SpeechPercept` and publishes it synchronously.
17. Hearing must not know the observer's voice, attribute a speaker, create an `Observation`, or depend on Mind.

### Perception Faculties

18. Mind obtains configured senses from the owning Character's `Components` projection. Perceptions are independently
    owned direct child `Node`s of Mind, discovered in scene order, implementing non-generic `IPerception` and typed
    `IPerception<TPercept>` contracts; Resource arrays must not configure faculties.
19. A faculty declares one input percept family. Mind matches a published concrete type when
    `faculty.PerceptType.IsAssignableFrom(publishedConcreteType)`. Every concrete type declared by a configured sense
    must have at least one match; multiple matches intentionally fan out in direct-child order.
20. Missing matches, duplicate sense-declared exact types, incompatible generic mappings, and senses that publish an
    undeclared concrete type must fail clearly before activation or publication, as applicable.
21. `IPerception` interpretation is asynchronous and cancellation-aware. On `ISense.Perceived`, Mind synchronously
    validates the publisher's exact concrete type, snapshots the ordered matching faculties, and queues the percept
    without blocking the publisher.
22. Mind processes queued percepts serially in publication order and awaits matching faculties sequentially in their
    snapshotted order. A successful component refresh affects later publications only. Async faults are contained and
    logged; lifetime cancellation and a final lifetime guard forbid post-exit commits.
23. When a live owning Character commits a replacement `Components` projection, Mind must synchronously revalidate and
    replace its sense bindings as one refresh operation. For a successful rebind, it must remove every previous sense
    handler before subscribing to current senses. Repeated equivalent refreshes must not duplicate delivery; node exit
    must unsubscribe projection and sense handlers and cancel queued or active interpretation.
24. Each concrete faculty type owns one fixed, non-authorable initial semantic attention contribution in the inclusive
    range `0..1`. Generic `AttentionSettings` contains only maximum, decay, retention threshold, and context threshold
    values.

### Faculty Behaviour

25. `SpeechPerception` compares the percept's source voice `Id` with the observer's current voice `Id` using ordinal
    value equality. Equal values, including the installed character-owned voice ID, identify self speech and produce no
    attention effect or observation.
26. For non-self speech, `SpeechPerception` resolves current-scene characters whose composed voice `Id` ordinally equals
    the source voice `Id`. Blank configured candidate IDs do not match.
27. Other values follow ordinary attribution handling: zero matches produce exactly one unknown `ObservedSpeech`; one
    match reinforces that character's canonical `FullId` and produces exactly one recognised `ObservedSpeech` with that
     `ActorId`; and multiple matches fail without attention, timeline, notable-accumulation, or other effects.
28. Recognised and unknown observations retain the speech and raw local source voice `Id`. That ID remains operational
    attribution, not authenticated provenance.
29. `VisualSurveyPerception` returns one attention reinforcement for every subject `FullId` in percept order and no
    observations.
30. Active-look perception tracks the current `VisualCue?` from look-target transitions. Clear updates that state and
    emits no observation. An invalid or freed cue, or a cue with no associated `IVisualSubject`, emits no observation.
31. For a valid newly active cue, `VisualDescriptionPerception` awaits `cue.Describe(context.Scene,
    context.Character)` and returns one `ObservedVisualDescription` with exact key `vision.description`, the associated
    subject's canonical `FullId`, and the rendered description.

### Results, Attention, And Atomicity

32. `PerceptionResult` contains an ordered sequence of attention effects and an ordered sequence of zero or more
    `Observation` records. Both sequences are immutable after return.
33. Mind concatenates every matching faculty result in faculty order while preserving each result's internal order. It
    applies duplicate filtering, calculates importance, and validates the complete aggregate before any mutation. Any
    failure leaves attention, timeline, notable accumulation, timestamps, and notifications unchanged.
34. After successful validation, Mind applies attention effects sequentially in declared order. Duplicate subject
    effects are valid and compound in that order.
35. Mind then atomically ingests all accepted observations in result order through AI-001's existing timeline and
    notable-observation accumulation path. Existing wake and interruption signalling behaviour remains unchanged.
36. Attention is keyed by canonical `FullId` using ordinal comparison. Reinforcement applies
    `current + (maximum - current) * contribution` without exceeding maximum.
37. Attention decays lazily and linearly with elapsed game time on percept commit, queries, and snapshots. Entries
    below retention are evicted; every entry at or above the context threshold is eligible for context. Retention-level
    snapshot presence is also the membership criterion for AI-002's attended-speaker determination — `speak` blocking
    and `wait` waking — deliberately decoupled from the context threshold used for prompt-context eligibility.
38. Maximum must be finite and positive; decay must be finite and non-negative; thresholds must be finite and satisfy
    `0 <= retention <= context <= maximum`. Settings validation must complete before activation or mutation.
39. Attention snapshots are immutable identity/value sequences ordered by `FullId` using ordinal comparison. Attention
    stores no live subject, percept, or observation reference.
40. Attention contracts, settings, snapshots, policies, and effects live in `AlleyCat.Mind.Attention`.
    `Mind.GetAttentionSnapshot()` remains the public read API. AI-007 is the separately composed post-attention consumer
    of that snapshot; this specification remains normative for attention production and mutation.

### Ownership And Composition

41. Mind owns incoming percept subscription, assignability-matched faculty fan-out, result validation, attention
    mutation, observation ingestion, and notable-observation accumulation. It must not select or assign an `IVision`
    look target from a sense, survey, faculty, or attention effect.
42. AgenticMind owns only provider, prompt, render-context, and tool concerns. It must not interpret incoming percepts.
    The existing speech output tool and exactly-once self-action observation path remain unchanged.
43. `Character.Components` deliberately includes configured `ISense` components in deterministic holder order, in
    addition to its required embodied components. No `CharacterPerception` component or bespoke wiring remains.
44. AgenticMind session prompt context contains self and each attention-eligible `FullId` that currently resolves
    through `ISceneContext.Find(FullId)` to an `ICharacter` subject. It performs no additional visual survey.
45. Shared male and female NPC role templates must compose `SpeechPerception`, `VisualSurveyPerception`, and
    `VisualDescriptionPerception` as deterministic direct Mind children with independently owned state. Player
    composition remains unchanged.

## In Scope

- Immutable percept families and the synchronous non-generic sense event bridge.
- EyesBehaviour-owned visual survey cadence and Hearing-owned speech acquisition.
- Mind-owned direct-child Node faculties, assignability matching, deterministic fan-out, ordered asynchronous
  interpretation, attention, and aggregate atomic result handling.
- Attention contract namespace and immutable snapshot publication for AI-007's separately composed post-attention
  consumer; not gaze policy or target assignment.
- Speech interpretation, observation-free visual reinforcement, and transition-driven focused visual descriptions.
- Sense projection through `Character.Components` and approved dependency direction.
- Mind attended-speaker voice-activity resolution through the established scene-character `IVoice` attribution
  precedent.
- Post-commit projection refresh and Mind sense rebinding.
- NPC role-template perception composition with independently owned faculty state; unchanged player composition.

## Out Of Scope

- Gaze selection or direct look-target assignment by sensing, surveys, faculties, or attention mutation. AI-007 alone is
  the separately composed post-attention consumer that may assign a look target.
- Spatial hearing, acoustics, distance attenuation, or directionality.
- Non-sensory stimuli.
- Parallel or out-of-publication-order faculty dispatch, Reactive Extensions, or unbounded background processing.
- Separate assemblies for the dependency layers.
- Replacing the current Mind owner with a narrower owner abstraction.
- Top-N attention selection or context budgets.
- Removal of the current render-context fallback behaviour.
- Final tuning values for cadence, contributions, decay, and thresholds.
- Pose-change detection, continuous reinspection while a cue remains selected, and broad renaming of existing
  `Observation` subtypes.

## Acceptance Criteria

### User Requirements

1. NPCs record exactly one recognised or unknown speech memory for each accepted non-self speech publication and none
   for self speech.
2. Periodic visual surveys reinforce every visible subject in survey order without producing visual memories.
3. Session prompt context contains self and every currently resolvable attention-eligible character.
4. Focusing a valid visual cue can create one durable subject description; clearing, an invalid or freed cue, or a cue
   without an associated visual subject creates none.
5. Visual sensing preserves all existing visibility and eye presentation behaviour, including gaze, saccades, and
   blinking, and remains separate from target-selection policy.
6. Invalid sense, faculty, cadence, or attention authoring fails before sensing activates.
7. After character installation or recomposition, eligible speech still creates exactly one recognised or unknown
   memory per accepted non-self publication.
8. Repeating the latest equivalent description for one subject creates no duplicate history entry, while a changed
   description or another subject remains recordable.

### Technical Requirements

1. Dependency checks verify that Sense remains the neutral percept-contract domain; modality and delivery domains
   depend on Sense as required; Mind sensing and attention production remain modality-neutral; AI-007 alone may consume
   `IVision`; and no modality or delivery domain depends on Mind.
2. Contract tests verify immutable behaviour-free `IPercept`, synchronous `ISense.Perceived`, the covariant generic
   family marker with its non-generic bridge, deterministic exact concrete metadata, sense-owned lifecycle, and no
   active/passive distinction.
3. Vision tests verify `IVision : ISense<IVisualPercept>` has no public `Scan()`, declares exactly
   `VisualSurveyPercept` and `LookTargetChangedPercept`, and both concrete types implement `IVisualPercept`.
4. Routine-survey integration tests verify unchanged subject discovery, cue validation, field of view, distance, and
   occlusion, with no descriptions, observations, gaze selection, `LookTarget` change, saccade change, or blink change.
5. Hearing tests verify top-level `AlleyCat.Speech` ownership, `IHearing.ReceiveVoice(string, IVoice)` listener
   lifecycle, rejection of blank transport speech only, and synchronous immutable speech and raw source-ID snapshots
   without observer-voice or Mind knowledge.
6. Registry tests verify at least one assignability-compatible faculty for every exact type declared by configured
   senses, deterministic multi-faculty fan-out, and clear failure for missing, incompatible, duplicate-declared, or
   publisher-undeclared concrete types.
7. Live-composition tests verify a committed Character component refresh revalidates mappings and rebinds Mind exactly
   once without duplicate delivery, while already published work retains its faculty snapshot and tree exit removes
   handlers and cancels interpretation.
8. Speech tests verify ordinal source/observer ID self filtering, including the installed character-owned voice ID;
   ordinal zero, one, and ambiguous scene matching; ambiguity without effects; recognised `FullId` reinforcement; and
   exactly one recognised or unknown observation.
9. Visual faculty tests verify survey effects follow percept order and return no observations. Active-look and visual
   description tests verify clear, invalid or freed cues, and cues without associated subjects emit nothing; a valid cue
   awaits `Describe(scene, observer)` and emits `vision.description` with canonical subject `FullId` and description.
10. Settings tests verify generic settings contain only maximum, decay, retention, and context thresholds, while each
   concrete faculty supplies its fixed valid semantic contribution.
11. Atomicity tests verify all matching faculties run sequentially in direct-child order; results aggregate in faculty
    and internal order; duplicate filtering, importance calculation, and complete validation precede mutation; failure
    changes no state; and accepted observations commit once atomically in order.
12. Attention tests verify ordinal canonical identity, the reinforcement formula, lazy decay, retention, context
   eligibility, immutable ordered snapshots, and absence of live object references.
13. Composition tests verify faculties are independently owned direct Mind-child Nodes discovered in scene order; male
    and female NPC templates compose speech, visual survey, and visual description faculties deterministically; two NPC
    instances do not share faculty state; player composition is unchanged; and Resource faculty arrays do not exist.
14. Foreground-context tests verify self inclusion, eligible `FullId` resolution, omission of unresolved or
    non-character results, no top-N selection, and no second visual survey.
15. Boundary tests verify sensing, surveys, faculties, and attention mutation never call `IVision.SetLookTarget` or
   `IVision.ClearLookTarget`; AI-007 alone consumes the published attention snapshot as the separately composed
   post-attention gaze consumer.
16. Dependency checks verify Mind's attended-speaker resolution uses only current-scene characters' composed `IVoice`
    via `ICharacter.TryGetVoice()`, consistent with the established `SpeechPerception` precedent and adding no new
     dependency, and that attendance membership uses retention-threshold snapshot presence rather than the context
     threshold.
17. Transition tests verify an effective `VisualCue?` target change publishes one immutable previous/current percept,
    same-cue assignment publishes none, and clear publishes `current -> null` without transferring gaze policy to
    sensing or perception.
18. Async tests verify synchronous publisher validation and binding snapshots, non-blocking intake,
    publication-order serialisation, sequential cancellation-aware faculties, contained and logged faults, and no
    post-lifetime commit.
19. Duplicate tests verify default-allow behaviour and `ObservedVisualDescription` latest-retained comparison by same
    concrete type, ordinal subject scope, and ordinal subject-plus-description equality excluding `ObservedAt`. Earlier
    accepted staged entries participate; removed or summarised entries do not.
20. Scope tests verify focused inspection is transition-driven and adds no pose-change detection, continuous
    reinspection, or broad `Observation` subtype renaming.

## References

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [AI-007: Attention-Driven Gaze Target Selection](../007-attention-gaze-target-selection/index.md)
- [VISION-001: Eyes](../../vision/001-eyes/index.md)
- [SPCH-006: Hearing Component](../../speech/006-hearing/index.md)
- [SPCH-005: Voice Component](../../speech/005-voice/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [CORE-009: Identifiable Identity](../../core/009-identifiable-identity/index.md)
- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
