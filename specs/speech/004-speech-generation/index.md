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
6. Speech-generation consumers must be able to observe streamed audio chunks as
   soon as the backend provides them, reducing perceived TTS latency where a
   consumer can play or buffer partial audio safely.
7. Developers can observe backend generation latency as opt-in pipeline
   diagnostics without affecting generation behaviour.
8. A character's queued utterances are generated one at a time in request
   order; a busy queue waits rather than rejects (orchestrated per voice by
   SPCH-005).
9. One character's speech generation never blocks another's: distinct
   characters' voices may generate concurrently.
10. Flushing or failing one character's speech work affects only that
    character's pending queue; other characters' pending speech is untouched.

## Technical Requirements

1. An abstract SpeechGenerator class must be defined as a Node or Node3D subclass.
2. A Generate(string text, string? instruction = null) method must be defined as
   async Task<byte[]>, preserving the existing full-response contract. Backends
   that do not support instruction must silently ignore it.
3. A streaming generation API must allow an asynchronous chunk callback while
   still returning the full generated audio bytes on completion.
4. A Godot signal SpeechGenerationCompleted(byte[] audio) must be emitted on success.
5. A Godot signal SpeechGenerationChunkReceived(byte[] audioChunk) must be
   emitted for backend-provided audio chunks when the dispatch path uses a
   streaming-capable backend.
6. Generated audio must be raw backend output on both delivery paths: streamed
   chunks and the `SpeechGenerationCompleted` payload. The generator must not
   parse, resample, or otherwise normalise audio; consumers requiring normalised
   input (for example lip-sync inference, SPCH-001/SPCH-002) own that conversion.
7. An exported Enabled property must control whether generation is permitted.
8. On generation failure, errors must be logged via `ILogger` and a failure signal emitted.
9. The concrete OpenAISpeechGenerator implementation must use the official OpenAI
   .NET SDK and its streaming speech API when dispatching speech generation.
10. Godot signals and hooks for streamed chunks, completion, and failure must be
    dispatched on the Godot thread through the deferred action pattern.
11. Enabled and single in-flight generation behaviour must apply to the streaming
    dispatch path as well as the full-response path. The single in-flight guard
    is per generator instance and therefore per owning voice queue (SPCH-005):
    at most one generation request runs at a time within one production queue,
    while distinct voice-owned generators may run concurrently and must never
    block one another.
12. Configuration must bind/read subsystem-owned TTS options from CORE-006 `IConfiguration`, or build a local
    custom-path YAML configuration when an explicit path is supplied. Options include Host (full endpoint URL), ApiKey
    (optional API key), and additional API-supported properties.
13. Implementation must be under game/src/Speech/Generation/.
14. Integration tests must be under integration-tests/src/.
15. Backend latency must be recorded through the shared pipeline diagnostic log (CORE-007) as log-only Trace latency
    entries for backend return and stream completion under the `AlleyCat.Pipeline` category — opt-in console
    diagnostics without notification eligibility. These diagnostics must not change generation behaviour.
16. Per-voice FIFO ownership, one-at-a-time generation, and flush boundaries are defined at the orchestrating voice
    layer (SPCH-005). The `SpeechGenerator` backend API gains no queueing, ownership, or flush surface; generation
    remains one in-flight per production queue (TR-11).
17. A failed generation settles only its own request — logged failure plus the failure signal — and must not block or
    fail the owning queue's later items (SPCH-005 failure isolation).

## In Scope

- Abstract SpeechGenerator class with Enabled property.
- Async Generate method returning full-response byte[].
- Signal contracts for completion and failure.
- Streaming chunk API and signal contract.
- Error handling using `ILogger`.
- OpenAISpeechGenerator using OpenAI .NET SDK.
- Subsystem-owned configuration from CORE-006 `IConfiguration` or explicit custom-path YAML loading.
- Raw backend audio output contract for streamed chunks and completion.
- Backend latency diagnostics through the shared pipeline diagnostic log (CORE-007).
- Per-production-queue single-in-flight generation with concurrency across distinct voice-owned queues.
- Failure isolation between queued generation items.

## Out Of Scope

- Speech-to-text (STT) or transcription.
- Real-time streaming audio playback beyond exposing backend chunk events.
- Multiple simultaneous generation sessions within one production queue; distinct voice-owned queues may generate
  concurrently (TR-11).
- Local-only TTS without network.
- Non-OpenAI-compatible backend implementations beyond OpenAISpeechGenerator.
- Audio preprocessing beyond API parameters.
- Compressed format transcoding (WAV PCM only).

## Acceptance Criteria

1. The spec defines both user-visible speech generation outcomes and technical
   implementation contracts.
2. The abstract SpeechGenerator class defines Enabled, Generate, streaming chunk
   API, and signal contracts.
3. Error handling uses `ILogger` for raw diagnostics.
4. OpenAISpeechGenerator uses the official OpenAI .NET SDK and owns its TTS option binding/loading.
5. Runtime integration boundaries (config, signals, lifecycle) are explicitly
   defined.
6. Implementation path and test path are specified.
7. The spec does not exclude mandatory delivery contracts through Out Of Scope.
8. Streamed chunk delivery and completion audio are covered as a raw
   backend-output contract, with no generator-side parsing, resampling, or
   sample-rate normalisation.
9. OpenAISpeechGenerator dispatch uses the OpenAI-compatible streaming speech API
   while preserving Enabled and per-queue single in-flight behaviour (TR-11).
10. Tests verify backend latency diagnostics route through the shared pipeline diagnostic log as log-only Trace
      entries without notification eligibility and without changing generation behaviour.
11. Tests verify generation runs one request at a time per voice-owned queue in FIFO order, while distinct voice-owned
      generators run concurrently without mutual blocking.
12. Tests verify one item's generation failure settles only that item — logged with its failure signal — and the
      queue's later items still generate.
13. Acceptance confirms per-voice FIFO ownership and flush boundaries stay at the voice layer (SPCH-005) with no
      `SpeechGenerator` backend API change for queueing, ownership, or flush.

## References

### Implementation

- game/src/Speech/Generation/SpeechGenerator.cs
- game/src/Speech/Generation/OpenAISpeechGenerator.cs
- game/AlleyCat.yaml

### Related Specs

- SPCH-001: Wav2Arkit LipSync Player
- SPCH-002: Audio2Face LipSync Player
- SPCH-003: Transcriber Component
- SPCH-005: Voice Component
- CORE-006: Microsoft Configuration Integration
- CORE-007: Microsoft Logging Integration

### External Dependencies

- OpenAI .NET SDK (NuGet package)
- Godot XR Tools or native XR input API
