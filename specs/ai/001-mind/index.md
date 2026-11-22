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
7. In scenes with several characters, speech addressed to an NPC makes its stale active reasoning eligible for
   replacement. Overheard or recipient-unspecified NPC speech does not force a replacement. As a temporary measure,
   player speech marks each hearing NPC's active reasoning stale despite declaring no recipients. None of these
   signals forces a parked NPC to wake or speak; the [AI-002 wait gate][runtime-wait] governs when it may act again.
8. Recalled speech states whom the speaker addressed — the recalling NPC or named other characters — and stays neutral
   when the addressee is undetermined, leaving the addressee to be inferred from context.

## Technical Requirements

1. Mind owns a mostly append-only accepted observation log. Each accepted entry is immutable and has a monotonically
   increasing, never-reused sequence ID, an `ObservedAt` game-time timestamp, its observation payload, and the
   acceptance-time metadata defined in TR-7.
2. Mind owns a concrete-type lifetime-policy registry. Every declared concrete observation type has exactly one
   configured
   policy; duplicate registrations fail activation. An undeclared runtime type is retained with `NeverExpire`.
3. A policy defines its own matching, equivalent suppression, supersession, and expiry semantics. Mind must not impose a
   global equality, scope, or concrete-type switch. Suppressed observations have no accepted entry or scheduling effect.
4. A policy may remove active retained-log entries. Removal notifies registered projectors, but creates no observation,
   event-timeline entry, scheduling signal, attention change, or model message.
5. Initial policy outcomes are:
   - `ObservedSpeech`: `NeverExpire`, event-eligible.
   - `ObservedVisualDescription`: equal values suppress; changed values supersede; normally event-ineligible; a
     retained observation record that is not currently rendered into any model-visible surface — attended-character
     status describes characters through their lore descriptions (AI-006 TR-9).
   - `ObservedRelativePosition`: equal values suppress; changed values retain a finite evidence window adequate for
     hysteresis; normally event-ineligible and supplied as current-scene input.
   - visual presence: attention-only; it creates no retained observation initially.
6. Mind independently appends every event-eligible accepted entry to a persistent event timeline. Active-log expiry or
   supersession never removes timeline entries. AI-002 delivers this timeline's entries to the model through
   runtime-authored scene-update exchanges; the timeline remains Mind's canonical memory, and the provider transcript
   is derived presentation.
7. Mind evaluates the fresh-turn flag and computes, validates, and stores observation importance once per accepted
   entry. Importance remains entry metadata: it does not accumulate as pressure, invalidate requests, or wake a
   waiting Mind. Freshness drives payload-free invalidation under [AI-002][runtime-wait] without completing or
   cancelling an active wait. It stays pending until a valid response confirms a request snapshot covering the entry.
   The wait gate reads its own bounded evidence separately.
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
    properties. `ObservedAt` remains an absolute timestamp on the game clock. When both `ObservedAt` and the
    composition's current game time are available, the framing method appends one event-age suffix relative to that
    composition time; otherwise it omits the suffix. Observation rendering is independent of prompting and templating.
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
    - Self speech never requires a fresh turn and has no attention effect.
    - Addressed to me requires a fresh turn, with the speaker's attention weight raised above the undecided baseline.
    - Addressed to another does not require a fresh turn, with a base attention effect on the speaker plus a smaller
      effect on each addressee.
    - Undecided does not require a fresh turn, with the baseline attention effect on the speaker only. An unrecognised
      speaker has no canonical identity and contributes no attention effect.
    - Temporary exception: trusted non-self player speech requires a fresh turn for each hearer even when undecided or
      addressed to another; AI-006 delivers player speech as undecided. It does not bypass the wait gate.
    Attention weights are tunable; the state mapping and the fresh-turn rules are contractual.
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
- Observer-relative speech-addressee states and their fresh-turn, attention, and rendering policies.
- Declared speech recipients: the `speak` tool's recipient argument, its roster validation and self-observation
  stamping, and recipient transport into hearing without filtering reach.
- Integration boundaries for AI-002 request scheduling, AI-003 current-scene projection, and AI-006 perception.

## Out Of Scope

- Expressions, planner agent behaviour, compaction, and broader perception changes.
- Final finite-evidence-window and attention-weight tuning values.
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
6. Acceptance shows, in a scene with multiple characters, that speech to the observing NPC can replace stale active
   reasoning, while overheard or recipient-unspecified NPC speech is still available without forcing replacement.
   Recipient-unspecified player speech retains the temporary invalidation rule, but does not force a parked NPC to act
   (UR-7; TR-7, TR-14; [AI-002][runtime-wait]).
7. Acceptance shows recalled speech states whom the speaker addressed — the observer or named others — and makes no
   addressee claim when undecided.

### Technical Requirements

1. Tests verify immutable entries with never-reused monotonic sequence IDs, absolute game-time timestamps, payloads,
   and the exactly-once evaluation and storage of freshness and validated importance. Freshness drives invalidation;
   importance neither accumulates as pressure nor wakes a waiting Mind (TR-1, TR-7).
2. Tests verify one policy per declared concrete type, activation failure for duplicates, and `NeverExpire` for unknown
   undeclared runtime types.
3. Tests verify policy-owned match, suppression, supersession, and expiry behaviour without a Mind concrete-type
   catalogue.
4. Tests verify event-timeline persistence across active-log expiry.
5. Tests verify removal notifications reach projectors without creating an observation, timeline record, scheduling
   signal, or model text.
6. Tests verify the generic speech commit-identity gate rejects duplicates before all acceptance effects and survives
   for the Mind node lifetime, while `ObservedSpeech` exposes none of the raw voice, group, segment, continuation, or
   commit-identity members.
7. Tests verify type-owned canonical text and a fallback based only on `TypeKey`. With both times present, the suffix
   shows event age without changing `ObservedAt`; without either time, the suffix is absent (TR-12).
8. Tests verify actor-relative speech text never exposes provenance or continuation transport metadata.
9. Tests verify the three observer-relative addressee states map exactly to their fresh-turn, attention, and rendering
   policies. Addressed-to-me requires a fresh turn, undecided and addressed-to-another require one only when
   player-authored, and self speech has neither fresh-turn nor attention effects. Confirming a snapshot clears only
   covered freshness; the invalidation signal carries no text and does not complete an active wait (TR-7, TR-14).
10. Tests verify `ObservedSpeech` exposes the addressee set as semantic canonical character full IDs — empty meaning
    undecided — that trusted player provenance stays internal and unrendered, and that neither it nor raw voice
    provenance or continuation transport metadata is exposed or rendered.
11. Tests verify the `speak` tool's recipients contract (TR-16): duplicate declarations collapse into one distinct
    ordered set, omitted or empty means unspecified, an unknown ID, the speaker's own ID, or a blank entry returns a
    model-correctable error with nothing submitted or observed, and the committed self observation records the
    resolved recipient set.
12. Tests verify declared recipients ride the voice submission — including the admission-capable committed path —
     into the hearing-side percept for every listener without restricting hearing reach, that unrecognised-speaker
     declarations are ignored, and that player speech carries blank recipients and triggers fresh-turn invalidation
     through its trusted provenance (TR-14, TR-17; AI-006).

## References

- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [AI-004: Lore And Backstory Source Compilation](../004-lore-backstory/index.md)
- [AI-006: Percept-Based Sensing And Attention](../006-character-perception-and-attention/index.md)

[runtime-wait]: ../002-agent-runtime/index.md#scheduling-and-wait
