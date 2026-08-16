---
id: AI-007
title: Attention-Driven Gaze Target Selection
---

# Attention-Driven Gaze Target Selection

## Requirement

NPCs must turn Mind-owned attention into stable, believable gaze anchors without giving sensing, surveys, or the eye
presentation system responsibility for semantic gaze decisions.

## Goal

Make an NPC's gaze reflect the subjects currently important to it while preserving deterministic attention semantics,
existing EyeBehaviour presentation, and a clear boundary for future trigger strategies.

## User Requirements

1. An NPC looks at an attention-relevant character cue, holds that focus briefly, and may make a brief secondary glance
   before returning or choosing a new focus.
2. Gaze remains stable for its configured dwell even when attention rankings change, rather than twitching between
   targets.
3. When no valid attention-relevant cue exists, the NPC returns to the ordinary vision fallback rather than retaining a
   stale target.
4. Visual surveys, blinking, and saccades remain presentation-neutral: surveys do not choose gaze, while assigned
   targets continue to anchor saccades and blinking continues normally.
5. Player characters remain unaffected; only NPC role templates that compose the selector gain attention-driven gaze.

## Technical Requirements

### Ownership And Namespace

1. The selector is a direct child of its owner `Mind` and lives under the dedicated `AlleyCat.Mind.Attention` namespace.
   It is Mind-side policy, not `EyesBehaviour` or another Vision concern.
2. The selector is not an `IComponent` and is not a member of `Character.Components`. NPC role templates compose it
   below Mind; player and shared player-base templates do not compose it.
3. Existing attention contracts, settings, snapshots, policies, and effects must move to
   `AlleyCat.Mind.Attention`. `Mind.GetAttentionSnapshot()` remains the public read API for an immutable attention
   snapshot.
4. The selector consumes only its owner Mind's attention snapshot and invokes the owner character's
   `IVision.SetLookTarget` or `IVision.ClearLookTarget`. It must not add a Mind dependency to Vision or EyesBehaviour.

### Candidate Resolution And Ranking

5. Attention is authoritative and the selector is semantics-blind. It must not use speech, conversation, pointing, type,
   distance, angle, occlusion, visibility, or a new visual scan to choose a target.
6. At an evaluation boundary, the selector resolves every snapshot identity with `ISceneContext.Find(FullId)`. It skips
   an unresolved subject and every cue that is disabled or has non-finite or non-positive prominence.
7. Current resolution supports characters only, using their published visual cues as candidate target nodes, because
   SCN-001 initially maps only `char` identities. This is a resolution boundary, not target-selection type logic.
   Generic non-character subject resolution is deferred.
8. Each valid cue score is its subject attention multiplied by its prominence. The primary candidate is the highest
   score. Equal scores resolve deterministically by ordinal subject `FullId`, then by provider cue order.

### Transition Policy

9. The selector maintains primary and secondary dwell states. Primary and secondary dwell durations are configurable;
   secondary dwell is configured as the shorter glance duration.
10. A primary target remains assigned throughout its primary dwell despite candidate ranking changes. It may be
    abandoned only at an evaluation boundary when it is invalid or no longer resolvable.
11. At a primary evaluation boundary, the selector uses a configurable probability to decide whether to take a secondary
    glance. A secondary candidate excludes the current primary and is selected score-weightedly from valid candidates.
12. A secondary target remains assigned for its secondary dwell despite ranking changes. It may be abandoned only at an
    evaluation boundary when invalid or unresolved. At its boundary, the selector returns to a still-valid primary
    target or reselects from the current snapshot; it clears the look target when no valid candidate remains.
13. When no valid candidate exists at an evaluation boundary, the selector calls `ClearLookTarget`, leaving IVision's
    existing fallback gaze behaviour in effect. Current scope has no urgent or mid-dwell interruption.

### Lifecycle, Triggering, And Testability

14. Initial evaluation uses a configurable periodic cadence and performs no catch-up after a delayed frame. This is an
    initial, replaceable trigger strategy: a future perception or attention subscription may request evaluation without
    changing the ranking policy, dwell semantics, or permitting mid-dwell interruption.
15. Target application is delta-driven and deterministic: unchanged assignments do not produce redundant Vision calls,
    and a change or clear is applied exactly when the state transition requires it.
16. Exported cadence and dwell settings must be finite and positive; secondary dwell must be shorter than primary dwell.
    Secondary probability must be finite and within `0..1`. All settings validate before activation. Probability,
    weighted selection, and any other randomness must use an injectable deterministic test seam.
17. Mind lifecycle must wait for the owner Character's component projection to commit before resolving `IVision` for the
    selector. Projection changes must not leave stale Vision bindings.
18. Selector teardown must not call `ClearLookTarget`. Without a look-target ownership token, it cannot prove that the
    currently assigned target remains its assignment.

## In Scope

- A direct Mind-child, Mind-side attention-to-gaze selector under `AlleyCat.Mind.Attention`.
- Migration of attention contracts, settings, snapshots, policies, and effects to that namespace while preserving
  `Mind.GetAttentionSnapshot()` as the public read API.
- Character-only SCN-001 identity resolution, published-cue validation, deterministic ranking, dwell policy, and
  fallback clearing.
- Initial periodic evaluation, a replaceable evaluation-request seam, delta-driven Vision application, and deterministic
  randomness for tests.
- NPC-only role-template composition, Mind/component-projection lifecycle integration, and no-clear teardown behaviour.
- Preservation of Vision's assigned-target, saccade, blink, and survey responsibilities.

## Out Of Scope

- Speech, conversation, pointing, relationship, type-based, distance, angle, occlusion, or visibility-based gaze policy.
- Changes to `EyesBehaviour`, visual-survey acquisition, saccade behaviour, blinking, or Vision target fallback.
- Generic non-character scene-subject resolution beyond SCN-001's current character mapping.
- Urgent target priority, mid-dwell interruption, or a final tuning value for cadence, dwell, probability, or weighting.
- Replacing periodic evaluation now; future perception or attention subscriptions are an allowed trigger extension only.
- Look-target ownership tokens or teardown clearing based on inferred ownership.

## Acceptance Criteria

### User Requirements

1. NPC play and integration coverage show an attention-relevant character cue held for a primary dwell, an optional
   brief secondary glance, and a return or reselection without ranking-change jitter.
2. Coverage shows that no valid candidate clears the assigned target and restores ordinary IVision fallback behaviour.
3. Coverage confirms that surveys do not select gaze, assigned targets still anchor saccades, blinking continues, and
   the player template remains free of the selector.

### Technical Requirements

4. Contract and composition tests verify a direct Mind child under `AlleyCat.Mind.Attention`, no `IComponent` or
   `Character.Components` membership, NPC-only template composition, and no Vision or EyesBehaviour ownership of policy.
5. Migration tests verify attention contracts, settings, snapshots, policies, and effects live in
   `AlleyCat.Mind.Attention`, while `Mind.GetAttentionSnapshot()` remains the public immutable read API.
6. Candidate tests verify `ISceneContext.Find(FullId)` resolution, unresolved-subject skipping, character-only support,
   published-cue order, disabled/non-finite/non-positive prominence rejection, and no semantic or visibility filtering.
7. Ranking tests verify attention-times-prominence scoring, ordinal `FullId` primary tie resolution, and provider
   cue-order tie resolution.
8. State tests verify configurable primary and shorter secondary dwell, probability-controlled score-weighted
   secondary selection excluding the primary, return/reselection, no-candidate clearing, dwell stability, and
   boundary-only invalid abandonment with no urgent or mid-dwell interruption.
9. Trigger tests verify configurable periodic evaluation without catch-up and establish that a future evaluation request
   uses the same policy and dwell semantics rather than a special interruption path.
10. Lifecycle and output tests verify component-projection-before-Vision resolution, projection-safe rebinding,
    delta-driven `SetLookTarget` and `ClearLookTarget` calls, deterministic injected randomness, finite positive cadence
    and dwell validation, shorter secondary dwell, valid probability, and no teardown clear.

## References

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-006: Percept-Based Sensing And Attention](../006-character-perception-and-attention/index.md)
- [VISION-001: Eyes](../../vision/001-eyes/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
