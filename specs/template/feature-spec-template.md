# Feature Specification Template

## Purpose

Use this template when creating or substantially rewriting a feature/component specification under `@specs/`.

## How To Use This Template

1. Copy this file structure into your target spec path.
2. Replace all placeholder text.
3. Keep both requirement layers explicit:
   - `User Requirements`
   - `Technical Requirements`
4. Ensure `Out Of Scope` does not exclude mandatory implementation contracts.
5. Ensure `Acceptance Criteria` verify both requirement layers.

## Copyable Frontmatter

```yaml
---
id: <SPEC-ID>
title: <Spec Title>
---
```

## Required Structure

Use the following section order unless a stronger domain-specific reason requires a small adjustment.

```markdown
# <Spec Title>

## Requirement

<Single-paragraph statement of what must be delivered.>

## Goal

<Single-paragraph statement of why this spec exists and what successful delivery enables.>

## User Requirements

1. <Player/user-visible behaviour outcome.>
2. <Player/user-visible behaviour outcome.>

## Technical Requirements

1. <Implementation contract needed to deliver the user requirements.>
2. <Implementation contract needed to deliver the user requirements.>

## In Scope

- <Required behaviour or technical delivery surface included in this spec.>
- <Required behaviour or technical delivery surface included in this spec.>

## Out Of Scope

- <Optional extension or unrelated subsystem intentionally deferred.>
- <Optional extension or unrelated subsystem intentionally deferred.>

## Acceptance Criteria

1. <Verifies user requirement coverage.>
2. <Verifies technical requirement coverage.>

## References

- <Related spec path>
- <Implementation path(s)>
```

## Technical Requirement Guidance

Where relevant, include concrete implementation contracts such as:

- Godot node setup and topology boundaries.
- Runtime integration boundaries and startup/lifecycle contracts.
- State-machine/architecture contracts.
- Data/resource contract boundaries.
- Required validation hooks (tests, runners, artefacts).

Keep tuning constants flexible where appropriate (for example thresholds and curves), but do not omit required
implementation structure.

## Authoring Checklist

- [ ] `User Requirements` and `Technical Requirements` are both present and distinct.
- [ ] `Out Of Scope` does not exclude mandatory implementation requirements.
- [ ] Acceptance criteria cover both requirement layers.
- [ ] References include normative dependencies and key implementation paths.
