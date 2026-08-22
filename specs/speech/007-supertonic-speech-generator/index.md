---
id: SPCH-007
title: Supertonic Speech Generator Component
---

# Supertonic Speech Generator Component

## Requirement

The system must provide a fully offline text-to-speech backend as a new
SupertonicSpeechGenerator component extending the abstract SpeechGenerator
contract (SPCH-004), performing local ONNX inference with no network transport
and returning valid native 44.1 kHz PCM16 mono WAV output without generator-side
resampling.

## Goal

Deliver local speech synthesis for the AlleyCat VR experience using the
MIT-licensed Supertonic ONNX pipeline, so generated dialogue works without
network connectivity or API keys while inheriting the shared generation
lifecycle (Enabled gating, single in-flight behaviour, and failure signalling).
The generator preserves native 44.1 kHz output; lip-sync adapts only its
inference input where required.

## User Requirements

1. Players hear audible, correctly lengthed speech when dialogue is triggered,
   synthesised entirely locally with no network connection. Playback retains the
   generator's native 44.1 kHz output rather than a lip-sync inference rate.
2. Players receive complete utterances only; no partial audio chunks are played
   or delivered mid-generation.
3. Users can select among the preset voice styles (F1-F5/M1-M5); selecting an
   unknown or missing voice still produces speech by falling back to the
   default style rather than failing generation.
4. Failures must be logged and handled gracefully without crashing the game.

## Technical Requirements

1. A `[GlobalClass]` SupertonicSpeechGenerator must extend the abstract
   SpeechGenerator contract (game/src/Speech/Generation/SpeechGenerator.cs).
   Synthesis is whole-utterance only; the default non-streaming
   GenerateStreamingCore fallback is inherited and emits no partial chunks.
2. Inference must run locally via Microsoft.ML.OnnxRuntime.Gpu 1.29.0 (a
   superset package bundling the CUDA and CPU execution providers), which is
   referenced by game/AlleyCat.csproj. No network transport and no API keys
   are involved.
3. Inference must support a configurable execution backend with an incremental
   CUDA → CPU fallback chain:
   - The generator exports `ExecutionBackend`, an enum typed
     `SupertonicExecutionBackend { Cuda, Cpu }`, defaulting to `Cuda`.
     Configuration uses exported properties only, with no CORE-006 `"TTS"`
     binding.
   - When `Cuda` is requested, the runtime attempts to append the CUDA
     execution provider and initialise the inference sessions; on any failure
     (missing native libraries, initialisation error) it rebuilds CPU-only
     sessions and continues. Availability is determined by this append and
     session-initialisation attempt, not by inspecting the provider list,
     because the GPU package lists CUDA even when its native libraries cannot
     load.
   - On fallback exactly one warning is logged via `ILogger`, naming the
     failure reason and the requested backend. The active backend is recorded
     once at first generation as a log-only PipelineDebugLog Trace entry under
     AlleyCat.Pipeline, alongside the latency trace.
   - Implementation: a new
     game/src/Speech/Generation/Supertonic/SupertonicExecutionBackend.cs
     hosts the enum, a Godot-free session-options factory (retaining the
     ORT_ENABLE_ALL and ORT_SEQUENTIAL options), and the fallback chain.
     SupertonicInferencePipeline.Create gains a backend parameter, and
     SupertonicSpeechGenerator passes the export through at lazy pipeline
     init. Session creation must be injected into the factory so unit tests
     can stub failures.
4. The synthesis pipeline must be ported/adapted from the official MIT-licensed
   C# reference (github.com/supertone-inc/supertonic, csharp/ExampleONNX.cs +
   csharp/Helper.cs): unicode tokenisation via unicode_indexer.json →
   duration_predictor.onnx → text_encoder.onnx → flow-matching sampling loop
   over vector_estimator.onnx → vocoder.onnx.
5. Model assets must live under game/models/supertonic-3/: ONNX graphs and
   tts.json under onnx/, and preset voice styles F1-F5/M1-M5 as JSON files
   under voice_styles/.
6. Configuration must use exported properties only, with no CORE-006 `"TTS"`
   configuration binding:
   - ModelDirectoryPath (default res://models/supertonic-3/onnx)
   - VoiceStylesDirectoryPath (default res://models/supertonic-3/voice_styles)
   - Voice (default "M1")
   - VoiceOverride (empty by default)
   - Language (default "en")
   - SpeedRatio (default 1.0; supported range approximately 0.7-2.0)
   - QualitySteps (flow-matching steps; default 8; tuning range approximately
     5-12)
   - ExecutionBackend (default Cuda)
7. Effective voice resolution order must be VoiceOverride > Voice > "M1". The
   effective name resolves to {VoiceStylesDirectoryPath}/{name}.json; an
   unknown or missing style name logs a warning via `ILogger` and falls back to
   "M1", with generation still succeeding.
8. res:// paths must be resolved to physical locations at runtime via
   ProjectSettings.GlobalizePath(), following the Wav2ArkitLipSyncPlayer
   precedent (SPCH-001).
9. Output contract: the vocoder yields mono float32 samples at 44100 Hz; the
   generator wraps them into native 16-bit PCM mono RIFF/WAVE bytes at 44100 Hz
   and performs no sample-rate conversion. Per SPCH-004, the completed audio is
   raw backend output. Per SPCH-005, AIVoice retains that source rate for
   playback. LipSyncPlayer preserves the original playback stream and resamples
   only its backend inference copy to its declared BackendSampleRate; for
   A2FLipSyncPlayer, BackendSampleRate is 16000 Hz (SPCH-002). Templates and
   their tests author and assert that rate on the lip-sync player, never on the
   generator.
10. Threading: inference runs off the Godot thread inside GenerateCore
    (async); completion/failure Godot signals are marshalled onto the Godot
    thread through the base-class deferred action pattern; Enabled gating and
    single in-flight behaviour are inherited from the base class.
11. The instruction parameter is silently ignored, as permitted by the SPCH-004
    backend contract.
12. Backend latency must be recorded through PipelineDebugLog (CORE-007) as
    log-only Trace entries under the AlleyCat.Pipeline category, mirroring
    OpenAISpeechGenerator. These diagnostics must not change behaviour.
13. Implementation must be under game/src/Speech/Generation/Supertonic/; unit
    tests under tests/src/ (Godot-free); integration tests under
    integration-tests/src/.
14. Errors must be handled via `ILogger`; failures log a diagnostic and emit
    the failure signal without crashing.

## In Scope

- SupertonicSpeechGenerator as a `[GlobalClass]` SpeechGenerator subclass with
  whole-utterance local ONNX synthesis.
- The five-stage pipeline: unicode tokenisation → duration predictor →
  text encoder → flow-matching vector estimator → vocoder.
- Exported properties for model/voice-style directories, voice selection,
  language, speed ratio, and flow-matching quality steps.
- Configurable execution backend selection (ExecutionBackend export, default
  Cuda) with incremental CUDA → CPU fallback and fallback diagnostics.
- Voice style mapping with warn-and-fallback to "M1".
- Native PCM16 mono WAV output at 44100 Hz, with no generator-side resampling.
- LipSyncPlayer inference adaptation that preserves the native playback stream
  and resamples only its backend inference copy to BackendSampleRate (16000 Hz
  for A2FLipSyncPlayer); templates and their tests author and assert that rate
  on the lip-sync player, never on the generator.
- Off-thread inference with inherited signal marshalling, Enabled gating, and
  single in-flight behaviour.
- Latency diagnostics through PipelineDebugLog as log-only Trace entries.
- Error handling via `ILogger` with failure signalling.
- Model and voice-style asset layout under game/models/supertonic-3/.
- Unit tests (Godot-free) and integration test coverage at the specified paths.

## Out Of Scope

- Chunked/streaming synthesis (whole-utterance delivery only).
- Expression tags such as `<laugh>`.
- Per-character languages or automatic language detection.
- Generator-side sample-rate conversion or changes to lip-sync resampling
  quality. The required integration contract remains: the generator returns
  native 44100 Hz WAV output, while LipSyncPlayer adapts only its inference copy
  to the declared BackendSampleRate.
- A general exported-build PCK packaging solution for model assets.

Note that exported builds must ship the Supertonic model files as physical
files reachable by absolute path — a constraint shared with Wav2Arkit (SPCH-001)
— so this remains an explicit validation concern for acceptance even though a
general PCK packaging solution is deferred.

## Acceptance Criteria

1. With no network access, triggering generation produces audible speech whose
   duration matches the requested utterance text (user layer).
2. Selecting valid preset voice styles changes the rendered voice; selecting an
   unknown or missing style logs a warning and still generates speech using the
   "M1" fallback instead of failing (user layer).
3. An injected failure logs via `ILogger`, emits the failure signal, and leaves
   the generator usable without crashing the game (user layer).
4. All exported properties exist with the stated defaults (ModelDirectoryPath,
   VoiceStylesDirectoryPath, Voice, VoiceOverride, Language, SpeedRatio,
   QualitySteps, ExecutionBackend) and no CORE-006 `"TTS"` binding is present
   (technical layer).
5. res:// paths are resolved through ProjectSettings.GlobalizePath() and the
   pipeline loads graphs and style JSON from the globalised locations
   (technical layer).
6. Completed audio is native 16-bit PCM mono RIFF/WAVE at 44100 Hz, with no
   generator-side sample-rate conversion. AIVoice retains that source rate for
   playback, while LipSyncPlayer preserves the playback stream and adapts only
   its inference copy to BackendSampleRate; A2FLipSyncPlayer receives 16000 Hz
   inference audio. Templates and their tests author and assert that rate on the
   lip-sync player, never on the generator (technical layer).
7. Inference executes off the Godot thread inside GenerateCore while signals
   arrive marshalled on the Godot thread (technical layer).
8. PipelineDebugLog records log-only Trace latency entries under
   AlleyCat.Pipeline without altering generation behaviour (technical layer).
9. ExecutionBackend defaults to `Cuda` (technical layer).
10. Requesting `Cpu` builds CPU-only sessions, makes no CUDA attempt, and
    still synthesises speech successfully (technical layer).
11. Requesting `Cuda` (the default) results in active CUDA inference when CUDA
    initialisation succeeds, and otherwise falls back to CPU with exactly one
    warning; tests assert the outcome consistent with the host's actual
    capability, so generation never hard-fails merely because CUDA is
    unavailable (user and technical layers).
12. The active backend is recorded as a log-only PipelineDebugLog Trace entry
    under AlleyCat.Pipeline (technical layer).
13. Unit tests exist under tests/src/ (Godot-free) and integration tests under
    integration-tests/src/ (technical layer).
14. `dotnet format --verify-no-changes AlleyCat.sln` and
    `dotnet build AlleyCat.sln -warnaserror` pass (technical layer).

## References

### Implementation

- game/src/Speech/Generation/SpeechGenerator.cs
- game/src/Speech/Generation/OpenAISpeechGenerator.cs (pattern precedent)
- game/src/Speech/LipSync/Wav2ArkitLipSyncPlayer.cs
- game/src/Speech/Generation/Supertonic/
- game/models/supertonic-3/

### Related Specs

- SPCH-004: Speech Generator Component
- SPCH-005: Voice Component
- CORE-007: Microsoft Logging Integration
- SPCH-001: Wav2Arkit LipSync Player
- SPCH-002: Audio2Face LipSync Player

### External Dependencies

- supertone-inc/supertonic C# reference implementation (MIT licence):
  github.com/supertone-inc/supertonic
- Supertonic TTS ONNX models distributed under the OpenRAIL-M licence; the
  licence note must accompany shipped model assets
