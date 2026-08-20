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
2. Players see intentional error notifications for actionable failures without unsolicited debug output.
3. Suppressed debug or trace diagnostics do not create noticeable runtime overhead.
4. Existing player-facing error behaviour is preserved while diagnostics migrate away from direct `GD.*` calls.
5. Developers can configure a third-party component's dedicated logging category without suppressed sensitive payloads
   being serialised.
6. Developers can opt in to transient in-game diagnostics notifications — for example pipeline latency measurements and
   markers — through a single logging-configuration switch that also governs console output for the same category; the
   shipped default keeps both off, and ordinary error notifications are unchanged.

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
13. Third-party logging components that can expose sensitive request or response payloads must retain their dedicated
    category and level controls. Payload serialisation must be deferred until that category's required level is enabled;
    any subsystem feature gate remains an additional prerequisite. Subsystem specs define which payloads are sensitive
    and the required category and level.
14. A structured log entry state may mark itself notification-eligible by implementing `IUINotificationEntry`, whose
    `ToNotificationText()` renders the transient notification text, which may differ from the entry's console output.
    Eligibility is entry state only: the notification
    provider must post that text for any entry-carrying state it receives, relaxing its minimum level for those entries
    alone, while ordinary entries keep the existing minimum-level behaviour. Posting must never alter log output or the
    unchanged `Error`/`Critical` routing, and must be re-entrancy guarded. `IUINotificationEntry` also defines a toast
    lifetime, `NotificationTimeoutSeconds`, defaulting to five seconds; the provider must pass it to the sink with
    entry-driven posts, while non-entry `Error`/`Critical` posts keep the sink's own three-second default.
15. Notification-eligible pipeline diagnostics emit at `Trace` under their own diagnostics category, and that
    category's configured level is the single universal switch for console logs and notification toasts alike. The
    logging framework's level filter runs before providers, so an entry-carrying diagnostic reaching the notification
    provider has already been opted in by configuration — for example
    `"Logging": { "LogLevel": { "AlleyCat.Pipeline": "Trace" } }` through a per-user `user://AlleyCat.json` override
    enables both, while the shipped `Information` default filters such entries before any provider sees them, keeping
    both off. No separate notification switch may supplement the level.
16. The shared pipeline diagnostic log must log under the `AlleyCat.Pipeline` category: latency and marker entries as
    Trace entries carrying their notification-eligible state, and log-only latency variants that must never become
    notifications. Notification eligibility is closed to four stage kinds — the STT backend return, the speak-tool
    invocation marker, TTS audio generation, and TTS lip-sync preparation — so toasts track pipeline milestones rather
    than micro-stages. Every other pipeline stage — STT recording stop, STT request preparation, STT completion, TTS
    backend return, TTS stream completion, TTS audio parsing, and playback start — plus failures, high-frequency
    micro-stages, and session-end measurements must use the log-only latency kind, which preserves identical console
    coverage without notification eligibility. Notification-eligible latency entries may carry a notification detail
    that the toast renders in place of the console detail — omitted keeps the console detail, a shortened value
    replaces it, and an empty value omits the parenthesised suffix — while the console line always renders the full
    detail, so shortened toasts never reduce console coverage.
17. The notification sink must accept posts from any thread. When configured with the shared `IMainThreadDispatcher`
    (CORE-010), posts must marshal onto the Godot main thread before touching UI nodes; delivery failures and
    dispatcher shutdown races must be contained and never escape through logging callers.

## In Scope

- `Microsoft.Extensions.Logging` factory and provider registration.
- Godot console logging provider for default diagnostics.
- Notification logging provider for `Error` and `Critical` UI routing.
- Opt-in notification routing for `IUINotificationEntry` entry states through the notification logging provider,
  governed by the category's configured log level.
- The shared `AlleyCat.Pipeline` pipeline diagnostic log with notification-eligible and log-only entry kinds.
- Main-thread-marshalled, thread-safe notification sink posting through the shared dispatcher.
- Unified core logging resolver for non-constructor-injected Godot objects.
- Structured logging conventions for new and migrated diagnostics.
- Performance guidance for suppressed logs.
- Reusable category gating and deferred serialisation for sensitive third-party logging payloads.

## Out Of Scope

- External log aggregation, file sinks, or telemetry upload.
- Complete replacement of all existing low-priority `GD.*` diagnostics in this slice.
- Player-facing UI design beyond routing eligible error and opt-in diagnostic entries to existing notification UI.
- Project-wide default level tuning beyond category and call-site contracts required by subsystem specs.

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
10. Tests of a sensitive third-party payload logger verify its dedicated category remains independently configurable and
    that payload detail is not serialised below the subsystem-required level or while its feature gate is disabled.
11. Tests verify that a notification-eligible entry posts its rendered text purely on entry state — below the
    notification provider's minimum level — that the post carries the entry's `NotificationTimeoutSeconds` toast
    lifetime while non-entry `Error` posts keep the sink default, that ordinary entries below that level never post
    and never alter the `Error`/`Critical` routing, and that posting re-entrancy guarding is unchanged.
12. Tests verify that pipeline latency and marker entries log as `Trace` under `AlleyCat.Pipeline`, that only the four
    notification-eligible stage kinds post while every log-only latency variant keeps its console line without
    posting, that a notification detail shortens or omits the toast suffix while the console line keeps the full
    detail, and that a background-thread post reaches the notification UI through the shared main-thread dispatcher.
    Configuration-driven coverage verifies that a `Trace` level override for the category alone enables console logs
    and toasts together, with the shipped `Information` default keeping both off.

**Traceability Map:** User Requirements 1-6 -> AC-2, AC-5, AC-6, AC-7, AC-8, AC-10, AC-11, AC-12; Technical
Requirements 1-17 -> AC-1, AC-3, AC-4, AC-5, AC-6, AC-7, AC-8, AC-9, AC-10, AC-11, AC-12.

## References

### Implementation

- `@game/src/Core/Logging/`
- `@game/src/Game.cs`

### Related Specs

- [CORE-004: Global Service Resolution](../004-global-service-resolution/index.md)
- [CORE-010: Main-Thread Dispatcher](../010-main-thread-dispatcher/index.md)
- [SPCH-003: Transcriber Component](../../speech/003-transcription/index.md)
- [SPCH-004: Speech Generator Component](../../speech/004-speech-generation/index.md)
- [AI-001: Mind Component](../../ai/001-mind/index.md)
- [AI-002: Agent Runtime](../../ai/002-agent-runtime/index.md)

### External

- [Microsoft.Extensions.Logging][logging]

[logging]: https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.logging
