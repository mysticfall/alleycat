---
id: SPCH-004
title: Speech Generator Component
---

# Speech Generator Component

## Requirement

The system must provide text-to-speech capabilities via an abstract SpeechGenerator
that can be extended to different TTS backends.

## Goal

Enable text-to-speech capabilities in the AlleyCat VR experience via an abstract
SpeechGenerator with an OpenAI-compatible implementation as the initial backend.

## User Requirements

1. Players must hear AI-generated speech output when dialog is triggered.
2. Speech generation must support an optional instruction parameter for future
   backend extensibility.
3. Failures must be logged and handled gracefully without crashing.
4. The system must support OpenAI-compatible API endpoints.
5. An Enabled property must allow TTS to be toggled at runtime.
6. A TargetSampleRate property must allow output sample rate configuration for
   downstream consumers such as lip-sync compatibility.

## Technical Requirements

1. An abstract SpeechGenerator class must be defined as a Node or Node3D subclass.
2. An abstract method Generate(string text, string? instruction = null) must be
   defined as async Task<byte[]>, returning raw audio bytes. Backends that do not
   support instruction must silently ignore it.
3. A Godot signal SpeechGenerationCompleted(byte[] audio) must be emitted on success.
4. An exported Enabled property must control whether generation is permitted.
5. An exported TargetSampleRate property must allow configuration of output sample
   rate. When greater than 0, audio must be resampled before emission. Default: 0
   (no resampling). Recommended: 16000.
6. On generation failure, errors must be logged via GD.PushError and a failure
   signal emitted.
7. On resampling failure, errors must be logged and the failure signal emitted
   instead of completion.
8. The concrete OpenAISpeechGenerator implementation must use the official OpenAI
   .NET SDK.
9. Configuration must be loaded from the merged configuration API. The [TTS]
   section must contain Host (full endpoint URL), ApiKey (optional API key), and
   additional API-supported properties.
10. Implementation must be under game/src/Speech/Generation/.
11. Integration tests must be under integration-tests/src/.

## In Scope

- Abstract SpeechGenerator class with Enabled property.
- Async abstract Generate method returning byte[].
- Signal contracts for completion and failure.
- Error handling using GD.PushError.
- OpenAISpeechGenerator using OpenAI .NET SDK.
- Configuration from merged API.
- Audio resampling via TargetSampleRate property.

## Out Of Scope

- Speech-to-text (STT) or transcription.
- Real-time streaming audio playback.
- Multiple simultaneous generation sessions.
- Local-only TTS without network.
- Non-OpenAI-compatible backend implementations beyond OpenAISpeechGenerator.
- Audio preprocessing beyond API parameters.
- Compressed format transcoding (WAV PCM only).

## Acceptance Criteria

1. The spec defines both user-visible speech generation outcomes and technical
   implementation contracts.
2. The abstract SpeechGenerator class defines Enabled, Generate method, and signal
   contracts.
3. Error handling uses GD.PushError for raw errors.
4. OpenAISpeechGenerator uses the official OpenAI .NET SDK and loads configuration
   from the merged configuration API.
5. Runtime integration boundaries (config, signals, lifecycle) are explicitly
   defined.
6. Implementation path and test path are specified.
7. The spec does not exclude mandatory delivery contracts through Out Of Scope.
8. The resample feature is defined with TargetSampleRate, resampling applies before
   completion signal, and failure handling is distinct.

## References

### Implementation

- game/src/Speech/Generation/SpeechGenerator.cs
- game/src/Speech/Generation/OpenAISpeechGenerator.cs
- game/AlleyCat.cfg

### Related Specs

- SPCH-001: Wav2Arkit LipSync Player
- SPCH-002: Audio2Face LipSync Player
- SPCH-003: Transcriber Component
- SPCH-005: Speech Voice Component
- CORE-002: Configuration API

### External Dependencies

- OpenAI .NET SDK (NuGet package)
- Godot XR Tools or native XR input API