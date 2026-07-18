# Markdown Review Checklist

Use this checklist alongside any review that judges Markdown structure, navigability, or authoring quality.

## Source Of Truth

- User request and intended audience.
- Nearby Markdown files that establish local structure, terminology, and formatting conventions.
- Relevant consumer agent instructions, such as `writer`, `reviewer`, `loremaster`, or domain-specific agents.

## Checks

- [ ] The document has a clear audience, purpose, and consumer path.
- [ ] The structure starts with purpose or context, then rules or requirements, then examples or checklists when helpful.
- [ ] Headings use consistent Markdown levels and Title Case.
- [ ] Sections are short, single-purpose, and easy for downstream agents to quote or apply.
- [ ] Bullet lists are concise, actionable, and avoid filler, repeated content, or vague ownership.
- [ ] Terminology, section order, and formatting remain consistent with nearby documents.
- [ ] Existing links, anchors, and file references are preserved unless the change explicitly requires updating them.
- [ ] New or moved content is reachable through a clear, low-friction reference chain for its intended consumer.
- [ ] The document does not introduce behavioural or technical requirements that belong in an authoritative spec.

## Escalate Immediately When

- The intended audience, source of truth, or consumer path is ambiguous.
- A requested wording change would introduce unapproved behaviour or technical requirements.
- Navigation changes would orphan existing docs, break anchors, or require broader information-architecture approval.
