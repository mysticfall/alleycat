---
name: writer-guide-specs
description: Writer-only guide for specification writing under specs/.
---

# Specification Writing Guide

Use this guide when writing, rewriting, or materially updating files under `specs/`.

## Objective

Produce specifications that are authoritative for:

1. user-facing behaviour and outcomes, and
2. implementation contracts required to deliver those outcomes.

Keep both layers explicit and separate.

Specifications should supplement the code, not mirror it. Capture design purpose, intent, constraints, and context that
are not immediately obvious from reading the implementation or interface alone.

Default starting point: `@specs/template/feature-spec-template.md`.

## Source Of Truth

- User request and intended spec scope.
- Existing related specs reachable from `@specs/index.md`.

## Required Specification Shape

For feature/component specs, include or preserve sections with equivalent intent:

1. `Requirement`
2. `Goal`
3. `User Requirements`
4. `Technical Requirements`
5. `In Scope`
6. `Out Of Scope`
7. `Acceptance Criteria`
8. `References`

Parent or umbrella specs may keep details high-level, but must still identify both requirement layers and where
normative technical contracts live.

## Brevity And Value Density

- Keep specs as short as the problem allows.
- Do not pad a simple feature spec to resemble the length of a complex systems spec.
- Prefer concise statements of intent, constraints, and rationale over exhaustive restatement of code-visible details.
- If a reader can learn the same fact just by reading a small interface or obvious implementation shape, omit it unless
  the spec needs that fact to explain a non-obvious design decision.
- Use the spec to explain why the design exists, what behaviour or boundaries matter, and what future contributors must
  preserve.

## Writing Rules

1. Keep requirement layers explicit and separate:
   - **User Requirements** for player/user-visible behaviour and outcomes.
   - **Technical Requirements** for implementation contracts needed to deliver those outcomes.
2. Technical requirements must be actionable for implementers. Include concrete delivery contracts where relevant, while
   keeping tuning values flexible where appropriate.
3. `Out Of Scope` may defer optional extensions or unrelated systems, but must not exclude core implementation
   requirements needed to implement, integrate, or validate the feature.
4. Acceptance criteria must verify both user and technical requirement layers.
5. If technical detail is intentionally defined on a child page, contract page, or implementation-notes page, link it
   explicitly and state that it is a normative dependency for implementation.

## Layering Rules

- **User Requirements** define player/user-visible behaviour, interactions, and outcomes.
- **Technical Requirements** define implementation obligations and boundaries, for example Godot node setup/topology,
  runtime integration boundaries, state-machine architecture, data contracts, and validation hooks.
- Keep tunable values flexible where appropriate, for example thresholds, filter constants, and tuning curves, but do
  not omit implementation structure that delivery depends on.
- For simple contracts, keep technical requirements focused on non-obvious behavioural or integration constraints rather
  than copying signatures, properties, or enum members from code.

## Out Of Scope Guardrails

`Out Of Scope` can defer optional or unrelated work. It must not exclude mandatory implementation requirements.

Allowed examples:

- exact threshold numbers or final tuning constants,
- optional expansion states/features,
- unrelated subsystems.

Disallowed examples:

- required solver/node setup needed to deliver the feature,
- required runtime wiring or state-machine architecture contracts,
- required verification instrumentation needed by acceptance criteria.

## Acceptance Traceability

Acceptance criteria must verify both requirement layers.

Use either:

- explicit criteria that clearly cover user and technical requirements, or
- a short traceability map: requirement group → acceptance criteria IDs.

## Consistency Checks

- Requirement Layer Coverage: User Requirements = pass/fail; Technical Requirements = pass/fail.
- Out-Of-Scope Audit: pass/fail.
- Acceptance Traceability: pass/fail.
- Brevity Audit: pass/fail.
- Line-Length Audit (<=120 chars): pass/fail.
- Spec navigation remains reachable from `@specs/index.md`.
- Links to parent, child, and related specs remain valid.
- New requirements do not contradict accepted source-of-truth specs.

If any item is `fail` or uncertain, escalate instead of guessing.

## Escalate Immediately When

- A request attempts to classify required implementation contracts as out of scope.
- The requested wording introduces behaviour or implementation scope not supported by the user request or related specs.
- Requirement ownership between specs is unclear enough that writing would create competing sources of truth.
