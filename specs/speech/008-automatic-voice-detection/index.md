---
id: SPCH-008
title: Automatic Voice Detection
---

# Automatic Voice Detection

## Requirement

Provide hands-free player voice input alongside the existing hold-to-speak record button. A local Silero voice-activity
detector drives a two-level automatic lifecycle: the short endpoint-silence threshold closes one speech segment and
immediately dispatches exactly one whole-segment REST transcript through the single config-driven transcriber
(SPCH-003), while the longer continuation gap closes the logical speech group. Completed segments are durable
observations carrying generic grouping metadata. A qualified initial onset and each continuation resume immediately
raise a textless suppression cue carrying the exact `(SpeechGroupID, SegmentIndex)` identity, which holds new AI
reaction work for attending listeners and arbitrates deterministically against NPC TTS admission (AI-002). The manual
button path keeps its current behaviour and takes precedence over automatic input.

## Goal

Let players speak to characters naturally, without holding a button, while manual input remains exactly as predictable
as today. Local voice activity detection is authoritative for segment and group timing; the deployed OpenAI-compatible
backend (SPCH-003) supplies the authoritative transcript for each segment. Useful transcription work starts at endpoint
silence rather than at the continuation gap: every segment is finalised by exactly one whole-segment REST request. Only
committed segment transcripts reach downstream systems
through SPCH-005 and SPCH-006; the model-facing layer coalesces one group's segments into a single contiguous utterance
(AI-003). This spec owns the cross-cutting feature contract; transcriber mechanics are owned by SPCH-003, player
speaking windows by SPCH-005, and speech percepts by SPCH-006.

## User Requirements

1. **UR-1:** Players can speak without pressing any button; when automatic input is enabled, their speech is captured
   and transcribed automatically.
2. **UR-2:** Input mode is configurable — `ButtonOnly`, `AutomaticOnly`, or `ButtonAndAutomatic`. The shipped player
   template configures `ButtonAndAutomatic` as the player-facing default, while a bare Transcriber node without scene
   configuration safely defaults to `ButtonOnly`, so microphone monitoring never starts without explicit opt-in.
3. **UR-3:** Manual hold-to-speak behaviour is unchanged by automatic input: press starts capture, release or maximum
   duration stops it, and exactly one final transcription with one completion or failure follows. The manual path
   bypasses voice-activity detection and the automatic speech lifecycle.
4. **UR-4:** Manual input takes precedence: pressing the button during an open automatic speech group abandons that
   group — every in-flight segment request and unsettled outcome — with no public result, and automatic admission stays
   suppressed until the manual session finishes. Players never observe two overlapping public speaking windows.
5. **UR-5:** Each endpoint-delimited segment of automatic speech produces exactly one authoritative transcript,
   requested immediately when endpoint silence closes the segment — never per word and never per detection frame.
   Completed segments may publish while their group is still open; nothing is published for a segment while it is still
   being voiced, and nothing is published before a qualified onset.
6. **UR-6:** Utterance timing follows the player's speech at two scales: a short pause closes one segment, pauses
   shorter than the continuation gap keep the same logical speech group, silence reaching the continuation gap closes
   the group, and force-close at the maximum speech duration commits the captured prefix.
7. **UR-7:** Segment outcomes settle honestly: a failed segment surfaces through the existing failure path without
   inventing text and without discarding the group's already-successful segments, and a blank segment transcript is
   abandoned silently — no percept and no surfaced failure.
8. **UR-8:** When automatic voice detection cannot start, the player is told once in clear terms: push-to-talk remains
   available in `ButtonAndAutomatic`, and `AutomaticOnly` reports the failure rather than silently idling.
9. **UR-9:** Configured context hints — `Prompt` general context and `Hotwords` for unusual words — apply to every
   automatic segment request with configured wording preserved (SPCH-003).
10. **UR-10:** Pauses do not end the player's turn: speech separated by pauses shorter than the continuation gap remains
    one logical speech group, and downstream systems present it as one contiguous utterance whose model-facing
    coalescing is owned by the AI specs (AI-003).
11. **UR-11:** When speech begins (a qualified automatic onset) or resumes before the continuation gap expires, the game
    immediately emits a textless suppression cue so an NPC attending the speaker at that moment holds new AI reaction
    work and never replies to a half-finished utterance. An NPC not attending the speaker at cue receipt is unaffected.
    The hold settles with the speech: completed text arrives as a fresh turn, while blank, failed, or abandoned speech
    releases it without invented text. Already-committed responses and effects are never retracted (AI-002).
12. **UR-12:** Racing between a qualified speech onset and an attending NPC's in-progress reply is deterministic and
    never cuts audible speech: a reply already admitted to the voice pipeline before the cue settles naturally, while a
    reply that loses the race never becomes audible and its tool result reports the speech as not delivered (AI-002).

## Technical Requirements

1. **TR-1:** Define the input-mode selection `VoiceInputMode { ButtonOnly, AutomaticOnly, ButtonAndAutomatic }`. The
   C# property default is `ButtonOnly`, so a bare Transcriber node without scene configuration never monitors the
   microphone — explicit opt-in is required. The shipped player template
   (`game/assets/characters/templates/reference_female/reference_female_player.tscn`) configures
   `ButtonAndAutomatic` as the player-facing default. `ButtonOnly` disables automatic admission, `AutomaticOnly`
   disables the manual button path, and `ButtonAndAutomatic` enables both under TR-6 precedence.
2. **TR-2:** Local voice activity detection uses the Silero VAD v6.2.1 ONNX model, executed locally on CPU through the
   existing `Microsoft.ML.OnnxRuntime` dependency:
   - The model is a committed repository asset at `game/models/silero/silero_vad.onnx` (Git LFS; MIT licence, Silero
     Team). There is no user download or configuration step.
   - Provenance is pinned to upstream tag `v6.2.1`, commit
     `7e30209a3e901f9842f81b225f3e93d8199902b1`, SHA-256
     `1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3`, 2,327,524 bytes.
   - The model path is fixed in code and is not user-configurable. The speech-probability threshold is a Godot tuning
     export with default `0.5` and range `[0, 1]`.
   - Local detection is authoritative for speech timing; no other component decides segment or group boundaries.
3. **TR-3:** Captured audio is continuously downmixed and resampled to mono 16 kHz for detection. The detector consumes
   exact 512-sample frames, carries the model's recurrent state and 64-sample context between frames, and resets both
   on restart. There is no alternative detection path when the model fails to load (see TR-8).
4. **TR-4:** The automatic lifecycle tuning surface covers pre-roll retention (default 250 ms), minimum voiced duration
   (default 120 ms), endpoint silence (default 700 ms), maximum continuation gap (default 2000 ms), and maximum
   automatic speech duration (default 30 s). Exact values are tunable; semantics are fixed:
   - Minimum voiced duration qualifies a group onset; retained audio before qualification (beyond pre-roll) is never
     public.
   - Endpoint silence closes the active segment and dispatches its finalisation (TR-7); the speech group remains open.
   - The maximum continuation gap is measured from the latest voiced audio frame. Speech resuming before the gap expires
     starts a new segment in the same group; silence reaching the gap closes the group.
   - Maximum automatic speech duration force-closes the group: trim exactly at the maximum boundary, dispatch the active
     segment once, close the group, and suppress automatic rearming until clean silence.
5. **TR-5:** All segment and group timing uses sample positions on the 16 kHz resampled sample timeline — never
   accumulated frame deltas, wall-clock time, or network arrival times. Segment audio is sliced at exact sample
   deadlines on this timeline.
6. **TR-6:** Manual precedence: a button press abandons any open automatic speech group — every in-flight segment
   request and unsettled outcome — emitting no public aggregate for it, and suppresses automatic admission until the
   manual session finishes. No overlapping public logical speaking windows may occur.
7. **TR-7:** Segment finalisation: at endpoint silence (or force-close), exactly one whole-segment REST request — mono
   16 kHz WAV covering the complete segment audio — is dispatched immediately through the single transcriber's REST
   path (SPCH-003). Requests are issued once per endpoint-delimited segment, never per word and never per detection
   frame. Each request's result is the authoritative transcript for its segment; its failure settles that segment
   failed per UR-7, without discarding successful siblings (SPCH-003 ordered settlement).
8. **TR-8:** VAD failure policy. Whether automatic input fails at initialisation or becomes unavailable mid-session,
   exactly one structured warning is logged and shown:
   - Detailed diagnostics — including the model path and the exception — go to the full log; the notification carries
     concise user-facing text, for example "Automatic voice detection is unavailable. Use push-to-talk."
   - The notification routes through the structured logging pipeline (`IUINotificationEntry`, CORE-007), not direct UI
     calls.
   - Initialisation failure in `ButtonAndAutomatic` disables automatic detection while preserving manual push-to-talk
     through the same transcriber.
   - Initialisation failure in `AutomaticOnly` additionally emits one `TranscriptionFailed` and latches automatic
     input unavailable. The system never silently idles.
   - Mid-session unavailability — for example an inference failure — latches automatic monitoring unavailable and
     fires the structured warning exactly once. When an automatic speech group is open in either input mode — including
     while any segment outcome remains unsettled — the Transcriber abandons the group, cancelling in-flight segment
     requests and suppressing late callbacks, and emits exactly one `TranscriptionFailed` for it, so the player
     speaking window closes; manual push-to-talk, where the mode provides it, is unaffected.
   - When no automatic speech group is open in `ButtonAndAutomatic`, mid-session unavailability remains warning-only:
     no `TranscriptionFailed` is emitted.
   - `AutomaticOnly` semantics are unchanged: unavailability always emits exactly one `TranscriptionFailed`.
9. **TR-9:** Speaking windows: a qualified automatic onset opens the player speaking window through the ordinary
   `RecordingStarted` signal — emitted once per group, never per segment. The window stays open across segment
   closures and continuation gaps and closes only when the group is closed and all ordered segment outcomes have
   settled (SPCH-005).
10. **TR-10:** Cross-system coordination: the REST transport, request format, hint plumbing, structured segment
    completion metadata, and ordered settlement are SPCH-003 contracts; the `PlayerVoice` speaking window and segment
     publication are SPCH-005 contracts; speech percepts and their grouping metadata are SPCH-006 contracts; durable
     observations, model-facing coalescing, and onset/resume suppression effects with their TTS-admission arbitration
     are owned by AI-001, AI-003, and AI-002 respectively. Those specs are normative dependencies for this feature and
     must not contradict this spec.
11. **TR-11:** Two orthogonal lifecycles. A speech group spans from qualified onset to the continuation deadline; a
    segment spans one voiced run from its first voiced frame to endpoint silence. One group may own many segments. The
    coordinator is a state machine over `Monitoring`, `Candidate`, `CapturingSegment`, `AwaitingContinuation`, and
    `RearmSuppressed`. A single silent detection frame may cross both endpoint and continuation boundaries; the
    transition returns ordered compound actions that the caller applies in sequence within that frame, establishing
    state for the following frame.
12. **TR-12:** Exact-deadline rules (normative):
    - Silero supplies one voiced/unvoiced decision for each complete 512-sample (32 ms) VAD frame; it cannot locate a
      voice onset or offset within that frame. Retained audio is nevertheless sliced at the exact sample position of an
      endpoint, continuation, or maximum-duration deadline.
    - The first silent VAD frame that crosses the endpoint deadline emits the segment-endpoint transition and slices
      the retained segment audio at the exact endpoint deadline.
    - If that same silent VAD frame also crosses the continuation deadline, it emits the ordered segment-endpoint then
      group-close transitions.
    - A following voiced VAD frame is evaluated from the state established by the crossing frame. It resumes an
      awaiting group only when its frame start is strictly before the continuation deadline (frame start < deadline).
      After a group-close transition, it becomes the next group's candidate; it is neither dropped nor retained in the
      old group.
    - Force-close takes precedence over every other boundary in its frame: trim exactly at the maximum-duration
      boundary, dispatch any active segment exactly once, close the group, retain clean-silence rearm suppression, and
      never create an empty trailing segment.
13. **TR-13:** Segment identity: each group owns a stable opaque `SpeechGroupID` allocated at qualified onset; each
    segment owns a zero-based `SegmentIndex` within its group; `Continued` is exactly `SegmentIndex > 0`. These travel
    on automatic completion and failure outcomes (SPCH-003), on completed speech percepts (SPCH-006), and on the
    textless onset and resume suppression cues (TR-14). They are generic grouping metadata only.
14. **TR-14:** Immediate onset and resume suppression cues. A qualified group onset (TR-4) and a resume while the group
    awaits continuation each raise their lifecycle transition synchronously in the qualifying or resuming voiced frame.
    Each cue is textless and transient — a lifecycle signal, not speech — and carries the exact segment identity of
    TR-13, with `SegmentIndex = 0` at the initial qualified onset; the onset cue precedes the group's `RecordingStarted`
    window signal (TR-9). Cues are forwarded through the player voice layer (SPCH-005) without entering the hearing path
    (SPCH-006) and are attention-gated on the listening Mind side — source-generic, sampled exactly once at cue receipt
    (AI-001 TR-47). Suppression effects are owned downstream (AI-002 TR-25, TR-26): the cue immediately holds the
    attending listener's new AI reaction work, and its race against that listener's NPC TTS admission is deterministic,
    arbitrated under the normative lock order `AIVoice._submissionLock → AgentSessionRunner._stateLock`:
    - Cue first: admission is refused — no TTS request, no queue item, no hearing event, no self-observation — and the
      speak tool returns its existing not-delivered result.
    - Admission first: the NPC speech is protected for its whole pipeline life and settles naturally; already-audible
      playback is never cut by this player turn.
    Blank, failed, and abandoned automatic segments settle their hold textlessly; only interrupted turns replay.

## In Scope

- Input-mode selection, defaults, and manual/automatic precedence policy.
- Local Silero detection: model provenance and runtime contract, audio conditioning, and threshold tuning.
- Two-level lifecycle semantics: speech-group and segment boundaries, onset qualification, pre-roll, endpoint silence,
  continuation gap, and maximum-duration force-close with rearm suppression.
- Exact-deadline and ordered multi-boundary VAD-frame transition rules.
- Per-segment single-request finalisation, segment identity metadata, and the failure, blank-segment, and max-duration
  policies.
- Immediate textless onset and resume suppression-cue signalling, with its deterministic TTS-admission arbitration
  contract against NPC speech.
- Mode-specific VAD failure policy and its user-facing notification.
- Automatic speaking-window continuity through ordinary recording and completion signals (SPCH-005).
- Cross-system coordination pointers into SPCH-003, SPCH-005, SPCH-006, and the AI specs.

## Out Of Scope

- Exact lifecycle tuning constants (durations and thresholds beyond the pinned defaults' semantics); the semantics
  above are normative.
- Player-facing microphone indicators, tones, or haptics (deferred until playtesting).
- Interrupting already-audible NPC playback on player speech onset. Speech successfully admitted into the voice
  pipeline before the cue is protected and settles naturally — it is never cut by this player turn (AI-002 TR-26).
- Transcriber transport mechanics, request format, hint plumbing, completion metadata, and ordered settlement
  (SPCH-003).
- `PlayerVoice` speaking-window mechanics and segment publication (SPCH-005), and speech percepts with their grouping
  metadata (SPCH-006).
- Durable observation retention, model-facing coalescing of group segments into one utterance, and the consumption and
  effects of onset/resume suppression cues with their admission arbitration, which AI-001, AI-003, and AI-002 own.
- Streaming or partial transcription is not part of this feature.

## Design Decisions

### Local Timing Authority

Segment and group boundaries are decided locally from the resampled sample timeline. The coordinator never infers
timing from backend behaviour, so boundary decisions are deterministic and independent of request latency, concurrent
in-flight segment requests cannot reorder group timing, and each segment's retained audio always matches the segment
that is finalised.

### Committed Local Silero Model

The Silero model is a small, MIT-licensed, pinned repository asset executed on CPU through the existing ONNX Runtime
dependency. Committing the asset with a checksum removes any user installation step, keeps detection fully local and
offline, and makes behaviour reproducible across installs. The fixed model path is deliberate: model selection is a
system concern, not a user tuning surface, while the speech-probability threshold remains exposed for tuning.

### Pause-Delimited Segments

Useful STT — and downstream AI — work can begin at endpoint silence plus REST latency rather than continuation-gap
plus REST latency. Each closed segment produces exactly one authoritative transcript from its complete retained audio,
so the speech side needs no textual assembly: the model-facing layer coalesces one group's segments into a single
contiguous utterance (AI-003), and manual recordings and automatic segments share one REST contract in SPCH-003.

### Deterministic Exact-Deadline Semantics

Silero produces one voiced/unvoiced decision for each complete 512-sample (32 ms) VAD frame and cannot identify a
voice boundary within that frame. A silent frame that crosses a deadline establishes the lifecycle state before the
following voiced frame is evaluated; it may emit ordered endpoint then group-close transitions when it crosses both
deadlines. Retained audio remains sliced at the exact sample deadline, preserving precise output without claiming
sub-frame detection. Because every rule is expressed on the sample timeline, adjacent-frame boundary behaviour is
reproducible in deterministic tests.

### Immediate Onset And Resume Suppression

The onset and resume transitions are emitted synchronously and carry no text, so suppression reaches attending
listeners before any continued transcript exists. Carrying the exact `(SpeechGroupID, SegmentIndex)` identity lets
downstream systems key holds precisely and settle them against the matching outcome. Arbitration against NPC TTS
admission is deterministic rather than best-effort: the protection boundary is successful AIVoice queue admission, so
speech admitted before the cue settles naturally and is never cut, while a cue that wins simply prevents the reply from
starting. Already-committed responses and effects remain causal history regardless (AI-002).

### Input Modes And Manual Precedence

The player-facing default `ButtonAndAutomatic` keeps hold-to-speak available for players who want guaranteed capture
while adding hands-free input. The manual path is deliberately unchanged and bypasses detection and the automatic
speech lifecycle so its predictability is unaffected by automatic input. Precedence avoids overlapping public speaking
windows and dual transcripts for the same speech.

### Never-Silent Failure Policy

A missing or unloadable detector must not leave `AutomaticOnly` players with silently dead input. One structured
warning — concise in the UI, detailed in the log — tells the player what happened and what to use instead, and the
failure is surfaced once through the ordinary failure signal where the mode provides no alternative.

## Acceptance Criteria

### User Requirements

1. **AC-U1:** With automatic input enabled (for example the shipped `ButtonAndAutomatic` player template), speaking
   without any button press produces authoritative transcripts through the ordinary completion path — exactly one per
   endpoint-delimited segment, published in speaking order (UR-1, UR-5).
2. **AC-U2:** Each input mode behaves as configured: `ButtonOnly` shows no automatic admission, `AutomaticOnly`
   disables the button path, the shipped player template defaults to `ButtonAndAutomatic` (pinned by
   `CharacterSceneOwnershipIntegrationTests`), and a bare Transcriber node defaults to `ButtonOnly` (pinned by
   `TranscriberIntegrationTests.ManualInput_WithButtonOnlyDefault_TranscribesNormally`) (UR-2).
3. **AC-U3:** Manual sessions behave exactly as the SPCH-003 manual contract requires — one capture, one final, one
   completion or failure — unaffected by automatic input (UR-3).
4. **AC-U4:** A button press during an open automatic speech group abandons it — every in-flight segment and unsettled
   outcome — without any public aggregate and suppresses automatic admission until the manual session finishes; no test
   observes overlapping public speaking windows (UR-4).
5. **AC-U5:** Boundaries track speech at both scales: segment closure after endpoint silence, group continuation across
   pauses shorter than the continuation gap, group closure when silence reaches the continuation gap,
   prefix-committing force-close at maximum duration, and nothing public before qualified onset (UR-6).
6. **AC-U6:** Segment failure surfaces the existing failure signal without discarding the group's successful segments,
   and blank segments are silently abandoned (UR-7).
7. **AC-U7:** When detection cannot start, exactly one warning notification with concise user-facing text is shown,
   full-log diagnostics include the model path and exception, `ButtonAndAutomatic` preserves manual push-to-talk, and
   `AutomaticOnly` emits one `TranscriptionFailed` and latches unavailable (UR-8).
8. **AC-U8:** Hint configuration affects recognition on the automatic path — per segment request — with configured
   wording preserved (UR-9).
9. **AC-U9:** Speech separated by pauses shorter than the continuation gap remains one logical speech group, presented
   downstream as one contiguous utterance by the AI-owned coalescing (UR-10).
10. **AC-U10:** An NPC attending the speaker immediately holds new AI reaction work when a qualified automatic onset
    begins and again when speech resumes before the continuation gap — never replying to a half-finished utterance —
    while an NPC not attending at cue receipt is unaffected and completed speech still reaches it as a fresh turn;
    blank, failed, or abandoned speech releases the hold without invented text, and already-committed responses and
    effects are retained (UR-11).
11. **AC-U11:** Both arbitration outcomes are deterministic for the player: an attending NPC's speech admitted before
    the cue completes naturally and is never cut by the player's turn, while a reply that loses the race never becomes
    audible and its tool result reports the speech as not delivered (UR-12).

### Technical Requirements

1. **AC-T1:** Tests verify segment and group timing derives from sample positions on the 16 kHz resampled timeline and
   not from accumulated frame deltas, wall-clock time, or network arrival times, including exact sample-deadline
   slicing of segment audio (TR-5).
2. **AC-T2:** Tests verify lifecycle semantics: onset qualification gates publicity, endpoint silence closes the active
   segment and dispatches its finalisation while the group remains open, the continuation gap is measured from the
   latest voiced frame and joins pauses into one group, and force-close trims at the maximum boundary, dispatches the
   active segment once, closes the group, and suppresses rearming until clean silence (TR-4).
3. **AC-T3:** Tests verify precedence mechanics: a manual press emits no aggregate for the open group and suppresses
   automatic admission until manual completion or failure (TR-6).
4. **AC-T4:** Tests verify the detector contract: the committed model is loaded from the fixed path, framing is exact
   512-sample windows over mono 16 kHz audio, recurrent state and context carry between frames and reset on restart,
   and the threshold export is honoured within `[0, 1]` (TR-2, TR-3).
5. **AC-T5:** Tests verify finalisation: exactly one whole-segment REST request per endpoint-delimited segment,
   dispatched immediately at segment closure and never per word or per detection frame, with failure settling that
   segment without discarding successful siblings (TR-7).
6. **AC-T6:** Tests verify the failure policy: initialisation failure produces exactly one structured warning routed
   through `IUINotificationEntry`, mode-specific behaviour for `ButtonAndAutomatic` and `AutomaticOnly`, and the
   unavailable latch; mid-session unavailability with an automatic speech group open, in either input mode, abandons
   the group with exactly one `TranscriptionFailed` and closes the player speaking window, while `ButtonAndAutomatic`
   with no group open emits no `TranscriptionFailed` (TR-8).
7. **AC-T7:** Tests verify the automatic speaking window opens through the ordinary `RecordingStarted` signal once per
   qualified group onset, stays open across segment closures and continuation gaps, and closes only when the group is
   closed and all ordered segment outcomes have settled (TR-9).
8. **AC-T8:** Verification confirms SPCH-003, SPCH-005, and SPCH-006 implement their delegated contracts without
   contradicting this spec (TR-10).
9. **AC-T9:** Tests verify the coordinator state machine — `Monitoring`, `Candidate`, `CapturingSegment`,
    `AwaitingContinuation`, `RearmSuppressed` — and that a silent frame crossing both endpoint and continuation
    boundaries returns ordered endpoint then group-close actions that establish the state for the following frame
    (TR-11).
10. **AC-T10:** Direct tests verify the exact-deadline rules: retained audio is sliced at exact endpoint,
     continuation, and maximum-duration sample deadlines; a silent frame crossing the endpoint closes and dispatches
     the segment at that deadline; and a silent frame crossing both deadlines emits endpoint then group close. Adjacent
     voiced-frame tests verify that a frame starting strictly before the continuation deadline resumes the awaiting
     group, while a frame after a group-close transition becomes the next group's candidate without being dropped or
     retained in the old group. Tests also verify force-close precedence and no empty trailing segment (TR-12).
11. **AC-T11:** Tests verify the onset and resume cues are emitted synchronously in the qualifying or resuming voiced
     frame, carry no text, carry the exact `(SpeechGroupID, SegmentIndex = 0)` identity at qualified onset and the
     correct continuation identity at resume, are forwarded only through the player voice lifecycle path (SPCH-005) —
     never the hearing path (SPCH-006) — and reach only listeners attending the source at cue receipt (TR-14).
12. **AC-T12:** Tests verify automatic outcomes, completed percepts, and the textless onset and resume suppression cues
     carry the stable opaque `SpeechGroupID`, zero-based `SegmentIndex`, and `Continued == SegmentIndex > 0` as generic
     grouping metadata (TR-13).
13. **AC-T13:** Tests verify both deterministic arbitration outcomes against NPC TTS admission under the TR-14 lock
     order: cue-first refuses admission with no TTS request, queue item, hearing event, or self-observation while the
     speak tool returns its existing not-delivered result, and admission-first leaves the admitted speech protected
     through natural settlement (TR-14; AI-002 TR-25, TR-26).

**Traceability Map:** UR-1, UR-5 → AC-U1, AC-T5; UR-2, TR-1 → AC-U2; UR-3 → AC-U3; UR-4, TR-6 → AC-U4, AC-T3;
UR-6, TR-4 → AC-U5, AC-T2; UR-7, TR-7 → AC-U6; UR-8, TR-8 → AC-U7, AC-T6; UR-9 → AC-U8; UR-10 → AC-U9; UR-11,
UR-12, TR-14 → AC-U10, AC-U11, AC-T11, AC-T13; TR-2, TR-3 → AC-T4; TR-5 → AC-T1; TR-9 → AC-T7; TR-10 → AC-T8;
TR-11 → AC-T9; TR-12 → AC-T10; TR-13 → AC-T12.

## References

### Implementation

- `@game/src/Speech/Transcription/SileroVoiceActivityDetector.cs`
- `@game/src/Speech/Transcription/AutomaticUtteranceCoordinator.cs`
- `@game/src/Speech/Transcription/AutomaticVoiceInputOptions.cs`
- `@game/src/Speech/Transcription/OpenAITranscriber.cs`
- `@game/models/silero/silero_vad.onnx`
- `@tests/src/Speech/AutomaticVoiceDetectionTests.cs`
- `@integration-tests/src/Speech/TranscriberIntegrationTests.cs`
- `@integration-tests/src/Speech/VoiceSpeakingWindowIntegrationTests.cs`

### Related Specs

- [SPCH-003: Transcriber Component](../003-transcription/index.md)
- [SPCH-005: Voice Component](../005-voice/index.md)
- [SPCH-006: Hearing Component](../006-hearing/index.md)
- [AI-001: Mind Component](../../ai/001-mind/index.md)
- [AI-002: Agent Runtime](../../ai/002-agent-runtime/index.md)
- [AI-003: Prompt API](../../ai/003-prompt-api/index.md)
- [CORE-006: Microsoft Configuration Integration](../../core/006-microsoft-configuration-integration/index.md)
- [CORE-007: Microsoft Logging Integration](../../core/007-microsoft-logging-integration/index.md)

### External Dependencies

- Silero VAD v6.2.1 ONNX model (MIT licence, Silero Team), committed at `game/models/silero/silero_vad.onnx`.
- `Microsoft.ML.OnnxRuntime` (CPU execution).
- WhisperLive speech-to-text server, deployed REST-only on port 8000 (SPCH-003).
