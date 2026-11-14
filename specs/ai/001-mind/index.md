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
3. Speech is a durable event. Visual presence remains attention-only, and
   ordinary visual scene state remains current context rather than an event stream.
4. Removing transient retained evidence must not look like a new NPC experience.
5. An NPC's own successfully committed speech remains remembered exactly once for that Mind's lifetime.
6. Every recalled event has canonical, consistent text. An unfamiliar event type gives the NPC a safe generic account
   without disclosing arbitrary private evidence.
7. In scenes with several characters, speech scheduling follows the speaker's declared recipients: an NPC responds
   promptly to speech whose declared recipients include it, notices speech addressed to other characters without being
   forced into a response, and notices recipient-unspecified NPC speech without it claiming the NPC's turn. As a
   temporary measure, player speech — which declares no recipients — still claims each hearing NPC's turn.
8. Recalled speech states whom the speaker addressed — the recalling NPC or named other characters — and stays neutral
   when the addressee is undetermined, leaving the addressee to be inferred from context.

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
   - visual presence: attention-only; it creates no retained observation initially.
6. Mind independently appends every event-eligible accepted entry to a persistent event timeline. Active-log expiry or
   supersession never removes timeline entries. AI-002 delivers this timeline's entries to the model through
   runtime-authored scene-update exchanges; the timeline remains Mind's canonical memory, and the provider transcript
   is derived presentation.
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
10. Mind discovers direct-child projectors as specified by AI-003. It owns their node-lifetime
    registration, validation, and teardown.
11. Node exit is terminal: intake, queued work, projectors, session work, and deferred callbacks must not
     produce
     post-exit observation, projection, or world effects.
12. Every `Observation` owns canonical model-facing text through a public framing method and a protected, overridable
    body method. The base body uses only `TypeKey` for safe fallback wording and never renders arbitrary payload
    properties. The framing method appends one invariant `ObservedAt` suffix when timestamped; unstamped observations
    have no suffix. Observation rendering has no dependency on prompting or templating.
13. `ObservedSpeech` owns actor-relative self, recognised-other, and unknown wording, plus observer-relative addressee
    wording. Its public API contains only semantic actor, content, and addressee fields; the addressee value is the
    speaker's declared recipient set — canonical character full IDs, defensively copied at construction, empty when
    the speaker left the address unspecified (undecided). Trusted player provenance is an internal member beside the
    payload — set only by ingestion, never model-supplied — and must never be exposed through the public API,
    rendered, or carried in any model-visible value. Raw voice provenance and continuation transport metadata must
    neither be exposed nor rendered.
14. `ObservedSpeech` derives one observer-relative addressee state from that set — addressed to me (the observer is in
    the set), addressed to another (a non-empty set without the observer), and undecided (an empty set) — and owns the
    speech scheduling and attention policy per state:
    - Self speech is unchanged: importance zero, never a fresh turn, no attention effect.
    - Addressed to me: high importance requiring a fresh turn, with the speaker's attention weight raised above the
      undecided baseline.
    - Addressed to another: low importance below the observation-importance threshold that accumulates as ordinary
      pressure — bystanders catch up through accumulation or their next request — never a fresh turn, with a base
      attention effect on the speaker plus a smaller effect on each addressee.
    - Undecided: notable importance that crosses the observation-importance threshold, waking a waiting NPC without
      interrupting active reasoning, never a fresh turn, with the flat baseline attention effect on the speaker only
      (an unrecognised speaker contributes no effect, having no canonical identity to key).
    - Temporary rule — player-authored speech claims the turn: undecided and addressed-to-another speech require a
      fresh turn exactly when the observation carries trusted player provenance. Player speech always reaches hearers
      as undecided (AI-006), so in practice every accepted non-self player line claims each hearing NPC's turn despite
      declaring no recipients. This is a temporary measure pending future player-addressing work, not a permanent
      contract; replacing it is a deliberate future revision.
    Exact importance values, thresholds, and attention weights are tunable; the state mapping, its fresh-turn
    boundaries, and the temporary player rule are contractual.
15. `ObservedSpeech` renders its addressee state canonically, with addressee lists rendered as backticked canonical
    full IDs joined by commas and a final `and` (AI-004 requirement 51):
    - Self speech with declared recipients names them — ``I said to `A` and `B`:`` — so the speaker's own record
      states whom it addressed; unspecified self speech keeps the plain ``I said:`` shape.
    - Addressed to me presents the speaker as speaking to the observer — ``Heard `X` say to me:``.
    - Addressed to another names each addressee — ``Heard `X` say to `A` and `B`:``.
    - Undecided makes no addressee claim — ``Heard `X` say:`` — so the conversation model can infer the addressee
      from context.
    - Unknown-speaker speech is always undecided, because declared recipients cannot be attributed without a
      recognised speaker, and keeps its unknown-speaker wording.
16. The `speak` tool accepts an optional `recipients` argument: an array of canonical character full IDs supplied as
    bare strings — the backticked IDs shown in the model's context, without their backticks (AI-004 requirement 51).
    The Mind resolves the declarations against the live scene roster: duplicates collapse preserving
    first-declaration order, and an omitted or empty array means unspecified — never "everyone". A recipient the
    roster does not recognise, the speaker's own ID, or a blank entry fails as a model-correctable error naming the
    problem, with nothing submitted, spoken, or observed. The committed self observation is owner-stamped with the
    resolved recipient set so the speaker's own record states whom it addressed.
17. Declared recipients are transport metadata, never a hearing filter: they ride the voice submission and its commit
    — admission-capable voices carry them on the admitted submission — into the hearing-side speech percept, and every
    listener in hearing reach perceives the speech regardless of whom it addresses
    ([SPCH-006](../../speech/006-hearing/index.md) bounds reach; AI-006 carries the percept's declared recipients
    into observations).

## In Scope

- Immutable accepted-log entries, policy-driven active retention, and persistent event timeline ownership.
- Type-owned canonical event text, safe base fallback, and shared timestamp framing.
- Exact-once scheduling metadata and payload-free delivery signals.
- Projector notification on active-log removal without a synthetic semantic observation.
- Speech commit-identity exact-once enforcement for the Mind lifetime.
- Observer-relative speech-addressee states and their importance, fresh-turn, attention, and rendering policies.
- Declared speech recipients: the `speak` tool's recipient argument, its roster validation and self-observation
  stamping, and recipient transport into hearing without filtering reach.
- Integration boundaries for AI-002 request scheduling, AI-003 current-scene projection, and AI-006 perception.

## Out Of Scope

- Expressions, planner agent behaviour, compaction, and broader perception changes.
- Final importance thresholds and finite-evidence-window tuning values.
- The permanent player-addressing mechanism that will replace the temporary player fresh-turn rule (TR-14); the
  temporary rule itself is required delivery.
- Live-LLM behavioural validation of the addressee policy in multi-NPC scenes; deterministic acceptance covers this
  delivery.

## Acceptance Criteria

### User Requirements

1. Acceptance shows that speech remains recallable after active retained evidence expires, while routine visual state
   is not presented as a new event.
2. Acceptance shows that unchanged descriptions and positions do not repeatedly reach the NPC, changed values do, and
   visual presence affects attention only.
3. Acceptance shows that expiring or superseding retained evidence produces no synthetic observation or model-facing
   event.
4. Acceptance shows that a committed own-speech identity is remembered once only.
5. Acceptance shows an unfamiliar event is presented with safe, canonical wording rather than arbitrary payload data.
6. Acceptance shows, in a scene with multiple characters, that speech whose declared recipients include the observing
   NPC produces a fresh reply opportunity, speech addressed to another character is noticed without forcing one,
   recipient-unspecified NPC speech is notable without claiming one, and — under the temporary rule — player speech
   claims a fresh reply opportunity despite declaring no recipients.
7. Acceptance shows recalled speech states whom the speaker addressed — the observer or named others — and makes no
   addressee claim when undecided.

### Technical Requirements

1. Tests verify immutable entries with never-reused monotonic sequence IDs, game-time timestamps, payloads, and
   exactly-once
   evaluated scheduling metadata.
2. Tests verify one policy per declared concrete type, activation failure for duplicates, and `NeverExpire` for unknown
   undeclared runtime types.
3. Tests verify policy-owned match, suppression, supersession, and expiry behaviour without a Mind concrete-type
   catalogue.
4. Tests verify event-timeline persistence across active-log expiry.
5. Tests verify removal notifications reach projectors without creating an observation, timeline record, pressure, or
   text.
6. Tests verify the generic speech commit-identity gate rejects duplicates before all acceptance effects and survives
   for the Mind node lifetime, while `ObservedSpeech` exposes none of the raw voice, group, segment, continuation, or
   commit-identity members.
7. Tests verify type-owned canonical text, a fallback based only on `TypeKey`, invariant timestamp framing, and no
   timestamp suffix for unstamped observations.
8. Tests verify actor-relative speech text never exposes provenance or continuation transport metadata.
9. Tests verify the three observer-relative addressee states map exactly to their importance, fresh-turn, attention,
   and rendering policies — addressed-to-me always requires a fresh turn, undecided and addressed-to-another require
   one exactly when player-authored (the temporary player rule), and self speech stays unchanged (importance zero,
   never fresh, no attention effect).
10. Tests verify `ObservedSpeech` exposes the addressee set as semantic canonical character full IDs — empty meaning
    undecided — that trusted player provenance stays internal and unrendered, and that neither it nor raw voice
    provenance or continuation transport metadata is exposed or rendered.
11. Tests verify the `speak` tool's recipients contract (TR-16): duplicate declarations collapse into one distinct
    ordered set, omitted or empty means unspecified, an unknown ID, the speaker's own ID, or a blank entry returns a
    model-correctable error with nothing submitted or observed, and the committed self observation records the
    resolved recipient set.
12. Tests verify declared recipients ride the voice submission — including the admission-capable committed path —
    into the hearing-side percept for every listener without restricting hearing reach, that unrecognised-speaker
    declarations are ignored, and that player speech carries blank recipients whose trusted provenance claims the
    fresh turn (TR-14, TR-17; AI-006).

## References

- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [AI-004: Lore And Backstory Source Compilation](../004-lore-backstory/index.md)
- [AI-006: Percept-Based Sensing And Attention](../006-character-perception-and-attention/index.md)
