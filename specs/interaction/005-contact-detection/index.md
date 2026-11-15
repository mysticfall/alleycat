---
id: INTR-005
title: Contact Detection
---

# Contact Detection

> **Related specs:** [CHAR-003: Character Physical Response](../../character/003-physical-response/index.md),
> [RIG-001: Physical Collision](../../rigging/001-physical-collision/index.md), and
> [INTR-004: Character Physical Interaction API](../../interaction/004-physical-interaction/index.md)

> **Status:** This spec delivers receiver-end contact sensing only — detection, classification, and the event API in
> `AlleyCat.Contact`. Reactions, audio, haptics, and `AgenticMind` perception subscribe in later features. Delivery
> supersedes the legacy hand-specific dynamic-interaction vocabulary and eligibility contract.

## Requirement

Characters must gain a uniform, receiver-framed sense of physical contact. The player and NPCs, treated identically as
receivers, must sense what contacted them, where on their body — down to authored sub-part regions such as
"right cheek" — how the contact behaved (impact/hit, touch, press, caress/stroke, rest/lean), how hard it was, and
from which direction, through one detection and classification contract that stays decoupled from every downstream
consumer.

## Goal

Deliver the sensing side that feeds the CHAR-003 physical-response pipeline — contact as pipeline input, not a
named stage of it — as a standalone system: an objective raw contact lifecycle stream, a tunable classification
stage that emits five contact classes on transitions only, and an immutable classified `ContactEvent` API. Later
consumers — reactions, audio, haptics, and `AgenticMind` perception — subscribe to these events without touching
detection logic.

## User Requirements

1. Player and NPC characters sense contact identically from the receiver's perspective: any character can know what
   contacted them, where on their body, how, how hard, and from which direction.
2. Contact locations resolve to authored sub-part regions — a touch on the face can be distinguished as "right cheek"
   rather than just "head" — and contacts that match no authored region still report body-part granularity.
3. Contact falls into five intuitive classes — Impact/Hit, Touch, Press, Caress/Stroke, and Rest/Lean: a strike reads
   as a hit from the moment of impact, a gentle slide reads as a caress even when it began as a plain touch, and
   leaning weight reads as rest.
4. Every classified contact reports a class-appropriate intensity, its direction, its duration, and a counterpart
   description that attributes whose hand, foot, or object caused it.
5. Non-character surfaces can sense contact: an opted-in floor receives footsteps as ground-receiver events with foot
   attribution, through the same machinery as character bodies.
6. A character's own contact with their body — self-contact — is sensed through the same path as external contact.
7. Contact events may carry an authored natural-language description; when none is authored, the description is absent
   rather than synthesised.
8. Detection alone changes nothing visible: no reactions, audio, haptics, or NPC perception effects arise from this
   system.

## Technical Requirements

Contact detection types shall live under `game/src/Contact/` and use the `AlleyCat.Contact` namespace.

1. The public vocabulary shall be receiver-neutral: `ContactSource`, `ContactMonitor`, `ContactEvent`, and the
   `contact_receiver` group name. Character-body-tied or hand-specific names shall not be introduced.
2. Events shall be receiver-framed: a delivered event means "contact arrived on this receiver". Because receivers such
   as generated `PhysicalBodyPart3D` proxies are kinematic (`AnimatableBody3D`) and kinematic bodies report no
   contacts in Godot, detection is driven by `ContactSource` sweepers that move query shapes and discover
   receiver-eligible bodies. Sweepers are engine mechanics; they shall never own delivery or payload semantics.
3. For each contact pair — one sweeper's query shapes against one receiver body — the raw layer shall maintain a
   stable contact id and a Begin/Update/End lifecycle. Every Update shall carry the current world-space contact
   point, contact normal, relative velocity, penetration depth, press rate, tangential slip speed, accumulated
   duration, and a contact breadth measure of the spatial extent of the contact. The raw layer shall carry objective
   measurements only; no classification logic may live below the raw/classified seam.
4. Validation instrumentation shall expose raw stream state — recorded snapshots or debug events — sufficient to
   verify contact identity stability, lifecycle ordering, and every carried measurement. The raw stream itself is
   monitor-internal rather than publicly subscribable (Technical Requirement 18a).
5. `ContactMonitor` shall aggregate raw contacts for its receiver host, transform contact points into bone-local
   space, host the classification engine — one state machine per active contact — and emit classified events. Monitor
   composition shall follow the project's component and scene-installer conventions.
6. The classification stage sits above the seam and is tunable. It shall emit exactly five classes — Impact/Hit,
   Touch, Press, Caress/Stroke, and Rest/Lean — and shall emit classified events on classification transitions only,
   never per physics tick; steady contact must produce no event stream. Indicative semantics: Impact/Hit is classified
   at contact Begin from approach kinematics; Touch is the gentle-begin default; Press is sustained or increasing
   normal pressure; Caress/Stroke confirms after a tangential-motion window and may revise an earlier Touch
   classification, emitted as one revision transition; Rest/Lean is prolonged settled contact. Exact thresholds,
   windows, and curves are tunable (Technical Requirement 17).
7. Grab shall not be a contact class. Grab is intentional by definition — expressed by the player or an NPC through
   the grab systems (INTR-002) — and shall never be inferred by physics heuristics; this spec shall not couple to
   INTR-002.
8. Classified `ContactEvent` instances shall be immutable and self-describing. Each shall carry: the class; a
   class-appropriate intensity measure derived from raw-stream quantities; the receiver body part and resolved
   region or regions; a counterpart descriptor identifying the contacting entity and, when it belongs to a character,
   attributing its owner; the contact direction as a receiver-relative normal expressed in the bone-local space of
   the contacted body part, with world-space data remaining available from the raw layer; the duration; and an
   optional template-authored natural-language description that is nullable and absent unless authored — payload
   data only, never classification logic.
9. Events shall stay character-relative and self-describing so a future `AgenticMind` percept bridge (AI-001, AI-006)
   can wrap them without re-deriving context. This spec shall not wire Mind, senses, or any AI-006 faculty, and shall
   not define Mind-facing suppression policy.
10. Receiver capability shall be hostable by any physics body. Generated character body parts are receivers through
    `PhysicalBodyPart3D` (RIG-001, INTR-004). World surfaces opt in through the `contact_receiver` group; the
    approved plan's "group/layer" wording deliberately resolves to the group as the normative contract, and any
    dedicated collision-layer arrangement behind sweep queries is an implementation detail and shall not rename the
    group contract.
11. Character receivers and world receivers shall share one delivery path. Owner attribution rides in the event
    payload, never in the delivery mechanism; footsteps are ground-receiver events carrying foot attribution, not a
    special case.
12. Player and NPC receivers shall use the same contracts and components, with parity asserted by running the same
    scenarios against both roles. Self-contact is in scope through the same path, bounded by RIG-001 self-collision
    exception topology, which determines which self pairs can physically co-exist in contact.
13. `ContactSource` sweepers shall be delivered for player XR hands, for NPC hands driven by arm IK (IK-002), and
    for held objects such as a carried stick. Actor-end INTR-004 initiation from the `PlayerVRIK` hand-actuator path
    (IK-005) is carried by `HandPhysicalInteractionChannel` (INTR-004 Technical Requirements 16-17), which succeeded
    the removed hand-specific controller. Delivery is receiver-only in v1; no actor-side "did my hit connect"
    feedback API is included.
14. A sweeper may expose INTR-004 source capability (`IPhysicalInteractionSource`,
    `IImpactPhysicalInteractionSource`) so actor-end interaction keeps working, and receivers keep INTR-004
    interpretation. Ownership stays separate: INTR-004 owns actor-end interaction (carrying, constraining); this
    spec owns receiver-end sensing.
15. As delivered, generic receiver eligibility governs hand and dynamic-body interaction. The legacy eligibility
    contract — collision layer 2 plus the `hand_dynamic_interaction_body` group (formerly RIG-001 Technical
    Requirement 20) — and the hand-specific controller vocabulary are superseded and removed. Migrated dynamic props
    (mirror-room `test_ball.tscn`, `test_stick.tscn`) shall retain INTR-004 impact receipt behaviour under the new
    eligibility.
16. `ContactRegion` definitions shall be authored as named, tagged annotation volumes resolved to bones through the
    sibling `ContactRegionProfile` source-scene workflow — closest ancestor `BoneAttachment3D` bone resolution
    (RIG-001 Technical Requirement 6, Technical Requirement 18c). Regions shall generate no physics bodies, no
    collision shapes, and no collision layer or mask presence. The `ContactMonitor` shall transform contact points
    into bone-local space to refine a body-part location to matching regions; contacts matching no region remain at
    body-part granularity.
17. Classification tunables shall be authored as exported `float` properties on `ContactMonitor` (export group
    `Classification`), named identically to their `ContactClassificationPolicy` counterparts and documented with
    units: `ImpactApproachSpeedThreshold` (m/s), `PressDepthThreshold` (m), `PressRateThreshold` (m/s),
    `PressConfirmationSeconds` (s), `CaressSlipSpeedThreshold` (m/s), `CaressConfirmationSeconds` (s),
    `RestDelaySeconds` (s), and `RestSettledSpeedThreshold` (m/s). Values are not normative: defaults initialise
    from `ContactClassificationPolicy.Default`, absent authoring keeps the code defaults, and scene-authored values
    win. Validation is normative and runs once, at monitor initialisation, through the shared
    `ContactClassificationPolicy` constructor — the single validation owner: every tunable must be finite and
    positive, with the rest settled-speed threshold strictly below the caress slip-speed threshold; authored zero,
    negative, or non-finite values are unusable and fail fast rather than counting as unset.
18. The following implementation details were deferred to delivery; each is resolved, and its final choice is part of
    the delivered contract:
    a. Raw-stream public subscription — final choice: the raw stream is monitor-internal, with classified events and
       validation snapshots (Technical Requirement 4) as the observability surfaces.
    b. Multi-shape coalescing — final choice: all query shapes of one sweeper against one receiver coalesce into a
       single contact per (source, receiver) pair; contact breadth is the maximum pairwise distance between the
       contributing contact points — their extent diameter.
    c. Region resource contract — final choice: a sibling `ContactRegionProfile` resource authored alongside
       `BodyColliderProfile`, carrying its own `SourceScene` export and sharing the same bone-resolution workflow
       rather than extending the collider profile itself.
    d. Region overlap and multi-region reporting — final choice: matching regions are ordered by authored priority
       descending, then volume ascending, then region name ordinal; the first match is the primary region, all
       matching regions are reported with the primary marked, and a contact matching no region stays at body-part
       granularity.
    e. NPC hand sweeper collider mechanics — final choice: `HandContactSweeperController` hosts profile-backed
       query-only shapes on the arm-IK hand targets for both player and NPC roles, swept each physics tick; no
       colliders are registered, so hand collision behaviour is unchanged.
    f. Receiver-side velocity sampling — final choice: the raw frame carries receiver velocity, and the stream
       derives relative velocity from the counterpart and receiver velocities.
19. Deterministic scripted kinematic contact scenarios (TEST-001) shall assert every class, hit classification at
    Begin, touch-to-caress revision, region refinement, ground-receiver footsteps with attribution, player and NPC
    parity, self-contact, legacy migration with INTR-004 impact behaviour preserved, and the absence of consumer
    wiring.

## In Scope

- `AlleyCat.Contact` contracts: the raw contact lifecycle stream, the classification engine, `ContactEvent`,
  `ContactSource`, and `ContactMonitor`.
- Tunable classification policy authored through exported `ContactMonitor` properties, with its structure and
  validation normative.
- v1 sweepers: player XR hands, NPC hands, and held objects.
- Generic receiver eligibility, including world-surface opt-in via `contact_receiver`.
- Player and NPC receiver parity; self-contact; ground contact.
- `ContactRegion` authoring, bone resolution, and bone-local refinement without any physics presence.
- Optional template-authored event descriptions as payload data.
- Supersession of the legacy hand-specific vocabulary and eligibility, with mirror-room prop migration preserving
  INTR-004 impact behaviour.
- Validation instrumentation for the raw stream and deterministic integration tests (TEST-001).

## Out Of Scope

- Object traits such as hot or abrasive surfaces, and live trait state carried on events.
- Grab classification; grab stays intentional through INTR-002 with no physics heuristic.
- Thrown or released-object sweepers.
- Actor-side hit-connect feedback; delivery is receiver-only in v1 (Technical Requirement 13).
- The `AgenticMind` percept bridge and Mind-facing suppression policies; AI-001/AI-006 integration is a later
  feature, and the event contract keeps that bridge cheap (Technical Requirement 9).
- Reactions, haptics, and audio consumers.
- Region-tag sensitivity weighting.
- Exact threshold and tuning values — structure, exported-property authoring, and validation are normative
  (Technical Requirement 17); only the values are deferred.
- Multiplayer synchronisation.

## Acceptance Criteria

### User Requirements

1. User Requirements 1 and 4 validated: scripted kinematic scenarios deliver classified events carrying the class, a
   class-appropriate intensity, the receiver body part, direction, duration, and counterpart owner attribution, and
   identical scenarios against player and NPC receivers produce equivalent events.
2. User Requirement 2 validated: a face contact resolves to authored regions — "right cheek" versus crown — through
   bone-local refinement, and a contact on an unannotated body part stays at body-part granularity.
3. User Requirement 3 validated: scenarios produce all five classes; Impact/Hit is classified at contact Begin; a
   stroke that begins as Touch is revised to Caress/Stroke exactly once, as a transition.
4. User Requirement 5 validated: an opted-in floor receives a footstep contact carrying foot attribution through the
   same delivery path as body receivers.
5. User Requirement 6 validated: a character's hand contacting their own body produces a self-contact event through
   the same path.
6. User Requirement 7 validated: event descriptions are null unless authored, and authored wording is carried
   verbatim.
7. User Requirement 8 validated: no reaction, audio, haptic, or Mind wiring exists in the delivered system.

### Technical Requirements

1. Technical Requirements 1 and 2 validated: types live in `AlleyCat.Contact` under `game/src/Contact/`, the public
   vocabulary is receiver-neutral, and delivered events are receiver-framed.
2. Technical Requirements 3 and 4 validated: instrumentation verifies stable contact ids, Begin/Update/End ordering,
   and each carried raw measurement — contact point, normal, relative velocity, penetration depth, press rate,
   tangential slip speed, duration, and contact breadth.
3. Technical Requirements 5 and 6 validated: the monitor hosts one state machine per active contact, and steady
   contact emits no classified events after the entry transition.
4. Technical Requirement 7 validated: no grab class exists and no INTR-002 dependency is introduced.
5. Technical Requirements 8 and 9 validated: `ContactEvent` is immutable, self-describing, receiver-relative, and
   description-optional, with no Mind or AI-006 wiring present.
6. Technical Requirements 10 and 11 validated: receiver capability is hostable by an arbitrary physics body,
   world opt-in uses `contact_receiver`, and character and world receivers share one delivery path.
7. Technical Requirement 12 validated: the same parity and self-contact scenarios pass against player and NPC rigs.
8. Technical Requirement 13 validated: player XR hand, NPC hand, and held-object sweepers each produce classified
   receiver events, and no actor-side feedback API exists.
9. Technical Requirements 14 and 15 validated: sweepers can expose INTR-004 source capability, mirror-room props
   keep INTR-004 impact receipts under generic eligibility, and the legacy group, layer, and controller vocabulary
   are gone.
10. Technical Requirement 16 validated: region authoring resolves through the `ContactRegionProfile` source-scene bone
    workflow, and region definitions add no physics bodies or collision shapes — generated-node counts are unchanged.
11. Technical Requirement 17 validated: classification tunables are exported on `ContactMonitor`, absent authoring
    keeps code defaults, and unusable authored values — non-finite, non-positive, or settled not strictly below
    slip — fail fast at monitor initialisation.
12. Technical Requirement 18 validated: each deferred implementation detail is resolved and its final choice is
    documented.
13. Technical Requirement 19 validated: the deterministic scenario suite covers every acceptance scenario above.

## References

- [CHAR-003: Character Physical Response](../../character/003-physical-response/index.md)
- [RIG-001: Physical Collision](../../rigging/001-physical-collision/index.md)
- [INTR-002: Hand Grab Execution](../../interaction/002-hand-grab-execution/index.md)
- [INTR-004: Character Physical Interaction API](../../interaction/004-physical-interaction/index.md)
- [AI-001: Mind Component](../../ai/001-mind/index.md)
- [AI-006: Percept-Based Sensing And Attention](../../ai/006-character-perception-and-attention/index.md)
- [IK-002: Arm And Shoulder IK System](../../ik/002-arm-shoulder-ik/index.md)
- [IK-005: Target Pipeline](../../ik/005-target-pipeline/index.md)
- [TEST-001: Integration Test Framework](../../testing/001-test-framework/index.md)
- [HandPhysicalInteractionChannel Implementation](@game/src/IK/HandPhysicalInteractionChannel.cs) — actor-end
  INTR-004 hand initiation, successor of the removed hand-specific controller
- [PhysicalBodyPart3D Implementation](@game/src/Rigging/Physics/PhysicalBodyPart3D.cs)
- [DynamicPhysicalRig Implementation](@game/src/Rigging/Physics/DynamicPhysicalRig.cs)
- [BodyColliderProfile Implementation](@game/src/Rigging/Physics/BodyColliderProfile.cs)
- [AgenticMind Implementation](@game/src/Mind/AI/AgenticMind.cs)
- [ContactMonitor Implementation](@game/src/Contact/ContactMonitor.cs)
- [ContactClassificationPolicy Implementation](@game/src/Contact/ContactClassificationPolicy.cs)
