# Documentation Review Checklist

Use this checklist with [Markdown Checklist](./checklist-markdown.md) when reviewing documentation, workflow notes, or
Markdown guidance outside stricter spec, lore, or OpenCode configuration scopes.

## Source Of Truth

- User request and intended audience.
- Relevant specs, agent files, skills, or workflow documents referenced by the change.

## Checks

- [ ] The Markdown checklist passes for structure, headings, links, anchors, and consumer path.
- [ ] The guidance preserves source-of-truth intent and avoids scope drift.
- [ ] Updates are small and focused unless the request explicitly calls for a broader rewrite.
- [ ] The document is practical for its expected consumer to execute without extra interpretation.
- [ ] The change does not introduce behavioural or technical requirements that belong in a spec.
- [ ] Follow-up work is clearly marked as non-blocking or escalated for a decision.

## Escalate Immediately When

- Source-of-truth conflict exists between the user request, relevant spec, agent file, or workflow document.
- The requested wording introduces behavioural requirements not present in approved specs.
- The documentation cannot preserve existing links, anchors, or consumer paths without broader refactor approval.
