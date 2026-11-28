---
title: Animation Catalogue Mind Integration
---

# Animation Catalogue Mind Integration

## Purpose

This page is the normative contract for how NPC Minds choose, see and observe [ANIM-003](index.md) catalogue
entries. It is written for implementers and reviewers of the Mind tools, prompt, projectors and perception. It
extends [AI-002](../../ai/002-agent-runtime/index.md), [AI-003](../../ai/003-prompt-api/index.md),
[AI-001](../../ai/001-mind/index.md) and [AI-006](../../ai/006-character-perception-and-attention/index.md) without
changing their delivery or retention rules.

## Agent Tools

1. AgenticMind composes two in-character tools bound to the associated character's catalogue, one per category:
   - `change_stance(id)` for the idle category;
   - `change_gait(id)` for the locomotion category.
   The model supplies only the entry id. The character binding is trusted and comes from composition.
2. A valid id sets the character's standing agent preference for that category ([ANIM-003](index.md) TR10). The tool
   returns once the preference is accepted. It does not wait for a transition to start or finish.
3. These tools do not trigger [AI-002 TR-39][parking] automatic parking. Composition declares them non-parking, and they
   never pause the session.
4. An unknown id, or one that is incompatible with the character, is refused with an in-character result, such as
   being unable to adopt that stance. The preference is unchanged. Tool descriptions and results are written in
   character and follow AI-002 TR-15 accuracy and AI-004 TR-51 backtick presentation for ids and tool names.
5. A preference applies through the controllers. A new idle takes effect through an idle→idle bridge while standing.
   A new gait applies to current or next movement under ANIM-002 TR4 variant switching.

[parking]: ../../ai/002-agent-runtime/index.md#decision-wait-gate-and-automatic-parking

## Prompt Catalogue Fragment

1. A static catalogue section is rendered once in the AI-003 session-start `SystemInstruction`, following the Lore
   Catalogue pattern. It is not rebuilt during the session. In the shared NPC static stack it comes immediately
   after `Lore Catalogue`, so the existing `Characters`, `Dialogue Examples` and `Lore Catalogue` order is
   unchanged.
2. It lists each category under an in-character heading (for example, stances and gaits). Each compatible entry
   appears once as its backticked id followed by its tags. Descriptions, paths and pack names are not listed. Empty
   categories are omitted.
3. Locomotion is listed by variant (entry), never by ANIM-002 movement roles.

## Scene Status Line

1. An `ISceneStatusProjector` direct child of Mind (AI-003 TR3–5) projects the agent's own current stance and gait,
   giving each as the effective entry's description followed by its backticked id.
2. The projection is typed, immutable and uses a stable id. It renders freshly with every request under AI-003 TR2.

## Observation Of Changes

1. An NPC observes only other NPCs' stance and gait: once at first look, then whenever they change. The player is
   ignored, and an NPC does not observe itself.
2. Observations are driven by the [ANIM-003](index.md) TR15 transition-started event `(subject, category, entry,
   kind)`. An observation is triggered when the event's target entry differs from the observer's last observed entry
   for that subject and category. The rule is the same for every event kind:
   - idle→idle into a different idle is an idle change;
   - locomotion→idle into an idle that differs from the last observed idle is an idle change;
   - idle→locomotion in a gait that differs from the last observed gait is a locomotion variant change;
   - a locomotion variant switch is a locomotion variant change.
   Starting or stopping movement into the last observed entries produces no observation. Examples are walking off in
   the same gait, or stopping back into the same idle.
3. The last observed entry is tracked per observer, subject and category. It is the entry of the observer's most
   recent delivered observation for that pair, whether from a change (item 2) or a focus check (item 4). An event
   that is not delivered, such as one outside focus (item 6), leaves it unchanged. When an observer has no last
   observed entry for a subject and category, the first delivered event counts as a change and produces an
   observation. There is no silent baseline, because no other model-visible surface describes another NPC's current
   stance or gait (AI-006 TR5, TR9).
4. **First Look and Re-Focus:** each time another NPC newly becomes the observer's effective focus (item 6), the
   observer runs a focus check. The check covers only the category the subject is currently displaying:
   - **Idle:** when the subject is in native idle or in a bridge into an idle. The current entry is that idle.
   - **Locomotion:** when the subject is moving, or in a bridge into locomotion. The current entry is its gait.
   If the current entry differs from the last observed entry for that subject and category, or there is none yet,
   the observer receives one observation with the current entry's `description`. Otherwise nothing is produced.
   So a static NPC is described at first look. A change the observer missed while the subject was out of focus is
   reported at the next re-focus. A re-focus on an unchanged subject is silent. The other category is not described
   at focus, because it is not visible: a standing NPC shows no gait and a moving NPC shows no stance. It is reported
   by the change rule (item 2) when the subject next transitions into it.
5. Change observations are published when the bridge to the new entry starts. If a bridge is interrupted, the new
   target produces a new observation of its own only when it differs from the last observed entry. The abandoned
   target produces nothing further. Focus-check observations are published when focus is acquired.
6. Delivery uses the same subject condition as `ObservedVisualDescription`
   ([AI-006 TR5](../../ai/006-character-perception-and-attention/index.md#technical-requirements)). The subject must
   be the observer's effective focus: the visual subject of its currently assigned cue, within the
   [VISION-001](../../vision/001-eyes/index.md) field-of-view, distance and line-of-sight limits. Being in the
   attention set is not enough. A change in a subject that is not focused produces no observation.
7. The observation carries the subject's canonical identity and the entry `description` only, never its id or tags.
   Its canonical text names the subject and gives the description as a visible account. First-look, re-focus and
   change observations use the same type and text.
8. Its AI-001 lifetime policy follows the visual precedent, `ObservedVisualDescription`. Within each observer's Mind
   it is keyed by subject and category, matching the last observed entry (item 3): equal values suppress, and changed
   values supersede the earlier active entry. Unlike that precedent, the type is event-eligible. Event-ineligible
   visual records reach no model-visible surface, so the description would never be delivered. It is delivered
   through AI-002's automatic scene-update exchange. Supersession does not remove timeline entries (AI-001 TR-6).

## Acceptance Criteria

1. **ANIM-003-MIND-AC-001:** Tests verify the following tool behaviour:
   - preferences are set for both tools;
   - the tools return without awaiting a transition;
   - no automatic parking follows a call;
   - unknown and incompatible ids are refused in character with the preference unchanged;
   - the binding is trusted.
2. **ANIM-003-MIND-AC-002:** Stack tests verify that the static fragment lists ids and tags per category under
   in-character headings, with locomotion by variant. They also verify that the projector line shows the agent's own
   description and id.
3. **ANIM-003-MIND-AC-003:** Tests verify observation behaviour:
   - NPC-only observation, with the player and self excluded;
   - triggers at bridge start, for every event kind, only when the target differs from the last observed entry for
     that observer, subject and category;
   - locomotion→idle into a different idle, and idle→locomotion in a different gait, each produce an observation;
   - walking off in the same gait, and stopping back into the same idle, produce none;
   - an observer's first delivered event for a subject and category produces an observation;
   - an undelivered (unfocused) event leaves the last observed entry unchanged;
   - after an interruption, a new observation only when the new target differs;
   - first look at a static NPC (one that never transitions) produces one observation for its displayed category;
     a later event with the same entry produces none;
   - a moving subject at first look is described by gait only, and a standing subject by stance only;
   - re-focus on an unchanged subject produces nothing, and re-focus after a change missed out of focus produces one
     observation with the new entry;
   - visual gating;
   - description-only content, with no id or tags in any rendered text.
4. **ANIM-003-MIND-AC-004:** A live session shows an NPC choosing and using a stance and a gait in character, and a
   second NPC receiving only the description. Unknown-id refusal is not part of the live session; MIND-AC-001 and the
   deterministic end-to-end test of [ANIM-003-UAC-002](index.md#user-requirement-acceptance) verify it.
