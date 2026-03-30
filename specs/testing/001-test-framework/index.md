---
id: TEST-001
title: Integration Test Framework
---

# Integration Test Framework

## Requirement

Provide a dependable integration test framework for behaviours that require Godot runtime APIs. It must support local and headless execution, selective runs, stable test identity, and actionable diagnostics.

## Current State

- Project split is complete:
  - framework code is in `test-framework/`
  - integration tests are in `integration-tests/`
- Microsoft Testing Platform hook is wired and registers a custom Godot-backed test framework.
- Discovery scans parameterless `[Fact]` methods and publishes deterministic node UIDs.
- UIDs are deterministic hashes of canonical method identity (declaring type identity, method name, generic arity, and parameter types); persisted UIDs from older identity schemes may not match.
- Two Godot-backed execution paths exist:
  - **probe mode** (`--integration-probe`) validates dynamic load and runtime readiness
  - **run-fact mode** (`--integration-run-fact`) executes one test method in Godot
- Runtime command runner exists in game code and performs dynamic assembly loading and dependency resolution.
- Process execution is hardened:
  - start failure handling
  - concurrent stdout/stderr draining
  - configurable preflight/run/cleanup timeouts
  - forced process-tree cleanup on timeout/cancellation
- Structured result parsing is implemented using `ALLEYCAT_INTEGRATION_TEST_RESULT:` JSON payloads, mapped to passed/failed/error outcomes.
- Baseline sample integration test exists and validates end-to-end discovery + execution.

## Per-Test Lifecycle Support

The integration framework supports xUnit-style per-test lifecycle contracts through interfaces and constructors, so tests can use familiar APIs without attribute-based setup/teardown.

### Supported Now

- One test-class constructor invocation per test method execution.
- `IDisposable.Dispose` for synchronous post-test cleanup.
- `IAsyncDisposable.DisposeAsync` for asynchronous post-test cleanup.
- `Xunit.IAsyncLifetime.InitializeAsync` and `Xunit.IAsyncLifetime.DisposeAsync` for asynchronous setup and teardown.

### Not Supported Yet

- `IClassFixture<TFixture>`.
- `ICollectionFixture<TFixture>`.
- Collection-level lifecycle orchestration.
- NUnit-style `[SetUp]` and `[TearDown]` attributes.

## Lifecycle Invocation Policy

High-level execution order for each selected test method is:

1. Construct test class instance.
2. Run async setup when `Xunit.IAsyncLifetime.InitializeAsync` is implemented.
3. Execute test method.
4. Always attempt teardown in finally-style flow:
   - run `Xunit.IAsyncLifetime.DisposeAsync` when implemented;
   - then run `IAsyncDisposable.DisposeAsync` and/or `IDisposable.Dispose` when implemented.

### Failure Handling Policy

- Setup failure fails the test method and skips normal test-body execution.
- Teardown is always attempted after test-body execution, including when assertions fail.
- If both test execution and teardown fail, the outcome is reported as **failed** with the test-body failure as primary and teardown failure appended as secondary diagnostic detail.

## Rollout Note

Per-test lifecycle support via constructor/`IDisposable`/`IAsyncDisposable`/`IAsyncLifetime` is newly introduced in this spec revision. Fixture-scope orchestration remains future work.

## Supported CLI Options

- `--test-class <Fully.Qualified.TypeName>`
  - Narrows selection to tests declared on the exact type.
- `--test-method <Fully.Qualified.TypeName.MethodName>`
  - Narrows selection to one exact test method.
- Precedence rule:
  - If both are supplied, `--test-method` takes precedence over `--test-class`.
- Current limitation:
  - Advanced trait/category filters are not yet supported.

## In Scope

- Discovery and execution of Godot-dependent integration tests.
- Headless local execution.
- Selective execution using filters/UID selection.
- Clear separation of framework/runtime errors versus assertion failures.
- Minimal, practical guidance for authoring and running tests.

## Out of Scope

- Replacing unit tests in `tests/`.
- Defining gameplay-specific assertions for all systems.
- Non-essential editor UX work.
- Load/performance benchmarking infrastructure.

## Remaining Work

1. **Multi-test session model (highest priority)**
   - Move beyond one-process-per-test where safe.
   - Reuse runtime sessions to cut startup cost while preserving determinism.
2. **Isolation fixtures and lifecycle contracts**
   - Add shared fixture scopes for scene tree, autoloads, and temporary data (`IClassFixture`/`ICollectionFixture` equivalents).
   - Define explicit reset guarantees between tests.
3. **Richer filtering and metadata**
   - Add trait/category filtering and clearer filter-to-selection feedback.
   - Print the selected test list before execution for easier debugging.
4. **Diagnostics and reporting**
   - Improve failure summaries (command, timeout source, relevant output tail).
   - Emit machine-readable artefacts for local tooling and reports.
5. **Documentation and run flow**
   - Add concise authoring and troubleshooting guides.
   - Finalise defaults/prerequisites for stable integration runs.

## Acceptance Criteria

1. A new contributor can add and run a Godot-backed integration test locally using documented steps.
2. The integration suite runs headlessly with deterministic pass/fail/error mapping.
3. Selective execution runs only the requested subset in normal feature workflows.
4. Framework/runtime failures are clearly distinguishable from assertion failures.
5. Isolation fixtures and guidance prevent cross-test contamination across repeated runs.
6. Docs cover quick start, filtering, diagnostics, and common recovery steps.

## References

- @test-framework/src/TestingPlatformBuilderHook.cs
- @test-framework/src/GodotTestFramework.cs
- @test-framework/AlleyCat.TestFramework.csproj
- @test-framework/AlleyCat.TestFramework.Tests.csproj
- @integration-tests/AlleyCat.IntegrationTests.csproj
- @integration-tests/src/Sample/SampleIntegrationTests.cs
- @game/src/Testing/TestRuntimeRunner.cs
- @specs/index.md
