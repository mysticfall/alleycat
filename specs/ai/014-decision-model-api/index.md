---
id: AI-014
title: Decision Model API
---

# Decision Model API

## Requirement

Provide a C# API for typed decisions over shared evidence, independent of conversational LLM generation.
This is the normative contract for implementing and reviewing the decision subsystem, not a gameplay integration.

## Goal

Allow developers to evaluate the same bounded questions through the local SystemOne service, OpenRouter or OpenAI
without confusing their wire formats, inheriting conversational settings, or making unintended paid requests. The
unauthenticated local service is the zero-configuration default; OpenRouter remains the first intended hosted
experiment provider, and documented compatibility does not establish live availability or model quality.

## User Requirements

1. Developers can submit text or structured JSON evidence with named boolean, choice and ordered-score questions,
   receiving typed answers whose question and option identities remain intact.
2. Users can configure a decision backend separately from conversational AI through shipped/user YAML precedence.
   With no decision configuration, the default backend is the local unauthenticated SystemOne service, so a consumer
   gets working typed decisions with zero configuration and no API key. Ordinary gameplay still starts without
   sending decision requests automatically.
3. Callers can cancel evaluation and distinguish refusal, configuration, transport and invalid-output failures from
   valid decisions. Missing optional metadata must not become invented confidence, probabilities or cost.

## Technical Requirements

### Public Boundary and Data

1. The AI subsystem owns a small client/provider boundary under `game/src/Mind/AI/Decision/`, exposing asynchronous
   evaluation with caller cancellation. A consumer can explicitly obtain the configured client without invoking
   `IChatClient`, Agent Framework, generative tool calls or Mind scheduling. Concrete C# type names are implementation
   choices; the following data and outcome semantics are mandatory.
2. One request supplies shared evidence as either a text string or a JSON object/array, plus a nonempty ordered set
   of questions with unique, nonblank caller names and plain-text instructions. JSON remains structured evidence,
   not a stringified conversation or executable instructions. Reject unsupported evidence/question features locally.
3. Question and successful answer contracts are:

   | Kind | Question | Answer |
   |---|---|---|
   | Boolean | Instructions defining the condition | Probability that the condition is true, in `[0, 1]` |
   | Choice | Distinct string option values and textual criteria for each | One supplied option value |
   | Score | 2–10 ordered textual level criteria | Fractional score in `[0, level count - 1]` |

   Choice options are nonempty; retain their supplied values and order without renaming or sorting. Score identity
   is the zero-based supplied level index; retain the original criteria and order. Scores mean the
   probability-weighted average of these indices, not a selected level or a normalised `[0, 1]` rating. Boolean
   results are probabilities, not thresholded `bool` values; threshold policy belongs to the consumer.
4. Boolean guidance is instructions-only in the portable API. Do not accept richer true/false provider criteria
   and then discard them on OpenAI. Choice and score criteria are plain text; no provider-specific structured
   guidance is implied by support for JSON evidence.
5. Results are keyed by caller question identity and retain caller question/option ordering independently of response
   ordering. Expose typed answers or explicit typed refusals per question, preserving any provider-supplied refusal
   detail without turning it into a decision. Preserve the resolved model, request identifier when supplied, token
   usage and optional provider/cost metadata. Optional confidence and probability distributions remain absent when
   omitted; do not derive confidence from a distribution or substitute the configured model for a missing resolved one.
6. Validate the whole response before returning it: reject malformed JSON, missing/duplicate/unknown question names,
   answer-kind mismatches except explicit refusals, unknown choices, invalid level indices, and incompatible option
   or rubric mappings. Numeric answers, confidence and probabilities must be finite and within their semantic bounds.
   Supplied distributions must map to the requested options/levels without duplicates, omissions or extras, and
   their probabilities must sum to one within a single symmetric allowance: the sum may deviate from one in either
   direction by at most 0.005 per supplied option/level, plus a documented floating-point epsilon. A supplied score
   distribution must reproduce the reported score within the same family, 0.005 per supplied level plus the
   documented epsilon. These allowances are guards against grossly malformed responses (for example a five-option
   distribution summing 0.9), not statements about provider rounding: hosted providers drift from one in both
   directions through ordinary response formatting (two-decimal rounded probabilities and ±1e-4 mixed-precision
   sums), which is not a correctness failure and must not reject otherwise-correct output. Reject invalid output
   rather than clamp, default or return a partially validated decision set.

### Explicit Provider Dialects

7. `APIKind`, not hostname inference, selects one of three supported JSON POST adapters; the hosted kinds are
   bearer-authenticated, SystemOne sends no credential:

   | Mapping | OpenRouter | OpenAI | SystemOne |
   |---|---|---|---|
   | Default example endpoint | `https://openrouter.ai/api/alpha/decisions` | `https://api.openai.com/v1/decisions` | `http://localhost:8081/v1/systemone` |
   | Evidence | `state`: text or JSON object/array | `input`: text or user message with `input_json.data` | `state`: text or JSON object/array |
   | Questions/answers | Maps keyed by question name | Arrays carrying question `name` | Maps keyed by question name |
   | Boolean | `noul` question; answer `noul` probability | `predicate` question; answer `probability` | `noul` question; answer `noul` probability |
   | Choice criteria | `criteria` map: value → text | `choices` array: `value` and `description` | `criteria` map: value → text |
   | Score criteria | Ordered `criteria` array | Ordered `levels` array using criterion text as `label` | Ordered `criteria` array |
   | Distributions | Maps keyed by option value or score index | Arrays carrying `value` and `probability` | Maps keyed by option value or score index |

   OpenAI score labels preserve criterion text; no separate level-description input is required by this subset.
   Map provider results back to the common identities, including OpenAI's explicit refusal answers. The linked
   provider references define exact wire fields; these are distinct dialects, not host-only compatibility.
   OpenRouter's operation-specific `/api/alpha/decisions` route overrides its generic API server example. SystemOne
   is the local llama.cpp `/v1/systemone` surface: it reuses the OpenRouter wire shapes, writes the configured
   `Model` (or an empty string when unset; the service accepts both), always reports the service's launch-time model
   rather than the configured value, omits request identity/provider/cost metadata, sends no credential, and has no
   refusal answers — provider refusals remain an OpenAI-dialect concept.
8. Use managed, reusable HTTP transport and existing supported dependencies. Do not assume the conversational
   OpenAI .NET `ChatClient` implements Decisions; typed .NET Decisions support is not established by this contract.
   Do not upgrade SDKs speculatively or introduce a generic provider framework.

### Configuration and Transport

9. Decision options and binding are subsystem-owned under [CORE-002][core-options] and [CORE-006][core-config]. Core
   configuration must not reference concrete decision options. Bind the separate `Decision` section from the shared
   `IConfiguration`: user overrides in `user://AlleyCat.yaml` take precedence over `res://AlleyCat.yaml` defaults.
   No credentials, model or reasoning controls fall back to `AI`.
10. The keys are `APIKind` (`OpenRouter`, `OpenAI` or `SystemOne`), `Host` (API base URL), `Model` (provider-specific
    identifier), `ApiKey` (bearer credential), and `Timeout` (finite positive seconds). Absent keys default only on
    the SystemOne dialect: `APIKind` itself defaults to `SystemOne` when absent, `Host` defaults to
    `http://localhost:8081/v1`, `Timeout` defaults to 30 seconds, and `Model`/`ApiKey` are optional and unused (no
    credential is sent). Explicitly selected hosted kinds keep requiring `Host`, `Model`, `ApiKey` and `Timeout`
    exactly as before; any present key overrides its default, and any invalid value still fails naming the affected
    key without exposing its secret value. Validate when a decision client is explicitly requested, before network
    access; an absent section must not break ordinary startup and yields the SystemOne defaults.
11. Append exactly one operation segment to the configured API base path — `decisions` for the hosted kinds,
    `systemone` for SystemOne — preserving `/api/alpha` or `/v1` with or without a trailing slash. `Host` is a base
    URL including its version prefix, never an already-composed operation URL: it must carry a nonempty path and must
    not end with a supported dialect's operation segment (`decisions` or `systemone`). Shipped examples stay
    commented/inactive; real secrets belong only in the user override. With the whole section commented out, the
    zero-configuration default targets the local Docker `kev-decision-api` service:

    ```yaml
    # Zero-configuration default: SystemOne at http://localhost:8081/v1/systemone (no key, no model needed).
    # Decision:
    #     APIKind: OpenRouter
    #     Host: "https://openrouter.ai/api/alpha"
    #     Model: "typesafe/jev-1.14-20261014"
    #     ApiKey: "<set only in user://AlleyCat.yaml>"
    #     Timeout: 30
    ```

    An OpenAI example uses `APIKind: OpenAI`, `Host: "https://api.openai.com/v1"` and `Model: "gpt-6-luna"` with
    independently supplied credentials. An explicit SystemOne section may pin `Host` (default
    `http://localhost:8081/v1`) and `Model` (advisory only: the service's model is fixed at launch, so the value is
    neither validated against nor reported by the server). These are documented model examples, not measured quality
    winners or inferred equivalents; model availability can change.
12. Honour caller cancellation and configured timeout through request dispatch and response reading. Expose
    distinguishable typed outcomes/errors for invalid configuration/input, unsupported features, cancellation,
    timeout, transport failure, non-success HTTP status, invalid response and provider refusal. Exception versus
    result-envelope design is an implementation choice; errors/refusals never become default answers.
13. Bound response reading for success and error bodies, including oversized or malformed output. Non-success status
    remains an HTTP failure with safe status/request metadata, not a successful decision. No automatic retries or
    automatic paid calls during startup or tests. Use existing `ILogger` conventions: API keys and raw request/response
    bodies must not appear in ordinary logs or error messages, including provider error/refusal text.

## In Scope

- The common C# decision boundary, independent configuration, all three wire adapters and managed transport
  lifecycle.
- The zero-configuration SystemOne default targeting the shipped local Docker `kev-decision-api` service.
- Deterministic fixtures, focused validation and a small explicit-client consumer example without gameplay changes.

## Out Of Scope

- Images, OpenRouter Box, conversation/tool histories, structured question guidance and provider-specific extensions,
  including caching/routing controls; reject unsupported requests rather than imply full provider parity.
- Generated explanations, arbitrary generation, action execution or tool calls.
- Local Python inference, new dependencies without approval, runtime polling/feedback, behaviour trees, perception
  changes or watch fixes. This API does not establish AI-010/runtime-experiment acceptance.
- Live hosted tests, quality/latency claims or hosted/local comparisons. Paid experiments require separate approval
  of models, fixed cases, request count and spend cap; deterministic compatibility is not live verification.
  Automated tests must not depend on any live container: one-off live validation of the default SystemOne backend is
  a delivery activity whose evidence is retained under `game/temp/`, not a test-suite dependency.

## Acceptance Criteria

1. **UR-1; TR-1–8:** Deterministic documented request/response fixtures cover all three complete dialects for
   boolean, choice and score, text/JSON evidence, identity/order, fractional zero-based score semantics, optional
   metadata and OpenAI refusal; SystemOne fixtures derive from retained real service captures. A consumer example
   chooses the configured decision client without using the conversational client.
2. **UR-2; TR-9–11, 13:** Verify independent section binding, shipped/user precedence, base paths with/without
   trailing slash for every dialect, missing/invalid settings and finite positive timeout. Missing configuration
   allows ordinary startup and yields the SystemOne defaults; explicitly selected hosted kinds still fail on
   missing required settings; examples are inactive, credentials remain user-owned and startup/tests send no
   automatic requests.
3. **UR-3; TR-2–6, 12–13:** Focused tests cover cancellation, timeout, non-success HTTP, bounded responses,
   malformed/mismatched answers, invalid options/probabilities, the single symmetric distribution-sum and
   score-consistency allowance (drift in both directions accepted, sums outside it rejected) and unsupported
   features. Refusal/error stays typed, optional data is not fabricated and diagnostics expose neither
   credentials nor raw bodies.
4. **TR-1, 8–13:** Affected deterministic tests, solution formatting verification and warnings-as-errors build pass.
   Independent review checks both directions of code/spec alignment, configuration ownership and secret safety;
   live endpoint behaviour, model quality and runtime outcomes remain unproved by these checks. One-off live
   validation evidence for the default SystemOne backend, when performed, is retained under `game/temp/` outside the
   automated suites.

## References

- [AI System](../index.md) and [AI-002: Agent Runtime](../002-agent-runtime/index.md):
  separate conversational ownership.
- [CORE-002: Configuration API][core-options] and [CORE-006: Microsoft Configuration Integration][core-config].
- [kev decision service](../../../docker/docker-compose.yml): the shipped local Docker `kev-decision-api` backend
  (llama.cpp `llama-server` exposing `/v1/systemone`), normative for the SystemOne dialect's live surface.
- [OpenRouter Decisions Operation][or]: normative OpenRouter schema and operation-specific route.
- [OpenAI Decisions Guide][openai]: normative OpenAI dialect and decision semantics.
- [OpenAI .NET SDK](https://github.com/openai/openai-dotnet): verify actual SDK support before relying on typed APIs.

[core-options]: ../../core/002-configuration-api/index.md
[core-config]: ../../core/006-microsoft-configuration-integration/index.md
[or]: https://openrouter.ai/docs/api/api-reference/alphadecisions/submit-a-decisions-questions-and-answers-request.md
[openai]: https://developers.openai.com/api/docs/guides/decisions
