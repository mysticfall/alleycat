---
name: godot-integration-testing
description: Use for authoring, running, triaging, or reporting Godot integration tests, especially before final handoff.
---

# Godot Integration Testing

Use this skill when you need to author, run, triage, or report `integration-tests/AlleyCat.IntegrationTests.csproj`.

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
  hardware. For example, `UI/SplashScreenIntegrationTests.SplashScreen_FadeLifecycleFixtures_MatchConfiguredTimingAndCompletionSignal`
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
dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- \
  --test-class Fully.Qualified.TypeName
dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- \
  --test-method Fully.Qualified.TypeName.MethodName
```

If both filters are supplied, `--test-method` takes precedence over `--test-class`.

## Fixture Authoring

- Integration tests must not depend on broad production or content scenes, including temporary playtest environments.
- Use focused fixtures that mimic only the production wiring relevant to the behaviour under test.
- If a fixture needs world/environment lighting, instance `res://assets/testing/test_environment.tscn` by default instead
  of creating an ad-hoc `WorldEnvironment`, unless the test contract specifically requires custom environment settings.
- Include only relevant components and wiring unless the test explicitly validates component conflicts or interaction
  between multiple systems.
- Character fixtures should reference only the reference female character.
- Avoid production role installers in component fixtures; use installers only for installer tests or dedicated
  complete-character wiring/runtime-scene tests.
- Component, IK, pose, hand, eye, and locomotion tests should use minimal authored fixtures or direct resource setup so
  each test focuses on the code under test and is not affected by unrelated runtime wiring.

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
- whether the run used Xvfb (`xvfb-run`) and any software-rendering caveats observed;
- pass/fail counts;
- duration or timeout used;
- any known limitations, especially if only targeted or headless-safe tests were run.
