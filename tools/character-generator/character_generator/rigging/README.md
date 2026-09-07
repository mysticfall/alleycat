# Forearm-Twist Diagnostics

These read-only Blender tools provide historical and generation diagnostics for
[RIG-002](../../../../specs/rigging/002-forearm-twist/index.md). They do not regenerate assets, save a `.blend`, change
weights, or call production twist maths.

Their provenance, topology, vertex, triangle, import-parity, and metric results are not delivery gates. They may help
investigate generated inputs or record a study, but cannot prove or reject current player-visible deformation quality.

## Delivery Acceptance

Follow the normative photobooth matrix and acceptance sequence in
[RIG-002](../../../../specs/rigging/002-forearm-twist/index.md#acceptance-criteria).
An independent reviewer must inspect the captured images, followed by explicit user visual approval.

Development-time skeleton tests separately prove helper transforms, authority ordering, hand-pose preservation, and
fail-closed behaviour. Refer to RIG-002 for the required fixture, capture-time assertions, and acceptance sequence.

## Blender Audit

Run the read-only source audit when historical or generation diagnostics are needed:

```bash
python tools/character-generator/commands/run_forearm_twist_audit.py
```

It writes `game/temp/RIG-002/phase-1/blender-evidence.json`, including source topology, frames, binds, weight,
cross-section, and triangle diagnostics. The audit may record source-identity and neutral-relative measurements; those
records are provenance or diagnostic evidence only.

## Axial Candidate Audit

Run the read-only candidate study when inspecting a declared generation candidate:

```bash
python tools/character-generator/commands/run_forearm_twist_optimisation.py
```

It writes `game/temp/RIG-002/twist-only/candidate-evidence.json`. Its candidate classifications and metric thresholds
describe the recorded study only; they are not runtime-quality proof or a prerequisite for photobooth review.

## Generated Diagnostics

After ordinary regeneration, use a separate diagnostic output so existing evidence remains intact:

```bash
python tools/character-generator/commands/run_forearm_twist_audit.py \
  --output game/temp/RIG-002/phase-2/generated-blender-evidence.json
python tools/character-generator/commands/run_generated_forearm_weight_validation.py
```

The generated source-formula record is written to
`game/temp/RIG-002/phase-2/generated-weight-validation.json`. These records support generation investigation only;
they do not create an import-seam gate or replace frozen-photobooth acceptance.
