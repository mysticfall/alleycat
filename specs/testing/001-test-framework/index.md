---
id: TEST-001
title: Integration Test Framework
---

# Integration Test Framework

## Requirement

Provide a dependable integration test framework for behaviours requiring Godot runtime APIs. It must support local and
headless execution, selective runs, stable test identity, actionable diagnostics, and materially faster full-suite
execution through persistent reusable runtime sessions.

## Goal

Enable contributors and agents to validate Godot-runtime behaviour with repeatable, debuggable integration-test runs,
keeping full-suite execution fast enough for routine pre-handoff verification.

## User Requirements

1. Developers must be able to run targeted integration tests locally with clear pass/fail outcomes.
2. Failure diagnostics must clearly indicate whether failures originate from framework/runtime issues or test
   assertions.
3. Test authoring should follow familiar xUnit lifecycle patterns for predictable maintenance.
4. Full-suite wall-clock time must drop materially relative to one-process-per-test execution, through reuse of
   persistent runtime sessions.
5. Every test must still be reported individually, and each failure must remain attributable to the exact test that
   produced it.
6. An unhealthy session must never poison the results of tests that run after it is replaced.

## Technical Requirements

1. The framework must support deterministic discovery and execution of Godot-backed parameterless `[Fact]` tests.
2. Headless and windowed execution controls must be explicit and overrideable via attribute and CLI policy, and the
   effective headless mode must route each test to its session.
3. The host must lazily maintain at most one reusable headless session and one reusable windowed session, each a
   persistent Godot process launched with `--integration-test-session`. Rendering mode cannot change within a process,
   so sessions partition by rendering mode; CLI `--headless` collapses all tests into the single headless session.
4. Execution within a session must be strictly serial, with one in-flight test per session.
5. Result transport must use the session wire protocol — newline-delimited JSON over redirected stdin/stdout with
   stable test-UID correlation — as defined in [Session Execution Model](#session-execution-model).
6. Outcomes must preserve the assertion-versus-framework diagnostic separation: `failed` for test-owned assertion,
   setup, or teardown failures; `error` for framework, runtime, or protocol failures.
7. Per-test lifecycle must keep the existing xUnit contracts — constructor injection, `IDisposable`, `IAsyncDisposable`,
   and `IAsyncLifetime` — executed by the existing per-test lifecycle executor inside the session.
8. Individual Microsoft Testing Platform reporting must be unchanged: every test UID must get its own InProgress and
   terminal node on the host.
9. The existing one-off dynamic-load probe preflight and the optional import preflight must be retained.
10. Between tests, the runtime must perform and validate the baseline restoration defined in
    [Session Execution Model](#session-execution-model), after every test including failures.
11. The framework must document the test-owned cleanup limits it cannot reset, as listed in
    [Session Execution Model](#session-execution-model).
12. Session restart semantics must follow [Session Execution Model](#session-execution-model): tainted sessions are
    marked or killed and lazily replaced; the active test is never auto-retried; host cancellation kills all sessions.
13. Timeout environment variables must keep their roles as mapped in
    [Session Execution Model](#session-execution-model).
14. Graceful shutdown must correlate a non-empty `RequestId` between `shutdown` and `shutdown-complete`. A missing,
    invalid, or mismatched acknowledgement is a protocol and cleanup fault; the host must force termination within the
    cleanup timeout rather than accepting the session as cleanly shut down.
15. OS-process and CLR isolation between tests is NOT guaranteed within a session; baseline restoration and restart
    semantics bound the resulting blast radius.
16. The [Integration-Test Contributor Guide](contributor-guide.md) is the normative operational contract for
    contributor run, filter, diagnostic, timeout, and recovery workflows.

## In Scope

- Discovery and execution of Godot-dependent integration tests.
- Headless and windowed local execution.
- Selective execution using filters and UID selection.
- Clear separation of framework or runtime errors versus assertion failures.
- Per-test lifecycle support via constructor and disposal patterns.
- Persistent session execution: mode-partitioned reusable sessions, serial in-session execution, the session wire
  protocol, runtime-owned baseline restoration, and session restart semantics.

## Out Of Scope

- Replacing unit tests in `tests/`.
- Defining gameplay-specific assertions for all systems.
- Non-essential editor UX work.
- Load and performance benchmarking infrastructure.
- Collection-level fixtures (`IClassFixture`, `ICollectionFixture`).
- Parallel execution within a session; execution is strictly serial.
- OS-process and CLR isolation between tests within a session.

## Contributor Operations

The [Integration-Test Contributor Guide](contributor-guide.md) is a normative dependency for the contributor workflow
required by Technical Requirement 16.

## Session Execution Model

This section is a normative dependency for the session-related Technical Requirements above. The host launches
persistent `--integration-test-session` Godot processes and dispatches tests into them rather than launching one
process per test.

### Sessions And Routing

- The host lazily maintains at most one reusable headless session and one reusable windowed (default) session.
- Each test routes by its effective headless mode, resolved through the existing attribute/CLI precedence.
- Rendering mode cannot change within a process, which is why sessions partition by rendering mode.
- CLI `--headless` collapses every test into the single headless session.
- Execution within a session is strictly serial: one in-flight test per session.

### Wire Protocol

- Transport is newline-delimited JSON over redirected stdin/stdout.
- A malformed (non-JSON or wrong-shape) host-to-runtime line is skipped by the runtime with a logged warning rather
  than faulting the session; host-side per-request timeouts bound any resulting stall, and malformed runtime-to-host
  output remains a protocol fault.
- Runtime-to-host lines are prefixed `ALLEYCAT_INTEGRATION_SESSION:`.
- Every message carries `Version:1` and a `Kind` of `ready`, `run`, `result`, `shutdown`, or `shutdown-complete`.
- Every required string property must be present and contain a non-whitespace JSON string. A missing property, `null`,
  empty string, or whitespace-only string is wrong shape. Every required Boolean property must be present and contain a
  JSON Boolean; a missing, `null`, or non-Boolean property is wrong shape.
- Host-to-runtime commands:
    - `run` — carries non-empty `RequestId` (the stable test UID), `Type`, and `Method` identifying the test to
      execute.
    - `shutdown` — carries a non-empty `RequestId`.
- Runtime-to-host messages:
    - `ready` — signals session start; on startup failure it carries `Outcome:"error"` with a `Message`.
    - `result` — exactly one per `run`, carrying non-empty `RequestId`, `Outcome` (`passed`, `failed`, or `error`),
      `Message`, `Stack`, and required Boolean `SessionReusable`, the runtime's post-test verdict on whether the
      session may execute further tests. `SessionReusable:false` is an explicit valid verdict and is not equivalent to
      an absent property.
    - `shutdown-complete` — carries the non-empty `RequestId` from the corresponding `shutdown`.

### Outcome Taxonomy

- `failed` — test-owned assertion, setup, or teardown failure.
- `error` — framework, runtime, or protocol failure.

This preserves the existing assertion-versus-framework diagnostic separation.

### Baseline Restoration

After every test — including failures — the runtime restores and validates the session baseline:

1. Complete test teardown through the per-test lifecycle executor.
2. Force `SceneTree.Paused = false`.
3. Restore the session-start `Engine.TimeScale`.
4. Remove test-created scenes and root children, protecting the runner.
5. Let deferred frees settle over frames.
6. Re-create a fresh `Global` autoload; preserving it would leak its service provider, logging, XR, and `Game.Instance`
   state.
7. Restore the fresh-process startup scene arrangement.
8. Validate the baseline: runner alive, tree unpaused, `Global` present, no unexpected root children.

The existing isolated-`Game` policy still frees `Global` before tests that require it; the baseline re-creates it
afterwards.

### Test-Owned Cleanup Limits

The framework cannot reset test-owned state. Tests remain responsible for cleaning up:

- CLR static fields and static event subscriptions.
- Native or unmanaged singletons.
- The `ResourceLoader` cache and mutated shared resources.
- Detached background tasks.
- Arbitrary `InputMap`, audio-server, rendering-server, project-setting, and OS state.

### Session Restart Semantics

- Assertion failure with successful cleanup → report `failed` and reuse the session.
- Teardown or cleanup-validation failure, protocol error, per-test timeout, or process crash → report the affected
  test (`failed` with diagnostics appended, or `error`), mark or kill the session, and lazily start a replacement for
  the remaining tests.
- A `passed` result with `SessionReusable:false` means baseline restoration failed after an otherwise-passing test;
  the runtime carries its baseline diagnostics in `Message`. The host converts this to an `error` result for that
  test carrying those diagnostics, and the session is lazily replaced for the remaining tests.
- For graceful shutdown, the host sends `shutdown` with a non-empty `RequestId` and waits for a
  `shutdown-complete` carrying the same identifier. A missing, invalid, or mismatched acknowledgement is a
  protocol/cleanup fault: the host must not accept a clean shutdown and must force termination within the cleanup
  timeout.
- The active test is never auto-retried.
- Host cancellation kills all sessions.

### Timeout Mapping

Timeout environment variables keep their roles:

- `ALLEYCAT_GODOT_PREFLIGHT_TIMEOUT_MS` — session-start/`ready` timeout.
- `ALLEYCAT_GODOT_RUN_FACT_TIMEOUT_MS` — per-request timeout.
- `ALLEYCAT_GODOT_CLEANUP_TIMEOUT_MS` — wait for matching `shutdown-complete` during graceful shutdown, then force
  termination if it is absent, invalid, mismatched, or late.
- `ALLEYCAT_GODOT_IMPORT_TIMEOUT_MS` — retains its role for the optional import preflight.

## Lifecycle Invocation Policy

For each test method execution, inside the session:

1. Construct test class instance.
2. Run async setup when `Xunit.IAsyncLifetime.InitializeAsync` is implemented.
3. Execute test method.
4. Always attempt teardown in finally-style flow:
    - run `Xunit.IAsyncLifetime.DisposeAsync` when implemented;
    - then run `IAsyncDisposable.DisposeAsync` or `IDisposable.Dispose` when implemented.

**Failure handling:**
- Setup failure fails the test method and skips normal test-body execution.
- Teardown is always attempted after test-body execution, including when assertions fail.
- If both test execution and teardown fail, the outcome is reported as **failed** with the test-body
  failure as primary and teardown failure appended as secondary diagnostic detail.

## Headless Mode Control

Integration tests run in windowed (non-headless) mode by default. Test authors can opt into headless mode
using the `[Headless]` attribute.

- `[Headless]` or `[Headless(true)]` — headless mode (opt-in from default windowed)
- `[Headless(false)]` — windowed mode (explicit, matches default)

**Resolution order (first match wins):**
1. Method-level `[Headless]` attribute
2. Class-level `[Headless]` attribute
3. Default: non-headless (windowed)

**CLI override:**
- `--headless` — forces all tests to run in headless mode, overriding all attribute settings.

**Session routing:** the resolved effective mode selects the session that executes the test — windowed for the
default, headless for `[Headless]` — because a session process's rendering mode is fixed at launch.

Windowed mode is retained as the default because it mirrors editor/runtime rendering more closely. Headless mode is an
explicit framework capability for tests whose contracts do not depend on a renderer.

## Supported CLI Options

- `--test-class <Fully.Qualified.TypeName>` — narrows selection to tests on the exact type.
- `--test-method <Fully.Qualified.TypeName.MethodName>` — narrows selection to one exact test method.
- `--headless` — forces all tests to run in headless mode. Overrides per-test and per-class `HeadlessAttribute`
  settings. Intended for tests known to be safe without renderer-backed behaviour.

**Precedence:** If both `--test-class` and `--test-method` are supplied, `--test-method` takes precedence.

**Limitation:** Advanced trait or category filters are not yet supported.

## Remaining Work

1. **Richer filtering** — add trait or category filtering and print selected test list before execution.
2. **Diagnostics** — improve failure summaries and emit machine-readable artefacts.

## Acceptance Criteria

1. A new contributor can add and run a Godot-backed integration test locally using documented steps, with clear
   pass/fail outcomes for targeted runs.
2. Full-suite execution completes with materially lower wall-clock time than one-process-per-test execution through
   session reuse.
3. Every test reports individually with its own Microsoft Testing Platform InProgress and terminal node, and results
   stay correlated to the exact test UID.
4. Assertion failures remain clearly distinguishable from framework or runtime failures in diagnostics.
5. A failed test whose cleanup succeeds does not block or corrupt subsequent tests in the same session.
6. A tainted session — teardown or cleanup-validation failure, protocol error, per-test timeout, or process crash —
   is replaced, its remaining tests still run, and the active test is never auto-retried.
7. The suite supports both headless and windowed execution with deterministic pass/fail/error mapping.
8. Selective execution runs only the requested subset in normal feature workflows.
9. Same-mode tests share one session process; mixed-mode runs use at most one headless and one windowed session; CLI
   `--headless` collapses every test into the single headless session.
10. Per-test lifecycle via constructor and disposal patterns is preserved inside sessions.
11. Baseline restoration between tests is validated: scene tree unpaused, session-start time scale restored,
    test-created root children removed, fresh `Global` autoload present, no unexpected root children, runner alive.
12. The dynamic-load probe preflight and the optional import preflight are retained.
13. Wire-protocol validation warning-skips a `shutdown` command whose `RequestId` is missing, `null`, empty, or
    whitespace-only. The host sends a non-empty identifier and accepts only a `shutdown-complete` carrying the same
    non-empty value; a missing, `null`, empty, whitespace-only, or mismatched acknowledgement is a protocol/cleanup
    fault that forces termination within the cleanup timeout and never counts as clean shutdown. It also distinguishes
    `SessionReusable:false` from an absent property.
14. The [Integration-Test Contributor Guide](contributor-guide.md) provides a linked, executable quick start; exact
    selectors; windowed, Xvfb, headless, and XR guidance; outcome and session diagnostics; timeout mapping; and
    actionable recovery for protocol, timeout, crash, non-reusable-session, import-cache, and preflight failures.
15. Criteria 1-6 and 14 verify User Requirements; criteria 3, 6, 7-14 verify Technical Requirements.

## References

- @test-framework/src/TestingPlatformBuilderHook.cs
- @test-framework/src/GodotTestFramework.cs
- @test-framework/AlleyCat.TestFramework.csproj
- @test-framework/AlleyCat.TestFramework.Tests.csproj
- @integration-tests/AlleyCat.IntegrationTests.csproj
- @integration-tests/src/Testing/ReusableSessionIntegrationTests.cs
- @game/src/Testing/TestRuntimeRunner.cs
- [Integration-Test Contributor Guide](contributor-guide.md)
- @specs/index.md
