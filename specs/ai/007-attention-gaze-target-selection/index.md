---
id: AI-007
title: Attention-Driven Gaze Target Selection
---

# Attention-Driven Gaze Target Selection

## Requirement

An NPC's model chooses where to look from the character cues available through its own attention, or at nothing
in particular. Vision presents the resulting assignment; it does not decide what deserves attention.

## Goal

Let an NPC look at the person it is addressing, notice a detail, or look away as the scene develops. Attention
and cue prominence help bound the alternatives; neither determines which alternative wins.

## User Requirements

1. An NPC can hold, change or release its gaze in response to the scene as it experiences it.
2. Looking at nothing in particular restores ordinary fallback gaze. A failed decision does not move the eyes.
3. Assigned targets still anchor saccades; blinking and visual surveys work independently. Only NPC role templates
   that compose the selector gain this behaviour; player gaze is unaffected.

## Technical Requirements

### Ownership and Namespace

1. The selector is a direct child of its owner `Mind` under `AlleyCat.Mind.Attention`, not an `IComponent` or a
   member of `Character.Components`. Attention contracts, settings, policies and effects stay in that namespace;
   `Mind.GetAttentionSnapshot()` remains the public immutable read API.
2. Bind `IVision` after character component projection commits. Register the selector with the
   [subject's scene decisions][scene-decisions] while bound; invalidate outstanding work on rebind or teardown.
   Do not clear on teardown: the selector has no exclusive ownership token for Vision's current assignment.

### Candidate Resolution and Model Choice

3. Resolve attended identities through `ISceneContext.Find(FullId)` and use the subject's published visual cues.
   Skip unresolved subjects, invalid cues, non-finite or negative attention, non-finite or non-positive prominence,
   and cue keys containing the `#` separator. No separate visibility scan selects gaze.
4. Bound the offer per subject and overall. Prefer higher-attention subjects (ordinal ID breaks ties); within a
   subject prefer more prominent cues (provider order breaks ties for inclusion). Supply a single `gaze_target`
   choice: `FullId#cue` alternatives with descriptions or cue labels and weak attention/prominence hints, plus `none`.
   Alternatives and trigger-specific guidance belong in the question, not in shared evidence. Elapsed time on the
   current cue is context for the model, not a minimum hold or a timer that forces a switch.
5. Re-resolve the chosen subject and cue by identity and check live cue validity before calling `IVision.SetLookTarget`.
   An unavailable choice leaves the current assignment alone; an unchanged cue does not restart its elapsed time or
   cause another assignment. `none` clears a previous assignment through `IVision.ClearLookTarget`. With no candidates
   and no assignment, contribute no question; with an assignment but no candidates, offer only `none`.
6. Vision owns publication of an effective `LookTargetChangedPercept`. Sensing may interpret the transition through
   AI-006; the selector does not publish or interpret that percept, and sensing does not assign the target.

### Lifecycle, Triggering, and Testability

7. Evaluate initially and on a configurable finite positive cadence. Speech intention and own speech completion,
   attended-speaker onset/resumption and completion, a newly attended subject, or loss of the applied subject from
   attention can bring evaluation forward. Coalesce nudges; do not start an overlapping request.
8. On refusal, invalid answer or request failure, retain the target and back off periodic evaluation within a bound.
   A nudge need not wait for that backoff. Cancelled work cannot change the assignment.

## In Scope

- NPC-only selector composition, valid character cue choices, delta-driven Vision assignment and release.
- Registration, trigger coalescing and local failure handling under the shared scene-decision contract.

## Out Of Scope

- Changing sensing, visual interpretation, survey acquisition, head motion, saccades, blinking or Vision fallback.
- Extending scene identity resolution beyond the current character mapping or adding look-target ownership tokens.

## Acceptance Criteria

1. With scripted choices, an NPC can look at a valid lower-prominence cue instead of the first offer, hold it on a
   repeat choice, switch to another cue, and release to fallback on `none`. Player gaze, blinking, surveys and
   saccades retain their respective owners; an effective change can still produce a focused description.
2. Verify deterministic bounded inclusion, unambiguous IDs, invalid-cue exclusion, both empty-candidate cases and
   revalidation after a subject disappears or a cue becomes invalid. No stale or redundant Vision write occurs.
3. Verify NPC-only Mind-child composition, projection-safe binding and no teardown clear. Exercise cadence, coalesced
   nudges, refusal, failure backoff and cancellation with and without a due wait gate.

## References

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-002: Per-Subject Scene Decisions][scene-decisions] — normative for dispatch, shared evidence and cancellation.
- [AI-006: Percept-Based Sensing And Attention](../006-character-perception-and-attention/index.md)
- [AI-009: Attention-Driven Head Orientation](../009-attention-head-orientation/index.md)
- [VISION-001: Eyes](../../vision/001-eyes/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)

[scene-decisions]: ../002-agent-runtime/index.md#per-subject-scene-decisions
