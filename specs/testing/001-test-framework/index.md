---
id: TEST-001
title: Integration Test Framework
---

# Integration Test Framework

## Requirement

Provide a dependable integration test framework for behaviours requiring Godot runtime APIs. It must support
local and headless execution, selective runs, stable test identity, and actionable diagnostics.

## Goal

Enable contributors and agents to validate Godot-runtime behaviour with repeatable, debuggable integration-test runs.

## User Requirements

1. Developers must be able to run targeted integration tests locally with clear pass/fail outcomes.
2. Failure diagnostics must clearly indicate whether failures originate from framework/runtime issues or
   test assertions.
3. Test authoring should follow familiar xUnit lifecycle patterns for predictable maintenance.

## Technical Requirements

1. The framework must support deterministic discovery and execution of Godot-backed parameterless `[Fact]` tests.
2. Headless and windowed execution controls must be explicit and overrideable via attribute and CLI policy.
3. Runner process management must handle startup, timeout, output capture, and cleanup robustly.
4. Result transport must use structured payload parsing with stable identity semantics for selection and reporting.
5. Per-test lifecycle must support constructor injection, `IDisposable`, `IAsyncDisposable`, and
   `IAsyncLifetime` contracts.

## In Scope

- Discovery and execution of Godot-dependent integration tests.
- Headless and windowed local execution.
- Selective execution using filters and UID selection.
- Clear separation of framework or runtime errors versus assertion failures.
- Per-test lifecycle support via constructor and disposal patterns.

## Out Of Scope

- Replacing unit tests in `tests/`.
- Defining gameplay-specific assertions for all systems.
- Non-essential editor UX work.
- Load and performance benchmarking infrastructure.
- Collection-level fixtures (`IClassFixture`, `ICollectionFixture`).

## Lifecycle Invocation Policy

For each test method execution:

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

## Supported CLI Options

- `--test-class <Fully.Qualified.TypeName>` — narrows selection to tests on the exact type.
- `--test-method <Fully.Qualified.TypeName.MethodName>` — narrows selection to one exact test method.
- `--headless` — forces all tests to run in headless mode. Overrides per-test and per-class `HeadlessAttribute`
  settings. Useful for CI environments.

**Precedence:** If both `--test-class` and `--test-method` are supplied, `--test-method` takes precedence.

**Limitation:** Advanced trait or category filters are not yet supported.

## Remaining Work

1. **Multi-test session model** — reuse runtime sessions to reduce startup cost while preserving determinism.
2. **Richer filtering** — add trait or category filtering and print selected test list before execution.
3. **Diagnostics** — improve failure summaries and emit machine-readable artefacts.
4. **Documentation** — add authoring and troubleshooting guides.

## Acceptance Criteria

1. The specification defines both user-facing test-workflow outcomes and technical framework contracts.
2. A new contributor can add and run a Godot-backed integration test locally using documented steps.
3. The integration suite supports both headless and windowed execution with deterministic pass/fail/error
   mapping.
4. Selective execution runs only the requested subset in normal feature workflows.
5. Framework or runtime failures are clearly distinguishable from assertion failures.
6. Per-test lifecycle via constructor and disposal patterns is supported.
7. Docs cover quick start, filtering, diagnostics, and common recovery steps.

## References

- @test-framework/src/TestingPlatformBuilderHook.cs
- @test-framework/src/GodotTestFramework.cs
- @test-framework/AlleyCat.TestFramework.csproj
- @test-framework/AlleyCat.TestFramework.Tests.csproj
- @integration-tests/AlleyCat.IntegrationTests.csproj
- @integration-tests/src/Sample/SampleIntegrationTests.cs
- @game/src/Testing/TestRuntimeRunner.cs
- @specs/index.md