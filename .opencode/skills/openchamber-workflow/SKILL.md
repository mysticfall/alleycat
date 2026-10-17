---
name: openchamber-workflow
description: >-
  Shared native OpenChamber planning, approval, progress, delegation and recovery workflow.
  Load for every planner, loremaster, brainstormer or workflow session, including resumed work and handoffs.
---

# OpenChamber Workflow

Use this shared contract for the four primary agents: `planner`, `loremaster`, `brainstormer`, and `workflow`.
Role instructions retain domain decisions and specialist gates. This skill owns coordination, not product requirements.
Use native tool schemas available in the session; if a required tool is unavailable, report the blocker and ask the
user how to proceed. Do not substitute another approval mechanism or invent storage paths.

## Authority and Ownership

- Start requirement discovery at [Project Specifications](../../../specs/index.md).
- Separate exploration, plan review, implementation authorisation, and evidence-based acceptance. Sending a plan to
  improve or review is not approval. A native implementation action or an explicit user implementation instruction
  authorises the stated scope; resolve ambiguous short replies before acting.
- Prefer internal `subagent` delegation for bounded work whose result the primary consumes. Use native
  `session.create`, `session.send`, or `session.fork` for sessions the user explicitly asks to dispatch, not as a
  substitute for internal delegation. Set `returnResult` when the user wants the outcome back or the next step needs it.
- Preserve role boundaries. Brainstormer explores and hands off, not implements; loremaster owns lore context but
  cannot approve new canon on the user's behalf; workflow changes collaboration, not gameplay.
- No automatic Goal Mode. Enable it only at the user's explicit request.

## Plans and Approval

Use a saved plan for multipart work, three or more distinct steps, multiple subagents, or staged validation/recovery.
Simple bounded work can proceed under an explicit user instruction without manufacturing a plan. Brainstormer does
not create an implementation plan; hand that responsibility to planner.

1. Call `openchamber_knowledge` with `knowledge.list` in the intended project before adding knowledge. Reuse the
   existing plan rather than creating duplicates. Read its returned file path and current text.
2. Draft a self-contained Markdown plan starting with `# Title` and a short `## Steps` checklist (`- [ ]` per coarse
   outcome), followed by objective, approved constraints, scope, acceptance/evidence, decisions and dependencies.
3. Save with `knowledge.save`, `kind: plan`, and the whole `body`; retain the returned ID and actual path as canonical.
   Never assume a `.openchamber` directory or infer a path from the title.
4. Call `knowledge.show` with that plan ID, then wait for the user to read, comment, or authorise implementation.
   A save, display, review dispatch, or silence does not authorise execution.
5. Apply requested revisions before seeking approval again. For a whole replacement of a plan or note, pass `expected`
   containing the exact last-read text. On a mismatch, re-read and reconcile; never overwrite concurrent user edits.

During authorised execution, read the actual plan file and tick a coarse step (`- [x]`) only when its required evidence
supports completion. Change nothing else in the plan unless the user authorises a revision. A permission-limited
primary may delegate this exact bookkeeping edit; permissions are not a reason to replace the document.
Do not append unapproved blockers, rewrite scope, drop constraints, or silently defer steps. Ask for revision authority
when discoveries require a different plan. An authorised replacement must preserve all still-relevant objectives,
requirements, decisions, acceptance and open work; use the last-read text as `expected`.

## Coarse TODO Overview

The user has explicitly made a standing request: after approval, all four primary agents maintain a coarse session
progress overview in OpenChamber's TODO tab. This is the user-requested exception to the default backlog-only rule,
not permission to create detailed task trackers for other users or unapproved work. For brainstormer, mirror only an
approved exploration/handoff stage or an existing approved plan; this does not authorise an implementation plan.

- Call `knowledge.list` first. Use `knowledge.save`, `kind: todo`, for a few coarse outcome stages, not each substep,
  assertion, blocker, or report field. The approved plan remains the authority; the TODO tab is only an overview.
- Use short, readable task-first labels so task names remain visible in a narrow TODO tab. Add a short human-readable
  scope suffix only when needed for disambiguation, for example `Verify Arrival Motion (Locomotion)`.
  Never put raw plan/session UUIDs in display text. Keep actual plan/session IDs, returned TODO IDs and their ownership
  mapping in the continuity handoff instead. List and dedupe before adding; match owned items by stored TODO ID, not
  ambiguous labels. If ownership cannot be recovered, resolve it before updating or creating a replacement.
- Update only owned items by ID after evidence triage, using `completed: true/false`. For a state-only update omit
  `body` to preserve user wording. Re-read before changing text; if the user changed it, reconcile rather than replace.
- Identify the current stage in progress communication; native TODOs have a completion boolean, not an invented
  in-progress state. Failed or paused work stays open. A checkbox cannot establish readiness or symptom resolution.
- Do not bulk clear or delete TODOs, touch unrelated entries, or delete completed records as routine cleanup.
  `knowledge.delete` is for an explicitly authorised deletion of the identified item, not progress management.
- Outside this standing request, unrequested TODOs remain backlog only: add when leaving work for later, not to track
  current execution. Dedupe against existing entries and distinguish unapproved suggestions from committed work.

## Continuity and Recovery State

After compaction, restart, session movement, or resumption, re-read the canonical plan and current project knowledge
before delegating. Recover scope from the approved document, not a remembered summary. Keep a concise continuity
handoff in the session: plan ID/path, session ID, project/directory, TODO IDs and their plan/session ownership mapping,
authorisation, parent outcome/evidence paths, current stage, stable blocker IDs, unproved remainder, supporting-work
count and next authorised evidence step.
Do not amend the approved plan just to store these progress records. Preserve user decisions and partial changes.
Config-time edits require the user to quit and restart OpenCode before relying on revised instructions; the handoff
must withdraw superseded readiness claims and retain unresolved outcomes through that restart.

## Bounded Delegation Brief

Every internal delegation or user-requested session dispatch needs a self-contained Markdown brief:

1. **Objective and Motivation:** Requested user-visible outcome and why it matters.
2. **Current State:** Relevant changes, evidence and risks; separate confirmed facts, hypotheses and intended behaviour.
3. **Approved Action and Limits:** Authorisation, allowed paths/scope and exclusions; resolve short-reply antecedents.
4. **References:** Exact spec sections and evidence paths with their purpose, plus enough inline context to start.
5. **Bounded Task:** One coherent outcome, next evidence-producing action and decision/stopping condition.
6. **Acceptance and Deliverable:** Required checks, report fields, unresolved issues and applicable specialist gates.

Read as a fresh agent: can it determine what, why, authority, where to start, and when to stop without history?
Do not send a bare file list or “resume previous work”. Define essential terms and keep Markdown readable. For uncertain
designs, request the smallest representative end-to-end feasibility observation before infrastructure or case matrices;
an experiment does not waive safety or final acceptance. Split investigation from implementation when scope is unclear.

## Outcome Evidence

For reproducible bugs, establish before production-fix delegation: original symptom, representative scenario, complete
user-visible observation window, independently justified criterion/bounds and a baseline failing because of the symptom,
not setup or environment. Carry this parent contract and current red/green state in every bug handoff.

- Accept an `outcome` only when the same criterion, bounds and full observation window pass on the final candidate.
  Rerun the parent regression after meaningful production changes, including blocker fixes. Keep baseline-red,
  intervening diagnostics and final-candidate evidence in order; old partial green evidence is not current readiness.
- Mechanism-green tests, blocker closure, component completion and static endpoints cannot replace symptom evidence.
  Narrow delegated work, never parent acceptance; retain the unproved remainder in every report.
- If reproduction is impossible, escalate. Only the exact alternative evidence contract explicitly authorised by the
  user permits conditional work/handoff. Repeat its conditions, limitations, risks and unproved remainder; never call
  it verified regression resolution. Do not derive a threshold from the fix or shorten observation to pass.
- Urgent safety/integrity containment may precede reproduction only when delay itself is unsafe. Limit it to the
  protective change, classify as partial or reviewed containment and keep the parent symptom open. Ordinary urgency
  and test inconvenience do not qualify.

## Response Triage and Scope Admission

After each result, reconcile critical requirements with named evidence and missing proof. Do not relay reports verbatim.
Extract the required fields (equivalent heading spellings are acceptable):

- `coder`: Implementation Summary; Validation; Risks/Follow-Ups; Escalations.
- `writer`: Content Changes; Consistency Checks; Open Questions; Escalations.
- `reviewer`: Blocking Issues; Non-Blocking Improvements; Verified Checks; Handoff Decision.
- `lore-compiler`: Content Context; Source Delta; Graph Delta; Validation; Determinism Check; Escalations.

Classify evidence as `diagnostic`, `partial`, `reviewed`, `conditional`, or `outcome`, separately from disposition:

- `accepted`: State exactly what is accepted, then update evidence-backed coarse progress and proceed. Accepting a
  diagnostic report does not close implementation or review gates.
- `follow-up`: Retain valid evidence and delegate only the admitted unresolved delta, not a full report rewrite.
- `escalated`: Stop that branch and ask the user for a specific scope/product/contract decision immediately.

Before admitting any finding as blocking work, establish **authority** (user instruction/exact approved requirement),
**evidence and relevance** (violation or missing required proof and impact on outcome/safety/gates), and **smallest next
step** (reuse evidence or repair the narrow check, with a closure observation). A reviewer label, test failure, proposed
spec, or implementation choice is not independent authority. Distinguish defects from faulty oracles, fixtures,
environment failures and optional hardening. Unsupported concerns stay non-blocking; uncertain material risks require
a bounded investigation or user decision, not invented requirements. Expensive approved work cannot be silently dropped.
Record this briefly in the disposition, not a new ledger or unapproved plan amendment.

## Review and Specialist Gates

- Run a dedicated `reviewer` before final code/config/test handoff; apply stricter role gates for lore and documents.
  Pass sources of truth, accepted changes, evidence and open risks. If no relevant artefact changed, say why review
  is skipped. Only explicit user risk acceptance can waive a blocking requirement, and it is not verified success.
- On `Not Ready`, apply scope admission. Request missing authority/evidence before commissioning an unjustified fix,
  but do not overrule unresolved `Not Ready`. Group shared-cause fixes into bounded tasks retaining every blocker ID,
  meaning, required fix and closure evidence. Re-run reviewer; each original blocker must be resolved or re-raised.
  New names or unrelated passing checks cannot close old blockers. Do not claim overall readiness from local closure.
- For visual acceptance, load `godot-visual-verification`; map `READY` to accepted (review pending),
  `FOLLOW-UP REQUIRED` to follow-up, and `ESCALATE` to escalated. Pass full observation windows and evidence to review.
  Screenshot capture failure or uninspected files cannot pass; require coder inspection and independent reviewer `read`
  inspection. A primary may inspect only if review is unavailable or to resolve escalation. User contradiction reopens
  the gate and requires fresh objective evidence; static images never prove temporal correctness.
- For `specs/` edits, require `writer-guide-specs`, distinct User Requirements and Technical Requirements, core delivery
  contracts present or normatively linked, no mandatory contracts excluded by Out Of Scope, and acceptance of both
  layers. Missing checks require follow-up; source-of-truth conflicts require escalation.
- Check code/spec consistency in both directions when either changes. Delegate alignment within approved scope, request
  authority for scope changes, and report `in sync`, `updated`, or `deferred-with-risk`. Known drift blocks completion
  unless the user explicitly accepts deferred sync.

## Bounded Recovery

User cancellation or interruption pauses work; it is not a retry trigger. Before an authorised resumption, inspect
partial changes/evidence and send a fresh bounded brief. Never assume an interrupted run changed nothing.

For an unexpected abort, timeout, empty/placeholder result or unusable handoff: classify as follow-up, proactively state
what failed and what will change, inspect partial work, then retry once with tighter scope and a stopping condition.
If still unusable, escalate. For incomplete but usable reports request only missing evidence; repeated handoff-quality
failure also gets one tightened retry, then escalation. Avoid ineffective tooling validation: establish installed
version/supported output once and retain it. Rerun checks for changed work, failures, explicit stability requirements or
unresolved concerns, preserving mandatory independent final gates.

Judge progress by new parent-outcome evidence or measured improvement against unchanged acceptance. After two successive
supporting-work delegations without either, pause before a third even if local blockers closed. Show the last outcome
observation, what remains unproved and the smallest outcome-producing check; ask whether to continue prerequisites,
change approach or defer approved work. Blocker renaming, replanning and session restart do not reset this count.
Preserve it in the continuity handoff. Repeated failure without outcome progress requires a pause, not endless retries.

## Named Worktrees and Session Dispatch

Do not create or move to a worktree without user approval. Propose a short task-specific folder and branch name first.
Use `openchamber` action `session.moveToWorktree` to move the current session, with `worktree` and optionally `branch`.
It branches from the repository's default branch by default; confirm any different required base before supplying
`startRef`. Do not assume the current checkout is the base. Carry only explicitly named files owned by this task using
`carry`; inspect a busy checkout and preserve others' work. Alternatively, the user may manually create the named tree.
Use the returned directory, verify access to plan/evidence there, and retain the original canonical plan reference
rather than inventing or silently copying one. If movement or access fails, stop and resolve it before dispatch.

For brainstormer-to-planner handoff: agree the brief, propose the worktree/branch name and ask approval, move the
session when approved, then use a separately user-authorised `session.create` with `directory` set to the moved path,
`agent: planner`, and the bounded brief as `prompt`. Approval can explicitly cover both movement and dispatch; otherwise
ask before the second action. Follow native model-selection instructions; no automatic Goal Mode. If the user declines
isolation, honour their approved destination. If they only want a copy-ready brief, do not dispatch.

## Progress and Handoff

Lead with observable user behaviour, not counts of tests, blockers or TODOs. Report objective/current coarse stage/open
work, delegated agent, evidence class/validation, `Disposition: accepted | follow-up | escalated`, blockers/recovery,
and next action. For bugs include original symptom state, baseline/final regression, observed behaviour and unresolved
remainder. Preserve conditional authorisation exactly. Keep technical detail and artefacts linked, not dumped.
