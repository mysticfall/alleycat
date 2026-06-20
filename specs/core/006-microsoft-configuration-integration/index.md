---
id: CORE-006
title: Microsoft Configuration Integration
---

# Microsoft Configuration Integration

## Requirement

Provide project-wide configuration through `Microsoft.Extensions.Configuration`, with shipped defaults and optional
per-user overrides available through dependency injection before gameplay services are constructed.

## Goal

Make configuration predictable and shared across subsystems without making core depend on concrete subsystem APIs.

## User Requirements

1. Developers can configure STT, TTS, and AI backends from a shipped default JSON file.
2. Users can override selected settings from their user data directory without editing shipped defaults.
3. Missing user overrides do not prevent the game from starting with default settings.
4. Subsystems can bind their own typed configuration without changing player-visible behaviour.

## Technical Requirements

1. `Game` registers `IConfiguration` before building the global service provider.
2. The default configuration source is `res://AlleyCat.json`.
3. The optional user override source is `user://AlleyCat.json`.
4. User override values take precedence over shipped defaults using standard Microsoft configuration merge semantics.
5. `Game` registers core configuration and logging infrastructure before dependent services are built.
6. Core registration must not bind or reference subsystem option models such as `AIOptions`, `STTOptions`, or
   `TTSOptions`.
7. Subsystems bind/read their own option sections from core-provided `IConfiguration`.
8. Subsystems that need explicit custom JSON paths build a local Microsoft JSON configuration source.
9. Configuration integration lives in `AlleyCat.Core.Configuration` and is resolved through the DI container.
10. Configuration registration must happen before services that consume configuration are registered or resolved.
11. The shared configuration helper is `GameConfiguration`; `AlleyCatConfiguration` is not a supported name.

## In Scope

- `Microsoft.Extensions.Configuration` registration and source ordering.
- Shipped JSON defaults and optional user JSON overrides.
- Core/subsystem dependency direction for option ownership and binding.
- Subsystem-owned explicit custom-path JSON loading.
- DI availability for gameplay systems and tests.

## Out Of Scope

- Runtime hot-reload of configuration values.
- In-game configuration editing UI.
- Remote or cloud-hosted configuration.
- Encryption or secret storage policy beyond reading configured values.
- Core-owned binding or validation for subsystem-specific option models.
- Backward compatibility for removed legacy configuration bridge APIs.

## Acceptance Criteria

1. With only `res://AlleyCat.json`, the game starts and `IConfiguration` exposes shipped defaults.
2. With `user://AlleyCat.json` present, matching values override shipped defaults while unspecified defaults remain.
3. `Game` registers `IConfiguration` and logging infrastructure before building the service provider.
4. Core registration does not reference `AIOptions`, `STTOptions`, `TTSOptions`, or subsystem binding APIs.
5. Subsystems bind/read their own option sections from `IConfiguration` or build local custom-path JSON configuration.
6. `Out Of Scope` defers only optional extensions and does not exclude startup registration or ownership boundaries.
7. Core configuration helper references use `GameConfiguration`, not `AlleyCatConfiguration`.

**Traceability Map:** User Requirements 1-4 -> AC-1, AC-2, AC-5; Technical Requirements 1-11 -> AC-3, AC-4, AC-6,
AC-7.

## References

### Implementation

- `@game/src/Core/Configuration/`
- `@game/src/Game.cs`
- `@game/AlleyCat.json`

### Related Specs

- [CORE-002: Configuration API](../002-configuration-api/index.md)
- [CORE-004: Global Service Resolution](../004-global-service-resolution/index.md)
- [SPCH-003: Transcriber Component](../../speech/003-transcription/index.md)
- [SPCH-004: Speech Generator Component](../../speech/004-speech-generation/index.md)
- [AI-001: Mind Component](../../ai/001-mind/index.md)

### External

- [Microsoft.Extensions.Configuration][configuration]

[configuration]: https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.configuration
