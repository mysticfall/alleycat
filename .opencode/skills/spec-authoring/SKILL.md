---
name: spec-authoring
description: >-
  Use whenever creating or updating files in specs/. Enforce explicit separation of
  User Requirements and Technical Requirements, and ensure specs remain
  authoritative for both behaviour and implementation delivery.
---

# Specification Authoring

Use this skill for any task that creates, rewrites, or materially updates files under `@specs/`.

## Objective

Produce specifications that are authoritative for:

1. user-facing behaviour and outcomes, and
2. implementation contracts required to deliver those outcomes.

Keep both layers explicit and separate.

Specifications should supplement the code, not mirror it. Capture design purpose, intent, constraints, and context
that are not immediately obvious from reading the implementation or interface alone.

Default starting point: `@specs/templates/feature-spec-template.md`.

## Required Specification Shape

For feature/component specs, include (or preserve) these sections with equivalent intent:

1. `Requirement`
2. `Goal`
3. `User Requirements`
4. `Technical Requirements`
5. `In Scope`
6. `Out Of Scope`
7. `Acceptance Criteria`
8. `References`

Parent/umbrella specs may keep details high-level, but must still identify both requirement layers and where normative
technical contracts live.

## Brevity And Value Density

- Keep specs as short as the problem allows.
- Do not pad a simple feature spec to resemble the length of a complex systems spec.
- Prefer concise statements of intent, constraints, and rationale over exhaustive restatement of code-visible details.
- If a reader can learn the same fact just by reading a small interface or obvious implementation shape, omit it unless
  the spec needs that fact to explain a non-obvious design decision.
- Use the spec to explain why the design exists, what behaviour or boundaries matter, and what future contributors must
  preserve.

## Formatting

- Wrap prose so each line stays within 120 characters.
- Keep Markdown easy to scan; prefer short paragraphs and tight bullet lists.

## Layering Rules

- **User Requirements** define player/user-visible behaviour, interactions, and outcomes.
- **Technical Requirements** define implementation obligations and boundaries (for example Godot node setup/topology,
  runtime integration boundaries, state-machine architecture, data contracts, and validation hooks).
- Keep tunable values flexible where appropriate (for example thresholds, filter constants, tuning curves), but do not
  omit implementation structure that delivery depends on.
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
- a short traceability map (requirement group → acceptance criteria IDs).

## Delegated Response Contract

When returning from a spec-writing task, include in **Consistency Checks**:

1. `Requirement Layer Coverage: User Requirements = pass/fail; Technical Requirements = pass/fail`
2. `Out-Of-Scope Audit: pass/fail`
3. `Acceptance Traceability: pass/fail`
4. `Brevity Audit: pass/fail`
5. `Line-Length Audit (<=120 chars): pass/fail`

If any item is `fail` or uncertain, escalate instead of guessing.
