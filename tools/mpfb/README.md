# MPFB Forearm-Twist Source Assets

These generic source assets support [RIG-002](../../specs/rigging/002-forearm-twist/index.md). They were authored for Blender 5.2 and MPFB 2.0.17. The custom-rig and weights JSON schema version is `110`.

The assets are generic only. Use the installed reference preset appropriate to the character being regenerated; keep content-specific presets outside this directory.

## Manual Installation

Close Blender before installing or refreshing these assets. For the supported Blender 5.2 installation, MPFB stores user-owned files at:

- Configuration: `~/.config/blender/5.2/extensions/.user/blender_org/mpfb/config/`
- User data: `~/.config/blender/5.2/extensions/.user/blender_org/mpfb/data/`

Manually create `data/rigs/` if it does not already exist, then copy these repository files without renaming them:

| Repository Source | MPFB Destination |
|---|---|
| `tools/mpfb/config/human.alleycat_female.json` | `config/human.alleycat_female.json` |
| `tools/mpfb/config/human.alleycat_male.json` | `config/human.alleycat_male.json` |
| `tools/mpfb/data/rigs/alleycat_female.json` | `data/rigs/alleycat_female.json` |
| `tools/mpfb/data/rigs/weights.alleycat_female.json` | `data/rigs/weights.alleycat_female.json` |
| `tools/mpfb/data/rigs/alleycat_male.json` | `data/rigs/alleycat_male.json` |
| `tools/mpfb/data/rigs/weights.alleycat_male.json` | `data/rigs/weights.alleycat_male.json` |

Do not overwrite an existing destination silently. If a destination exists, compare it with the repository source first. To keep local work, manually move the existing file to a clearly named backup in the same MPFB user-data tree, then copy the repository file. If its provenance is unknown, stop and resolve the conflict before continuing.

## Verification And Refresh

Start Blender and open MPFB's human-preset selector. Confirm that the two installed presets are available and that their rig settings are respectively `custom.alleycat_female` and `custom.alleycat_male`. Generate a disposable human from each preset and confirm the armature has exactly `LeftForearmTwist` and `RightForearmTwist`, both directly parented to their matching lower-arm bones. Confirm the hand remains directly parented to its matching lower arm.

Refresh by closing Blender, preserving a manual backup of each destination that differs, and repeating the manual copy and verification steps. Regenerate source characters through the ordinary MPFB workflow after a verified refresh; never edit generated `.blend` files directly.

Before changing helper topology, weights, or runtime twist, capture the generated-asset baseline with:

```bash
python tools/rigging/run_forearm_twist_audit.py
```

The command opens the checked-in female and male `.blend` files in Blender background/factory mode, never saves them,
and writes machine-readable evidence to `game/temp/RIG-002/phase-1/blender-evidence.json`. The evidence includes the
actual hierarchy and deform flags, rest and object frames, modifier settings, dense bilateral weight summaries, source
weight resolution and normalisation, contamination and transition diagnostics, and neutral-relative deformation for
the controlled hand/helper axial-rotation scenarios. Values under `assets` are measurements; the separate `thresholds`
object documents validation tolerances. Run the focused Godot parity test after producing this file.

## Uninstall

Close Blender, then manually move or delete only the six installed files listed above. Leave all other MPFB user data untouched. Restart Blender and confirm the two custom presets no longer appear.
