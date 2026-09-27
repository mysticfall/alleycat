---
name: godot-integration-testing
description: Use for authoring, running, triaging, or reporting Godot integration tests.
---

# Godot Integration Testing

Use this skill when you need to author, run, triage, or report `integration-tests/AlleyCat.IntegrationTests.csproj`.
The contractual limits — sessions, wire protocol, outcome taxonomy, timeouts, CLI options, and the live-LLM gate — are
specified in [TEST-001](../../../specs/testing/001-test-framework/index.md); this skill owns the operational workflow.

## Quick Start

Run commands from the repository root. The framework uses `GODOT_PATH` when set; otherwise it launches `godot-mono`.
Set `GODOT_PATH` when the required Godot executable is not available as `godot-mono` on `PATH`.

1. Add a public, parameterless xUnit `[Fact]` to `integration-tests/`; the framework discovers only this test shape.
2. Run the full suite in its default windowed mode:

   ```bash
   dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj
   ```

3. Prefer a narrow class or method run while developing; see [Targeted Runs](#targeted-runs).

## Core Rule

This skill owns integration-test execution, fixture authoring, and failure triage. The
`reviewer-checklist-implementation` skill owns whether a focused re-review or final gameplay review requires targeted or
full suites. Workflow-only instruction reviews do not run gameplay suites.

When the implementation checklist requires the full integration suite, run it in windowed mode using the Quick Start
command. Do not substitute a full headless run for the final handoff gate. Several integration tests depend on an
actual renderer, and headless mode can hide renderer-dependent failures.

For focused implementation iteration, use the narrowest targeted integration run that covers the changed behaviour
unless the invoking agent or user explicitly requests broader validation.

## XR Mode

Integration test execution must launch Godot with `--xr-mode off` to avoid the OpenXR warning dialog blocking
unattended runs. Without this flag, a run may hang until the warning is dismissed, or pass only after user
intervention. The integration test framework applies `--xr-mode off` to its Godot subprocesses automatically, so the
`dotnet run` examples below do not add an extra CLI flag. Direct `godot-mono` commands outside this framework must
pass `--xr-mode off` explicitly:

```bash
godot-mono --path game --xr-mode off
```

## Headless Mode

Use `--headless` only when the selected tests are known to be safe in headless mode, or when a spec/test explicitly
requires it. Good candidates include narrow non-renderer tests and tests marked or documented as headless-safe. Do not
use `--headless` as a default way to avoid OpenXR prompts; `--xr-mode off` is the required mechanism for that.

```bash
dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- \
  --headless --test-class AlleyCat.IntegrationTests.Mind.AI.MindIntegrationTests
```

`--headless` overrides every test's `Headless` attribute and routes all selected tests to the single headless session.

If a test validates rendering, screenshots, visual timing, viewport contents, animation visibility, or other
renderer-backed behaviour, prefer windowed execution unless the test's own contract says headless is valid.

## Virtual Display (Xvfb)

Windowed integration runs open real Godot windows on the active display server. For the full suite this means
windows pop up and vanish for several minutes, which disrupts the machine being used to run them. When
`xvfb-run` (or `Xvfb`) is available, wrap windowed integration runs so Godot renders into a virtual framebuffer
instead of the interactive display:

```bash
xvfb-run -a dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- \
  --test-class Fully.Qualified.TypeName
```

The integration test framework launches Godot with `UseShellExecute = false`, so the Godot subprocess inherits the
`DISPLAY` that `xvfb-run` sets. No extra flags are required for the Godot side.

Enforce this wrapper whenever the host has a display server and `xvfb-run` is on `PATH`, including the final
handoff windowed gate. It preserves the actual-renderer requirement of the windowed gate (Godot still
initialises a renderer under Xvfb, so renderer-dependent failures are still caught) while keeping the
interactive display free.

Use `xvfb-run -a` so a free display number is chosen automatically. If `xvfb-run` is unavailable, fall back to a
plain windowed run and report the limitation.

### Software-Rendering Caveat

Xvfb has no GPU, so Godot falls back to software rendering (typically Mesa llvmpipe). Software rendering changes
per-frame timing compared with a real GPU:

- Per-frame `delta` values are larger and more variable.
- Frame/timing-sensitive assertions that assume tight GPU pacing can fail under Xvfb even though they pass on real
  hardware. For example, the splash-screen fade-lifecycle timing test
  was frame-pacing-sensitive under software rendering (it failed under Xvfb but passed on a real display). Because the
  assertion checks animation timing rather than renderer output, it was marked `[Headless]`, while the sibling layout
  test stays windowed to keep exercising the actual renderer.

Handle this caveat as follows:

- For windowed tests whose assertions are frame-pacing-sensitive and that do not truly need a visible renderer,
  mark them `[Headless]` so they run deterministically without any window.
- Where a windowed renderer is genuinely required, use tolerant ranges for timing/animation assertions rather than
  exact GPU-paced bounds, or validate those specific tests on the real display when exact pacing matters.
- Treat a failure that only reproduces under software rendering as an environment limitation, not a product defect,
  unless the test contract explicitly requires real-GPU pacing.

## Targeted Runs

Coder agents should use targeted runs while iterating on a feature:

```bash
# All tests on one exact type; comma-separate additional types to run their union.
dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- \
  --test-class AlleyCat.IntegrationTests.Testing.ReusableSessionIntegrationTests

# Exact methods as <Fully.Qualified.TypeName>.<MethodName>; comma-separate entries to run several.
dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- \
  --test-method Fully.Qualified.TypeName.MethodName,Other.Qualified.TypeName.OtherMethod
```

- Selectors match exact fully qualified names; they are not pattern filters.
- Each selector option takes one value that may be a comma-separated list of such names and selects the union of all
  matches; a single name behaves exactly as a lone selector.
- Every `--test-method` entry must be a well-formed `<Fully.Qualified.TypeName>.<MethodName>` selector; a malformed
  entry is rejected during validation.
- A `--test-class` list must contain at least one non-empty class name; an all-empty list (for example `","`) is
  likewise rejected during validation.
- If both selectors are supplied, `--test-method` takes precedence and `--test-class` is ignored.
- Advanced trait and category filters are unsupported.

Selection is separate from the live-LLM gate: tests marked `[LiveLlm]` additionally require `--live-llm`, and no
selector can bypass that gate. See [Live LLM Tests](#live-llm-tests).

## Live LLM Tests

A small set of integration tests call real LLM providers. They are opt-in: excluded from every default, filtered,
and exact-selected run unless `--live-llm` is supplied, which keeps routine runs free of credentials, network
access, and provider cost. This section is the operational workflow; the gate, configuration-boundary, and harness
contracts live in
[TEST-001's Live LLM Testing section](../../../specs/testing/001-test-framework/index.md#live-llm-testing).

### Identifying Live Tests

Live tests carry `[LiveLlm]` from `AlleyCat.TestFramework` on the test method or on the declaring class; either
marker gates every fact in the class, and the attribute is inherited. The representative fixture is
`AlleyCat.IntegrationTests.Mind.AI.LiveRoleplayIntegrationTests`, which is also `[Headless]`.

### Selecting Live Tests

| Run | Ordinary Tests | `[LiveLlm]` Tests |
| --- | --- | --- |
| Default (no options) | Selected | Excluded: no discovered, in-progress, or terminal node. |
| Exact selectors or MTP UID filter | Selected when matching. | Still excluded; selectors cannot bypass the gate. |
| `--live-llm` | Selected as usual. | Permitted when also matching the selectors. |
| `--live-llm --headless …` | Headless-routed as usual. | Permitted and headless-routed. |

`--live-llm` only permits; it never excludes ordinary tests, so one run can mix both. Excluded live tests are
invisible in results — not skipped — and can never produce a passed node. `--live-llm` takes no arguments;
`--live-llm <value>` is rejected during validation.

### Configuring AITest

Live clients read only the `AITest` section of the running game's merged configuration; the production `AI` section
is never consulted. The shipped `game/AlleyCat.yaml` deliberately defines no active `AITest` section, so supply your
own values through the user override (`user://AlleyCat.yaml`; on Linux
`~/.local/share/godot/app_userdata/AlleyCat/AlleyCat.yaml`):

```yaml
AITest:
    Host: "https://<your-openai-compatible-host>/v1"
    Model: "<your-model>"
    ApiKey: "<your-key>"
    #Timeout: 120   # Optional; positive seconds. Omitted keeps the client default.
```

`Host` must be an absolute HTTP(S) URL including the API base path; a bare host or root-only path is rejected.
Never commit credentials: the user override is the runtime-only secret boundary, and each contributor supplies
their own.

### Configuration Failures

Missing or invalid settings fail fast with fixed, section-correct, secret-free messages naming the key — for
example `AITest:Timeout` — and never echo configured values. Treat these as local configuration problems, not
product regressions: fix your user override and rerun.

### Writing or Extending a Live Test

For a single-shot judged call, follow the `LiveRoleplayIntegrationTests` pattern:

1. Mark the fixture `[LiveLlm]` (plus `[Headless]` when renderer-independent).
2. Assert deterministic pre-network state first — for example that the real rendered prompt carries the expected
   committed lore facts — so prompt-pipeline regressions fail cheaply without provider calls.
3. Inside the running `Game`, call `LiveLLMClientFactory.CreateLiveClients()` and make one live call through
   `LiveLLMEvaluation.EvaluateAsync` with an explicit grounding context (for example
   `GroundednessEvaluatorContext`) and an explicit threshold within the 1–5 rubric.
4. Assert deterministic post-response shape afterwards — non-empty text, no function calls, no leaked `char:`
   identifiers. The judge verdict complements these assertions; it never replaces them.
5. Do not assert exact generated sentences and do not retry a failed evaluation until it passes.

For a multi-request experiment batch, follow the `llm-experiment-driven-development` skill; the harness contracts it
uses are in TEST-001's Live LLM Testing section.

### Cost and Timeouts

- Every permitted live test makes real billable calls. Per trial, budget for the declared target requests, any
  continuation-generator requests, and the judge request. Keep selectors narrow and never re-run failures to grind
  out a pass.
- All provider work runs serially within the fact; a larger declared batch costs more wall-clock time, not
  parallelism.
- Facts receive no runner cancellation token. `ALLEYCAT_GODOT_RUN_FACT_TIMEOUT_MS` (default 120,000 ms) bounds the
  whole fact — the entire declared batch, including every scenario request, continuation, judge evaluation, and the
  aggregation; raise it for slow providers or larger declared batches. `AITest:Timeout` is only the per-request SDK
  network timeout, not the test budget.

### Running Live Tests

```bash
# Default suite: live tests stay excluded; no AITest credentials required.
dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj

# The selected live fixture, explicitly permitted:
dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- \
  --live-llm --headless --test-class AlleyCat.IntegrationTests.Mind.AI.LiveRoleplayIntegrationTests
```

### Support APIs

- `LiveLLMClientFactory.CreateLiveClients()` — builds a `LiveLLMClientPair` from the merged configuration's
  `AITest` section; the only authorised credential reader.
- `LiveLLMClientPair` — disposable target/judge `IChatClient` pair owned by the test or experiment for its complete
  lifetime; execution and evaluation operations borrow its clients and never dispose them.
- `LiveLLMEvaluation.EvaluateAsync(…)` — single-shot convenience composition: one text-only target request, then one
  judge evaluation, with disposal of the pair delegated to it.

## Fixture Authoring

- Prefer focused fixtures that contain only the production wiring relevant to the behaviour under test.
- For bug work, prove that a minimal fixture reproduces the reported symptom or the relevant production wiring and
  active state. If it cannot, use a representative runtime fixture or scene and justify that choice. If neither is
  available,
  preserve the unresolved defect and escalate the evidence gap.
- If a fixture needs world/environment lighting, instance `res://assets/testing/test_environment.tscn` by default
  instead of creating an ad-hoc `WorldEnvironment`, unless the test contract requires custom environment settings.
- Include only relevant components and wiring unless the test explicitly validates component conflicts or interaction
  between multiple systems.
- Character fixtures should reference only the reference female character.
- Avoid production role installers in component fixtures; use installers only for installer tests or dedicated
  complete-character wiring/runtime-scene tests.
- Component, IK, pose, hand, eye, and locomotion tests should normally use minimal authored fixtures or direct resource
  setup, unless doing so would remove the reported symptom or production interaction under test.

### Timing Hygiene For Physics-Driven Waits

Physics-tick systems — VRIK/`CharacterIK` solving, `SkeletonModifier3D` solvers, `NavigationServer3D` map
activation and region upload syncs, and Jolt hand-collision proxies — advance on physics ticks, not process frames.
When a test waits for such a system to settle or sync, wait on physics frames via
`TestUtils.WaitForPhysicsFramesAsync` or a physics-frame poll loop, never a fixed process-frame count.
`VrikSettlePhysicsFrames` in the optical finger-tracking photobooth tests and
`NPCNavigationIntegrationTests.WaitForNavigationLaneSyncAsync` are the precedents.

The project config disables vsync (`window/vsync/vsync_mode=0`), so a warm windowed session renders uncapped and a
fixed process-frame wait spans an environment-dependent number of physics ticks.

For deterministic solver sampling, step the solver manually instead of sampling across real frames: set the
skeleton's `ModifierCallbackModeProcess` to `Skeleton3D.ModifierCallbackModeProcessEnum.Manual` and drive explicit
`Skeleton3D.Advance` steps, as the HeadHips and NeckSpine IK integration tests do.

## Session Model

One run lazily maintains at most one headless session and one windowed session, each a persistent Godot process.
Tests within a session run serially, one at a time. The framework restores its runtime baseline between tests and
replaces tainted sessions; it does not provide OS-process or CLR isolation between tests, so tests must clean up
their own static state, static event subscriptions, unmanaged singletons, shared-resource mutations, and background
tasks. The complete contract is
[TEST-001's Session Execution Model](../../../specs/testing/001-test-framework/index.md#session-execution-model).

## Reading Results

The host publishes an individual Microsoft Testing Platform InProgress and terminal result for every selected test
UID. Interpret terminal outcomes operationally:

| Outcome | Meaning | First Action |
| --- | --- | --- |
| `passed` | The test and its post-test baseline restoration succeeded. | Continue. |
| `failed` | A test-owned assertion, setup, or teardown failed. | Fix the test or behaviour; rerun the exact selector. |
| `error` | A framework, session, runtime, preflight, timeout, process, or protocol fault. | Follow the ladder below. |

Framework errors include captured session stdout and stderr. A preflight failure is reported as an `error` for
every selected test because none can start safely. The outcome contract is
[TEST-001's Outcome Taxonomy](../../../specs/testing/001-test-framework/index.md#outcome-taxonomy).

## Timeouts and Triage

The full suite launches many Godot processes and can take several minutes. Use a command timeout that is comfortably
above the observed suite duration before treating a run as hung.

When a run fails, classify the failure before acting:

- **Assertion failure** — test reached the expected runtime and the behaviour under test failed.
- **Framework/runtime failure** — Godot process startup, import, scene loading, timeout, or result transport failed.
- **Environment failure** — missing display server, missing import cache, unavailable renderer, or external timeout.

Classify from observed evidence, not convenience. Never loosen timing, shorten the symptom observation window, or label
an assertion failure as environmental merely to make a run pass. Timing bounds must follow the approved behaviour or an
independent justification rather than the current implementation's output.

### Recovery Ladder

Work the rungs in order; after each fix, rerun the exact narrow selector:

1. **Protocol fault or unexpected result:** keep the captured output, fix the runtime/protocol cause, then rerun the
   exact selector. The affected test is `error`; its tainted session is discarded and later tests receive a
   replacement.
2. **Session-ready or per-test timeout:** use the captured output to identify the blocked startup or test. Narrow
   the run and increase only `ALLEYCAT_GODOT_PREFLIGHT_TIMEOUT_MS` or `ALLEYCAT_GODOT_RUN_FACT_TIMEOUT_MS` when
   justified. The timed-out session is killed; the active test is not retried automatically.
3. **Godot crash or non-reusable session:** treat the affected result as `error`, inspect the output, correct the
   cause, and rerun the exact selector. A `SessionReusable:false` result also discards the session; a nominal
   `passed` result with that value becomes `error` because baseline restoration failed.
4. **Import cache or preflight failure:** confirm `GODOT_PATH` and the repository root, run the optional import
   preflight before the dynamic-load probe, then rerun the narrow selector. If it still fails, keep the probe/import
   stdout and stderr with the report:

   ```bash
   ALLEYCAT_INTEGRATION_IMPORT_PREFLIGHT=1 \
     dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- \
     --test-class AlleyCat.IntegrationTests.Testing.ReusableSessionIntegrationTests
   ```

5. **Windowed environment failure:** use Xvfb when available. If a renderer-dependent test — including the final
   handoff windowed gate — cannot obtain a display, report the environment limitation; never silently replace the
   required windowed coverage with headless execution.
6. **Live LLM failure:** classify before acting. A secret-free `AITest:…` configuration message means a local
   settings problem — fix your user override. A per-test timeout means the fact budget was exceeded — narrow the run
   and raise `ALLEYCAT_GODOT_RUN_FACT_TIMEOUT_MS` for slow providers or larger declared batches. A valid
   below-threshold trial or a sanitised fail-closed metric failure is a genuine recorded result — inspect the
   scenario's trace artefacts and the deterministic assertions; do not retry until it passes. An invalid-evaluation
   or execution-failure trial category points at judge output, transport, or harness causes — diagnose those from the
   partial trace instead of re-running the batch.

## Handoff Reporting

For final handoff, report:

- exact integration command used;
- whether the run was windowed or headless, and why headless was valid if used;
- whether the run used Xvfb (`xvfb-run`) and any software-rendering caveats observed;
- pass/fail counts;
- duration or timeout used;
- any known limitations, especially if only targeted or headless-safe tests were run.
