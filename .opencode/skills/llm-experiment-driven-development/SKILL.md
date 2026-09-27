---
name: llm-experiment-driven-development
description: Use when the user asks for a live-LLM test or experiment, or when developing or verifying NPC behaviour that deterministic tests cannot verify — for example whether the model actually uses supplied context, which prompt or context presentation produces better behaviour, or how an agent behaves across a developing scene. Covers designing, running, judging, and reporting controlled experiments with the integration-test harness. Not for routine Mind API changes; ordinary deterministic tests verify those.
---

# LLM Experiment-Driven Development

Use this skill when a behavioural question about an NPC's LLM cannot be answered by deterministic tests alone. Running,
gating, timeouts, and Xvfb/headless usage follow the `godot-integration-testing` skill; do not duplicate them here.

## Scope

- Default to deterministic tests. Most Mind API changes — mechanics, rendering, scheduling, tool plumbing, prompt
  structure — are fully verified by ordinary unit and integration tests; they need no live model.
- Reach for a live experiment only for model-dependent behaviour: whether the model notices and uses supplied
  context, which of several input designs produces better behaviour, how the agent responds as a scene develops.
- Live experiments are billable. Run them when the user requests one or has approved a planned experiment and its
  budget — never automatically, and never as an implicit pre-handoff gate.

## Core Principle

When an experiment is warranted, treat the model-facing request — system instruction, per-request context messages,
and tool schemas — as an experimental input contract:

1. Discover the contract empirically with live models FIRST, through controlled experiments.
2. Design production APIs (derived observation types, prompt sections, tools) to produce the winning contract.
3. Lock the contract in with ordinary deterministic tests.

Iterate on candidates in the experiment harness, not directly in production assets: an untested production change
cannot distinguish a real behavioural improvement from noise, and the production wiring becomes the experiment.

## Experiment Kinds

- **Single-request** — measures decision quality at one boundary. Cannot prove behaviour across a developing scene.
- **Scripted multi-request sequence** — fixed continuations drive the conversation, proving sustained behaviour
  across developments. Cannot prove that gameplay produces the evidence.
- **Runtime-connected** — real gameplay feeds the requests, proving the runtime produces the evidence the winning
  contract needs. Slowest and most expensive; use it after a contract already looks strong.

## Fixed-Case, Breadth-First Design

- Hold one representative situation, model, request settings, and tool inventory fixed across arms.
- Vary input *mechanisms* broadly — evidence presentation, message placement, instruction scope, derived events,
  grounding context, images — not minor rewordings of one sentence.
- Compare every arm against a production-shaped baseline, so results indicate improvement rather than absolute
  quality.
- Do not tune wording until a mechanism winner has emerged.

## Judging

- Prefer behaviour-level categories over numeric vibes — for example grounded-spatial-uptake,
  spatially-indeterminate, grounded-interaction-failure, unsupported-spatial-claim.
- Judges see evidence lists and ground truth, but never arm identities, thresholds, or target-facing criteria.
- Parse fail-closed: a malformed verdict is an invalid evaluation, never a pass.

## Fixed Declared Batches

- Declare trial counts, outcome categories (success, failure, invalid evaluation, execution failure), and the
  aggregation rule before running.
- Report every attempted trial in its category; a failure consumes its slot.
- Never retry until pass and never extend the batch to improve a ratio.

## Verify Delivered Evidence First

- Inspect what the model actually received — readable request logs — before interpreting behaviour; the delivered
  request, not the intended one, is the input under test.
- The primary artefact is a readable variant-comparison document: verbatim changed fragments shown once, per-arm
  outcomes alongside.
- Emit per-trial tool-call logs only when tool mechanics themselves are under study.

## Recording

Record in run metadata: model and host, request settings, prompt/variant SHA-256 pins, script and judge prompt
versions, token usage, and timestamps.

## Generalisation

After a winner emerges on the fixed case, test it across contrasting scenarios — different scenes, distances,
occlusion, crowd density, dialogue states — before promoting it.

## Promotion

- Findings become production specs, then implementation, then deterministic unit and integration tests.
- A harness pass alone never closes a gameplay defect: representative runtime regression over the full observation
  window is required.

## Harness Entry Points

- `LiveLLMScenarioExecutor` — runs a bounded, declared scenario against borrowed clients and records its evidence
  trace.
- `LiveLLMEvidenceEvaluation` — judge evaluation over a completed execution's captured evidence.
- `LiveLLMBatch`/`LiveLLMTrial` — fixed-batch aggregation with pre-declared trial counts and categories.
- `LiveLLMTraceWriter` — writes sanitised, versioned traces under `game/temp/live-traces`, git-ignored via the
  `game/temp/*` convention.
- Ad-hoc export pattern — OpenAI-compatible request bodies under `game/temp/adhoc/` for manual testing in external
  tools.

Contracts for these entry points: [TEST-001](../../../specs/testing/001-test-framework/index.md#live-llm-testing).

## Status

This skill codifies an in-progress body of practice; refine it as experiments conclude.
