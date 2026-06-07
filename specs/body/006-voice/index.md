---
id: BODY-006
title: Voice Component
---

# Voice Component

## Requirement

Provide an abstract `Voice` component that represents an identifiable 3D speech origin, concrete `AIVoice` and
`PlayerVoice` implementations, and listener dispatch for systems that consume generated voice events.

## Goal

Enable character and player speech events that systems can identify and locate in the AlleyCat VR experience, matching
the real-world expectation that a voice belongs to a speaker and originates from a 3D position near that speaker.

## User Requirements

1. Players must hear AI-generated speech output when speech text is triggered in the game.
2. Lip-sync animation must play in synchronisation with the audio playback.
3. The system must allow runtime toggling of voice output via an `Enabled` property.
4. Speech failures must be logged and handled gracefully without crashing or desynchronising playback.
5. A manual test scene must allow testers to type arbitrary speech text and observe character speech output.
6. Speech events must be attributable to a stable voice `Id` so listeners can identify which voice produced them.
7. Speech must expose an `Origin` so listeners and tools can resolve its world-space location.
8. Systems that listen for generated voice events must receive the spoken speech and source voice.
9. Completed player transcription must trigger player voice output when the transcript contains spoken text.
10. Empty or whitespace-only transcription results must be ignored so silence does not produce speech events.

## Technical Requirements

1. An abstract `[GlobalClass]` `Voice` class must be defined as a `Node3D` subclass in the
   `AlleyCat.Body.Voice` namespace at `@game/src/Body/Voice/Voice.cs`.
2. `IVoice` must define stable identity, location, and output capability through `string Id { get; }`,
   `Vector3 Origin { get; }`, and `void Speak(string speech)`.
3. `Voice` must implement `IVoice`, expose `abstract void Speak(string speech)`, and resolve `Origin` from its
   world-space `GlobalPosition`.
4. `Voice` must expose exported `Id` and `Enabled` properties.
5. `Voice` must keep the `SpeechFailed(string error)` signal and deferred Godot action dispatcher helpers.
6. `Voice` must define a protected virtual post-generation hook that receives the generated speech.
7. The post-generation hook must query nodes in `IVoiceListener.GroupName`, filter `IVoiceListener` implementations,
   and invoke them with the speech and source `IVoice`.
8. `IVoiceListener.GroupName` must be the global Godot listener group constant `"voice_listeners"`.
9. `Voice` may expose protected helper behaviour for applying `Enabled` before the post-generation hook, but runtime
   control state such as `Enabled` must remain on `Voice` rather than the `IVoice` capability contract.
10. `IHasVoice` must follow the component-holder trait pattern and expose `TryGetVoice(out IVoice? voice)` and
    `RequireVoice()` helpers over `IComponentHolder`.
11. The concrete `AIVoice` implementation must:
   - Accept configured `SpeechGenerator` and `LipSyncPlayer` nodes via exports.
   - Generate audio via `SpeechGenerator.Generate()`.
   - Convert generated `byte[]` to an `AudioStreamWav` compatible with `LipSyncPlayer.Play(AudioStreamWav)`.
   - Invoke `LipSyncPlayer.Play(AudioStreamWav)` as the single playback initiation point.
   - Invoke the post-generation hook only after successful asynchronous generation, conversion, and playback handoff.
12. Generated audio must be convertible to PCM 16-bit, 16 kHz, mono WAV format for `LipSyncPlayer.Play()`.
13. If conversion fails or backend output is incompatible, `AIVoice` must log an error and emit `SpeechFailed`.
14. `SpeechGenerator` owns sample-rate normalisation when `TargetSampleRate` is configured; `AIVoice` must not resample.
15. All playback timing is governed by `LipSyncPlayer.Play()`.
16. The concrete `PlayerVoice` implementation must:
    - Extend `Voice` as a `[GlobalClass]` in the `AlleyCat.Body.Voice` namespace.
    - Accept a configured `AlleyCat.Speech.Transcription.Transcriber` node via exported direct reference.
    - Subscribe to `Transcriber.TranscriptionCompleted` during `_Ready()` and unsubscribe during `_ExitTree()`.
    - Avoid duplicate subscriptions and remove event handlers when leaving the tree.
    - Forward only non-empty, non-whitespace transcript text through `Speak(string speech)`.
    - Honour the inherited `Enabled` contract in its speech path.
17. The manual voice test scene must use `@game/assets/characters/reference/female/reference_female.tscn`, place the
    `AIVoice` node under `Character/Female_export/GeneralSkeleton/Head`, and keep playback audio spatially attached to
    that voice origin.

## In Scope

- Abstract `Voice` class definition with `Id`, `Enabled`, `Speak(string speech)`, and post-generation hook contracts.
- `IVoice` as the voice capability interface with `Id`, `Origin`, and `Speak(string speech)`.
- `IHasVoice` as the component-holder trait for resolving a composed voice capability.
- Concrete `AIVoice` implementation coordinating `SpeechGenerator` and `LipSyncPlayer`.
- Concrete `PlayerVoice` implementation coordinating `Transcriber` completion with inherited voice output.
- Locatable `Node3D` voice origins suitable for spatial listening and tooling.
- `IVoiceListener` notifications through the `voice_listeners` Godot group.
- Audio compatibility contract for `AudioStreamWav` (PCM 16-bit, 16 kHz, mono).
- Synchronisation contract via `LipSyncPlayer.Play()` initiation path.
- Signal contract for generation/conversion failures.
- Error handling contract using `GD.PushError` for raw errors.
- Implementation under `@game/src/Body/Voice/` using the `AlleyCat.Body.Voice` namespace.
- Manual test scene under `@game/tests/body/voice/voice_test.tscn`.
- Unit and integration tests using mocks/fakes for voice, transcription, generation, and lip-sync dependencies.

## Out Of Scope

- Visual verification or runtime XR testing (requires backend access).
- New live microphone capture or real-time streaming input beyond the existing `Transcriber` dependency.
- Multiple simultaneous speech sessions.
- Additional speech-generation implementations beyond the required `AIVoice` and `PlayerVoice` classes.
- Audio preprocessing or post-processing beyond conversion to WAV format.
- Character animation beyond lip-sync via `LipSyncPlayer`.
- Playback completion notification after audio finishes.

## Voice Contract

| Member | Type | Description |
|--------|------|-------------|
| `Id` | `string` | Stable voice identifier. Default: empty string. |
| `Enabled` | `bool` | Controls whether speech is permitted. Default: `true`. |
| `SpeechFailed(string error)` | Signal | Emitted when generation or conversion fails. |
| `Origin` | `Vector3` | World-space origin matching the voice node `GlobalPosition`. |
| `Speak(string speech)` | `void` | Abstract initiator matching `IVoice.Speak(string speech)`. |
| Post-generation hook | Protected virtual method | Notifies grouped listeners after speech handoff. |

## Component Capability Contract

| Member | Type | Description |
|--------|------|-------------|
| `IVoice.Id` | `string` | Stable identifier used to attribute generated voice events to a speaker. |
| `IVoice.Origin` | `Vector3` | World-space position where the voice originates. |
| `IVoice.Speak(string speech)` | `void` | Initiates speech output for the supplied speech text. |
| `IVoiceListener.GroupName` | `string` | Global Godot group constant: `"voice_listeners"`. |
| `IVoiceListener.ReceiveVoice(...)` | `void` | Receives speech and the source `IVoice`. |
| `IHasVoice.TryGetVoice(out IVoice? voice)` | `bool` | Resolves exactly one voice from a component holder. |
| `IHasVoice.RequireVoice()` | `IVoice` | Resolves exactly one voice or throws when unavailable. |

## AIVoice Implementation Contract

| Member | Type | Description |
|--------|------|-------------|
| `SpeechGenerator` | `SpeechGenerator (Node)` | Export - the speech generation component. |
| `LipSyncPlayer` | `LipSyncPlayer (Node)` | Export - the lip-sync playback component. |

### Behaviour

1. If `Enabled` is `false`, `Speak()` returns early without error.
2. Call `SpeechGenerator.Generate(speech)` to obtain `byte[]` audio data.
3. Convert `byte[]` to `AudioStreamWav` with PCM 16-bit, 16 kHz, mono format.
4. Invoke `LipSyncPlayer.Play(audioStream)` as one coordinated playback and lip-sync boundary.
5. Invoke the inherited hook after successful playback handoff only, passing the speech to listeners.
6. On generation, conversion, or missing dependency failure, log via `GD.PushError` and emit `SpeechFailed`.
7. The spec does not require notification after audio playback finishes.

## PlayerVoice Implementation Contract

| Member | Type | Description |
|--------|------|-------------|
| `Transcriber` | `Transcriber (Node)` | Export - the player transcription component. |

### Behaviour

1. Subscribe to the configured transcriber's completion event in `_Ready()` and unsubscribe in `_ExitTree()`.
2. Ignore `null`, empty, or whitespace-only transcript text.
3. Forward accepted transcript text through `Speak(string speech)`.
4. Do not create duplicate event subscriptions or keep handlers attached after the voice leaves the tree.

## Audio Compatibility Contract

The `AIVoice` implementation must ensure audio passed to `LipSyncPlayer.Play(AudioStreamWav)` meets the
`LipSyncPlayer` requirements:

- Format: PCM 16-bit, 16 kHz, mono WAV.
- If `SpeechGenerator` returns audio in a different format, `AIVoice` must convert it before playback handoff.
- If conversion is not possible, log the raw conversion error, emit `SpeechFailed("Audio format incompatible")`, and do
  not attempt playback.

## Synchronisation And Spatial Origin Contract

- `AIVoice` must not independently control audio timing.
- All timing is derived from `LipSyncPlayer.Play()`.
- The voice node is the spatial source for identifying and locating speech.
- `IVoice.Origin` is the voice node world-space position.
- Listener notification searches the `voice_listeners` group, ignores non-listener nodes, and sends the speech and
  source.
- In the manual test scene, the voice node and its spatial audio player are children of the reference character's
  `Head` bone attachment.
- If `LipSyncPlayer` is not configured or is `null`, `Speak()` must fail gracefully.

## Acceptance Criteria

1. The spec defines both user-visible speech output with lip-sync and technical implementation contracts.
2. `Voice` is an abstract `[GlobalClass] Node3D` with exported `Id` and `Enabled` properties.
3. `IVoice` contains `string Id { get; }`, `Vector3 Origin { get; }`, and `void Speak(string speech)`.
4. `IHasVoice` exposes component-holder helpers for resolving a single `IVoice`.
5. `IVoiceListener` defines the `voice_listeners` group constant and receives speech with the source `IVoice`.
6. `AIVoice` invokes the inherited post-generation hook only after successful asynchronous generation, conversion, and
   playback handoff.
7. Voice origin location is defined by the `Voice` node transform, and the manual test `AIVoice` is attached under the
   reference character `Head` bone attachment.
8. Listener dispatch ignores grouped nodes that do not implement `IVoiceListener`.
9. Audio compatibility contract is explicitly defined: generated audio must be convertible to PCM 16-bit, 16 kHz, mono
   WAV for `LipSyncPlayer.Play(AudioStreamWav)`.
10. Synchronisation contract is explicitly defined: all playback timing is governed by `LipSyncPlayer.Play()`.
11. Error handling contract uses `GD.PushError` for raw errors and emits `SpeechFailed` signal.
12. Manual test scene using `@game/assets/characters/reference/female/reference_female.tscn` is specified.
13. Visual verification/testing is explicitly out of scope.
14. Implementation path `@game/src/Body/Voice/`, namespace `AlleyCat.Body.Voice`, and test path
    `@game/tests/body/voice/` are specified.
15. The spec does not exclude any mandatory delivery contracts through `Out Of Scope`.
16. Normalisation responsibility is explicitly defined: `AIVoice` must not independently resample audio.
17. `PlayerVoice` is a `Voice`/`Node3D` that binds to a `Transcriber` by exported direct reference.
18. `PlayerVoice` invokes the inherited speech path once for a non-empty transcription completion, ignores empty or
    whitespace completions, honours `Enabled`, and disconnects from the transcriber on exit.

## References

### Implementation

- `@game/src/Body/Voice/IVoice.cs` - Voice capability interface
- `@game/src/Body/Voice/IHasVoice.cs` - Voice component-holder trait
- `@game/src/Body/Voice/IVoiceListener.cs` - Voice listener notification contract
- `@game/src/Body/Voice/Voice.cs` - Abstract Voice class
- `@game/src/Body/Voice/AIVoice.cs` - AIVoice implementation
- `@game/src/Body/Voice/PlayerVoice.cs` - PlayerVoice implementation
- `@game/src/Speech/Transcription/Transcriber.cs` - Transcription completion source
- `@game/tests/body/voice/voice_test.tscn` - Manual test scene

### Related Specs

- [SPCH-001: Wav2Arkit LipSync Player](../../speech/001-wav2arkit-lipsync-player/index.md)
- [SPCH-002: Audio2Face LipSync Player](../../speech/002-audio2face-lipsync-player/index.md)
- [SPCH-004: Speech Generator Component](../../speech/004-speech-generation/index.md)
- [CORE-002: Configuration API](../../core/002-configuration-api/index.md)
