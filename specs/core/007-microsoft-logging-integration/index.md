---
id: CORE-007
title: Microsoft Logging Integration
---

# Microsoft Logging Integration

## Requirement

Provide project-wide diagnostics through `Microsoft.Extensions.Logging`, routing normal diagnostics to Godot output and
player-facing failures to UI notifications only when appropriate.

## Goal

Replace ad-hoc `GD.*` diagnostics with structured, low-overhead logging that is available through dependency injection
and can be extended without changing gameplay consumers.

## User Requirements

1. Developers can diagnose configuration, backend, and gameplay failures through consistent structured logs.
2. Players see intentional error notifications for actionable failures without seeing raw debug output.
3. Suppressed debug or trace diagnostics do not create noticeable runtime overhead.
4. Existing player-facing error behaviour is preserved while diagnostics migrate away from direct `GD.*` calls.

## Technical Requirements

1. `Game` registers `ILoggerFactory` and logging providers before building the global service provider.
2. Consumers should receive `ILogger` or `ILogger<T>` through DI or service construction where possible.
3. New diagnostics must use `ILogger` or `ILogger<T>`, not direct `GD.Print`, `GD.PushWarning`, or `GD.PushError`.
4. Logs must use structured message templates and appropriate levels: Trace, Debug, Information, Warning, Error, or
   Critical.
5. Godot nodes or services that cannot use constructor injection must use the unified core logging resolver.
6. Required logger resolution must fail clearly when logging infrastructure is unavailable; missing loggers must not be
   silently suppressed.
7. Interpolated or expensive log-message/detail construction must be guarded with `IsEnabled` or an equivalent helper
   before constructing the value. Structured logging calls with cheap arguments do not need an extra guard.
8. The default Godot console provider routes diagnostics to Godot output.
9. The notification provider routes `Error` and `Critical` entries to UI when notification UI is available.
10. Protocol output, intentional UI notifications, and Godot API calls that are not diagnostics may remain explicit
   exceptions to the `ILogger` preference.
11. Low-priority legacy `GD.*` diagnostics may remain temporarily, but new or touched diagnostics should migrate to
   `ILogger` in this integration path.
12. Logging integration lives in `AlleyCat.Core.Logging` and is resolved through the DI container.

## In Scope

- `Microsoft.Extensions.Logging` factory and provider registration.
- Godot console logging provider for default diagnostics.
- Notification logging provider for `Error` and `Critical` UI routing.
- Unified core logging resolver for non-constructor-injected Godot objects.
- Structured logging conventions for new and migrated diagnostics.
- Performance guidance for suppressed logs.

## Out Of Scope

- External log aggregation, file sinks, or telemetry upload.
- Complete replacement of all existing low-priority `GD.*` diagnostics in this slice.
- Player-facing UI design beyond routing eligible errors to existing notification UI.
- Logging level tuning beyond selecting appropriate levels at call sites.

## Acceptance Criteria

1. `Game` registers `ILoggerFactory` and providers before the service provider is built.
2. New or touched diagnostics use `ILogger`/`ILogger<T>` with structured message templates and appropriate levels.
3. Godot objects that cannot use constructor injection resolve required typed loggers through the core resolver.
4. Missing required logging infrastructure fails clearly instead of silently suppressing diagnostics.
5. Interpolated or expensive debug/trace details are guarded before construction so disabled levels avoid unnecessary
   work.
6. Godot console output receives normal diagnostics through the logging provider.
7. `Error` and `Critical` logs reach notification UI when that UI is available, without exposing normal debug logs.
8. Protocol output, intentional UI notifications, and non-diagnostic Godot API calls remain explicit exceptions.
9. `Out Of Scope` defers optional sinks and full legacy cleanup without excluding required logging registration.

**Traceability Map:** User Requirements 1-4 -> AC-2, AC-5, AC-6, AC-7, AC-8; Technical Requirements 1-12 -> AC-1,
AC-3, AC-4, AC-5, AC-6, AC-7, AC-8, AC-9.

## References

### Implementation

- `@game/src/Core/Logging/`
- `@game/src/Game.cs`

### Related Specs

- [CORE-004: Global Service Resolution](../004-global-service-resolution/index.md)
- [SPCH-003: Transcriber Component](../../speech/003-transcription/index.md)
- [SPCH-004: Speech Generator Component](../../speech/004-speech-generation/index.md)
- [AI-001: Mind Component](../../ai/001-mind/index.md)

### External

- [Microsoft.Extensions.Logging][logging]

[logging]: https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.logging
