---
description: Evaluate this session and harden workflow assets
---

## Objective

Evaluate the current session for workflow failures, identify where execution deviated from project guidance, and
apply targeted workflow-asset updates so the same failure patterns are less likely in future runs.

Optional Focus Context: $ARGUMENTS

## Required Scope

Review this session against:

- `@AGENTS.md`
- `@specs/index.md` and any referenced spec paths used in the session
- `.opencode/agents/`
- `.opencode/skills/`

Do not implement gameplay/product features during this command. Focus only on workflow quality and collaboration
reliability.

## Evaluation Priorities

### 1) Subagent Response Governance First (Mandatory)

Before broader process critique, explicitly evaluate whether:

1. Required subagent response formats were used (`coder`, `reviewer`, `writer`).
2. Primary-agent handling classified results as `accepted`, `follow-up`, or `escalated`.
3. Escalations were surfaced quickly with a clear decision request to the invoking agent/user.
4. Reviewer `Handoff Decision: Not Ready` triggered a fix-and-re-review loop.

If any of the above is weak or inconsistent, treat it as top-priority workflow debt.

### 2) Deviation and Intervention Analysis

Identify concrete failures with evidence from this session, including:

- Guidance deviations (spec process, tool usage, validation gates, communication contract).
- Places where user intervention was needed because guidance was missing, ambiguous, or too weak.
- Repeated or avoidable recovery loops.

For each failure, determine the smallest asset change that would have prevented or reduced it.

## Required Refinement Action

Apply minimal, high-leverage edits directly to relevant workflow assets (instructions, skills, or agents).

Rules:

- Prefer small, testable changes over broad rewrites.
- Preserve existing role boundaries and spec-driven flow.
- Tighten prompts/checklists/contracts instead of adding verbose policy.
- Add or refine response-format and triage instructions where handoffs were weak.
- If no safe edit is possible without product/policy decisions, escalate clearly instead of guessing.

## Output Contract

Return concise results grouped by issue. For each issue include:

1. **Observed Workflow Issue**
2. **Why It Matters**
3. **Proposed Adjustment** (include exact file(s) updated)
4. **Response Handling Change** (what the primary agent should do differently after subagent output)
5. **Expected Impact**

Then include:

- **Edits Applied** — short summary of actual changes made.
- **Escalations** — decisions still required from the user/invoker (or `None`).
