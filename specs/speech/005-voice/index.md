---
id: SPCH-005
title: Voice Component
legacy_id: BODY-006
---

# Voice Component

## Requirement

Provide an abstract `Voice` component that represents an identifiable 3D speech origin, concrete `AIVoice` and
`PlayerVoice` implementations, per-voice FIFO speech submission with queue-wide silent flush on explicit cut or
pre-hand-off caller cancellation, speaking-activity state for turn-taking gates, and listener dispatch for generated
voice events. For automatic player voice input (SPCH-008), `PlayerVoice` keeps one continuous
 speaking window across the automatic speech-group lifecycle — across endpoint pauses — publishes each settled segment
 through the ordinary speech path without closing the window while the group remains open or ordered segment outcomes
 remain unsettled, and closes the window only at full group settlement. Automatic segments also settle through a
 generic, textless terminal-lifecycle contract. Speaking turns additionally open through a metadata-bearing, textless
 start-lifecycle event — the real automatic group identity at qualified onset, or an internal synthetic token for a
 manual press, whose key settles textlessly when the press yields no speech — and an admission-capable voice — the
 production `AIVoice` (TR-37) — arbitrates submission atomically against attended onset suppression cues (AI-002),
 while a voice without that capability keeps the ordinary cancellable submission path (TR-38).

## Goal

Enable reliable character and player speech whose requests are admitted without waiting for playback, while preserving
per-voice serial generation and inference, spatial attribution, lip-sync, safe node-lifetime behaviour, and
speaking-activity state that
supports Mind turn-taking and speech-ended wake cues under AI-002's session contracts, including deterministic
admission-versus-onset arbitration for suppression protection. The component also carries the player-side
speaking-window continuity and per-segment publication for automatic voice input (SPCH-008).

## User Requirements

1. Players must hear AI-generated speech output with synchronised lip-sync when valid speech is requested.
2. Speech requests made while AI voice generation is busy must queue in request order rather than being rejected. FIFO
   playback means the player hears every utterance complete and in order: a queued utterance must never audibly
   interrupt or cut short its predecessor. Production is FIFO and one at a time per voice — generation and lip-sync
   inference each run a single request at a time for that voice — and an ordinary queued successor never cancels or
   replaces its predecessor's generation or inference work; a successor's inference may run while the predecessor's
   audio is still playing.
3. A caller that successfully submits speech must not wait for generation or playback to finish.
4. Blank speech, disabled output, and missing required configuration must fail clearly rather than report success.
5. Cancellation before admission must cancel the request. After admission, an ordinary submission remains committed,
   while an explicitly cancellable submission may be withdrawn only until playback hand-off; withdrawal before
   hand-off silently flushes the voice's queue (UR-24).
6. One failed speech item — generation, conversion, preparation, or inference — must not block later queued items, and
   failures must be logged without crashing or desynchronising later playback. A predecessor item's inference fault is
   not inherited by the next queued item's preparation.
7. Runtime toggling through `Enabled` must remain supported.
8. Speech events must expose a stable voice `Id` and world-space `Origin` to listeners. The ID supports configured,
   operational attribution but is not authenticated provenance.
9. Each completed nonblank player segment transcript must trigger player voice output in settlement order; blank
   transcripts must be ignored.
10. Removing a voice from the scene must prevent queued or active work from accessing freed Godot nodes.
11. A manual test scene must allow testers to enter arbitrary speech and observe character speech output.
12. Minds must observe which voices are currently speaking: `speak` blocks and `wait` wakes based on speaking windows.
    Which voices a given Mind attends to — including whether the player's record-and-playback window counts — is
    defined by AI-002's speak and wait contracts and is not re-specified in this spec.
13. A voice's speaking state must remain active from the moment its window opens (submission admission, manual
    recording start, or qualified automatic onset (SPCH-008)) until its speaking window closes at the implementation's
    window boundary (TR-24), so no turn-taking gap exists between request and that boundary.
14. Withdrawing an explicitly cancellable submission before playback hand-off must be silent: no error messaging, no
    partial speech output, and no disruption to other speakers. Withdrawal flushes the voice's queue (UR-24) while
    committed playback plays to completion. Two later boundaries then govern retraction. Speech
    successfully admitted through the voice admission capability (TR-37) before a matching attended start or resume
    cue arrives (AI-002) is protected: the matching suppression never cuts it, and it settles naturally into audible,
    remembered speech.
    Playback hand-off remains the irreversible commitment — speech that has reached it is never retracted or cut by
    the submission's cancellation or by observation freshness (AI-002); playback hand-off commits speech (AI-002
    TR-25, TR-26).
15. Developers can observe TTS production latency — generation duration and generated byte size, lip-sync preparation,
    parsing, and playback start — as opt-in pipeline diagnostics without affecting speech behaviour.
16. Manual input takes precedence over automatic input: pressing the record button during automatic capture abandons
    the open automatic speech group — every in-flight segment and unsettled outcome — without any public aggregate,
    and exactly one public speaking window exists at any time (SPCH-008).
17. The player's speaking turn survives pauses: completed automatic segments are published to listeners as they settle
    while the speaking window remains open, so characters can begin reacting to confirmed speech without the player's
    turn ending.
18. A speech group's segments reach listeners in speaking order regardless of backend completion order (SPCH-003
    ordered settlement).
19. The speaking window closes only when nothing remains: no manual recording, no open automatic speech group, and no
    unsettled automatic speech group. A group whose final settlement is blank or failure-only closes the window without
    any broadcast.
20. An automatic segment that publishes no text settles honestly as blank, failed, or abandoned. It never appears as
    speech, a transcript, or a partial result; a published segment remains available through ordinary completed speech.
21. A manual speech press that ends without published speech — blank transcript, failure, abandonment, or voice
    teardown — must signal that textless outcome so characters held by the speech turn (AI-002) resume instead of
    waiting indefinitely. The release carries no words and leaves no public speech record: completed manual speech
    remains an ordinary ungrouped utterance.
22. A voice that does not provide the admission capability is never refused at a suppression cue: its speech follows
    the ordinary submission and cancellation rules of UR-5 and UR-14 — still withdrawable silently until playback
    hand-off — and is not arbitration-protected. This fallback is an intentional exception to suppression
    arbitration, which itself is unchanged for admission-capable voices.
23. An explicit `CutSpeech` on a character stops that character's current speech immediately and silently discards
    every pending and in-progress queued utterance already admitted to that voice — no error feedback, no partial
    publication, no listener notification, no retry. Speech submitted afterwards starts a fresh queue.
24. Upstream cancellation of a submitted utterance, observed before its playback hand-off, silently discards that
    utterance and the rest of the voice's queue without cutting speech that has already started playing. Observed
    after playback hand-off, it changes nothing: committed speech is never retracted or cut by its caller. Only an
    explicit `CutSpeech` or removing the voice from the scene may intentionally stop committed playback.
25. Queue flushes and cancellations are scoped per character: flushing one character's speech queue never affects
    another character's speech, and different characters may produce and play speech concurrently.

## Technical Requirements

1. An abstract `[GlobalClass]` `Voice : Node3D` must be defined under `AlleyCat.Speech.Voice` and implement `IVoice`.
2. `IVoice : IComponent, IIdentifiable` must expose mutable authored `string Id`, `Vector3 Origin`,
    `ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default)`, and compatibility
    `void Speak(string speech)`. It must also expose `bool IsSpeaking` and typed C# events
     `SpeechStarted(IVoice)` / `SpeechEnded(IVoice)`, optionally mirrored as Godot signals. It must expose a typed
     automatic-segment terminal-settlement event (TR-36) and a typed metadata-bearing speech-segment start event
     (TR-27).
3. `SpeakAsync` must return no dispatch-result value. Successful completion of an ordinary submission means the
   request has been submitted or admitted to the FIFO queue; it does not mean generation, playback hand-off, or
   playback has completed. The explicitly cancellable submission completes at playback hand-off (TR-25).
4. `SpeakAsync` must throw `ArgumentException` for blank speech and `InvalidOperationException` when voice output is
   disabled or required configuration is unavailable.
5. Cancellation observed before admission must surface as cancellation and admit no work. Cancellation after
     admission must not retract the committed queue item once playback hand-off has occurred; an explicitly
     cancellable submission may abort before hand-off, silently flushing the voice's queue (TR-25, TR-32). Playback
     hand-off is the irreversibility boundary for cancellable work.
6. `Speak` must remain a safe, deliberately lossy compatibility API. It must perform synchronous validation where
   possible, initiate `SpeakAsync`, and explicitly observe and log or signal asynchronous faults so no task exception is
   abandoned.
7. `Voice` must expose exported mutable authored `Id` and `Enabled`, resolve `Origin` from `GlobalPosition`, retain the
    `SpeechFailed(string error)` signal, provide deferred Godot action-dispatch helpers, and own all speaking-activity
    state and event plumbing: `IsSpeaking` transitions, `SpeechStarted`/`SpeechEnded` raising, and the protected
    window hooks. Subclasses define only their window boundaries.
8. `IVoice.Type` is exactly `voice`, and its canonical CORE-009 `FullId` is exactly `voice:<id>`. Semantic voice
   identity comparisons use ordinal `Id` or `FullId` values as appropriate, never object-reference equality.
9. `Voice` must define a protected virtual post-generation hook. The hook must query `IHearing.GroupName`, filter
   `IHearing` implementations, and invoke `ReceiveVoice(string, IVoice)` with the speech and source `IVoice`.
    Where the speaking window closes in the same synchronous chain as the hook's broadcast, the hook must clear
   `IsSpeaking` and raise `SpeechEnded` before invoking the broadcast, so Minds ingesting the speech observation in
   that chain observe the speaking window already closed. Publications that must not close the window — automatic
   segments of a still-open group — go through the base publication primitive instead (TR-35), which leaves
   `IsSpeaking` untouched.
10. `IHearing.GroupName` must remain the global Godot group constant `"voice_listeners"`.
11. Runtime control state such as `Enabled` must remain on `Voice`, not on the `IVoice` capability contract.
12. `IHasVoice` must follow the component-holder trait pattern and expose `TryGetVoice(out IVoice? voice)` and
    `RequireVoice()` over `IComponentHolder`.
13. `AIVoice` must admit valid requests atomically into one FIFO queue — subject to the suppression admission gate
      (TR-37) — and drain it serially. At most one generation, conversion, lip-sync preparation, and playback hand-off
      pipeline may run at a time per voice instance: generation keeps one request in flight per queue (SPCH-004) and
      lip-sync inference runs one request at a time per voice player (SPCH-002). An ordinary queued successor must
      never cancel or replace its predecessor's generation or inference work; its lip-sync inference begins only
      after the predecessor's streaming read loop settles naturally (SPCH-002) and may run while the predecessor's
      audio is still playing. Production of the next utterance may overlap the current utterance's active playback,
      and the hand-off itself is gated on active playback completion (TR-30). Queue scope and flush are per instance
      (TR-32, TR-40).
14. Busy requests must queue in admission order. `AIVoice` must not reject a valid request merely because another item
    is active.
15. For each admitted item, `AIVoice` must:
    - generate audio through the configured `SpeechGenerator`;
    - convert generated `byte[]` to compatible `AudioStreamWav` data;
    - await `LipSyncPlayer.PreparePlaybackAsync(...)`;
    - invoke `LipSyncPlayer.PlayPrepared(...)` as the playback initiation boundary, gated on completion of the active
      predecessor playback where one exists (TR-30); and
    - invoke the post-generation hook only after successful playback hand-off.
16. `AIVoice` must accept generated PCM 16-bit mono WAV at any sample rate, keep the source sample rate in the playable
    `AudioStreamWav.MixRate`, and perform no resampling; stereo and non-PCM-16 audio remain incompatible with no
    downmix support. Sample-rate normalisation for lip-sync inference is `LipSyncPlayer`'s responsibility
    (SPCH-001/SPCH-002), so a rate the lip-sync side cannot handle fails lip-sync only, not generation or conversion.
17. Failure of an admitted item's generation, conversion, preparation (including streaming inference), or hand-off
     must be logged and emit `SpeechFailed`. It must not notify listeners and must not prevent later FIFO items from
     running, and a predecessor item's inference fault must not be inherited by the next queued item's preparation
     (SPCH-002).
18. Voice or node teardown must settle active and queued submissions safely and prevent later callbacks from accessing
    freed Godot nodes. Expected teardown cancellation must not be reported as a generation failure.
19. `PlayerVoice` must subscribe once to its exported `Transcriber.TranscriptionCompleted` source during `_Ready()`,
    unsubscribe during `_ExitTree()`, and forward only nonblank transcript text through the ordinary speech path. For
    automatic segments it consumes the structured segment metadata (SPCH-003) and publishes each settled segment
    through the publication primitive in settlement order (TR-35). It must also forward each terminal automatic
    segment outcome through the generic `IVoice` settlement event exactly once (TR-36).
20. The manual voice test scene must place `AIVoice` under the character's head attachment and keep playback audio
    spatially attached to the voice origin.
21. After CORE-005 target-scene precedence resolves the final `Character.Id`, generic character installation must assign
    every character-owned Voice local `Id` to that exact value. The resulting canonical voice identity is
    `voice:<character-id>`. Template placeholder voice IDs must be valid lower `snake_case`; installation replaces them
    before voice identity is exposed and validates the assigned voice identity at the final installation boundary.
    AI-001 may use the local ID for operational attribution; another source presenting the same ID is an accepted
    limitation rather than authenticated ownership.
22. `Voiceprint` is a listener-recognition key. Matching or possessing it does not prove that a voice is owned by a
    particular character.
23. `Voice` must expose a new protected hook `OnSpeechStarted` alongside the existing `OnSpeechGenerated`, with a
    matching `OnSpeechEnded` window-close hook, as the only window-boundary declarations subclasses may make.
    `IVoice.IsSpeaking` and the typed activity events are observable contracts; the base class owns the underlying
    state and event raising.
24. Speaking-window boundaries:
    - the base sync path opens at admission and closes at the `OnSpeechGenerated` broadcast;
    - `AIVoice` opens at first FIFO admission and stays open continuously across queued items, closing at playback
      completion of the last queued item through the `LipSyncPlayer` playback-completed notification
      (SPCH-001/SPCH-002), and also on item failure, effective cancellation, node teardown, or a queue flush that
      leaves no committed playback running;
    - `PlayerVoice` opens once at the `RecordingStarted` signal — manual start or qualified automatic group onset
      (SPCH-008) — and stays continuously open across segment closures and continuation gaps, closing only at full
      group settlement per TR-33 — no manual recording, no open automatic group, and no unsettled automatic group —
      or on node teardown; manual preemption does not close the public window — the manual session sustains it
      (TR-33).
25. An explicitly cancellable submission (for example a `SpeakAsync` overload accepting a caller-supplied
     cancellation token) must honour cancellation through generation, conversion, and preparation until playback
     hand-off, which remains the irreversibility boundary for the speak action and its self-observation (TR-26;
      AI-002 TR-26). Pre-hand-off cancellation must abort silently and flush the voice's queue (TR-32): the stale
       submission's playback is refused and every pending or in-progress queued item is discarded as expected silent
       cancellation — no `SpeechFailed`, no `IHearing` broadcast, no self-observation, no retry — while committed
       playback is never cut by caller cancellation and plays to completion; the speaking window closes per TR-24.
       Post-hand-off cancellation — including fresh-turn invalidation under AI-002 — is a no-op: it must not retract
       or cut the committed item, cut playing audio, or cancel its stream (TR-39). Suppression protection uses an
       earlier, separate boundary: successful queue admission through
      the voice admission capability (TR-37) — not speak-tool selection and not playback hand-off — is the protection
      boundary against attended player-onset suppression (AI-002 TR-25, TR-26, TR-56), and only for voices that
      provide the capability (TR-38). A submission admitted before the matching attended start or resume cue
     linearises (TR-37) is protected for its whole pipeline life: the matching onset, resume, or completed-text
     invalidation must never cancel it, and it settles naturally — TTS, playback hand-off, self-observation, and its
     natural tool result — while unrelated fresh observations and node-lifetime cancellation retain their ordinary
     pre-hand-off cancellation (TR-18). Ordinary callers retain admission-only
     semantics with unchanged default behaviour.
26. The actor-stamped self-action observation ("I said X") must commit at playback hand-off, not at admission, through
    AI-001's ordinary ingestion path. Playback hand-off is thus the successful-commitment boundary for the speak
    action and its self-observation, distinct from the admission protection boundary of TR-25 (AI-002 TR-26).
27. `PlayerVoice` must subscribe to the transcriber's public `RecordingStarted` signal (SPCH-003) in `_Ready()` and
      unsubscribe in `_ExitTree()`, using it to open its speaking window for both manual sessions and qualified
      automatic onsets (SPCH-008). It must additionally raise a distinct, metadata-bearing start event on the `IVoice`
      start-lifecycle contract (TR-2): a qualified automatic group onset raises the real `(SpeechGroupID,
      SegmentIndex = 0)` identity — derived from the structured segment metadata (SPCH-003) — at group-open and
      strictly before the compatibility `RecordingStarted` window event, while a manual press raises one opaque
      internal synthetic token created for that press. The synthetic token is transient internal correlation state
      only: it never becomes public grouping metadata, and published manual speech stays ungrouped (TR-34). The start
      event is textless and transient — it never dispatches to `IHearing`, creates no percept (SPCH-006), and never
      opens or re-opens the speaking window — and it must not overload the idempotent `SpeechStarted` window event,
      whose window-idempotence is unchanged. Mind routes the start cue as a transient suppression lifecycle signal
      (AI-001 TR-47, AI-002 TR-56).
28. `AIVoice` must record its TTS production pipeline through the shared pipeline diagnostic log (CORE-007) as Trace
    entries under `AlleyCat.Pipeline`: notification-eligible latency entries for audio generation (including generated
    byte count) and lip-sync preparation (TR-29), a log-only stage entry for request receipt, log-only latency entries
    for audio parsing and playback start, and log-only failure latency. These diagnostics must not change speech
    behaviour.
29. The lip-sync preparation entry must be emitted immediately after the playback hand-off dispatch with the elapsed
    snapshot taken at the preparation boundary, so measured latency stays preparation-only while the console detail
    renders the prepared frame count and mapped mesh count on a single line; the notification text carries the frame
    count only (CORE-007). Emission must run in a `finally` block so console coverage is preserved on hand-off
    success, failure, and cancellation; when the hand-off does not bind meshes, the mesh count is the last-known
    mapping — zero on the first utterance — and the console line remains adjacent to the playback-start line.
 30. AIVoice-orchestrated playback uses background preparation with a gated hand-off. Generation, conversion, and
     lip-sync preparation of a queued utterance may proceed in the background while the predecessor utterance is still
     playing, but the playback hand-off must wait until the active playback session has raised its playback-completed
     notification (`LipSyncPlayer.PlaybackCompleted`, SPCH-001/SPCH-002). A queued utterance must never replace or
     audibly cut its predecessor: FIFO order yields complete audible utterances in admission order. Inference and
     playback ordering are distinct: a successor's lip-sync inference is sequenced by the predecessor's streaming
     read-loop completion (SPCH-002), while its audible playback is sequenced by the predecessor's playback
     completion, so inference may be complete — and playback still waiting — while the predecessor is audibly
     playing.
 31. The gating rule is an AIVoice orchestration concern only. `LipSyncPlayer.Play` and `LipSyncPlayer.PlayPrepared`
      keep their direct-replacement semantics — stopping the active session and starting the new one — for direct
      callers (SPCH-001/SPCH-002). This spec changes how `AIVoice` times its hand-off, not the `LipSyncPlayer`
      contracts. Because `AIVoice` hands off only after predecessor playback completion, ordinary FIFO progression
      never exercises the direct-replacement cut and produces no `DirectPlaybackReplacement` or `ReplacementAdmission`
      cancellation origin (SPCH-002).
 32. Queue flush and waiting-item lifecycle:
      - an explicit cut through `AIVoice.CutSpeech` stops the active playback immediately and silently flushes the
        voice's queue: every pending and in-progress successor item — generating, converting, preparing, or prepared
        and waiting at the playback gate — is discarded as expected silent cancellation, with no `SpeechFailed`, no
        `IHearing` publication, no self-observation, and no retry; a cut does not raise the playback-completed
        notification (SPCH-001/SPCH-002), so the gate must not keep waiting for one, and a later submission starts a
        fresh queue generation;
      - pre-hand-off caller cancellation flushes the same way through the guarded pipeline-cancellation callback
        (TR-39) but never cuts committed playback: the active utterance, if any, plays to completion; and
      - node teardown while items are queued must produce no late hand-off, `IHearing` broadcast, or listener
       notification (TR-18).
 33. The `PlayerVoice` speaking window opens once at the manual or qualified automatic `RecordingStarted` signal,
       stays continuously open across endpoint pauses and continuation gaps, and closes only at full group settlement:
       no manual recording, no open automatic group, and no unsettled automatic group (SPCH-008, SPCH-003). Blank or
       failure-only final settlement closes the window without any broadcast; the final nonblank publication closes
       the window immediately before its listener broadcast, preserving the TR-9 ordering guarantee. Manual
       preemption abandons the open automatic group without closing the public window; the manual session sustains it
       until its own close, so no overlapping public windows occur.
 34. Committed transcripts — manual or automatic — flow only through the ordinary completion path
       (`TranscriptionCompleted` → speech publication). Automatic segment transcripts publish per segment, in ordered
       settlement (SPCH-003), carrying their grouping metadata — `SpeechGroupID`, `SegmentIndex`, and `Continued`
       (SPCH-008, SPCH-006) — through to percepts.
35. `PlayerVoice` owns group/window settlement. It tracks each open automatic speech group and the ordered settlement
       state of its segments (SPCH-003) and closes the speaking window only under the TR-33 conditions. The base
       `Voice` provides a protected publication primitive that broadcasts completed speech to grouped `IHearing`
       receivers without unconditionally closing the speaking window; publications that do end the window keep the
       TR-9 ordering (clear `IsSpeaking` and raise `SpeechEnded` before the broadcast). `PlayerVoice` also forwards the
    transcriber's speech-resume transition (SPCH-008) as a transient lifecycle event: it carries no text, is never
    broadcast to `IHearing`, creates no percept (SPCH-006), and never re-opens an already open window — it is
    consumed downstream by Minds (AI-002).
 36. Automatic segment settlement is a generic `IVoice` contract. `SpeechSegmentMetadata` must be immutable and contain
     the opaque `SpeechGroupID`, zero-based `SegmentIndex`, and `Continued` identity values defined by SPCH-008. The
     typed terminal-settlement event must carry the source `IVoice`, that metadata, and exactly one terminal outcome:
     `Published`, `Blank`, `Failed`, or `Abandoned`. `PlayerVoice` must forward every automatic segment's terminal
     settlement exactly once after SPCH-003 ordered settlement:
     - `Published` follows its single ordinary completed-text and percept delivery and must not create a second
       transient release;
     - `Blank`, `Failed`, and `Abandoned` carry no text, transcript, percept, or `IHearing` dispatch; and
     - manual and ordinary AI speech remain ungrouped and must not fabricate public speech events — no transcript,
       percept, `IHearing` dispatch, publication, or grouping metadata.
     The manual start key settles through the same terminal-settlement event (TR-27): its synthetic token is
     transient internal correlation state — never public grouping metadata — so manual blank, failure, abandonment,
     transcriber replacement, and node teardown each emit exactly one textless terminal settlement (`Blank`,
     `Failed`, or `Abandoned`) for the synthetic identity, releasing downstream holds exactly once with no leaked
     holds (AI-002 TR-57). Completed manual speech never emits a synthetic `Published` event: its ordinary ungrouped
     publication settles the matching hold through ordinary delivery, and duplicate settlements change nothing. When
     a manual press pre-empts an open automatic group, every unsettled automatic key settles before the manual start
     event is raised — the automatic hold releases before the manual hold begins — while the public speaking window
      stays continuous (TR-33, TR-27). Mind routes `Started`, `SpeechResumed`, and non-published settlements only as
      transient lifecycle signals to AI-002. Their identity match is the source voice plus the immutable group and
      segment metadata, or the synthetic token for a manual start key. Node-lifetime shutdown remains terminal: no
      late forwarding or replacement lifecycle work may occur after it begins.
 37. Speech owns a voice admission capability — an optional interface implemented by the production `AIVoice`
      (implementation note: a Speech-defined capability interface carried alongside `IVoice`; consumers discover it
      as an optional capability of the resolved voice, never through a cast to a concrete voice class) — through
      which the agent runner's speech-admission transaction (AI-002 TR-25, TR-56) is hosted: queue admission and the
      runner's protected-admission state commit as one transaction, arbitrated against attended onset cues under the
      normative lock order — the capability implementation's submission lock (`AIVoice._submissionLock`) first, then
      the `AgentSessionRunner` state lock (`_stateLock`). The capability keeps the existing delegate-based
      admission-transaction dependency inversion, so Speech defines no Mind dependency. The onset path takes only the
      runner state lock and must never take the voice submission lock, so the arbitration cannot deadlock. When a
      matching attended start or resume cue linearises first, admission is refused: no TTS request, no queue item, no
      `IHearing` event, and no self-observation — the speak tool returns its existing not-delivered result rather
      than throwing (AI-002 TR-27). When admission completes first, the submission is protected for its whole
      pipeline life (TR-25). The gate applies only to submissions that carry the runner's admission transaction
      through the capability — the `speak` tool path (AI-002); ordinary submissions keep admission-only semantics
      (TR-25). Refusals produce no queue item, so FIFO draining, window state, and teardown contracts are unaffected.
 38. A voice that does not implement the admission capability keeps the ordinary cancellable submission path: the
      caller submits through the ordinary `IVoice` cancellable submission (TR-25), and such speech is not
      arbitration-protected and is never refused at a cue. This fallback is an intentional, specified suppression
       exception — admission arbitration and its cue-first refusal (TR-37) apply only through the capability — and
       every other submission, cancellation, playback hand-off, and teardown contract is unchanged for such voices.
       Capable-voice arbitration itself is unchanged.
 39. `AIVoice` owns an independent pipeline cancellation source that is linked to no caller token. Caller cancellation
      reaches the pipeline only through a guarded callback executed under the submission lock, linearised with the
      playback hand-off: observed before the submission's hand-off commits, the item is marked stale, its playback is
      refused, and the voice's queue is flushed (TR-32); observed after hand-off, the callback is a no-op that cuts
      no playing audio and cancels no stream. Node teardown and the queue-flushing `CutSpeech` remain authoritative
      and may intentionally stop committed playback.
 40. Queue scope is per `AIVoice` instance. Admission, FIFO draining, one-at-a-time generation and inference
      ownership, and flush apply within a single voice's queue only: flushing or cancelling one voice never
      discards, blocks, or cuts another voice's work, and distinct voices may run their pipelines concurrently.

## In Scope

- Abstract `Voice` component identity, location, control, submission, compatibility, failure, and listener contracts.
- Non-result `IVoice.SpeakAsync(...)` and safe lossy `Speak(...)` compatibility.
- FIFO `AIVoice` admission, serial per-voice generation and preparation that may overlap active playback, and
  playback hand-off gated on active playback completion (TR-30).
- Per-voice one-at-a-time generation and lip-sync inference ownership, with ordinary successor preparation that never
  cancels predecessor work (SPCH-002, SPCH-004).
- Queue-wide silent flush on `CutSpeech` and pre-hand-off caller cancellation, with the guarded pipeline-cancellation
  linearisation (TR-32, TR-39) and per-instance queue scope (TR-40).
- Speaking-activity state, `SpeechStarted`/`SpeechEnded` typed events, and base-owned window plumbing.
- Window boundary contracts per implementation, consuming the `LipSyncPlayer` playback-completed notification.
- Explicitly cancellable submissions with playback hand-off as the irreversibility boundary.
- `PlayerVoice` consumption of the transcriber's `RecordingStarted` signal for manual and automatic sessions.
- Automatic-input speaking-window continuity, per-segment publication without premature window closure, group/window
  settlement, and manual precedence at the voice layer (SPCH-008, SPCH-003).
- Failure isolation and safe active and queued work settlement on teardown.
- `PlayerVoice` transcription integration.
- PCM 16-bit mono WAV compatibility at any sample rate and lip-sync synchronisation.
- TTS production pipeline latency diagnostics through the shared pipeline diagnostic log (CORE-007).
- Character-owned voice ID installation and spatial voice origins.
- Generic terminal settlement of automatic segments, including textless blank, failed, and abandoned outcomes.
- Metadata-bearing speech-segment start lifecycle, including the internal manual synthetic correlation token (TR-27).
- Textless terminal settlement of the manual start key on blank, failure, abandonment, transcriber replacement, and
  teardown (TR-36).
- Speech-owned voice admission capability — implemented by the production `AIVoice` — hosting suppression-gated
  admission arbitration under AI-002's normative lock order, protected admitted speech, and the ordinary-path
  fallback for non-capable voices (TR-25, TR-37, TR-38).
- Manual voice test scene and automated unit and integration coverage.

## Out Of Scope

- Visual verification or runtime XR testing, which requires backend access.
- Concurrent speech generation or concurrent audible playback within one voice's queue; admission queues, per-voice
  production remains serial, and background preparation overlapping active playback stays in scope through the
  gated hand-off (TR-30). Concurrent pipelines across distinct voice instances are in scope (TR-40).
- Cancelling or reversing speech work after playback hand-off, except the authoritative node-teardown and `CutSpeech`
  stops (TR-32, TR-39).
- New live microphone capture or transcription mechanics beyond the existing `Transcriber` dependency
  (SPCH-003, SPCH-008).
- Additional speech-generation implementations beyond `AIVoice` and `PlayerVoice`.
- Audio processing beyond conversion to the required WAV format.
- Spatial hearing, acoustic propagation, distance attenuation, or directional perception filtering.
- Decoupling Voice from `IHearing.ReceiveVoice(string, IVoice)`; a later change may replace this mechanism.
- Character animation beyond lip-sync and playback-completion notification.
- Public transcript drafts, WebSocket streaming, sidecars, and per-word transcription requests or settlement signals.

## Voice Contract

| Member | Type | Description |
|--------|------|-------------|
| `Id` | `string` | Mutable authored local voice identifier. |
| `Type` | `string` | Read-only canonical type `voice`. |
| `FullId` | `string` | Canonical identifiable identity `voice:<id>`. |
| `Enabled` | `bool` | Controls whether speech is permitted. Default: `true`. |
| `Origin` | `Vector3` | World-space origin matching the voice node `GlobalPosition`. |
| `SpeakAsync(...)` | `ValueTask` | Ordinary submission completes at admission; the |
|                   |             | cancellable submission completes at playback |
|                   |             | hand-off (TR-25). |
| Admission capability | Optional interface | Speech-owned voice admission capability implemented by the |
|                |                       | production `AIVoice`; hosts the runner-owned speech-admission |
|                |                       | transaction under the normative lock order, and a cue-first |
|                |                       | refusal produces no queue item and no speech side effects |
|                |                       | (TR-37). Voices without it keep the ordinary path (TR-38). |
| `CutSpeech()` | Method | `AIVoice` immediate stop that also silently flushes that voice's queue (TR-32). |
| `Speak(string speech)` | `void` | Safe, lossy fire-and-forget compatibility initiator. |
| `SpeechFailed(string error)` | Signal | Reports an admitted item's asynchronous production failure. |
| `IsSpeaking` | `bool` | Observable speaking-window state; transitions owned by `Voice`. |
| `SpeechStarted(IVoice)` | Event | Typed C# event raised when the speaking window opens. |
| `SpeechEnded(IVoice)` | Event | Typed C# event raised when the speaking window closes. |
| `SpeechSegmentStarted(IVoice,` | Typed event | Metadata-bearing, textless start-lifecycle event; |
| `SpeechSegmentMetadata)` | | a qualified automatic onset carries the real |
| | | `(SpeechGroupID, 0)` before the window event, a |
| | | manual press the internal synthetic token; never |
| | | overloads the idempotent `SpeechStarted` (TR-27). |
| Terminal settlement | Typed event | Generic segment terminal settlement — automatic |
| | | segments and the manual synthetic key — carrying |
| | | source, identity, and one terminal outcome; |
| | | textless except through ordinary publication |
| | | (TR-36). |
| Window hooks | Protected virtual methods | `OnSpeechStarted` and `OnSpeechEnded`; the only |
|               |                          | window-boundary declarations subclasses may make. |
| Post-generation hook | Protected virtual method | Notifies grouped `IHearing` receivers after |
|                      |                          | successful playback hand-off, closing the |
|                      |                          | speaking window before the broadcast where |
|                      |                          | the window ends there. |
| Publication primitive | Protected method | Broadcasts completed speech to grouped |
|                       |                  | `IHearing` receivers without unconditionally |
|                       |                  | closing the speaking window (TR-35). |

## AIVoice Behaviour

1. Validate cancellation, text, enabled state, and required configuration before admission.
2. Atomically admit valid requests in FIFO order, including while another item is active, subject to the suppression
   admission gate (TR-37): a cue-first refusal admits no item and produces no TTS request, queue item, `IHearing`
   event, or self-observation.
3. Complete an ordinary submission's `SpeakAsync` when admission commits, without awaiting generation or playback;
   complete the explicitly cancellable submission at playback hand-off (TR-25).
4. Drain admitted items through one serial per-voice production pipeline — one generation and one lip-sync inference
   in flight at a time — preparing the next utterance in the background while the current utterance plays, without
   cancelling predecessor generation or inference work.
5. Isolate each item's failure, emit diagnostics, and continue with the next queued item.
6. Treat post-hand-off caller cancellation as a no-op; honour explicitly cancellable submissions until playback
   hand-off; withdraw a cancelled pre-hand-off submission by silently flushing the queue (TR-32) without cutting the
   active utterance, which plays to completion.
7. Settle the queue and active pipeline safely during node teardown, producing no late playback hand-off or listener
   notification from a waiting item.
8. Open the speaking window at first FIFO admission and keep it open continuously across queued items.
9. Close the window at playback completion of the last queued item, on item failure, on effective cancellation,
   during node teardown, and when a queue flush leaves no committed playback running.
10. Gate each playback hand-off on the active utterance's playback completion (TR-30); `CutSpeech` empties the queue
    instead of releasing a waiting successor (TR-32).
11. Keep admission, draining, cancellation, and flush scoped to this voice instance (TR-40): other voices' queues are
    unaffected and may run concurrently.

## PlayerVoice Behaviour

1. Subscribe to the configured transcriber's completion event in `_Ready()` and unsubscribe in `_ExitTree()`.
2. Ignore null, empty, or whitespace-only transcript text.
3. Forward nonblank manual transcript text through `Speak(string speech)`; publish settled automatic segments per
   item 6.
4. Do not duplicate subscriptions or retain handlers after leaving the tree.
5. Subscribe to the transcriber's `RecordingStarted` signal (SPCH-003) to open its speaking window when manual
   recording begins or a qualified automatic group onset occurs (SPCH-008), and raise the metadata-bearing start
   event (TR-27): the real `(SpeechGroupID, SegmentIndex = 0)` at automatic group-open strictly before the window
   event, or the internal synthetic token at a manual press.
6. Publish each settled nonblank automatic segment through the publication primitive (TR-35) in settlement order
   (SPCH-003), without closing the speaking window while the group remains open or ordered segment outcomes remain
   unsettled; close the window at full group settlement — blank or failure-only final settlement closes it without
   any broadcast — and during teardown.
7. Keep the speaking window continuously open across endpoint pauses and continuation gaps once it has opened at a
   manual or qualified automatic `RecordingStarted` signal; manual preemption abandons the open automatic group
   without closing the public window (SPCH-008).
8. Forward the transcriber's speech-resume transition (SPCH-008) as a transient lifecycle event carrying no text —
    never broadcast to `IHearing` and never a percept (SPCH-006).
9. Forward each automatic segment's immutable terminal settlement exactly once. Only `Published` follows ordinary
   completed-text publication; `Blank`, `Failed`, and `Abandoned` are textless and never dispatch to `IHearing`.
10. Settle the manual start key exactly once through the same terminal-settlement event (TR-36): manual blank,
    failure, abandonment, transcriber replacement, and teardown each emit one textless settlement for the synthetic
    identity; completed manual speech publishes ungrouped with no synthetic `Published` event; and manual
    pre-emption settles every unsettled automatic key before the manual start event is raised, keeping the public
    speaking window continuous (TR-33).

## Acceptance Criteria

1. Valid AI speech is heard with synchronised lip-sync and is attributable to the voice's stable `Id` and `Origin`.
2. `IVoice` exposes non-result `ValueTask SpeakAsync(...)` and compatibility `void Speak(...)`; no dispatch-result or
   busy-rejection contract remains.
3. Tests verify blank speech throws `ArgumentException`, while disabled or unconfigured speech throws
   `InvalidOperationException` before admission.
4. Tests verify cancellation before admission admits no work and surfaces as cancellation, while cancellation after
   admission does not retract the item once playback hand-off has occurred; explicit pre-hand-off cancellation aborts
   silently and flushes the voice's queue (TR-32).
5. Tests verify second and third busy submissions are admitted, processed FIFO, never create concurrent generation or
   lip-sync inference pipelines within the voice, never cancel predecessor generation or inference work, and play
   each utterance to completion before its successor starts, with no queued utterance audibly cutting its
   predecessor.
6. Tests verify an ordinary submission's `SpeakAsync` completes at admission without awaiting generation, playback
   hand-off, or playback, while the explicitly cancellable submission completes at playback hand-off (TR-25).
7. Tests verify one item's generation, conversion, preparation (including streaming inference), or hand-off failure
   logs and emits `SpeechFailed`, does not notify listeners, and does not block the next item; a predecessor item's
   inference fault is not inherited by the next queued item's preparation.
8. Tests verify compatibility `Speak` performs available synchronous validation and observes every asynchronous fault.
9. Tests verify teardown settles active and queued work without later access to freed nodes or misleading failure
   diagnostics.
10. Tests verify `IHearing.ReceiveVoice(string, IVoice)` notification occurs only after successful playback hand-off and
    ignores grouped non-hearing nodes.
11. Tests verify `AIVoice` accepts PCM 16-bit mono WAV at any sample rate, keeps the source sample rate in the playable
    stream without resampling, rejects stereo or non-PCM-16 audio, and delegates lip-sync input normalisation to
    `LipSyncPlayer`.
12. Tests verify `PlayerVoice` forwards each nonblank settled segment transcription in order, ignores blank
     transcripts, honours `Enabled`, and disconnects on exit.
13. Generic installation replaces each valid lower-`snake_case` character-owned voice placeholder ID with the final
    exact `Character.Id` after target-scene precedence, validates `voice:<character-id>` before identity exposure, and
    supports configured attribution without claiming authenticated provenance or rejecting a source that presents the
    same ID.
14. Acceptance verifies both user-visible FIFO speech — complete utterances heard in order, never cut by a queued
    successor — and failure isolation plus the validation, admission, serialisation, gated hand-off, queue-flush,
    per-instance isolation, cancellation, listener, and node-lifetime contracts.
15. Tests verify `IVoice : IComponent, IIdentifiable`, mutable authored local `Id`, exact Type `voice`, canonical
    `voice:<id>` `FullId`, and ordinal semantic identity comparison without object-reference equality.
16. Tests verify `IVoice` exposes `IsSpeaking` and typed `SpeechStarted`/`SpeechEnded` events (optionally mirrored as
    Godot signals), with all state and event plumbing owned by the `Voice` base and subclasses declaring only window
    boundaries.
17. Tests verify end-of-speech ordering: `IsSpeaking` clears and `SpeechEnded` raises before the `IHearing` broadcast
    when both occur in the same synchronous chain, so Minds ingesting the speech observation observe the speaking
    window already closed.
18. Tests verify window boundaries: the sync path opens at admission and closes at the post-generation broadcast;
     `AIVoice` opens at first admission, stays open across queued items, and closes at last-item playback completion,
      on failure, on effective cancellation, at teardown, and when a queue flush leaves no committed playback
      running; `PlayerVoice` opens once at `RecordingStarted` (manual
     start or qualified automatic group onset, SPCH-008), stays continuously open across segment closures and
     continuation gaps, and closes at full group settlement — no manual recording, no open automatic group, and no
     unsettled automatic group — or at teardown.
19. Tests verify the explicitly cancellable path: pre-hand-off cancellation aborts silently — no `SpeechFailed`, no
     `IHearing` broadcast, no listener notification — and flushes the voice's queue; post-hand-off cancellation never
     retracts, cuts playing audio, or cancels streams; ordinary submissions keep admission-only semantics.
20. Tests verify `PlayerVoice` subscribes to `Transcriber.RecordingStarted` (SPCH-003) once in `_Ready()`,
    unsubscribes in `_ExitTree()`, and opens and closes its speaking window accordingly.
21. Tests verify the actor-stamped self-action observation commits at playback hand-off rather than at admission,
    consistent with AI-001's ingestion contract.
22. Acceptance verifies both user-visible activity and turn-taking behaviour and the technical activity, ordering,
    window-boundary, cancellation, and observation-timing contracts.
23. Tests verify TTS production pipeline diagnostics — notification-eligible generation and lip-sync-preparation
    latency whose console detail carries the generated byte, frame, and mapped mesh counts while the lip-sync
    notification text keeps the frame count only; log-only request-receipt, parsing, and playback-start entries; and
    log-only failure latency — route through the shared pipeline diagnostic log without changing speech behaviour.
 24. Tests verify the gated hand-off: a successor utterance's playback does not start until the active playback session
      raises its playback-completed notification, background preparation overlaps active playback, and ordinary queue
      progression never cancels predecessor generation, inference, or playback work (SPCH-002, SPCH-004).
 25. Tests verify the queue flush lifecycle: `CutSpeech` stops active playback immediately and silently discards every
      queued item — no `SpeechFailed`, no `IHearing` publication, no self-observation, no retry — with later
      submissions starting a fresh queue; cancelling a pre-hand-off submission flushes the same way without
      disrupting committed playback, which plays to completion; and node teardown while items are queued produces no
      late hand-off, `IHearing` broadcast, or listener notification.
 26. Tests verify the automatic speaking window opens once at the qualified-onset `RecordingStarted` signal, stays
      continuously open across endpoint pauses and continuation gaps including across manual preemption, and closes
      only at full group settlement — no manual recording, no open automatic group, and no unsettled automatic group —
      or at teardown (TR-33; SPCH-008, SPCH-003).
 27. Tests verify manual precedence observable at the voice layer: a button press during an automatic speech group
      produces no public aggregate for it — every in-flight segment and unsettled outcome is abandoned — and
      automatic admission resumes only after the manual session finishes (SPCH-008).
28. Tests verify segment publication: each settled nonblank automatic segment broadcasts its completed text to
      hearers without closing the speaking window while the group remains open or ordered segment outcomes remain
      unsettled; blank or failure-only final settlement closes the window without any broadcast; the final nonblank
       publication closes the window immediately before its listener broadcast; and a group's segments publish in
       settlement order regardless of backend completion order (TR-35, TR-9; SPCH-003).
 29. Tests verify immutable terminal settlement metadata and exact-once forwarding for every automatic segment:
     `Published` follows ordinary delivery without a duplicate transient release; `Blank`, `Failed`, and `Abandoned`
     carry no text, transcript, percept, or `IHearing` dispatch; and manual and ordinary AI speech remain ungrouped
     with no fabricated public speech events, while manual textless outcomes settle the synthetic key exactly once
     (TR-36).
 30. Tests verify the metadata-bearing start event (TR-27): a qualified automatic onset raises the real
     `(SpeechGroupID, SegmentIndex = 0)` exactly once at group-open and strictly before the compatibility
     `RecordingStarted` window event; a manual press raises exactly one synthetic token; the event is textless — no
     `IHearing` dispatch, transcript, percept, or history entry (SPCH-006, AI-006) — never opens or re-opens the
     speaking window; and the idempotent `SpeechStarted` window contract is unchanged and never overloaded.
 31. Tests verify both admission-arbitration winners under the normative lock order through the voice admission
      capability (TR-37): cue-first refusal produces no TTS request, no queue item, no `IHearing` event, and no
      self-observation, with the speak tool returning its existing not-delivered result rather than throwing
      (AI-002 TR-27); admission-first submissions
      stay protected through generation, playback hand-off, self-observation, and a natural tool result — never
      cancelled by the matching onset, resume, or completed-text invalidation — while unrelated fresh observations
      and node-lifetime cancellation retain ordinary cancellation (TR-18, TR-25; AI-002 TR-26, TR-40).
 32. Tests verify each manual release path settles the synthetic key exactly once with no public speech events:
     blank transcript, transcription failure, abandonment, transcriber replacement, and node teardown each emit one
     textless terminal settlement; duplicate cues and duplicate settlements change nothing; completed manual speech
     publishes ungrouped and settles its downstream hold through ordinary delivery with no synthetic `Published`
     event; and no manual outcome creates a transcript, percept, `IHearing` dispatch, or grouping metadata (TR-36,
     AI-002 TR-57).
 33. Tests verify manual pre-emption ordering and window continuity: every unsettled automatic key reaches terminal
      settlement before the manual start event is raised — the automatic hold releases before the manual hold begins
      (AI-002 TR-57) — while exactly one continuous public speaking window persists across the transition (TR-33).
 34. Tests verify the non-capable fallback (TR-38, UR-22): a voice that does not implement the admission capability
      submits through the ordinary cancellable path — its speech is never refused at a cue and is not
      arbitration-protected — cue-first refusal and admission protection apply only through the capability, ordinary
       pre-hand-off cancellation still withdraws such speech silently, and capable-voice arbitration under TR-37 and
       AC-31 is unchanged.
 35. Tests verify the pipeline-cancellation linearisation (TR-39): caller cancellation is observed through the guarded
      callback under the submission lock; observed before the item's hand-off, it marks the item stale, refuses its
      playback, and flushes the queue, while observed after hand-off it is a no-op that cuts no playing audio and
      cancels no stream — with only node teardown and `CutSpeech` authorised to stop committed playback.
 36. Tests verify queue scope is per voice instance (TR-40): flushing or cancelling one `AIVoice`'s queue never cuts,
      discards, or blocks another voice's speech, and distinct voices produce and play speech concurrently.

## References

### Implementation

- `@game/src/Speech/Voice/IVoice.cs`
- `@game/src/Speech/Voice/IHasVoice.cs`
- `@game/src/Speech/Voice/Voice.cs`
- `@game/src/Speech/Voice/AIVoice.cs`
- `@game/src/Speech/Voice/PlayerVoice.cs`
- `@game/src/Speech/Transcription/Transcriber.cs`
- `@game/tests/speech/voice/voice_test.tscn`

### Related Specifications

- [AI-001: Mind Component](../../ai/001-mind/index.md)
- [AI-002: Agent Runtime](../../ai/002-agent-runtime/index.md)
- [AI-006: Percept-Based Sensing And Attention](../../ai/006-character-perception-and-attention/index.md)
- [SPCH-006: Hearing Component](../006-hearing/index.md)
- [SPCH-001: Wav2Arkit LipSync Player](../../speech/001-wav2arkit-lipsync-player/index.md)
- [SPCH-002: Audio2Face LipSync Player](../../speech/002-audio2face-lipsync-player/index.md)
- [SPCH-003: Transcriber Component](../../speech/003-transcription/index.md)
- [SPCH-004: Speech Generator Component](../../speech/004-speech-generation/index.md)
- [SPCH-008: Automatic Voice Detection](../../speech/008-automatic-voice-detection/index.md)
- [CORE-002: Configuration API](../../core/002-configuration-api/index.md)
- [CORE-007: Microsoft Logging Integration](../../core/007-microsoft-logging-integration/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
