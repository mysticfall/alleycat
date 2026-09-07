# Integration-Test Contributor Guide

## Purpose

Use this guide to add, run, filter, and diagnose Godot-backed integration tests. It is the contributor-facing
operational contract for [TEST-001](index.md); the specification defines the framework's contractual limits.

## Quick Start

Run commands from the repository root. The framework uses `GODOT_PATH` when set; otherwise it launches `godot-mono`.
Set `GODOT_PATH` when the required Godot executable is not available as `godot-mono` on `PATH`.

1. Add a public, parameterless xUnit `[Fact]` to `integration-tests/`. The framework discovers only this test shape.
2. Run the full suite in its default windowed mode:

   ```bash
   dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj
   ```

3. Prefer a narrow class or method run while developing; see [Filtering](#filtering).

The framework passes `--xr-mode off` to every Godot subprocess. Any direct `godot-mono` command outside the framework
must also pass `--xr-mode off`, for example:

```bash
godot-mono --path game --xr-mode off
```

## Filtering

Selectors match exact fully qualified names; they are not pattern filters. Both selector options accept one value
that may be a comma-separated list of such names and select the union of all matches; a single name behaves exactly
as before.

```bash
# All supported [Fact] tests on one exact type; comma-separate additional types to run their union.
dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- \
  --test-class AlleyCat.IntegrationTests.Testing.ReusableSessionIntegrationTests

# Exact methods as <Fully.Qualified.TypeName>.<MethodName>; comma-separate entries to run several.
dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- \
  --test-method Fully.Qualified.TypeName.MethodName,Other.Qualified.TypeName.OtherMethod
```

`--test-method` takes precedence when both selectors are present. Every `--test-method` entry must be a well-formed
`<Fully.Qualified.TypeName>.<MethodName>` selector, or the command is rejected. A `--test-class` list must contain at
least one non-empty class name; an all-empty list is likewise rejected. Trait and category filters are unsupported.

## Choosing an Execution Mode

Windowed mode is the default and is required for renderer-dependent tests. Do not substitute headless mode merely to
avoid OpenXR prompts; use `--xr-mode off` for direct Godot commands instead.

When `xvfb-run` is available, wrap windowed runs to preserve renderer coverage without opening windows on the active
display:

```bash
xvfb-run -a dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- \
  --test-class AlleyCat.IntegrationTests.Testing.ReusableSessionIntegrationTests
```

Godot inherits Xvfb's display. Xvfb uses software rendering, so a failure limited to frame-pacing assertions may be an
environment limitation; validate genuine renderer requirements on a real display when exact GPU pacing matters.

Use `--headless` only for a selected test known to be renderer-independent, such as one marked `[Headless]` or whose
contract explicitly permits it:

```bash
dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- \
  --headless --test-class AlleyCat.IntegrationTests.Testing.ReusableSessionIntegrationTests
```

`--headless` overrides every test's `Headless` attribute and routes all selected tests to the headless session.

## Reading Results

The host publishes an individual Microsoft Testing Platform InProgress and terminal result for every selected test UID.
Interpret terminal outcomes as follows:

| Outcome | Meaning | First Action |
| --- | --- | --- |
| `passed` | The test and its post-test baseline restoration succeeded. | Continue. |
| `failed` | A test-owned assertion, setup, or teardown failed. | Fix the test or behaviour; rerun the exact selector. |
| `error` | A framework/session, runtime, preflight, timeout, process, or protocol fault occurred. | See Recovery. |

Framework errors include captured session stdout and stderr. A preflight failure is reported as an `error` for every
selected test because none can start safely.

## Session Model And Limits

One run lazily maintains at most one headless session and one windowed session. Each is a persistent Godot process.
Tests within either session run serially, one at a time. Mode partitioning improves suite time while retaining an
individual MTP result for every test.

The framework restores its runtime baseline between tests and replaces tainted sessions. It does not provide OS-process
or CLR isolation between tests. Tests must clean up their own static state, static event subscriptions, unmanaged
singletons, shared-resource mutations, background tasks, and other state outside the framework baseline. See
[TEST-001's test-owned cleanup limits](index.md#test-owned-cleanup-limits) for the complete contractual boundary.

## Timeouts And Preflight

Timeout values are positive milliseconds. An unset, invalid, or non-positive value uses the current default.

| Environment Variable | Default | Applies To |
| --- | ---: | --- |
| `ALLEYCAT_GODOT_PREFLIGHT_TIMEOUT_MS` | 30,000 | Dynamic-load probe and session `ready` wait. |
| `ALLEYCAT_GODOT_RUN_FACT_TIMEOUT_MS` | 120,000 | One dispatched test request. |
| `ALLEYCAT_GODOT_CLEANUP_TIMEOUT_MS` | 5,000 | Matching shutdown acknowledgement and process-exit grace. |
| `ALLEYCAT_GODOT_IMPORT_TIMEOUT_MS` | 120,000 | Optional import preflight. |

The dynamic-load probe runs before selected tests. To run the optional import preflight before that probe, set
`ALLEYCAT_INTEGRATION_IMPORT_PREFLIGHT=1` for the command. It runs Godot import in headless recovery mode and is the
first recovery step for a missing or stale import cache:

```bash
ALLEYCAT_INTEGRATION_IMPORT_PREFLIGHT=1 \
  dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- \
  --test-class AlleyCat.IntegrationTests.Testing.ReusableSessionIntegrationTests
```

## Recovery

1. **Protocol fault or unexpected result:** keep the captured output, fix the runtime/protocol cause, then rerun the
   exact selector. The affected test is `error`; its tainted session is discarded and later tests receive a replacement.
2. **Session-ready or per-test timeout:** use the captured output to identify the blocked startup or test. Narrow the
   run and increase only `ALLEYCAT_GODOT_PREFLIGHT_TIMEOUT_MS` or `ALLEYCAT_GODOT_RUN_FACT_TIMEOUT_MS` when justified.
   The timed-out session is killed; the active test is not retried automatically.
3. **Godot crash or non-reusable session:** treat the affected result as `error`, inspect the output, correct the cause,
   and rerun the exact selector. A `SessionReusable:false` result also discards the session; a nominal `passed` result
   with that value becomes `error` because baseline restoration failed.
4. **Import cache or preflight failure:** confirm `GODOT_PATH` and the repository root, run the optional import
   preflight above, then rerun the narrow selector. If it still fails, keep the probe/import stdout and stderr with the
   report.
5. **Windowed environment failure:** use Xvfb when available. If a renderer-dependent test cannot obtain a display,
   report the environment limitation rather than replacing the required windowed coverage with headless execution.

## Verification Commands

Run the repository checks before hand-off:

```bash
dotnet format --verify-no-changes AlleyCat.sln
dotnet build AlleyCat.sln -warnaserror
```

## Related Contract

- [TEST-001: Integration Test Framework](index.md)
