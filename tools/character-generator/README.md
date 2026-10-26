# Character Generator

This tool owns MPFB/MakeHuman character generation and related import-sidecar and collider operations. Its normative
contract is [Character Generator](../../specs/tooling/character-generator/index.md).

## Prerequisites

- Run commands from the repository root.
- Install Blender 5.2 with MPFB 2.0.17 enabled in the launched Blender environment. `generate_character.sh` uses
  `BLENDER_BIN` when set, otherwise `blender`.
- Manually install the six generic MPFB source files using the [installation guide](mpfb/README.md), preserving their
  names and schema `110`. Install content-specific character presets in MPFB's user config before generation.
- Provide the character configuration and its referenced clothing, eye, hair, HDRI, and animation assets.
- Use Python 3.10 or newer for standalone helpers and Python test discovery.
- Run Godot import before using the sidecar updater; the updater changes existing sidecars and does not fabricate them.

## Commands

Inspect the generate command's required JSON configuration argument:

```sh
tools/character-generator/commands/generate_character.sh
```

With no argument, the wrapper prints usage and exits with status `2`; this does not generate a character.

Supply an existing configuration to generate a character, replacing the example path below with your own file:

```sh
tools/character-generator/commands/generate_character.sh /path/to/character-config.json
```

The specification defines the four configuration fields: `preset`, `name`, `outputFile`, and `amimations` (the spelling
is intentional). Configured asset paths resolve relative to `game/`.

Inspect the collider command through Blender. `--source` and `--output` are required; armature and body names may be
overridden when the source does not use the command defaults:

```sh
blender --background --python tools/character-generator/commands/generate_body_colliders.py -- --help
```

Inspect the existing-sidecar metadata command:

```sh
python3 tools/character-generator/commands/update_character_import_retarget_metadata.py --help
```

Use the metadata command only with its required generated `.blend` path and configured character name.

### Import Retarget Validation

Validate character import sidecars for the required MakeHuman retarget metadata on demand. A character whose sidecars
lack those keys imports with MakeHuman bone names, which the runtime rig then skips. The command accepts one or more
targets: a character directory, a character `.blend` or `.blend.import` path, or a pack/content root scanned
recursively for paired `.colliders.blend` sources. Paired collider sources distinguish generated characters from
animation and prop `.blend` assets sharing their directories:

```sh
python3 tools/character-generator/commands/check_character_import_retarget.py game/assets/characters/reference/female
```

The check verifies every `/Skeleton3D` entry in the main sidecar and the `PATH:Ragdoll/Skeleton3D` entry in the
collider sidecar for the three required retarget keys, prints per-file PASS/FAIL results with a summary, and reports
missing sidecars with the run-import-then-helper remedy. Key presence is validated, not key values. Exit status is
`0` when every sidecar passes, `1` for any validation failure, and `2` for unsupported targets. Its unit tests run
through the standard unittest discovery command below; headless Godot integration coverage lives in
`integration-tests/src/Characters/CharacterColliderRetargetGuardIntegrationTests.cs`.

### Forearm and Binding Helpers

The [forearm diagnostics guide](character_generator/rigging/README.md) documents the read-only source-audit,
axial-candidate, and generated-weight-validation launchers. Their Blender audit implementations are exposed through
`commands/audit_forearm_twist.py` and `commands/optimise_forearm_twist.py`.

Validate an ordinary generated character through Blender, replacing the example output path with your generated file:

```sh
blender --background --python-exit-code 1 \
  --python tools/character-generator/commands/validate_forearm_twist_weights.py -- \
  female /path/to/ordinary-generator-output.blend
```

The validator accepts `<variant> <ordinary-generator-output.blend>` pairs after `--`, reads the ownership sidecar,
and prints JSON measurements without saving the `.blend`.

After regenerating reference assets and re-saving their templates in Godot, run the read-only bone-binding guard:

```sh
python3 tools/character-generator/commands/check_character_template_bone_bindings.py
```

The [index-refresh command](commands/refresh_character_template_bone_indices.py) remains a separate Godot
template-maintenance operation; Blender generation does not automatically rewrite template indices.

## Outputs

The generator writes the configured character `.blend`, the paired `<stem>.colliders.blend`, and externalised textures.
It also writes three forearm JSON files beside the character output:

- `<stem>.forearm_twist_manifest.json`: pre-helper neutral-manifest diagnostics.
- `<stem>.forearm_twist_candidate_weights.json`: generated candidate-weight diagnostics.
- `<stem>.forearm_twist_generator_run_ownership.json`: schema-8 installed-preset and saved-output digest bindings,
  authored axial ownership, source-domain support, and physical-membership validation records.

Evidence retains the current producer identifiers `tools/generate_character.py (generator-run authored axial reference)`
and `tools/rigging/audit_forearm_twist.py`. These are serialised identities, not supported command locations.

## Usage Boundary

The generator command creates the main and paired collider Blender assets. The sidecar updater only updates existing
Godot import metadata. Neither command serialises Godot scenes or resources. Refer to the specification for required
configuration fields, import contracts, and verification.

Generation validation and diagnostics do not prove runtime deformation quality. [RIG-002][forearm-spec] still requires
independent inspection of the captured images and explicit user visual approval.

## Verification

Discover the tool's Python tests explicitly from the repository root:

```sh
PYTHONPATH="$PWD/tools/character-generator" python3 -m unittest discover \
  -s tools/character-generator/tests -p 'test_*.py' -v
```

The suite includes pure-Python and Blender-gated cases. A run with skipped Blender cases does not verify ordinary
regeneration. Blender coverage requires `blender` on `PATH`, consistent with `BLENDER_BIN`, MPFB 2.0.17 enabled in the
actual launched environment, manually installed presets, and the referenced content and reference assets.
`MPFB_EXTENSION_ROOT` changes only the manifest-presence check; it does not activate MPFB. Report skipped coverage
separately from exercised tests. Neither Python test discovery nor generation diagnostics replaces RIG-002's visual
acceptance.

[forearm-spec]: ../../specs/rigging/002-forearm-twist/index.md
