---
name: godot-integration-testing
description: Use for running, triaging, or reporting integration tests, especially before final handoff.
---

# Godot Integration Testing

Use this skill when you need to run or interpret `integration-tests/AlleyCat.IntegrationTests.csproj`.

## Core Rule

Reviewer agents run the full integration suite in windowed mode before final handoff:

```bash
dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj
```

Do not substitute a full headless run for the final handoff gate. Several integration tests depend on an actual
renderer, and headless mode can hide renderer-dependent failures.

Coder agents must not run the full integration suite while implementing unless the invoking agent or user explicitly
requests an exception. Coder validation should use the narrowest targeted integration run that covers the changed
behaviour.

## XR Mode

Integration test execution must launch Godot with `--xr-mode off` to avoid the OpenXR warning dialog blocking unattended
runs. Without this flag, a run may hang until the warning is manually dismissed, or pass only after user intervention. The
integration test framework applies `--xr-mode off` to its Godot subprocesses automatically, so the `dotnet run` examples
below do not add an extra CLI flag. Direct `godot-mono` commands outside this framework must pass `--xr-mode off`
explicitly.

## Headless Mode

Use `--headless` only when the selected tests are known to be safe in headless mode, or when a spec/test explicitly
requires it. Good candidates include narrow non-renderer tests and tests marked or documented as headless-safe. Do not use
`--headless` as a default way to avoid OpenXR prompts; `--xr-mode off` is the required mechanism for that.

Examples:

```bash
dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- \
  --headless --test-class AlleyCat.IntegrationTests.Mind.AI.MindIntegrationTests
```

If a test validates rendering, screenshots, visual timing, viewport contents, animation visibility, or other
renderer-backed behaviour, prefer windowed execution unless the test's own contract says headless is valid.

## Targeted Runs

Coder agents should use targeted runs while iterating on a feature:

```bash
dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- \
  --test-class Fully.Qualified.TypeName
dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- \
  --test-method Fully.Qualified.TypeName.MethodName
```

If both filters are supplied, `--test-method` takes precedence over `--test-class`.

## Timeouts and Triage

The full suite launches many Godot processes and can take several minutes. Use a command timeout that is comfortably
above the observed suite duration before treating a run as hung.

When a run fails, classify the failure before acting:

- **Assertion failure** — test reached the expected runtime and the behaviour under test failed.
- **Framework/runtime failure** — Godot process startup, import, scene loading, timeout, or result transport failed.
- **Environment failure** — missing display server, missing import cache, unavailable renderer, or external timeout.

If a full windowed run cannot execute because the environment lacks a display server, report that as an escalation. Do
not silently replace the final handoff gate with headless validation.

## Handoff Reporting

For final handoff, report:

- exact integration command used;
- whether the run was windowed or headless, and why headless was valid if used;
- pass/fail counts;
- duration or timeout used;
- any known limitations, especially if only targeted or headless-safe tests were run.
