---
title: Contact-Lock Coordinator Contract
---

# Contact-Lock Coordinator Contract

## Requirement

Define the reusable, role-neutral contact-lock boundary for NPC and player production composition.

## Goal

Preserve valid authored foot anchors independently and hand each one off at release without moving contact permission,
target actuation, controller policy, or route control into the coordinator. Movement correction remains singular.

## User Requirements

1. Player and NPC production rigs keep each planted support foot stable throughout its authored support phase.
2. Each foot releases independently into continuous ordinary fallback without a false acquire or handoff discontinuity.
3. An unavailable or unsafe lock leaves ordinary animation-synchronised motion and travel intact rather than forcing
   motion or a graph transition.
4. A valid current support may contribute one bounded correction without adding a second movement operation or changing
   player steering, NPC route, destination, or durable route outcome.
5. Unresolved jagged leg or foot motion on the Oblique route remains separate animation/IK continuity debt. It is not
   hidden by contact-lock or convergence acceptance.

## Technical Requirements

1. `ContactLockCoordinator` is reusable and role-neutral. Its authorised production adoption includes the existing NPC
   and production player compositions, covering both reference rigs.
2. ANIM-002 alone permits contact. Every provider evaluation matches the same physics tick's current catalogue role,
   clip, phase, anatomical foot, package identity, evidence identity, and generation. The coordinator consumes that
   immutable evidence; it does not infer, repair, override, or relax permission. Package replacement immediately revokes
   authority from the replaced package and generation.
3. The live evaluated `AnimationTree` clip and phase select explicit authored `Acquire`, `Support [start, end)`, and
   `Release` lifecycle data under the normative
   [ANIM-002 phase-publication contract](../../animation/002-locomotive-animation-set/index.md#technical-requirements).
   Selection supports cyclic wrap and boundary-censored finite partial lifecycles. Exact transition role owns finite
   polarity: idle-to-travel is `Release`, and travel-to-idle is `Acquire`. Missing endpoints remain missing. The
   coordinator and runtime publisher must not infer lifecycle from suffix, clip name, declaration order, inventory
   count, symmetry, release scheduling, provenance metadata, or a synthetic endpoint-recovery marker.
4. The coordinator owns independent world-anchor, lifecycle, feasibility, and release-handoff state for each anatomical
   foot. On a valid acquire, after foot synchronisation and before IK actuation, it captures that foot's complete
   synchronised global transform exactly once and retains it unchanged during continuous valid support. One foot's
   `Acquire`, `Support`, or `Release` must neither clear nor reject the other foot's valid state.
5. At each foot's exact authored `Release` boundary, its contact permission, lifecycle, and contact publication end
   immediately. The coordinator captures the residual correction offset that maps the ordinary fallback sampled at
   release to that foot's release anchor. The offset is translation-only. Each handoff output uses that tick's newly
   sampled live ordinary fallback `Basis` unchanged and adds the remaining offset only to its origin. It must not
   interpolate a fixed release anchor directly towards a moving fallback. The correction therefore translates every
   physical foot point equally while the raw live rotation remains authoritative. The fallback is resolved through the
   existing downstream provider chain for the same foot, with robust exclusion of the contact-lock provider across
   direct and nested resolution to prevent recursion.
6. The translation residual decays monotonically to zero using a deterministic smooth schedule. For a finite valid
   release residual within maximum reach, the non-zero bounded decay policy derives a mathematically finite duration.
   Eight physics ticks remains the nominal minimum, not a forced completion deadline; the derived duration may be longer
   when required by the established bounds. There is no arbitrary hard maximum duration and no maximum-tick path that
   refuses an otherwise valid handoff, snaps to fallback, clears a non-zero residual, or claims completion. Every
   returned target must remain within the established per-side, per-point displacement and velocity-vector-change bands,
   including the first and terminal handoff outputs and the next ordinary fallback output. Absolute speed remains
   diagnostic only. No extra product tuning value is prescribed by this contract.
7. Completion occurs only on a tick when the residual correction is zero and the returned target equals that tick's
   live fallback. The handoff and anchor then clear before the next resolution. The next ordinary fallback output must
   remain continuous under the same displacement and velocity-vector-change bounds. No tick count can substitute for
   zero residual or current live fallback equality. Every handoff tick resamples each foot's live fallback
   independently.
   A missing, refused, invalid, stale, non-finite, recursive, or unreachable fallback fails closed through provider
   semantics without cached fallback reuse, false completion, or a forced discontinuous ordinary fallback.
8. A foot's handoff fails closed on same-foot refusal, invalid or mismatched evidence; duplicate or late acquire; role,
   foot, clip, identity, or generation mismatch; cancellation; replacement; or infeasibility. Coordinator disable or
   teardown clears all state. Same-foot failures must not clear or reject the opposite foot's valid lifecycle or
    handoff, and must not re-acquire, infer, continue, or extend contact. A handoff must not publish contact acceptance,
    act as support correction policy, promote a post-IK observation to an anchor, or infer a missing lifecycle event. It
    is target-only: it cannot request, authorise, return, apply, or cause either movement consumer to consume actor/root
    translation, yaw, or a contact correction.
9. The coordinator returns values only. A release handoff returns only its per-foot target value and zero correction; it
   never writes a foot target, gate, modifier influence, root, hips, skeleton, or actor transform. `CharacterIK` remains
   the sole foot-target, gate, and influence actuator; ordinary animation-synchronised fallback is never contact
   acceptance.
10. Bounded correction is available only from a currently valid `Support` lifecycle. It remains singular: when multiple
    supports are valid, deterministic selection chooses at most one contribution and never sums them. The coordinator
    alone may request the correction, evaluate configurable translation, yaw, rate, and target-deviation caps, and
    record feasibility and selected resolution. Release handoff and absent support, refusal, staleness, invalidation,
    infeasibility, or cap breach return zero correction and must not force a hard exit or graph transition.
11. `NPCNavigation` and CTRL each consume only their own already-authorised bounded value, exactly once inside their
    existing coherent movement operation. Neither may calculate, select, feed back, compensate, steer from, or override
    it. NPC consumption remains NPC-only and must not change route choice, occurrence selection, path progression,
    destination, stopping-region, candidate, confirmation, or durable-outcome decisions. CTRL consumption must not
    create a second move or authorise the `*2i` graph hop. Collision-realised divergence produces zero applied
    correction and trace evidence, never silent compensation.
12. Production composition occurs after the relevant character, animation, and IK installers complete. Composition
    installation distinguishes incomplete wiring from an invalid package: waiting for installers or required nodes that
    have not yet been provided remains a legitimate readiness retry, while a terminal package validation failure is
    reported and latched once per installation attempt and the unchanged failing package is not re-read, re-parsed, or
    re-validated on later frames or physics ticks. The normative failure-reporting contract is owned by the
    [ANIM-002 delivery contract][anim-delivery]. Each composed role has exactly two side-specific contact providers,
    one for each anatomical foot, sharing the reusable coordinator while retaining independent lifecycle, anchor,
    handoff, refusal, and fallback state.
    Diagnostics are read-only and must not install production providers or coordinators. Native observations subscribe
    to the actual `TwoBoneIK3D.ModificationProcessed` signal.
13. Each physics tick records concurrent immutable diagnostic-only per-foot production records. Existing NPC evidence
    retains the `IK003PlaytestTraceV2` schema. Each per-foot resolver/actuation sample retains its same-tick live role,
    clip, phase, lifecycle window, raw synchronised source world transform, anchor and handoff state, provider selection
    and decision, and the singular selected support contribution. It records the requested, authorised, applied, and
    residual translation and yaw that produced the decision. It separately identifies release, non-contact handoff, and
    ordinary fallback. It records the live fallback provider selection and transform, release and remaining
    translation-only offsets, returned transform, unchanged live fallback `Basis`, continuity metrics, phase, handoff
    tick, derived duration, nominal minimum, applicable point bounds, and any fail-closed disposition; and signal-time
    native output,
    native target and end-foot observations, native target-to-end-foot error, post-IK anchor error, and raw
    source-to-anchor deviation. The latter is diagnostic animation drift, not a visual lock-failure judgement. A
    differing current-live observation is emitted only as a separately labelled observation and is never joined to the
    resolver sample. Player records also retain evaluated player role, clip, phase, foot, identity, generation, and
    correction-consumption disposition from that same physics tick. The trace neither installs composition, accepts
    contact, authorises travel release, nor steers movement.
14. Validation uses synthetic declared packages as behavioural oracles. Fixtures cover independent mirrored role-foot
    paths, cyclic wrap, both finite polarities, boundary-censored partial lifecycles, fallback-only paths, package
    replacement, minimum and extended handoffs, moving fallback, nested-recursion prevention, and invalid fallback
    refusal. For ankle, heel, midsole, ball/toe origin, and toe tip, deterministic checks prove equal translation while
    live fallback rotation remains authoritative. Current production blends and packages are limited to structural,
    openability, binding, and immutability checks until a content specification ratifies their identities and contacts.
15. Automated checks may establish deterministic lifecycle, continuity, geometry, and provenance properties. They do
    not claim subjective naturalness, visual quality, or broad animation/IK continuity.

## In Scope

- Package-declared role-foot lifecycle paths, independent concurrent per-foot state, singular bounded support
  contribution, bounded non-contact release handoff, and Trace V2.
- Player and NPC production composition, both-rig traces, targeted tests, and narrow real-scene wiring checks using
  synthetic declared packages for behavioural expectations.
- Support-only singular correction for CTRL and NPC NAV within their separate existing movement operations.

## Out Of Scope

- Optional role or package expansion beyond declared applicability and consumer-required capabilities.
- Route, path, destination, replan, persistent-outcome, terrain, pelvis, whole-body, warping, and emergency-policy
  rewrites.
- Broad fluidity suites, release claims, and speculative tuning matrices. Configured cap values remain playtest tuning.
- Screenshot, photobooth, contact-sheet, manual visual review, and visual-capture verification.
- Animation or IK remediation for the unresolved Oblique jagged leg or foot motion. This exclusion does not relax any
  coordinator lifecycle, handoff, fail-closed, or sole-actuation requirement.

## Acceptance Criteria

### User Requirement Acceptance

1. Fixture-owned packages in player and NPC production composition preserve valid planted support without forced motion
   or a new route regression.
2. Synthetic finite-role packages consume their declared idle-to-travel `Release` or travel-to-idle `Acquire` without
   inventing the boundary-censored opposite endpoint or wrapping a finite clock.
3. A refusal, invalidation, cap breach, or collision divergence preserves ordinary fallback motion and produces no
   forced correction.
4. NPC integration preserves its route and outcome behaviour; player integration preserves responsive steering and
   ordinary travel when no graph-release occurrence is admitted.
5. Review preserves the user's classification that Oblique convergence works while jagged leg or foot continuity remains
   unresolved animation/IK debt.

### Technical Requirement Acceptance

1. Synthetic declared-package tests prove runtime selection from the current catalogue role, clip, foot, phase, package
   identity, and generation. They cover deterministic acquire, half-open support, release, cyclic wrap, both finite
   polarities, valid partial lifecycles, fallback, and package replacement without name, suffix, order, count, or
   symmetry inference. Replacement revokes prior authority.
2. Focused tests prove one synchronised global anchor capture per valid foot acquire and immediate same-foot release of
   contact permission, lifecycle, and publication. Synthetic moving-fallback trajectories prove that each output is the
   current same-foot fallback plus a smoothly and monotonically decaying translation residual, with fresh fallback
   resolution on every tick. Direct and nested recursion are excluded. A nominal-minimum case completes in eight ticks;
   a duration-extension case derives a longer deterministic, mathematically finite duration from finite residual,
   maximum reach, and the non-zero bounded decay policy. Completion occurs only at zero residual and equality with that
   tick's live fallback. No maximum-tick fall-through exists, the next ordinary fallback remains continuous, and
   absolute speed does not fail an otherwise bounded trajectory. Tests prove that the output `Basis` equals the live
   fallback `Basis` every tick and that ankle, heel, midsole, ball/toe origin, and toe tip receive the same correction
   translation.
3. Missing, refused, invalid, stale, non-finite, recursive, or unreachable fallback fails closed through provider
   semantics without prior-sample reuse, false completion, or a forced discontinuous ordinary fallback. Mirrored
   Right-release/Left-acquire and Left-release/Right-acquire cases prove no wrong-foot clearing. Tests retain same-foot
   failure for duplicate, mismatched, identity, generation, cancellation, replacement, or infeasibility and all-state
   clearing on disable or teardown.
4. Tests prove the coordinator and providers return values only, never write targets or actor transforms, never
   re-acquire, continue, extend, or publish contact during handoff, and leave `CharacterIK` as the sole target, gate,
   and influence writer. Handoff requests, authorises, returns, and applies zero actor translation/yaw correction for
   both movement consumers.
5. Tests prove release handoff contributes zero actor correction. Absent support, refusal, staleness, infeasibility, cap
   breach, and collision divergence produce zero authorised or applied correction. When multiple supports are valid,
   the existing
   deterministic selection produces at most one bounded support contribution and never a sum.
6. Focused NPC and player movement tests each prove one consumption of an already-authorised value in the consumer's
   existing movement operation, with no calculation, selection, feedback, compensation, steering response, second move,
   CTRL graph-hop authority, or NAV route rewrite.
7. The real navigation scene and production player composition prove post-installer composition, two side-specific
   providers, read-only diagnostics, no diagnostic installation, concurrent per-foot
   release/handoff/fallback state, live fallback, translation-only residual, returned target, unchanged fallback
   `Basis`, continuity metrics, phase, tick, derived duration, nominal minimum, applicable point bounds, fail-closed
   disposition, singular selected support contribution, signal-time native output, full Trace V2 fields, and destination
   start.
8. Focused synthetic regressions prove mirrored overlap cases, bounded minimum and extended handoffs, no wrong-foot
   clearing, and no one-frame target discontinuity through ordinary fallback. Generic detector mutations continue to
   detect injected point and velocity-vector discontinuities. Invalid evidence, lifecycle identity, generation, phase,
   anchor feasibility, correction cap, collision, realisation, and sole-actuation failures remain fail-closed.
9. Finite-role fixtures prove idle-to-travel `Release`, travel-to-idle `Acquire`, phase-`1` retention where declared,
   no finite wrap, no invented absent endpoint, and bounded handoff completion at live fallback equality. Current real
   blends and packages are checked only for structure, openability, binding, and immutability; they are not behavioural
   or contact oracles.
10. Player production checks prove that each provider decision uses the same physics tick's evaluated player role, clip,
    phase, anatomical foot, evidence identity, and generation. Missing, refused, stale, mismatched, cancelled, replaced,
    duplicate, and infeasible same-foot evidence fails closed without clearing the other foot or reusing cached
    authority.
11. Player and NPC checks prove ANIM-002 remains the sole contact-permission owner. The same central permission and
    immutable authored evidence feed both compositions without CTRL/IK inference, override, symmetry substitution, or
    downstream repair.
12. Boundary checks prove travel-release occurrence authority and per-foot lifecycle evidence are independent. Foot
    contact, handoff, and correction cannot authorise CTRL's `*2i` hop; travel-release authority cannot admit contact,
    create an anchor, or authorise correction.
13. Composition-installation tests prove a synthetic invalid animation package produces exactly one latched diagnostic
    per installation attempt, with no per-frame or per-physics-tick re-read, re-parse, or re-validation of the
    unchanged package, while a composition whose installer wiring is genuinely incomplete still installs once the
    missing installers or nodes are provided.

### Acceptance Traceability

- User Requirement 1 → User Requirement Acceptance 1.
- User Requirement 2 → User Requirement Acceptance 2.
- User Requirement 3 → User Requirement Acceptance 3.
- User Requirement 4 → User Requirement Acceptance 4.
- User Requirement 5 → User Requirement Acceptance 5 and Technical Requirement Acceptance 8–9.
- Technical Requirements 1–3 → Technical Requirement Acceptance 1.
- Technical Requirement 4 → Technical Requirement Acceptance 2–5.
- Technical Requirements 5–8 → Technical Requirement Acceptance 2–5 and 8.
- Technical Requirements 9–11 → Technical Requirement Acceptance 4–6.
- Technical Requirements 12–13 → Technical Requirement Acceptance 7, 10, 13.
- Technical Requirement 14 → Technical Requirement Acceptance 8.
- Technical Requirement 15 → User Requirement Acceptance 5 and Technical Requirement Acceptance 8–9.
- Production player composition and authority separation → Technical Requirement Acceptance 10–12.

## References

- [IK-003 Overview](index.md)
- [Leg-Feet IK Contract](leg-feet-ik-contract.md)
- [ANIM-002: Locomotive Animation Set](../../animation/002-locomotive-animation-set/index.md)
- [NAV-001: NPC Navigation](../../nav/001-npc-navigation/index.md)

[anim-delivery]: ../../animation/002-locomotive-animation-set/index.md#full-catalogue-delivery-contract
