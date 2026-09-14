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

### 1) Original Outcome and Evidence Preservation (Mandatory)

Before formatting or blocker-bookkeeping critique, trace each reproducible user-reported defect through the session:

1. Did the `planner` establish and carry the parent symptom contract in every delegation?
2. Did the `coder` establish a genuine symptom-red baseline before the fix and rerun the unchanged regression after
   meaningful changes and on the final candidate?
3. Did the `reviewer` inspect that red-to-green chronology before blocker, implementation, and supporting evidence?
4. Were blocker closures treated as prerequisites rather than substitute outcomes?
5. Did retries and reporting lead with measured symptom state, observed behaviour, and unproved remainder?

Treat any replacement of the user-visible outcome by mechanism, blocker, static, or supporting-suite evidence as the
highest-priority workflow debt.

### 2) Subagent Response Governance

Before broader process critique, explicitly evaluate whether:

1. Required subagent response formats were used (`coder`, `reviewer`, `writer`).
2. Primary-agent handling classified results as `accepted`, `follow-up`, or `escalated`.
3. Escalations were surfaced quickly with a clear decision request to the invoking agent/user.
4. Reviewer `Handoff Decision: Not Ready` triggered a fix-and-re-review loop.

If any of the above is weak or inconsistent, treat it as high-priority workflow debt.

### 3) Deviation and Intervention Analysis

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

## Validation

- Walk the revised instruction path adversarially, including mechanism-green/symptom-red, blocker-only closure,
  implementation-derived bounds, truncated observation windows, static evidence for temporal claims, unavailable
  reproduction, blocker-review readiness, and restart-state preservation.
- For workflow-only changes, validate Markdown, links, frontmatter, role ownership, and the scoped diff. Do not run
  gameplay suites merely to validate instructions.
- For config-time changes, preserve unresolved outcomes in a current handoff and require the user to quit and restart
  OpenCode before relying on the changes.

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
