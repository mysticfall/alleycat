---
id: AI-001
title: Mind Component
---

# Mind Component

## Requirement

Mind must own an NPC's accepted observations, retention decisions, scheduling metadata, and projector notifications.

## Goal

Give each NPC a coherent node-lifetime experience while separating retained evidence, persistent event history, and
per-request scene context.

## User Requirements

1. NPCs retain accepted observations in order and can recall notable events after short-lived scene evidence expires.
2. Repeated unchanged scene information does not flood an NPC's memory or prompt context, while meaningful changes
   remain
   available for reasoning.
3. Speech and condition-owned watch transitions are durable events. Visual presence remains attention-only, and
   ordinary visual scene state remains current context rather than an event stream.
4. Removing transient retained evidence must not look like a new NPC experience.
5. An NPC's own successfully committed speech remains remembered exactly once for that Mind's lifetime.
6. Every recalled event has canonical, consistent text. An unfamiliar event type gives the NPC a safe generic account
   without disclosing arbitrary private evidence.

## Technical Requirements

1. Mind owns a mostly append-only accepted observation log. Each accepted entry is immutable and has a monotonically
   increasing, never-reused sequence ID, an `ObservedAt` game-time timestamp, its observation payload, and the results
   of
   scheduling evaluation performed exactly once at acceptance.
2. Mind owns a concrete-type lifetime-policy registry. Every declared concrete observation type has exactly one
   configured
   policy; duplicate registrations fail activation. An undeclared runtime type is retained with `NeverExpire`.
3. A policy defines its own matching, equivalent suppression, supersession, and expiry semantics. Mind must not impose a
   global equality, scope, or concrete-type switch. Suppressed observations have no accepted entry or scheduling effect.
4. A policy may remove active retained-log entries. Removal notifies registered projectors, but creates no observation,
   event-timeline entry, scheduling pressure, attention change, or model message.
5. Initial policy outcomes are:
   - `ObservedSpeech`: `NeverExpire`, event-eligible.
   - `ObservedVisualDescription`: equal values suppress; changed values supersede; normally event-ineligible and
     supplied
     as current-scene input.
   - `ObservedRelativePosition`: equal values suppress; changed values retain a finite evidence window adequate for
     hysteresis; normally event-ineligible and supplied as current-scene input.
    - dedicated typed proximity-transition observations: `NeverExpire`, event-eligible.
   - visual presence: attention-only; it creates no retained observation initially.
6. Mind independently appends every event-eligible accepted entry to a persistent event timeline. Active-log expiry or
   supersession never removes timeline entries. AI-002's `history` tool reads this timeline.
7. Every accepted entry evaluates importance, freshness, and any other scheduler metadata exactly once. Scheduling
   carries
   only that metadata; it must not carry or render observation payload text.
8. Mind accepts sense and faculty output through one serial, source-neutral queue. Each accepted observation applies its
   attention effect, retention decision, timestamps, log mutation, timeline append when eligible, and notifications as
   one atomic unit. Invalid work affects no unit and does not block later queued work.
9. Speech's ingestion-side optional commit identity remains a Mind node-lifetime, generic exact-once gate. A repeated
   committed identity is rejected before acceptance; this identity mechanism remains independent of lifetime-policy
   matching. Raw voice provenance and automatic continuation/group identity remain private ingestion metadata beside
   the accepted payload, never public `ObservedSpeech` API.
10. Mind discovers direct-child projectors and watches as specified by AI-003 and AI-010. It owns their node-lifetime
    registration, validation, and teardown.
11. Node exit is terminal: intake, queued work, projectors, watches, session work, and deferred callbacks must not
     produce
     post-exit observation, projection, or world effects.
12. Every `Observation` owns canonical model-facing text through a public framing method and a protected, overridable
    body method. The base body uses only `TypeKey` for safe fallback wording and never renders arbitrary payload
    properties. The framing method appends one invariant `ObservedAt` suffix when timestamped; unstamped observations
    have no suffix. Observation rendering has no dependency on prompting or templating.
13. `ObservedSpeech` owns actor-relative self, recognised-other, and unknown wording. Its public API contains only
    semantic actor and content fields; raw voice provenance and continuation transport metadata must neither be
    exposed nor rendered.

## In Scope

- Immutable accepted-log entries, policy-driven active retention, and persistent event timeline ownership.
- Type-owned canonical event text, safe base fallback, and shared timestamp framing.
- Exact-once scheduling metadata and payload-free delivery signals.
- Projector notification on active-log removal without a synthetic semantic observation.
- Speech commit-identity exact-once enforcement for the Mind lifetime.
- Integration boundaries for AI-002 request scheduling, AI-003 current-scene projection, AI-006 perception, and AI-010
  watches.

## Out Of Scope

- Expressions, additional watch types, planner agent behaviour, compaction, and broader perception changes.
- Final importance thresholds and finite-evidence-window tuning values.

## Acceptance Criteria

### User Requirements

1. Acceptance shows that speech and condition-owned watch transitions remain recallable after active retained evidence
   expires, while routine
   visual state is not presented as a new event.
2. Acceptance shows that unchanged descriptions and positions do not repeatedly reach the NPC, changed values do, and
   visual presence affects attention only.
3. Acceptance shows that expiring or superseding retained evidence produces no synthetic observation or model-facing
   event.
4. Acceptance shows that a committed own-speech identity is remembered once only.
5. Acceptance shows an unfamiliar event is presented with safe, canonical wording rather than arbitrary payload data.

### Technical Requirements

1. Tests verify immutable entries with never-reused monotonic sequence IDs, game-time timestamps, payloads, and
   exactly-once
   evaluated scheduling metadata.
2. Tests verify one policy per declared concrete type, activation failure for duplicates, and `NeverExpire` for unknown
   undeclared runtime types.
3. Tests verify policy-owned match, suppression, supersession, and expiry behaviour without a Mind concrete-type
   catalogue.
4. Tests verify event-timeline persistence across active-log expiry and that `history` reads it.
5. Tests verify removal notifications reach projectors without creating an observation, timeline record, pressure, or
   text.
6. Tests verify the generic speech commit-identity gate rejects duplicates before all acceptance effects and survives
   for the Mind node lifetime, while `ObservedSpeech` exposes none of the raw voice, group, segment, continuation, or
   commit-identity members.
7. Tests verify type-owned canonical text, a fallback based only on `TypeKey`, invariant timestamp framing, and no
   timestamp suffix for unstamped observations.
8. Tests verify actor-relative speech text never exposes provenance or continuation transport metadata.

## References

- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [AI-006: Percept-Based Sensing And Attention](../006-character-perception-and-attention/index.md)
- [AI-010: Agent Watches](../010-agent-watches/index.md)
