---
id: TEST-001
title: Integration Test Framework
---

# Integration Test Framework

## Requirement

Provide a dependable integration test framework for behaviours requiring Godot runtime APIs. It must support local and
headless execution, selective runs, stable test identity, actionable diagnostics, and materially faster full-suite
execution through persistent reusable runtime sessions. Integration tests that call live external LLM services must be
opt-in so default runs need no credentials and incur no provider cost. The same opt-in boundary authorises live LLM
experiment harnesses: bounded, caller-declared scenarios that may make several provider requests and offer tools,
capture inspectable evidence traces, and aggregate a fixed trial batch into one reported outcome.

## Goal

Enable contributors and agents to validate Godot-runtime behaviour with repeatable, debuggable integration-test runs,
keeping full-suite execution fast enough for routine pre-handoff verification, while live LLM tests stay explicitly
gated so only opted-in runs contact real providers. Live LLM support doubles as a reusable, Mind-independent
experiment workflow: discover effective model inputs empirically through bounded live scenarios, use the captured
evidence to design production APIs, then lock behaviour down with deterministic tests.

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
7. Default suite runs must not require live LLM credentials, network access, or provider cost: live-marked tests stay
   excluded from discovery and execution unless explicitly permitted.
8. Explicitly permitting live tests must add them to a run without removing ordinary tests, so one selected run can
   mix both.
9. A live test excluded by the gate must produce no result at all — never a passed node — so a green run without the
   opt-in never implies live coverage.
10. Live-test configuration, diagnostics, and evidence traces must never expose credentials: private settings are
    supplied locally by each contributor, and failure messages stay secret-free.
11. Contributors must be able to author live experiments that make a declared, bounded number of provider requests per
    test — including multi-request conversations and requests that offer tools — under the same opt-in gate, without a
    framework-imposed single-shot limit.
12. A completed live experiment must account for every attempted trial in distinct reported categories — successful,
    valid-but-unsuccessful, invalid evaluation, and execution failure — so a passing result never hides dropped trials.
13. Trial evidence — requests, tool exchanges, responses, and their ordering — must remain inspectable after the run so
    contributors can diagnose behaviour without re-running providers.

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
16. Operational how-to guidance for contributor run, filter, diagnostic, timeout, and recovery workflows is carried
    by the `godot-integration-testing` agent skill (`.opencode/skills/godot-integration-testing/SKILL.md`); this
    specification owns the contractual limits those workflows must respect.
17. Integration tests requiring live LLM access must carry `[LiveLlm]` on the method or its declaring class
    (inherited; either marker marks the test) and are governed by the central gate defined in
    [Live LLM Testing](#live-llm-testing).
18. The gate must apply to both discovery and execution with eligibility defined as: existing UID/CLI selection AND
    (`--live-llm` supplied OR test not live-marked). Exact `--test-class`/`--test-method` selectors and MTP UID
    execution filters must not bypass it, and an excluded live test must produce no discovered node, no in-progress
    node, no terminal node, and no passed result.
19. The `--live-llm` option must be zero-argument (arguments rejected during validation), default false, and fail
    closed when the command-line options service is unavailable. Supplying it permits live-marked tests without
    excluding ordinary tests and leaves `--headless`, selector precedence, session lifecycle, preflight, and result
    semantics unchanged.
20. Live test clients must resolve settings only from the `AITest` section of the running `Game` merged
    configuration — never the production `AI` section, with no fallback — validating required `Host`, `Model`, and
    `ApiKey` and optional positive `Timeout` (seconds) with secret-free, section-correct diagnostics, as defined in
    [Live LLM Testing](#live-llm-testing).
21. Live test execution must separate execution, evaluation, and assertion as defined in
    [Live LLM Testing](#live-llm-testing): a scenario declares a bounded number of target requests with scripted or
    explicitly generated continuations, the harness executes the scenario and captures evidence, evaluation consumes
    the captured evidence without re-running the target, and assertions consume the reported results.
22. The experiment or test must own its `LiveLLMClientPair` for its complete lifetime: execution and evaluation
    operations borrow the clients and must never dispose them. The single-shot evaluation helper may remain only as
    an actively-used convenience composition of these contracts, with its single-request and text-only restrictions
    treated as fixture-specific choices, not harness limits.
23. Scenarios may send tool-bearing requests: request options may carry tool definitions and settings, and responses
    containing tool calls are captured as legitimate experimental output whose validity is decided by
    scenario-specific checks. The harness must never automatically execute model-selected functions; tool results are
    scripted or produced by explicit bounded handlers and are labelled as simulated, not executed gameplay.
24. Judge evaluation must follow the fail-closed contracts in [Live LLM Testing](#live-llm-testing): a validated
    single declared numeric metric on the documented 1–5 rubric, an inclusive threshold, sanitised failure output,
    and no retry-until-pass.
25. Live experiments must use fixed-batch aggregation as defined in [Live LLM Testing](#live-llm-testing): trial
    counts and aggregation rules are declared before execution, every attempted trial is retained and reported in
    distinct categories, a valid below-threshold score is a recorded unsuccessful trial rather than an immediate
    abort, malformed judge output is an evaluation error that can never pass, an incomplete batch cannot pass as a
    successful subset, and execution neither retries failures nor stops once enough successes accumulate.
26. Scenarios must produce versioned, sanitised, inspectable evidence traces as defined in
    [Live LLM Testing](#live-llm-testing), preserved partially on failure and stored separately from evaluation
    results; credentials, configuration or client objects, and raw provider exceptions are never serialised into
    traces or assertion output.
27. Live timing must account for target, continuation-generator, and judge requests: the host's whole-fact timeout
    must cover the entire declared batch, as defined in [Live LLM Testing](#live-llm-testing).
28. Evaluation dependencies are permitted only in the integration-test project; the game project must not reference
    `Microsoft.Extensions.AI.Evaluation.Quality` or its evaluation-core transitives.

## In Scope

- Discovery and execution of Godot-dependent integration tests.
- Headless and windowed local execution.
- Selective execution using filters and UID selection.
- Clear separation of framework or runtime errors versus assertion failures.
- Per-test lifecycle support via constructor and disposal patterns.
- Persistent session execution: mode-partitioned reusable sessions, serial in-session execution, the session wire
  protocol, runtime-owned baseline restoration, and session restart semantics.
- Opt-in gated discovery and execution for `[LiveLlm]` integration tests, the dedicated `AITest` configuration
  boundary, the reusable live client and evaluation support layer, and the experiment harness contracts: bounded
  multi-request scenarios, tool-bearing exchanges, caller-owned client lifetimes, evidence traces, and fixed-batch
  trial aggregation.

## Out Of Scope

- Replacing unit tests in `tests/`.
- Defining gameplay-specific assertions for all systems.
- Non-essential editor UX work.
- Load and performance benchmarking infrastructure.
- Collection-level fixtures (`IClassFixture`, `ICollectionFixture`).
- Parallel execution within a session; execution is strictly serial.
- OS-process and CLR isolation between tests within a session.
- Provisioning, distributing, or storing live LLM credentials; each contributor supplies a private local override.
- Result caching, dashboards, or hosted reporting for live experiments beyond the per-test MTP nodes, the in-fact
  batch report, and the local trace artefacts.
- Divergent target/judge provider configuration; the injectable client pair is the seam, but both clients use the
  same `AITest` settings today.

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
6. Free every root-owned `Game` instance — matched by type, not name, so roots renamed to generated `@Node@N` names
   cannot escape — and await observed completion (freed nodes invalid, no root `Game` remaining, no registered
   singleton) within a bounded frame budget that warns on exhaustion; a surviving `Game` keeps the singleton claimed,
   so the next fixture's `Game.SetInstance` would fail the single-instance guarantee.
7. Re-create a fresh `Global` autoload; preserving it would leak its service provider, logging, XR, and `Game.Instance`
   state.
8. Restore the fresh-process startup scene arrangement.
9. Validate the baseline: runner alive, tree unpaused, `Global` present, no unexpected root children.

The runtime also drains pending finalisers — one full collection plus a finaliser-queue wait — after each test's
baseline restoration while the session is idle between commands, and again before session quit, so dangling
`GodotObject` wrappers are disposed against a live engine rather than racing a later test or teardown-time
object-database destruction.

The existing isolated-`Game` policy still removes the runtime global before tests that require it; the baseline
re-creates it afterwards. Removal follows the same teardown contract: free every root-owned `Game` by type and await
observed completion within a bounded frame budget — never a name-keyed free or a fixed frame count.

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

Timeout environment variables keep their roles. Values are positive milliseconds; an unset, invalid, or
non-positive value falls back to the listed default:

- `ALLEYCAT_GODOT_PREFLIGHT_TIMEOUT_MS` (default 30,000) — dynamic-load probe and session-start/`ready` timeout.
- `ALLEYCAT_GODOT_RUN_FACT_TIMEOUT_MS` (default 120,000) — per-request timeout bounding one dispatched test request.
- `ALLEYCAT_GODOT_CLEANUP_TIMEOUT_MS` (default 5,000) — wait for matching `shutdown-complete` during graceful
  shutdown, then force termination if it is absent, invalid, mismatched, or late.
- `ALLEYCAT_GODOT_IMPORT_TIMEOUT_MS` (default 120,000) — the optional import preflight.

The per-request timeout bounds the whole dispatched fact with no cooperative cancellation token reaching the test
body; live LLM tests must therefore fit their entire declared batch — every scenario request, continuation,
evaluation, and the aggregation — inside it (see [Live LLM Testing](#live-llm-testing)).

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

## Live LLM Testing

This section is a normative dependency for the live-LLM Technical Requirements above. It defines the opt-in gate, the
dedicated configuration boundary, and the reusable client, experiment, and evaluation support layer for integration
tests that call real LLM providers. Operational workflows live in the `godot-integration-testing` agent skill, and
live-LLM experiment methodology in the `llm-experiment-driven-development` skill.

### Opt-In Gate

- `[LiveLlm]` (`test-framework/src/LiveLlmAttribute.cs`) is valid on methods and classes, is inherited, and marks a
  test when either the method or its declaring type carries it.
- The zero-argument `--live-llm` CLI flag defaults to false, rejects arguments during validation, and fails closed
  (live tests stay excluded) when the command-line options service is unavailable.
- One gate in `GodotTestFramework.FilteredTests` serves both discovery and execution. Eligibility is: existing
  UID/CLI selection AND (`--live-llm` present OR test not live-marked). Exact `--test-class`/`--test-method`
  selectors and MTP UID execution filters cannot bypass it.
- An excluded live test produces no discovered node, no in-progress node, no terminal node, and no test process
  launch; it can never be reported as passed.
- `--live-llm` only permits; it never excludes ordinary tests. `--headless`, selector precedence, session lifecycle,
  preflight, and result semantics are unchanged by the flag.

### Configuration Boundary

- Live settings resolve only from the `AITest` section of the merged configuration of the running `Game`
  (`integration-tests/src/Support/AI/LiveLLMClientFactory.cs`). The production `AI` section is never consulted and
  no production default substitutes for a missing test setting.
- The shipped `game/AlleyCat.yaml` intentionally defines no active `AITest` section; a commented-out example
  documents its shape. Private values come from the contributor's local user override (`user://AlleyCat.yaml`) and
  must never be committed; the factory reading the running `Game` configuration is the only authorised credential
  boundary.
- Required settings: `Host` — an absolute HTTP(S) URL including the API base path (for example
  `https://api.openai.com/v1`); `Model`; `ApiKey`. Optional: `Timeout` — a positive number of seconds; omitted keeps
  the existing client default. The shape mirrors `AIOptions`.
- Validation failures are fixed, section-correct, and secret-free: they name keys (for example `AITest:Timeout`),
  distinguish configured from missing values, and never echo configured values. Each invalid shape produces its own
  message: missing or blank `Host`, `Model`, or `ApiKey`; a `Host` that is not an absolute HTTP(S) URL; a `Host`
  without an API base path; and a non-positive `Timeout`. Test-local clients disable SDK retries
  (`ClientRetryPolicy(0)`) and message-content logging without changing production logging.
- The game's production `AI` loading paths are unchanged; the arbitrary-section settings loader is an internal
  overload of `OpenAIClientProvider.OpenAIClientProviderSettings`
  (`game/src/Mind/AI/Provider/OpenAIClientProvider.cs`).

### Experiment Execution Model

- `LiveLLMClientFactory.CreateLiveClients()` returns a disposable `LiveLLMClientPair` holding separately constructed
  target and judge `IChatClient`s built from the same `AITest` settings. The pair's public constructor keeps each
  client independently injectable — the design boundary for deterministic fakes and future target/judge substitution —
  although no divergent target/judge configuration exists today.
- Live tests are experiments with three separated stages: **execution** runs a declared scenario and captures
  evidence, **evaluation** judges the captured evidence, and **assertion** turns reported results into test outcomes.
  Evaluation must consume captured evidence and never re-run the target to obtain it.
- A scenario declares a bounded number of target requests before execution. One request is the minimal scenario;
  multi-request sequences interleave target responses with continuations that are either scripted by the experiment
  or generated explicitly — including LLM-generated continuations that may use the same model under a separate
  context. Scenario code may declare a cooperative per-scenario timeout bound (`LiveLLMScenarioBounds.Timeout`)
  enforced by the harness through linked cancellation, with its expiry classified as a `Timeout` execution failure —
  a harness-internal deadline distinct from, and additive to, the host whole-fact budget.
- Execution records every request and response, the supplied continuations and tool results, their ordering, and a
  purpose label per entry (for example target, continuation, judge) into the scenario's evidence trace.
- The experiment or test owns its `LiveLLMClientPair` for its complete lifetime. Execution and evaluation operations
  borrow the pair's clients and must never dispose them; the owner disposes the pair exactly once on every path.
- `LiveLLMEvaluation.EvaluateAsync` (`integration-tests/src/Support/AI/LiveLLMEvaluation.cs`) remains the single-shot
  convenience composition: one text-only target request, immediate judge evaluation, fail-closed metric validation,
  inclusive threshold comparison, and disposal of the pair delegated to it. Its single-request, text-only, and
  pair-disposing restrictions are properties of this helper and of the fixtures that choose it — not harness-global
  limits.

### Tool-Bearing Exchanges

- Scenario requests may carry tool definitions and request settings; the execution support must not fix request
  options to null.
- A response containing tool calls is captured as legitimate experimental output. Whether a tool call is expected,
  permitted, or invalid is decided by scenario-specific checks — for example, a text-only fixture treats any
  function call as a failure.
- The harness never automatically executes model-selected functions. Tool results are scripted by the experiment or
  produced by explicit bounded handlers, and every supplied tool result is labelled as a simulated result, never as
  executed gameplay.

### Judge Evaluation

- Judge results are validated fail-closed before scoring: the evaluator declares exactly one metric; the metric is
  present and a `NumericMetric`; its value is finite and within the documented 1–5 rubric; its reason is non-empty;
  it carries no error-severity diagnostics; and its interpretation is not marked failed. The threshold comparison
  is inclusive.
- Malformed judge output is an evaluation error and can never pass, whatever the aggregation rule.
- Failure output carries metric name, score, interpretation rating, threshold, and bounded sanitised judge reasoning
  only. Raw provider exceptions, diagnostic collections, configuration records, and credential-shaped values are
  never serialised into assertion output.
- `Microsoft.Extensions.AI.Evaluation.Quality` (and its transitive evaluation core) is referenced only by
  `integration-tests/AlleyCat.IntegrationTests.csproj`, never by the game project.

### Fixed-Batch Aggregation

- An experiment declares its trial count and aggregation rules before execution starts; neither may be adapted to the
  results observed so far.
- Every attempted trial is retained and reported in distinct categories: successful trials, valid but unsuccessful
  trials (for example a below-threshold score), invalid evaluations (malformed judge output), and execution
  failures.
- A valid below-threshold score is recorded as an unsuccessful trial; it is not an immediate abort.
- An incomplete batch cannot pass as a successful subset: trials that never ran count against the declared batch,
  not as absent.
- There is no retry-until-pass and no early stop once enough successes accumulate; a failed trial is a recorded
  outcome, and the batch runs to its declared end or fails as incomplete.

### Trace Artefacts

- Every scenario produces a versioned, sanitised, inspectable evidence trace recording: requests, including tool
  schemas and request settings; responses, including tool-call ids and arguments; supplied tool results with their
  simulated labelling; ordering; purpose labels; and timing and usage data when the provider supplies them.
- Partial traces are preserved when a trial fails or is interrupted mid-scenario.
- Evidence traces are stored separately from evaluation results, so raw evidence survives independently of judge
  outcomes.
- Credentials, configuration or client objects, and raw provider exceptions are never serialised into traces or
  assertion output; trace sanitisation matches the assertion-output rules above.
- Raw trace artefacts live in `game/temp/live-traces`: `game/temp` is the project's designated git-ignored location
  for persisted temporary files (the `# AI agents` ignore block), so no dedicated ignore entry is needed. Sanitised
  summaries and findings may be committed to the repository; raw artefacts must not be.

### Cost and Timing

- Every permitted live test makes real, billable provider calls. Per trial, account for the declared target requests,
  any continuation-generator requests, and the judge request. All provider work is serial within the fact.
- Parameterless facts receive no runner cancellation token; `ALLEYCAT_GODOT_RUN_FACT_TIMEOUT_MS` bounds the host's
  whole-fact wait and must cover the entire declared batch — every scenario request, continuation, evaluation, and
  the aggregation. `AITest:Timeout` is only the per-request SDK network timeout. No new timeout mechanism exists;
  raise the fact budget for slow providers or larger declared batches rather than expecting cooperative
  cancellation.
- The representative fixture `integration-tests/src/Mind/AI/LiveRoleplayIntegrationTests.cs` is marked `[LiveLlm]`
  and `[Headless]`; it asserts the real rendered prompt's committed lore facts before any network call, performs
  the live target call through the single-shot helper, asserts response shape deterministically, then judges
  groundedness with an explicitly passed `GroundednessEvaluatorContext` at threshold 5 — deliberately stricter than
  the evaluator's built-in greater-than-or-equal-to-4 interpretation. Its text-only, single-request shape is a
  fixture choice, not a harness limit.

## Supported CLI Options

- `--test-class <Fully.Qualified.TypeName[,...]>` — narrows selection to tests whose declaring type exactly matches
  any listed fully qualified class name.
- `--test-method <Fully.Qualified.TypeName.MethodName[,...]>` — narrows selection to the listed exact test methods.
- `--headless` — forces all tests to run in headless mode. Overrides per-test and per-class `HeadlessAttribute`
  settings. Intended for tests known to be safe without renderer-backed behaviour.
- `--live-llm` — permits integration tests marked `[LiveLlm]` to run; live-marked tests stay excluded without this
  flag. Takes no arguments and never excludes ordinary tests; see [Live LLM Testing](#live-llm-testing).

**Selector lists:** each selection option takes one value that may be a comma-separated list of exact, fully
qualified selectors, selecting the union of all matches; a single selector behaves exactly as before. Every
`--test-method` entry must be a well-formed `<Fully.Qualified.TypeName>.<MethodName>` selector, or the command is
rejected during validation. Likewise, a `--test-class` list must contain at least one non-empty class name; an
all-empty list (for example `","`) is rejected during validation.

**Precedence:** If both `--test-class` and `--test-method` are supplied, `--test-method` takes precedence and
`--test-class` is ignored.

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
14. Operational how-to guidance for running, filtering, choosing execution modes, configuring and authoring live
    tests, reading results, and recovering from failures is carried by the `godot-integration-testing` and
    `llm-experiment-driven-development` agent skills; this specification contains only contractual limits and no
    contributor-operational instructions.
15. Without `--live-llm`, live-marked tests are absent from discovery and execution — including under exact
    `--test-class`, `--test-method`, and MTP UID selection — producing no discovered, in-progress, or terminal node
    and no passed result; the default suite runs without `AITest` credentials.
16. With `--live-llm`, live-marked tests run alongside ordinary tests under unchanged selector, headless, session,
    preflight, and result semantics.
17. `--live-llm` rejects arguments and fails closed when the command-line options service is unavailable.
18. Live settings resolve only from `AITest` with the documented required/optional shape; missing or invalid
    settings fail with secret-free, section-correct messages that never echo configured values.
19. Evaluation failures stay sanitised (metric name, score/rating, threshold, redacted bounded reasoning), the
    evaluation support layer is verifiable deterministically with fake clients, and no path retries a failed
    evaluation until it passes.
20. A multi-request scenario with tool definitions executes entirely under the existing live gate: tool-call
    responses are captured as evidence, model-selected functions are never automatically executed, supplied tool
    results are labelled simulated, evaluation consumes the captured evidence without re-running the target
    (verifiable with fake clients by counting requests), and the caller-owned pair is disposed exactly once by its
    owner and never by execution or evaluation operations.
21. Fixed-batch aggregation holds: the trial count and aggregation rules are declared before execution, every
    attempted trial is reported in its category, a valid below-threshold score is recorded as an unsuccessful trial
    rather than aborting, malformed judge output is an invalid evaluation that can never pass, an incomplete batch
    cannot pass as a successful subset, and the batch neither retries failures nor stops early on accumulated
    successes.
22. Trace artefacts are versioned, sanitised, and inspectable: a failed or interrupted trial still yields its
    partial trace; traces record requests with tool schemas and settings, responses with call ids and arguments,
    supplied tool results, ordering, purpose labels, and timing/usage when available; raw artefacts live only in the
    ignored `game/temp/live-traces` location; and no credentials, configuration or client objects, or raw provider
    exceptions appear in traces or assertion output.
23. The single-shot evaluation helper remains an actively-used composition of the execution, evaluation, and
    aggregation contracts, and its single-request and text-only restrictions are documented as fixture-specific.
24. Criteria 1-6 and 14 verify User Requirements; criteria 3, 6, 7-14 verify Technical Requirements. Criteria 15-23
    additionally verify User Requirements 7-13 and Technical Requirements 17-28.

## References

- @test-framework/src/TestingPlatformBuilderHook.cs
- @test-framework/src/GodotTestFramework.cs
- @test-framework/src/LiveLlmAttribute.cs
- @test-framework/AlleyCat.TestFramework.csproj
- @test-framework/AlleyCat.TestFramework.Tests.csproj
- @integration-tests/AlleyCat.IntegrationTests.csproj
- @integration-tests/src/Testing/ReusableSessionIntegrationTests.cs
- @integration-tests/src/Support/AI/LiveLLMClientFactory.cs
- @integration-tests/src/Support/AI/LiveLLMClientPair.cs
- @integration-tests/src/Support/AI/LiveLLMEvaluation.cs
- @integration-tests/src/Mind/AI/LiveRoleplayIntegrationTests.cs
- @game/src/Testing/TestRuntimeRunner.cs
- @specs/index.md
