---
name: reviewer-checklist-specs
description: Reviewer-only checklist for specification reviews under specs/.
---

# Specification Review Checklist

Use this checklist with `reviewer-checklist-markdown` when reviewing changes under `specs/`.

## Source Of Truth

- `specs/index.md` navigation and scope.
- Existing parent or sibling specs that define the affected feature area.

## Checks

- [ ] User Requirements and Technical Requirements are explicit and separated.
- [ ] Acceptance criteria verify both player/user-visible behaviour and implementation contracts.
- [ ] Technical requirements are actionable enough for implementation, validation, and integration.
- [ ] `Out Of Scope` does not exclude core delivery, validation, or integration requirements.
- [ ] The Markdown checklist passes for headings, links, anchors, and navigation reachability.
- [ ] New or moved pages remain reachable from `specs/index.md` or an appropriate indexed parent page.
- [ ] Links, anchors, terminology, and ownership boundaries remain consistent with neighbouring specs.
- [ ] Ambiguous, conflicting, or missing requirements are escalated rather than resolved by reviewer guesswork.

## Escalate Immediately When

- The spec cannot clearly separate user-visible requirements from technical requirements.
- The request attempts to classify required implementation, validation, or integration contracts as out of scope.
- Parent, sibling, or indexed specs conflict on normative behaviour or ownership.
