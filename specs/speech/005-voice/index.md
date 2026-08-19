---
id: SPCH-005
title: Voice Component
legacy_id: BODY-006
---

# Voice Component

## Requirement

Provide an abstract `Voice` component that represents an identifiable 3D speech origin, concrete `AIVoice` and
`PlayerVoice` implementations, FIFO speech submission, speaking-activity state for turn-taking gates, and listener
dispatch for generated voice events.

## Goal

Enable reliable character and player speech whose requests are admitted without waiting for playback, while preserving
serial generation, spatial attribution, lip-sync, safe node-lifetime behaviour, and speaking-activity state that
supports Mind turn-taking and speech-ended wake cues under AI-002's session contracts.

## User Requirements

1. Players must hear AI-generated speech output with synchronised lip-sync when valid speech is requested.
2. Speech requests made while AI voice generation is busy must queue in request order rather than being rejected.
3. A caller that successfully submits speech must not wait for generation or playback to finish.
4. Blank speech, disabled output, and missing required configuration must fail clearly rather than report success.
5. Cancellation before admission must cancel the request. After admission, an ordinary submission remains committed,
   while an explicitly cancellable submission may be withdrawn only until playback hand-off.
6. One failed speech item must not block later queued items, and failures must be logged without crashing or
   desynchronising later playback.
7. Runtime toggling through `Enabled` must remain supported.
8. Speech events must expose a stable voice `Id` and world-space `Origin` to listeners. The ID supports configured,
   operational attribution but is not authenticated provenance.
9. Completed nonblank player transcription must trigger player voice output; blank transcription must be ignored.
10. Removing a voice from the scene must prevent queued or active work from accessing freed Godot nodes.
11. A manual test scene must allow testers to enter arbitrary speech and observe character speech output.
12. Minds must observe which voices are currently speaking: `speak` blocks and `wait` wakes based on speaking windows.
    Which voices a given Mind attends to — including whether the player's record-and-playback window counts — is
    defined by AI-002's speak and wait contracts and is not re-specified in this spec.
13. A voice's speaking state must remain active from the moment its window opens (submission admission or recording
    start) until its speaking window closes at the implementation's window boundary (TR-24), so no turn-taking gap
    exists between request and that boundary.
14. Withdrawing an explicitly cancellable submission before playback hand-off must be silent: no error messaging, no
    partial speech output, and no disruption to other speakers. Speech that has reached playback hand-off is never
    retracted or cut by the submission's cancellation; interruption-driven cutting of already-audible speech is a
    separate mechanism (AI-002).

## Technical Requirements

1. An abstract `[GlobalClass]` `Voice : Node3D` must be defined under `AlleyCat.Speech.Voice` and implement `IVoice`.
2. `IVoice : IComponent, IIdentifiable` must expose mutable authored `string Id`, `Vector3 Origin`,
    `ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default)`, and compatibility
    `void Speak(string speech)`. It must also expose `bool IsSpeaking` and typed C# events
    `SpeechStarted(IVoice)` / `SpeechEnded(IVoice)`, optionally mirrored as Godot signals.
3. `SpeakAsync` must return no dispatch-result value. Successful completion of an ordinary submission means the
   request has been submitted or admitted to the FIFO queue; it does not mean generation, playback hand-off, or
   playback has completed. The explicitly cancellable submission completes at playback hand-off (TR-25).
4. `SpeakAsync` must throw `ArgumentException` for blank speech and `InvalidOperationException` when voice output is
   disabled or required configuration is unavailable.
5. Cancellation observed before admission must surface as cancellation and admit no work. Cancellation after
    admission must not retract the committed queue item once playback hand-off has occurred; an explicitly
    cancellable submission may abort silently before hand-off (TR-25). Playback hand-off is the irreversibility
    boundary for cancellable work.
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
     that chain observe the speaking window already closed.
10. `IHearing.GroupName` must remain the global Godot group constant `"voice_listeners"`.
11. Runtime control state such as `Enabled` must remain on `Voice`, not on the `IVoice` capability contract.
12. `IHasVoice` must follow the component-holder trait pattern and expose `TryGetVoice(out IVoice? voice)` and
    `RequireVoice()` over `IComponentHolder`.
13. `AIVoice` must admit valid requests atomically into one FIFO queue and drain it serially. At most one generation,
    conversion, lip-sync preparation, and playback hand-off pipeline may run at a time.
14. Busy requests must queue in admission order. `AIVoice` must not reject a valid request merely because another item
    is active.
15. For each admitted item, `AIVoice` must:
    - generate audio through the configured `SpeechGenerator`;
    - convert generated `byte[]` to compatible `AudioStreamWav` data;
    - await `LipSyncPlayer.PreparePlaybackAsync(...)`;
    - invoke `LipSyncPlayer.PlayPrepared(...)` as the playback initiation boundary; and
    - invoke the post-generation hook only after successful playback hand-off.
16. Generated audio must be PCM 16-bit, 16 kHz, mono WAV before lip-sync preparation. `SpeechGenerator` owns sample-rate
    normalisation; `AIVoice` must not resample.
17. Failure of an admitted item's generation, conversion, preparation, or hand-off must be logged and emit
    `SpeechFailed`. It must not notify listeners and must not prevent later FIFO items from running.
18. Voice or node teardown must settle active and queued submissions safely and prevent later callbacks from accessing
    freed Godot nodes. Expected teardown cancellation must not be reported as a generation failure.
19. `PlayerVoice` must subscribe once to its exported `Transcriber.TranscriptionCompleted` source during `_Ready()`,
    unsubscribe during `_ExitTree()`, and forward only nonblank transcript text through `Speak`.
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
      (SPCH-001/SPCH-002), and also on item failure, effective cancellation, or node teardown;
    - `PlayerVoice` opens when the transcriber's `RecordingStarted` signal fires and closes at the
      `OnSpeechGenerated` broadcast, on transcription failure, on blank transcript, or on node teardown.
25. An explicitly cancellable submission (for example a `SpeakAsync` overload accepting a caller-supplied
    cancellation token) must honour cancellation through generation, conversion, and preparation until playback
    hand-off, which is the new irreversibility boundary. Pre-hand-off cancellation must abort silently: no
    `SpeechFailed`, no `IHearing` broadcast, and no listener notification, and the speaking window closes.
    Post-hand-off cancellation must not retract the committed item. Ordinary callers retain admission-only
    semantics with unchanged default behaviour.
26. The actor-stamped self-action observation ("I said X") must commit at playback hand-off, not at admission, through
    AI-001's ordinary ingestion path.
27. `PlayerVoice` must subscribe to the transcriber's public `RecordingStarted` signal (SPCH-003) in `_Ready()` and
    unsubscribe in `_ExitTree()`, using it to open its speaking window.

## In Scope

- Abstract `Voice` component identity, location, control, submission, compatibility, failure, and listener contracts.
- Non-result `IVoice.SpeakAsync(...)` and safe lossy `Speak(...)` compatibility.
- FIFO `AIVoice` admission and serial generation, preparation, and playback hand-off.
- Speaking-activity state, `SpeechStarted`/`SpeechEnded` typed events, and base-owned window plumbing.
- Window boundary contracts per implementation, consuming the `LipSyncPlayer` playback-completed notification.
- Explicitly cancellable submissions with playback hand-off as the irreversibility boundary.
- `PlayerVoice` consumption of the transcriber's `RecordingStarted` signal.
- Failure isolation and safe active and queued work settlement on teardown.
- `PlayerVoice` transcription integration.
- PCM 16-bit, 16 kHz, mono WAV compatibility and lip-sync synchronisation.
- Character-owned voice ID installation and spatial voice origins.
- Manual voice test scene and automated unit and integration coverage.

## Out Of Scope

- Visual verification or runtime XR testing, which requires backend access.
- Parallel speech generation or playback; admission queues, but production remains serial.
- Cancelling or reversing speech work after playback hand-off.
- New live microphone capture or real-time streaming beyond the existing `Transcriber` dependency.
- Additional speech-generation implementations beyond `AIVoice` and `PlayerVoice`.
- Audio processing beyond conversion to the required WAV format.
- Spatial hearing, acoustic propagation, distance attenuation, or directional perception filtering.
- Decoupling Voice from `IHearing.ReceiveVoice(string, IVoice)`; a later change may replace this mechanism.
- Character animation beyond lip-sync and playback-completion notification.

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
| `Speak(string speech)` | `void` | Safe, lossy fire-and-forget compatibility initiator. |
| `SpeechFailed(string error)` | Signal | Reports an admitted item's asynchronous production failure. |
| `IsSpeaking` | `bool` | Observable speaking-window state; transitions owned by `Voice`. |
| `SpeechStarted(IVoice)` | Event | Typed C# event raised when the speaking window opens. |
| `SpeechEnded(IVoice)` | Event | Typed C# event raised when the speaking window closes. |
| Window hooks | Protected virtual methods | `OnSpeechStarted` and `OnSpeechEnded`; the only |
|               |                          | window-boundary declarations subclasses may make. |
| Post-generation hook | Protected virtual method | Notifies grouped `IHearing` receivers after |
|                      |                          | successful playback hand-off, closing the |
|                      |                          | speaking window before the broadcast where |
|                      |                          | the window ends there. |

## AIVoice Behaviour

1. Validate cancellation, text, enabled state, and required configuration before admission.
2. Atomically admit valid requests in FIFO order, including while another item is active.
3. Complete an ordinary submission's `SpeakAsync` when admission commits, without awaiting generation or playback;
   complete the explicitly cancellable submission at playback hand-off (TR-25).
4. Drain admitted items through one serial generation and playback-hand-off pipeline.
5. Isolate each item's failure, emit diagnostics, and continue with the next queued item.
6. Treat post-hand-off caller cancellation as non-retracting; honour explicitly cancellable submissions until
   playback hand-off.
7. Settle the queue and active pipeline safely during node teardown.
8. Open the speaking window at first FIFO admission and keep it open continuously across queued items.
9. Close the window at playback completion of the last queued item, on item failure, on effective cancellation, and
   during node teardown.

## PlayerVoice Behaviour

1. Subscribe to the configured transcriber's completion event in `_Ready()` and unsubscribe in `_ExitTree()`.
2. Ignore null, empty, or whitespace-only transcript text.
3. Forward nonblank transcript text through `Speak(string speech)`.
4. Do not duplicate subscriptions or retain handlers after leaving the tree.
5. Subscribe to the transcriber's `RecordingStarted` signal (SPCH-003) to open the speaking window when recording
   begins.
6. Close the speaking window at the post-generation broadcast, on transcription failure or blank transcript, and
   during teardown.

## Acceptance Criteria

1. Valid AI speech is heard with synchronised lip-sync and is attributable to the voice's stable `Id` and `Origin`.
2. `IVoice` exposes non-result `ValueTask SpeakAsync(...)` and compatibility `void Speak(...)`; no dispatch-result or
   busy-rejection contract remains.
3. Tests verify blank speech throws `ArgumentException`, while disabled or unconfigured speech throws
   `InvalidOperationException` before admission.
4. Tests verify cancellation before admission admits no work and surfaces as cancellation, while cancellation after
   admission does not retract the item once playback hand-off has occurred; explicit pre-hand-off cancellation aborts
   silently.
5. Tests verify second and third busy submissions are admitted, processed FIFO, and never create concurrent generation
   pipelines.
6. Tests verify an ordinary submission's `SpeakAsync` completes at admission without awaiting generation, playback
   hand-off, or playback, while the explicitly cancellable submission completes at playback hand-off (TR-25).
7. Tests verify one item's generation, conversion, preparation, or hand-off failure logs and emits `SpeechFailed`, does
   not notify listeners, and does not block the next item.
8. Tests verify compatibility `Speak` performs available synchronous validation and observes every asynchronous fault.
9. Tests verify teardown settles active and queued work without later access to freed nodes or misleading failure
   diagnostics.
10. Tests verify `IHearing.ReceiveVoice(string, IVoice)` notification occurs only after successful playback hand-off and
    ignores grouped non-hearing nodes.
11. Tests verify audio supplied to lip-sync is PCM 16-bit, 16 kHz, mono WAV and `AIVoice` does not resample it.
12. Tests verify `PlayerVoice` forwards one nonblank transcription, ignores blank transcription, honours `Enabled`, and
    disconnects on exit.
13. Generic installation replaces each valid lower-`snake_case` character-owned voice placeholder ID with the final
    exact `Character.Id` after target-scene precedence, validates `voice:<character-id>` before identity exposure, and
    supports configured attribution without claiming authenticated provenance or rejecting a source that presents the
    same ID.
14. Acceptance verifies both user-visible FIFO speech and failure isolation and the validation, admission,
    serialisation, cancellation, listener, and node-lifetime contracts.
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
    on failure, on effective cancellation, and at teardown; `PlayerVoice` opens at `RecordingStarted` and closes at
    the post-generation broadcast, on transcription failure, on blank transcript, and at teardown.
19. Tests verify the explicitly cancellable path: pre-hand-off cancellation aborts silently with no `SpeechFailed`, no
    `IHearing` broadcast, no listener notification, and a closed window; post-hand-off cancellation never retracts;
    ordinary submissions keep admission-only semantics.
20. Tests verify `PlayerVoice` subscribes to `Transcriber.RecordingStarted` (SPCH-003) once in `_Ready()`,
    unsubscribes in `_ExitTree()`, and opens and closes its speaking window accordingly.
21. Tests verify the actor-stamped self-action observation commits at playback hand-off rather than at admission,
    consistent with AI-001's ingestion contract.
22. Acceptance verifies both user-visible activity and turn-taking behaviour and the technical activity, ordering,
    window-boundary, cancellation, and observation-timing contracts.

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
- [CORE-002: Configuration API](../../core/002-configuration-api/index.md)
- [CORE-007: Microsoft Logging Integration](../../core/007-microsoft-logging-integration/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
