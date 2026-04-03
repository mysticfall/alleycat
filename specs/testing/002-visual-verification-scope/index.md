---
id: TEST-002
title: Visual Verification Scope
---

# Visual Verification Scope

## Requirement

Provide AI agents with a practical way to visually verify that implemented features conform to specification requirements when behaviour cannot be reliably validated with standard integration assertions alone.

## Goal

Create an objective visual verification capability that lets agents gather repeatable evidence for spec conformance during development and testing.

## In Scope

- Objective, spec-linked visual checks such as:
  - expected object presence and visibility
  - expected placement, orientation, and relative scale
  - expected readable UI/world-space text at defined viewpoints or distances
  - expected visual state transitions tied to interactions
- Captured visual evidence that can be reviewed by agents and humans.

## Out of Scope

- Subjective or aesthetic judgement, including atmosphere, mood, and artistic quality.
- Visual style reviews that cannot be reduced to objective, testable expectations.
- Replacing logic/unit/integration assertions that already validate behaviour reliably.

- Objective, spec-linked visual checks such as:
 - expected object presence and visibility
 - expected placement, orientation, and relative scale
 - expected readable UI/world-space text at defined viewpoints or distances
 - Captured visual evidence that can be revieweded by agents and humans.2. Visual checks focus on objective pass/fail criteria rather than subjective aesthetic assessment.
3. The workflow clearly positions visual verification as a complement to existing tests, not a replacement.

## References

- @specs/index.md
- @specs/testing/001-test-framework/index.md
