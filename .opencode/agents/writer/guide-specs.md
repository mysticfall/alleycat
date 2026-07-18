---
disable: true
---

# Specification Writing Guide

Use this guide when writing or updating files under `specs/`.

## Source Of Truth

- User request and intended spec scope.
- Existing related specs reachable from `@specs/index.md`.

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

## Consistency Checks

- Requirement-layer coverage is explicit.
- Spec navigation remains reachable from `@specs/index.md`.
- Links to parent, child, and related specs remain valid.
- New requirements do not contradict accepted source-of-truth specs.

## Escalate Immediately When

- A request attempts to classify required implementation contracts as out of scope.
- The requested wording introduces behaviour or implementation scope not supported by the user request or related specs.
- Requirement ownership between specs is unclear enough that writing would create competing sources of truth.
