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

Selection is separate from the live-LLM gate: tests marked `[LiveLlm]` additionally require `--live-llm`, and no
selector can bypass that gate. See [Live LLM Tests](#live-llm-tests).

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

## Live LLM Tests

A small set of integration tests call real LLM providers. They are opt-in: excluded from every default, filtered, and
exact-selected run unless `--live-llm` is supplied. This keeps routine runs free of credentials, network access, and
provider cost.

### Identifying Live Tests

Live tests carry `[LiveLlm]` from `AlleyCat.TestFramework` on the test method or on the declaring class; either marker
gates every fact in the class, and the attribute is inherited. The representative fixture is
`AlleyCat.IntegrationTests.Mind.AI.LiveRoleplayIntegrationTests`, which is also `[Headless]`.

### Selection Semantics

| Run | Ordinary Tests | `[LiveLlm]` Tests |
| --- | --- | --- |
| Default (no options) | Selected | Excluded: no discovered, in-progress, or terminal node. |
| Exact selectors or MTP UID filter | Selected when matching. | Still excluded; selectors cannot bypass the gate. |
| `--live-llm` | Selected as usual. | Permitted when also matching the selectors. |
| `--live-llm --headless …` | Headless-routed as usual. | Permitted and headless-routed. |

- `--live-llm` permits; it never excludes ordinary tests, so one run can mix both.
- Excluded live tests are invisible in results — not skipped — and can never produce a passed node.
- `--live-llm` takes no arguments; `--live-llm <value>` is rejected during validation.

### Configuring AITest

Live clients read only the `AITest` section of the running game's merged configuration. The shipped
`game/AlleyCat.yaml` deliberately defines no active `AITest` section (a commented-out example documents its
shape); supply your own values through the user override (`user://AlleyCat.yaml`; on Linux
`~/.local/share/godot/app_userdata/AlleyCat/AlleyCat.yaml`):

```yaml
AITest:
    Host: "https://<your-openai-compatible-host>/v1"
    Model: "<your-model>"
    ApiKey: "<your-key>"
    #Timeout: 120   # Optional; positive seconds. Omitted keeps the client default.
```

- `Host` must be an absolute HTTP(S) URL including the API base path; a bare host or root-only path is rejected.
- The production `AI` section is never consulted and never substitutes for missing `AITest` values.
- Never commit credentials: the user override is the runtime-only secret boundary, and each contributor supplies
  their own.

### Configuration Failures

Missing or invalid settings fail fast with fixed, section-correct, secret-free messages. Each failure names the key —
for example `AITest:Timeout` — distinguishes configured from missing values, and never echoes configured values.
Missing or blank `Host`, `Model`, or `ApiKey`, a non-absolute or non-HTTP(S) `Host`, a `Host` without an API base
path, and a non-positive `Timeout` each produce their own message. Treat these as local configuration errors, not
product regressions.

### Support APIs

- `LiveLLMClientFactory.CreateLiveClients()` — builds a `LiveLLMClientPair` from the merged configuration's
  `AITest` section; the only authorised credential reader.
- `LiveLLMClientPair` — disposable target/judge `IChatClient` pair. Its public constructor keeps each client
  separately injectable for deterministic fakes, and is the seam for future target ≠ judge substitution; no
  divergent configuration exists today.
- `LiveLLMEvaluation.EvaluateAsync(…)` — single-shot target-then-judge flow with fail-closed metric validation,
  inclusive threshold, sanitised failures, and disposal of the owned pair.

### Writing Or Extending A Live Test

Follow the `LiveRoleplayIntegrationTests` pattern:

1. Mark the fixture `[LiveLlm]` (plus `[Headless]` when renderer-independent).
2. Assert deterministic pre-network state first — for example that the real rendered prompt carries the expected
   committed lore facts — so prompt-pipeline regressions fail cheaply without provider calls.
3. Inside the running `Game`, call `LiveLLMClientFactory.CreateLiveClients()` and make one live call through
   `LiveLLMEvaluation.EvaluateAsync` with an explicit grounding context (for example
   `GroundednessEvaluatorContext`) and an explicit threshold within the 1–5 rubric. The evaluator must declare
   exactly one numeric metric. Threshold 5 is deliberately stricter than the library's built-in
   greater-than-or-equal-to-4 interpretation when the question requires the full record.
4. Assert deterministic post-response shape afterwards — non-empty text, no function calls, no leaked `char:`
   identifiers. The judge verdict complements these assertions; it never replaces them.
5. Do not assert exact generated sentences and do not retry a failed evaluation until it passes.

### Cost, Serial Execution, And Timeouts

- Every permitted live test makes real billable calls: one target request plus one judge request per evaluation.
  Keep selectors narrow and never re-run failures to grind out a pass.
- Target and judge calls run serially within the test; keep other provider work out of the fact.
- Facts receive no runner cancellation token. The host bounds the whole fact — including both LLM calls — with
  `ALLEYCAT_GODOT_RUN_FACT_TIMEOUT_MS` (default 120,000 ms); raise it for slow providers. `AITest:Timeout` is only
  the per-request SDK network timeout, not the test budget.

### Running Live Tests

```bash
# Default suite: live tests stay excluded; no AITest credentials required.
dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj

# The selected live fixture, explicitly permitted:
dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- \
  --live-llm --headless --test-class AlleyCat.IntegrationTests.Mind.AI.LiveRoleplayIntegrationTests
```

## Timing Hygiene For Physics-Driven Waits

VRIK/`CharacterIK` solving, `SkeletonModifier3D` solvers, `NavigationServer3D` map activation and region upload
syncs, and Jolt hand-collision proxies advance on physics ticks, not process frames. When a test waits for such a
system to settle or sync, wait on physics frames via `TestUtils.WaitForPhysicsFramesAsync` or a physics-frame poll
loop, never a fixed process-frame count. `VrikSettlePhysicsFrames` in the optical finger-tracking photobooth tests
and `NPCNavigationIntegrationTests.WaitForNavigationLaneSyncAsync` are the precedents.

- The project config disables vsync (`window/vsync/vsync_mode=0`), so a warm windowed session renders uncapped and a
  fixed process-frame wait spans an environment-dependent number of physics ticks. Two failure classes in suite
  history came from this mismatch: SplashScreen frame-pacing sensitivity under Xvfb software rendering (see
  [Choosing an Execution Mode](#choosing-an-execution-mode)), and VRIK-settle plus NavigationServer-sync races on a
  real uncapped-FPS display.
- For deterministic solver sampling, step the solver manually instead of sampling across real frames: set the
  skeleton's `ModifierCallbackModeProcess` to `Skeleton3D.ModifierCallbackModeProcessEnum.Manual` and drive explicit
  `Skeleton3D.Advance` steps, as the HeadHips and NeckSpine IK integration tests do.

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

For live LLM tests, the one dispatched test request covers both serial provider calls; there is no cooperative
cancellation token, so budget accordingly (see [Live LLM Tests](#live-llm-tests)).

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
6. **Live LLM failure:** classify before acting. A secret-free `AITest:…` configuration message means a local
   settings problem — fix your user override. A per-test timeout means the fact budget was exceeded — narrow the run
   and raise `ALLEYCAT_GODOT_RUN_FACT_TIMEOUT_MS` for slow providers. A sanitised threshold or fail-closed metric
   failure is a genuine evaluation result — inspect the deterministic prompt and response assertions; do not retry
   until it passes.

## Verification Commands

Run the repository checks before hand-off:

```bash
dotnet format --verify-no-changes AlleyCat.sln
dotnet build AlleyCat.sln -warnaserror
```

## Related Contract

- [TEST-001: Integration Test Framework](index.md)
