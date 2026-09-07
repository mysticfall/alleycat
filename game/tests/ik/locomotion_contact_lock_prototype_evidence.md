# Stage 5 Contact-Lock Prototype Evidence

## Scope and Isolation

This disposable synthetic-contact fixture uses only direct Female and Male reference-rig imports, a test-only C#
model, a snapshot export, and a test-only photobooth runner. It does not load player/NPC scenes, production
controllers, ANIM data, terrain, navigation, pelvis code, production IK modifiers, or production wiring.

`VisualSnapshots_AreExportedFromTestedSnapshotCreationWithRequiredProvenance` exports snapshot metadata. Every
pictured lifecycle and disposition is created by the tested `LocomotionContactLockPrototype.Resolve` model rather
than reproduced in GDScript. The metadata records its source test and capture provenance; it is mechanical evidence
only.

## Objective Evidence

Focused windowed integration coverage exercises three prototype tests:

1. Both direct reference rigs resolve their skeleton bones while invalid, stale, missing, and empty evidence blocks
   target publication. `NoEvent` remains distinct from missing or empty evidence and retains a synthetic anchor.
2. `FixtureFeasibility.Unproven` returns `DeferredRecoveryRequired`, emits zero synthetic output, and preserves the
   complete translation/yaw residual.
3. The snapshot export contains 15 visual scenarios and required scenario and document-level provenance. It explicitly
   excludes an actual-solve scenario.

The former direct-rig target-to-foot tests are retired. Their dynamically-created postprocessor was neither a
`Skeleton3D` modifier-stack solve nor provider-and-native-pipeline evidence. A static direct-rig foot therefore could
not meaningfully prove or refute the authoritative contact-lock contract.

Native provider precedence, per-foot isolation, target hand-off, and signal-time configured-end-bone reconciliation
are covered by `ContactLockLegSolveFixtureIntegrationTests` on the neutral test-only fixture. That fixture drives
native `TwoBoneIK3D` through the `Skeleton3D` pipeline and retains its 0.006 m target-to-end-bone contract.

No solver runs in this prototype. The direct imports are photobooth subjects only; their unchanged poses are not a
claim about a two-bone solver, animation, or a production contact lock.

## Archive And Supersession

This prototype is archived mechanical evidence. It does not execute a `TwoBoneIK3D` solve and is not Gate A evidence.

Gate A has been accepted only through the separate native provider-pipeline fixture and photobooth evidence defined in
the [IK-003 Test Setup Contract](../../../specs/ik/003-leg-feet-ik/test-setup-contract.md). This prototype neither
supplies nor overrides that gate.

## Historical Run Record

- Spec path:
  [specs/ik/003-leg-feet-ik/leg-feet-ik-contract.md](../../../specs/ik/003-leg-feet-ik/leg-feet-ik-contract.md)
- Test scene: `res://tests/ik/locomotion_contact_lock_prototype.tscn`
- Runner: `res://tests/ik/locomotion_contact_lock_prototype.gd`
- C# evidence: `integration-tests/src/IK/Prototype/LocomotionContactLockPrototypeIntegrationTests.cs`
- Intended generation run:
  `xvfb-run -a godot-mono -d -s --xr-mode off --path game "tests/ik/locomotion_contact_lock_prototype.gd"`
- Snapshot metadata: records mechanical source-test and capture provenance only; it does not assert Gate A acceptance.

## Per-Image Observations

| Image | Expected Visible Cue | Observed Visible Cue | Confidence | Pass/Fail |
| --- | --- | --- | --- | --- |
| Archived Snapshots | Synthetic markers and labels | Mechanical only; not Gate A evidence | N/A | N/A |

## Scenario Comparisons

| Pair | Expected Difference | Observed Difference | Distinct? |
| --- | --- | --- | --- |
| Synthetic Snapshots / Native Fixture | Prototype metadata vs native solve | Separate controls; no solve claim | N/A |
