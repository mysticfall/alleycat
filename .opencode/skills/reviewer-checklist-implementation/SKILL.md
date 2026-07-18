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

- Before any final implementation hand-off recommendation, run the full unit and integration test suites yourself and
  include the exact commands and outcomes in `Verified Checks` or `Blocking Issues`.
- Use the full-suite commands:
  - `dotnet test tests/AlleyCat.Tests.csproj`
  - `dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj`
- The integration test framework launches Godot with `--xr-mode off` automatically.
- Do not rely only on the coder's filtered test evidence for final readiness.
- Do not add `--headless` to the full integration-suite gate unless an explicit test-framework contract says the whole
  selected run is headless-compatible.
- Tooling-only tasks may skip unrelated game suites, but the skip reason must be recorded in `Verified Checks` and any
  relevant tooling validation must still be reviewed.

## C# And Godot Checks

- [ ] Changes align with the relevant spec and keep scope focused.
- [ ] Naming, file/class structure, access modifiers, nullability, and guard checks follow project conventions.
- [ ] Changed contracts such as save data, config, and messages remain backwards-compatible or document migration.
- [ ] Node lifecycle, signal handling, scene/node ownership, exported properties/resources, and autoload usage are safe.
- [ ] Per-frame and VR-critical paths avoid blocking work and avoidable allocations.
- [ ] Scene/resource changes preserve UID metadata and referenced assets still load by UID after mutation.
- [ ] Full-screen UI uses 1152x648 resolution with transparent background when applicable.

## Visual Evidence Checks

When screenshots or visual acceptance are central to readiness:

- [ ] Screenshot capture ran without `--headless`.
- [ ] Representative screenshots were independently loaded with the `read` tool.
- [ ] Distinct scenarios produce visibly distinct results, and key scenario pairs were compared directly.
- [ ] Scenario names match the visible cue, camera/framing is valid, and the cue is clear rather than ambiguous or tiny.
- [ ] Non-visual assertions cover the same behaviour and include objective anomaly guards where practical.

Treat missing functional screenshot inspection as blocking unless the evidence is explicitly unavailable and escalated for
user judgement.

## Escalate Immediately When

- Relevant spec requirements are missing, conflicting, or impossible to validate.
- A potential regression or risk is high-impact but evidence is incomplete.
- Required implementation validation evidence is missing and cannot be reproduced in the review environment.
- The requested acceptance threshold conflicts with project implementation or testing rules.
