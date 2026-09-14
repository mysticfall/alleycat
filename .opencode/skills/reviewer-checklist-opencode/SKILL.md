---
name: reviewer-checklist-opencode
description: Reviewer-only checklist for OpenCode agent, skill, command, and config reviews.
---

# OpenCode Review Checklist

Use this checklist with `reviewer-checklist-markdown` when reviewing `.opencode/` Markdown files, `opencode.json`,
agents, skills, commands, MCP configuration, or permission rules.

## Source Of Truth

- OpenCode schema and configuration rules from `customize-opencode`.
- `skill-creator` guidance when the change creates or updates skills.
- Existing agent definitions under `.opencode/agents/` and existing skills under `.opencode/skills/`.

## Checks

- [ ] Original user-visible outcomes and evidence contracts are preserved before report-format or blocker-bookkeeping
  concerns are assessed.
- [ ] The changed artefact is in the correct OpenCode location and uses the expected file shape.
- [ ] Markdown-based agent, command, or skill changes pass the Markdown checklist where applicable.
- [ ] Agent prompts describe runtime agent behaviour, not meta-instructions about editing the agent.
- [ ] Agent role boundaries are clear and do not duplicate unrelated agent responsibilities.
- [ ] Skills have valid frontmatter, focused trigger descriptions, and scoped instructions.
- [ ] Reviewer-only guidance lives in reviewer-owned files unless it should be globally discoverable as a skill.
- [ ] Configuration changes preserve schema validity and existing unrelated settings.
- [ ] Adversarial instruction walkthroughs cover mechanism-green/symptom-red, blocker-only closure,
  implementation-derived acceptance, truncated observation, static evidence for temporal claims, unavailable
  reproduction, blocker-only readiness, and restart-state preservation.
- [ ] Workflow-only validation is distinguished from gameplay suites; unrelated gameplay checks are not required for an
  instruction-only patch.
- [ ] Any restart handoff is concise and current, preserves unresolved status, withdraws superseded readiness claims,
  states the next authorised action, and is linked from workflow guidance.
- [ ] The handoff reminds the user to quit and restart OpenCode after config-time file changes.

## Escalate Immediately When

- A schema or file-shape rule is unclear enough that the OpenCode schema or source guidance must be checked first.
- A role-boundary change would make two agents own the same decision or leave a required decision owner unclear.
- A reviewer-only instruction is being promoted to a global skill without a clear cross-agent trigger.
