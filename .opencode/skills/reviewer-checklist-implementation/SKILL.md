---
name: reviewer-checklist-implementation
description: Reviewer-only checklist for implementation and handoff-readiness reviews.
---

# Implementation Review Checklist

Use this checklist when reviewing implementation quality and handoff readiness for gameplay, systems, C# code, Godot
scenes or resources, automated tests, repository tooling, or visual acceptance artefacts.

## Source Of Truth

- Relevant spec under `specs/`, starting from `specs/index.md`.

## Validation Gate

- Review a reproducible bug in this order:
  1. Original reported symptom, representative scenario, complete user-visible observation window, independent criterion
     and bounds, and a genuine baseline failure caused by that symptom.
  2. The same regression and observation window passing unchanged on the final candidate, with observed behaviour and
     any unproved remainder.
  3. Implementation correctness, blocker closure, and supporting focused checks.
  4. Applicable broader final gates below.
- Missing original-symptom red-to-green evidence blocks any claim of verified symptom resolution, even when supporting
  or full suites pass.
- If reproduction is impossible and the user explicitly authorises an alternative evidence contract, recommend only a
  conditional handoff under that exact contract. Record its conditions, risks, limitations, and unproved remainder in
  the decision; never label it red-to-green evidence or verified symptom resolution.
- Keep ordinary bugs and alternative evidence without explicit user authorisation blocked. Apply urgent safety or
  integrity containment only under the narrow conditions in the `reviewer` agent instructions; reviewed containment is
  not symptom resolution.
- For a focused implementation or blocker re-review, run checks that cover the changed delta and unresolved findings.
  Retain blocker IDs and report only named blocker closure; never infer overall readiness from a blocker-only review.
- Before a final gameplay implementation handoff, run the full unit and integration test suites yourself and include the
  exact commands and outcomes in `Verified Checks` or `Blocking Issues`.
- Use the full-suite commands:
  - `dotnet test tests/AlleyCat.Tests.csproj`
  - `dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj`
- The integration test framework launches Godot with `--xr-mode off` automatically.
- Do not rely only on the coder's filtered test evidence for final readiness.
- Do not add `--headless` to the full integration-suite gate unless an explicit test-framework contract says the whole
  selected run is headless-compatible.
- Tooling-only and workflow-only instruction tasks skip unrelated gameplay suites. Record the review type and skip
  reason in `Verified Checks`, and review the relevant tooling or structural validation instead.

## C# And Godot Checks

- [ ] Changes align with the relevant spec and keep scope focused.
- [ ] Naming, file/class structure, access modifiers, nullability, and guard checks follow project conventions.
- [ ] Changed contracts such as save data, config, and messages remain backwards-compatible or document migration.
- [ ] Node lifecycle, signal handling, scene/node ownership, exported properties/resources, and autoload usage are safe.
- [ ] Per-frame and VR-critical paths avoid blocking work and avoidable allocations.
- [ ] Scene/resource changes preserve UID metadata and referenced assets still load by UID after mutation.
- [ ] Full-screen UI uses 1152x648 resolution with transparent background when applicable.

## Visual Evidence Checks

Select evidence according to the representative claim and method in `godot-visual-verification`. Visual acceptance does
not by itself require screenshot, GDScript, photobooth, or C# integration-test infrastructure.

### Screenshot Evidence

Apply these checks only when screenshots are part of the selected evidence:

- [ ] Screenshot capture ran without `--headless`.
- [ ] Representative screenshots were independently loaded with the `read` tool.
- [ ] Distinct scenarios produce visibly distinct results, and key scenario pairs were compared directly.
- [ ] Scenario names match the visible cue, camera/framing is valid, and the cue is clear rather than ambiguous or tiny.
- [ ] Required visual-inspection tables record expected and observed cues, confidence, and scenario differences.

Treat missing required screenshot inspection as blocking when screenshots are selected, unless the evidence is
explicitly unavailable and escalated for user judgement.

### Temporal Evidence

When the selected method is deterministic video, representative playable observation, a sampled frame sequence, or a
synchronised trace:

- [ ] Temporal claims use time-resolved evidence across the complete reported transition, including visible arrival and
  a settling period long enough to expose recurrence.
- [ ] The evidence records why the method and scenario are representative, including method limitations and any unproved
  remainder.
- [ ] Objective visible cues are correlated with frame, time, or synchronised trace events where the method permits.
- [ ] Isolated screenshots, endpoints, state flags, actor-root stillness, and contact leases are not used to prove the
  absence of sliding, unwanted foot movement, or residual animation.
- [ ] Screenshot, GDScript, photobooth, or C# infrastructure is not required solely because acceptance is visual.
- [ ] If a runner or integration test is selected, its execution and assertions support the same temporal claim.

Treat an incomplete observation window or unsupported inference from static evidence as blocking for a temporal claim.

## Escalate Immediately When

- Relevant spec requirements are missing, conflicting, or impossible to validate.
- A potential regression or risk is high-impact but evidence is incomplete.
- Required implementation validation evidence is missing and cannot be reproduced in the review environment.
- The requested acceptance threshold conflicts with project implementation or testing rules.
