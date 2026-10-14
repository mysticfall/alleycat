---
id: TEST-002
title: Visual Verification Scope
---

# Visual Verification Scope

## Requirement

Provide AI agents with a practical, repeatable workflow for validating visual behaviour when logic assertions
alone are insufficient.

## Goal

Match evidence to the visible claim without requiring infrastructure that adds no relevant proof. This contract guides
planners, implementers, and reviewers; owning feature specifications retain any additional validation requirements.

## User Requirements

1. Contributors must be able to verify the actual visible outcome in a representative, repeatable scenario.
2. Reviewers must be able to distinguish demonstrated outcomes from supporting checks, uncertainty, and unproved claims.
3. Objective criteria must govern verification where applicable; user judgement remains required where the owning
   feature specification assigns it. Automated checks must not substitute for that judgement.

## Technical Requirements

1. Define the visible claim, representative scenario and scene wiring, complete observation window, and acceptance
   criterion before evaluating results. Justify objective bounds independently of the candidate implementation or its
   observed output; existing test thresholds alone are not approval.
2. Select the least-intrusive method that proves that claim. An existing runtime scene or harness may suffice.
   Screenshots, GDScript runners, photobooths, and C# integration tests are required only when selected for the evidence
   method or mandated by the owning feature contract, not solely because acceptance is visual.
3. For a reported defect, establish a genuine failing baseline caused by the original visible symptom before production
   implementation. Verify the final candidate against the same scenario, observation window, and criterion.
   Only urgent safety or integrity containment may precede reproduction when delay itself would be unsafe, as defined
   in [Bug Work](../../../AGENTS.md#bug-work). Make the minimum protective change; it is not verified resolution, and
   the parent regression remains open. Ordinary defects, delivery pressure, and inconvenient reproduction do not
   qualify.
4. Temporal claims require time-resolved evidence covering the complete reported transition, including visible arrival
   and sufficient settling to expose recurrence. Isolated screenshots, endpoints, state flags, actor-root stillness,
   or other mechanism checks cannot replace evidence of the visible outcome.
5. Selected screenshots require a working renderer, valid camera framing, and independent image inspection. Validate
   markers when used or required by the claim or owning contract; screenshots do not inherently require markers.
   File existence or successful capture alone is not acceptance.
6. Handoffs must record the method and why it is representative, reproducible setup, evidence locations or observation
   records, expected and observed outcomes, execution results for selected tests/runners, and any unproved remainder.
7. Missing reproduction blocks verified symptom resolution. Only explicit user authorisation permits a conditional
   handoff under an alternative evidence contract; record its exact conditions, risks, limitations, and unproved
   remainder. Such a handoff is neither red-to-green evidence nor verified symptom resolution.

## In Scope

- Feature-level visual checks tied to explicit spec expectations.
- Representative runtime observations and static or time-resolved evidence.
- Reusable photobooths, markers, capture runners, and C# assertions when relevant to the selected method.
- Reproducible evidence and independent review of visible outcomes.

## Out Of Scope

- Defining art-direction preferences or replacing user judgement assigned by an owning feature specification.
- Replacing existing logic, unit, or integration tests that already validate behaviour sufficiently.
- Mandatory creation of verification infrastructure unrelated to the claim or owning feature contract.

## Workflow Contract

1. **Define the Claim and Baseline**
   - Identify the owning feature requirements and any additional required checks or user judgement.
   - Record the scenario, complete visible window, and independently justified criterion; reproduce reported defects,
     subject only to the urgent containment exception in Technical Requirement 3.
   - If reproduction is unavailable, escalate the evidence gap rather than substituting a convenient mechanism check.
2. **Select Representative Evidence**
   - Prefer the actual runtime scene or existing harness when it represents the claim.
   - For static claims, expose the relevant pose, placement, framing, or appearance detail.
   - For temporal claims, use representative playable observation, deterministic video, sampled frames, or a
     synchronised trace only where it demonstrates the visible outcome throughout the required window.
3. **Validate the Selected Setup**
   - When using a photobooth, inherit a suitable reusable base and store feature scenes under `@game/tests/<feature>/`.
   - When selecting screenshots, verify required camera views and applicable directional conventions before capture.
     Validate markers when used or required by the claim or owning contract. Capture with a renderer and independently
     inspect representative images; escalate unclear evidence.
   - When selecting a runner or C# test, execute it against the representative scenario and retain relevant assertions
     as supporting evidence. Mechanism assertions do not discharge a separate visible-outcome requirement.
4. **Compare and Hand Off**
   - Rerun the original regression after meaningful production changes and on the final candidate without weakening it.
   - Provide reproducible records and distinguish symptom evidence, supporting checks, and outstanding user judgement.
   - Keep unsupported outcomes open. Reopen acceptance when the user reports a visual contradiction after a prior pass.

## Acceptance Criteria

1. The evidence plan identifies the owning requirements, representative scenario, complete window, independent
   criterion, and justified method without imposing unrelated infrastructure. (User 1, 3; Technical 1–2)
2. Reported defects have original-symptom baseline failure and final-candidate success under the unchanged scenario,
   window, and criterion before verified resolution is claimed. (User 1–2; Technical 3)
3. Temporal outcomes are demonstrated throughout the required transition and settling window, not inferred solely from
   static or mechanism checks. (User 1–2; Technical 4)
4. Selected screenshot evidence has validated framing, applicable marker validation, renderer-backed capture, and
   independent inspection of expected versus observed cues. Invalid or ambiguous evidence remains unresolved.
   (User 1–2; Technical 5)
5. Reviewers can reproduce the selected method from the handoff, locate its evidence, distinguish supporting checks from
   outcome proof, and identify required user judgement and any unproved remainder. (User 1–3; Technical 6)
6. Without reproduction, only an explicitly user-authorised alternative contract permits a conditional handoff, with
   its conditions and limitations recorded and no claim of verified symptom resolution. (User 2; Technical 7)

## References

- @specs/index.md
- [Visual Verification Guidance](@.opencode/skills/godot-visual-verification/SKILL.md)
- [Implementation Review Checklist](@.opencode/skills/reviewer-checklist-implementation/SKILL.md)
- @game/assets/testing/photobooth/photobooth.tscn
- @game/assets/testing/photobooth/templates/full_body_5_cams.tscn
- @docs/gdscript-api.md
