# Animation Pipeline

This tool hosts the Kimodo commands, the Rigify (non-Kimodo) idle bake and the ANIM-003 animation pack validator.

This tool owns Kimodo bootstrap, idle-source staging, and validation. Its normative contract is
[Kimodo Motion Sources](../../specs/tooling/kimodo-pipeline/locomotive-animation-set.md).

MotionBricks runtime requirements are defined by the [shared animation contract](../../specs/animation/index.md).
Its minimal read-only exports are documented below; source-preservation and calibration safeguards remain in force.

## MotionBricks Exports

For artist-authored and reviewed actions, use [Reviewed Source Ingestion](#reviewed-source-ingestion).
Source generation and artistic review are external to these export and publication commands.

The minimal reference-Female runtime data can be reproduced from the repository root:

```sh
blender --background --factory-startup --python-exit-code 1 \
  --python tools/animation-pipeline/export_motionbricks.py
"$KIMODO_PYTHON" tools/animation-pipeline/export_motionbricks_frames.py
```

`KIMODO_PYTHON` is a Python interpreter with the approved Kimodo installation and its dependencies available.
The first command reads the calibrated setup's canonical SOMA skeleton (names, parents and rests); it publishes no
clips. It hashes inputs before/after and never saves a `.blend`. The second exports only named
`SOMASkeleton77.global_rot_offsets`, not evaluation motions. Outputs are `poses.json` and `model-frames.json` under
`game/assets/animations/motionbricks/`. Existing outputs are replaced deliberately during provisioning; evidence
directories are never touched.

Moving and turn roles use the shared reviewed source set published below; NPC idles are native catalogue clips
([Native Idle Clips](#native-idle-clips)). The externally licensed SOMA FP32 ONNX model is not included. See the
[runtime launch instructions](../../game/src/Motion/README.md#launch).

## Native Idle Clips

`commands/export_idle_clip.py` produces [ANIM-003](../../specs/animation/003-animation-catalogue/authoring.md) native
idle clips: Godot `Animation` resources on the reference skeleton, with tracks bound to `%GeneralSkeleton:<bone>`. It
writes the clip only; pack metadata is authored by hand and checked by the
[validator](#animation-pack-validation). The author guide is
[`game/assets/animations/README.md`](../../game/assets/animations/README.md). Prerequisites: `blender` (tested 5.3) and
`godot-mono` with the imported game cache; override the executables with `BLENDER` and `GODOT_PATH`.

Kimodo path, which re-exports the default `sad` clip:

```sh
python3 tools/animation-pipeline/commands/export_idle_clip.py kimodo \
  --character-root game/assets/animations/source/reference/female \
  --source game/assets/animations/source/reference/female/idle/sad.blend \
  --action kimodo_m0a3dyf0 \
  --skeleton-scene res://assets/characters/reference/female/reference_female.blend \
  --check-skeleton-scene res://assets/characters/reference/male/reference_male.blend \
  --output game/assets/animations/packs/default/idle/sad.tres
```

Control-rig (Rigify or equivalent) path:

```sh
python3 tools/animation-pipeline/commands/export_idle_clip.py rig \
  --blend PATH/authoring.blend --control-rig rig --target Female --action my_idle \
  --skeleton-scene res://assets/characters/reference/female/reference_female.blend \
  --output game/content/my_pack/assets/animations/packs/my_pack/idle/my_idle.tres
```

Both paths share these options: `--name` (resource name, default the output stem), `--no-loop`, `--frame-start` and
`--frame-end` (default the action range), repeatable `--check-skeleton-scene`, `--bone-map` (default the MakeHuman
bone map), `--compare <res:// clip>` (diagnostic per-bone comparison in the report), `--preview-blend <game/temp
path>` (saves the prepared copy for review in Blender), `--temp-root` (default `game/temp/anim003-idle-clip`) and
`--stage-only` (build and validate without publishing). The Kimodo path also takes `--setup`, `--target-rig` and
`--approved-character-baselines` for disposable tests; the rig path takes `--action-blend` to append the action from
another blend.

The export has three stages beneath a fresh staging directory:

1. **Sample.** Blender (`commands/sample_native_clip.py`, `kimodo_pipeline/native_clip_sampling.py`) opens a
   byte-verified copy of the protected input and samples the evaluated reference armature's deform bones at every
   frame, which is exactly what the author previews. The Kimodo path first passes the setup-parity gate (KIMODO-SET-002)
   on the setup copy, appends the source action, refuses an action that animates the `_RET` calibration helpers, and
   binds it to `Kimodo_Source`, including slotted actions. The rig path binds the action to `--control-rig` and
   samples `--target`, which must follow the control rig through bone constraints and carry no action of its own. Both
   refuse a frozen result (KIMODO-SET-004). Inputs are hashed before and after and are never saved.
2. **Build.** Godot (`godot/commands/build_native_clip.gd`, `godot/lib/native_clip_builder.gd`) converts the samples
   to local bone poses on the imported skeleton. It recovers the importer's per-bone axis changes from the skin bind
   poses, fits and verifies a uniform rig scale (the reference female setup is a ×0.892 build of the imported
   character), holds bones the rig lacks (forearm twists) at rest, writes rotations for every sampled bone and
   positions only where a bone leaves its rest translation, and divides positions by `Skeleton3D.motion_scale`. It
   reloads the staged clip, checks that every SOMA-mapped track binds on each skeleton scene, and records preview parity:
   the largest distance between the clip's joints and the Blender-evaluated joints.
3. **Publish.** Only after both stages succeed is the output replaced atomically. An existing clip keeps its resource
   UID. Any failure leaves the previous clip untouched.

The command prints an `IDLE_CLIP_JSON=` summary; `report.json`, `samples.json` and both tool logs stay in the staging
directory. Outputs must be `.tres` files inside `game/` and outside authoring-source directories.

## Animation Pack Validation

`godot/commands/validate_animation_pack.gd` is the headless ANIM-003 pack validator. It calls the game's
`AnimationPackValidationCommand`, so it uses the same `AnimationPackLoader` validation as catalogue loading, and it
never writes files. Build the game's C# project first (`dotnet build AlleyCat.sln`).

```sh
godot-mono --headless --xr-mode off --path game \
  --script "$PWD/tools/animation-pipeline/godot/commands/validate_animation_pack.gd" -- \
  --pack=res://assets/animations/packs/default/pack.json \
  --template=res://assets/characters/templates/reference_female/reference_female_npc.tscn \
  --template=res://assets/characters/templates/reference_male/reference_male_npc.tscn
```

Options: repeatable `--pack=`; `--content-root=<res:// directory>` to resolve every pack in a content-pack context
rooted there; repeatable `--template=` to report layering overrides, duplicate pack ids and per-entry skeleton binding
for that character template; `--priority=` for packs a template does not reference (default: above every template
pack); and `--require-compatible`. Exit status is 0 when valid, 1 when invalid (or incompatible under
`--require-compatible`) and 2 for bad arguments. Diagnostics name the pack, entry, field and path; the last line is an
`ANIMATION_PACK_VALIDATION_JSON=` summary.

Test packs for every rejection class live in `game/tests/animation/catalogue/fixture/invalid/` (validate them with
`--content-root=res://tests/animation/catalogue/fixture/`) and `game/tests/animation/catalogue/extension/invalid/`.

Focused checks for both commands (they run Blender and Godot and write only beneath
`game/temp/anim003-idle-clip-tests`):

```sh
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s tools/animation-pipeline/tests -p test_native_idle_clip.py
```

## Reviewed Source Ingestion

`ingest_reviewed_source.py` is the maintained **Blender action → full ordered source → runtime-reader** path for
ANIM-002. It does not wire player/NPC clocks. Prerequisites:
Blender (tested 5.2.3), an existing calibrated SOMA77 `Kimodo_Source` saved with correct action rotation modes,
and the prototype `poses.json` calibration provisioned above. No Kimodo service, model inference, generation receipt,
MakeHuman bake/round-trip or Python environment change is required.

Create a UTF-8 descriptor with schema `anim002_reviewed_descriptor_v1`:

- `source`: `path`, saved-byte `sha256`, exact `action`, explicit `slot` identifier (or null for unslotted actions),
  `rig`, inclusive original `frames`, `fps: 30`. All frames in the action are retained. Identity-object/metre/30 Hz
  source and calibrated rest/joint order are required; unsupported inputs fail rather than implicitly retime/retarget.
- `variants`: one or more `{id, posture, stages}`; stages are `{id, coverage, roles}`. IDs cannot contain `/`.
  `coverage` is `partial` or `complete`; complete requires all eight ANIM-002 roles. Missing roles have no fallback.
- Each role declares `role`, inclusive `usable` and `representative` frame pairs, `travel_direction` in canonical
  XYZ, `loopability: non_cyclic`, and `phase: {intended, observed_support, transfer_compatibility: unverified}`.
  Observed support is `unknown`, `supported` or `mismatched`; intended phase is authored information, never inferred
  displayed contact. Representative intervals must be inside usable ranges. Optional `generation` records only known
  provenance; no pipeline generation recipe or receipt is required.

Human review is a separate `anim002_reviewed_approval_v1` JSON: `decision: accepted`, `reviewer`, `approval_origin`,
exact `descriptor_sha256`, identical `source`, `accepted_ranges` keyed `variant/stage/role`, full action `full_window`,
`preview_rig: Female`, `calibration_preserved: true` and `evidence_sha256` binding at least source/preview evidence.
Hash the final descriptor bytes, after review. Revised ranges require a new descriptor/review identity, not editing
failed receipts. Review is a human attestation, not a cryptographic signature or automatic aesthetic judgement.

```sh
blender --background --factory-startup --python-exit-code 1 \
  --python tools/animation-pipeline/ingest_reviewed_source.py -- \
  --descriptor PATH/descriptor.json --review PATH/review.json \
  --calibration game/assets/animations/motionbricks/poses.json --destination PATH/reviewed-source.json
```

The self-contained `anim002_reviewed_source_v1` JSON includes the original descriptor text/hash, approval, tool/source
provenance, full samples, and derived metadata. Publication validates before writing, fsyncs/read-validates a staged
JSON, then atomically replaces **one destination**; failures preserve prior bytes. Source/descriptor/review/calibration
and evidence paths cannot be destinations. Inputs are hashed before/after. There is no multi-file transaction promise.

### Shipped Feminine Source Replay

The shipped descriptor and review still bind the original relative paths and exact input bytes. To verify ingestion,
use the relocated source `.blend` (materialise Git LFS content first), descriptor, review and approval. Check
their hashes against the preserved [published bundle][feminine-bundle] before staging them and again inside
the mirror. Run Blender with the mirror as its working directory; keep the descriptor and review arguments
relative. Use absolute paths to the checkout's tool and relocated calibration and to the scratch output. Do not
restore the original layout in the checkout or edit retained evidence.

Run with Bash, Blender 5.3.0, `jq` and `sha256sum` from the checkout root. Scratch needs space for a copy of the
source and the replayed JSON.

```bash
set -euo pipefail
root=$PWD
scratch=$(mktemp -d "$root/game/temp/feminine-source-replay.XXXXXX")
mirror="$scratch/mirror"
# Original path prefix, used only inside the scratch mirror.
old=game/assets/characters/animations/source/locomotion/locomotion_feminine_landing_ablation_01
new=game/assets/animations/source/locomotion/locomotion_feminine_landing_ablation_01
published="$root/game/assets/animations/packs/default/locomotion/feminine/landing-ablation-01.json"
calibration="$root/game/assets/animations/motionbricks/poses.json"
output="$scratch/replayed-reviewed-source.json"
mkdir -p "$mirror/$(dirname "$old")"
for suffix in blend descriptor.json review.json approval.json; do
  case "$suffix" in
    blend) expected=$(jq -er '.descriptor.source.sha256' "$published") ;;
    descriptor.json) expected=$(jq -er '.descriptor_sha256' "$published") ;;
    review.json) expected=$(jq -er '.provenance.review_sha256' "$published") ;;
    approval.json)
      expected=$(jq -er --arg path "$old.$suffix" '.review.evidence_sha256[$path]' "$published") ;;
  esac
  printf '%s  %s\n' "$expected" "$new.$suffix" | sha256sum -c -
  cp --reflink=auto -- "$new.$suffix" "$mirror/$old.$suffix"
  printf '%s  %s\n' "$expected" "$mirror/$old.$suffix" | sha256sum -c -
done
(
  cd "$mirror"
  PYTHONDONTWRITEBYTECODE=1 blender --background --factory-startup --python-exit-code 1 \
    --python "$root/tools/animation-pipeline/ingest_reviewed_source.py" -- \
    --descriptor "$old.descriptor.json" --review "$old.review.json" \
    --calibration "$calibration" --destination "$output"
)
jq -Sc 'del(.provenance)' "$published" > "$scratch/published-payload.json"
jq -Sc 'del(.provenance)' "$output" > "$scratch/replayed-payload.json"
cmp "$scratch/published-payload.json" "$scratch/replayed-payload.json"
```

Compare the sorted JSON payloads, including ordered samples and review bindings, not the published and replayed
files byte-for-byte: export provenance is separate from the payload. If a pre-move replay is available, compare
replayed files byte-for-byte as an additional check. This replays approved ingestion only. The archived receipts
and capture records do not reconstruct the historical full review, including transient review files and recordings.
It does not regenerate motion, give new approval or verify motion quality.

### Multi-Take Reviewed Sets

`publish_reviewed_set.py --selection PATH/selection.json --destination PATH/set.json` binds multiple adjacent
`anim002_reviewed_source_v1` bundles without combining their frame numbers or losing individual action/review
identities. Selection lists `source`, `variant`, `stage`, `role`, `first`, `end_exclusive`, `kind` (`gait_cycle` or
`angular_episode`) and an explicit `reason`. All eight unique roles of one variant/stage are required. The publisher
validates existing approvals, binds bundle hashes, measures the raw SE2 endpoint transform, then stages/readbacks/
atomically replaces only the set. Incomplete selections refuse and preserve the previous set. Sources remain adjacent
durable JSON; embedded original source/evidence paths are provenance, not runtime requirements. Do not overwrite a
bundle in a live set: publish a new named bundle, then switch its set atomically.

Repetition metadata remains explicitly experimental, independent of human source approval. No source poses/ranges
are changed or admitted as seamless loops. A turn episode may include holds and is not a constant-rate stepping
cycle. The shared runtime `ReviewedRoleSampler` uses measured raw repetition/elapsed phase, with role changes
invalidating old phase and per-sample SE2 accumulation across wrapped windows. Normal CTRL stationary turns use
source-bound lift/recovery landmarks; normal NAV uses source-rate native-path targets and finite terminal turns.
See [the shipped feminine set][feminine-set] for selections,
controls and known quality limits. No activation flags or new generation command are involved. Coordinate the
fixed set/angular sidecar switch with consumers stopped; the publishers do not provide a multi-file transaction.

### Technical Transforms and Sampling

Only representation changes are applied: Blender `(x,y,z)` → canonical `(x,z,-y)` metres; evaluated bone/rest axes →
canonical identity-rest orientations; canonical world → parent-local transforms; normalised XYZW unit quaternions.
No extra sanitising was necessary on the reviewed probe. There is no pose, mesh, gait, foot, root-path, endpoint or seam
repair. The root is **evaluated Hips**, not ground `Root` or the model's separately smoothed root. Its global placement,
height and articulated local motion remain in full samples. Hips facing is `atan2(forward.x, forward.z)`, unwrapped;
travel is independently declared. Unit/rate conversion is not silently guessed. Non-rigid transforms are refused.

Per-sample forward differences retain uneven velocity/angular progression; the final sample has no invented future
derivative. `physical_reference` records net velocity, planar net/path speed and facing progression over the declared
representative interval. These are measurements, not intended speed, contact or a naturalness gate. Turns retain actual
measured translation/drift; no positive locomotion speed is fabricated. No cycle is admitted by this reader.

`AlleyCat.Motion.ReviewedMotionSource(path, calibration)` loads this data without changing `SomaMotionData` or normal
gameplay. `GetRole(variant, stage, role)` is explicit; `CopyWindow(role, originalFrame, callerBuffer)` copies four
frame-major canonical poses at 30 Hz, refusing starts before `usable[0]` or after `usable[1]-3`. No clamping/wrapping.
`FrameAtTime(seconds)` floors absolute time since the first source sample (one product ULP allowed for floating-point
boundary roundoff). It does not advance a clock, infer a phase, apply target placement or choose a horizon. The owning
controller must later supply those policies and decide what follows a non-cyclic end.

### Focused Contract Checks

The maintained checks cover reviewed-source identity, format, approval and publication failure behaviour, complete-set
publication and source-alias refusal. They do not run source generation or require a local Kimodo service. Source
fixtures and disposable publication outputs stay separate from the accepted Blender assets and runtime data.

```sh
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s tools/animation-pipeline/tests -p test_reviewed_source.py
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s tools/animation-pipeline/tests -p test_reviewed_set.py
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s tools/animation-pipeline/tests -p test_reviewed_set_source_alias.py
```

## Repository Source Assets

Durable authoring inputs split into two families: the three repository-fixed `.blend` artefacts under
`game/assets/animations/source/` (shared by every character) and per-character authored inputs under
`source/reference/{female,male}/` within the animation asset tree. Godot ignores `source/`, not the runtime asset
directories. Generated workspaces, staged copies, published reviewed sources and sets, exported MotionBricks data,
native idle clips, metadata and reports are derivatives and cannot replace a source merely because they contain
similar data. Locomotion sources reach runtime only through [reviewed-source ingestion](#reviewed-source-ingestion)
and set publication. The [authoritative specification][spec] owns their preservation and retirement requirements.

### Canonical Clean Rig

[`game/assets/animations/source/kimodo_source.blend`][canonical-source] is the repository-fixed clean
`Kimodo_Source` rig used by bootstrap and workspace composition. It contains no actions, meshes, or materials.
[`bootstrap_paths.py`][bootstrap-paths] owns the path; [`bootstrap_workspace.py`][bootstrap-workspace] owns and enforces
the exact approved baseline digest. Preserve the file byte-for-byte. Replace it only through deliberate baseline
re-approval with coordinated path, validation, and test updates.

### Character-Neutral Locomotion Template

[`game/assets/animations/source/locomotion/locomotion_template.blend`][locomotion-template] contains the pose
references, root waypoints, and neutral reference actions used for bootstrap workspace composition.
[`bootstrap_paths.py`][bootstrap-paths] owns this repository-fixed path, and
[`bootstrap_workspace.py`][bootstrap-workspace] owns and enforces its approved baseline digest. Preserve it
byte-for-byte. Replacement requires baseline re-approval and coordinated composition, validation, and test updates.

### Monolithic Locomotion Input

[`game/assets/animations/source/locomotion/locomotion.blend`][locomotion-source] is the read-only monolithic
bootstrap and extraction input. It carries source actions and semantic segments together with character and template
content. [`bootstrap_extraction.py`][bootstrap-extraction] owns this input and must never save over it. It is not a
runtime input. Retire it only after bootstrap extraction and every reproducibility and provenance dependency has an
approved replacement.

### Per-Character Authored Inputs

Each character owns two protected input families beneath its animation source directory
(KIMODO-SRC-006): the calibrated retarget setup at `kimodo_retarget_setup.blend` and the animated idle sources at
`idle/<variant>.blend` (for example the reference female's `sad.blend`). `--character-root` points to that source
directory, not the character model directory. These are authored inputs, not repository-fixed assets or derivatives.
[`bootstrap_paths.py`][bootstrap-paths] owns their path derivation. Idle sources are created only through
[`idle_source_bootstrap`][idle-source-bootstrap] (below), which stages a byte-verified copy of the character's
approved setup with the donor idle action appended, and refuses to overwrite an existing idle source. The pipeline
never modifies them.

Repository bootstrap and character input paths must report a clear error when a required input is missing; they must not
silently select a generated derivative. Source validation covers structure, path ownership, provenance, and read-only
or baseline byte integrity. Keep those checks separate from synthetic behavioural fixtures and real-content delivery
checks. Exact digest values remain in the owning implementation rather than this README.

## Environment

Run commands from the repository root. Blender-hosted commands require `blender`. External GDScript runs with
`godot-mono --path game --script "$PWD/tools/animation-pipeline/godot/commands/<script>.gd"`; this preserves game `res://`.
External tooling has no `.gd.uid` sidecars.

For Blender's project-local Python dependencies, run:

```sh
python3 tools/animation-pipeline/bootstrap_blender_dependencies.py
```

Set `KIMODO_BLENDER_PYTHON_DEPS` to place those dependencies outside the default ignored directory when required.

## Commands

Use these supported entry points:

```sh
blender --background --factory-startup --python tools/animation-pipeline/commands/idle_source_bootstrap.py -- \
    --character-root game/assets/animations/source/reference/female --variant sad \
    --donor-blend <donor.blend> --donor-action <idle_action>
python3 tools/animation-pipeline/commands/export_idle_clip.py --help
```

`idle_source_bootstrap` creates one per-character idle source. It alone publishes new sources to that `idle/` directory:
`--character-root`, `--variant`, `--donor-blend`, and `--donor-action` are required (`--setup`, `--temp-root`,
`--repository-root`, and `--approved-character-baselines` exist for disposable tests). It resolves the character's
Gate-1 approved baseline, copies the character setup byte-verified, appends only the donor's animated Kimodo-space
idle action (admission requires an animated loop: more than one distinct frame, Kimodo-space pose-bone channels,
finite values), reopens the staged blend to verify setup calibration parity, inert-rig absence, and the
exactly-one-action object set, and publishes transactionally to the derived idle-source path. An existing idle source
is a protected authoring input: the command refuses to overwrite one, and it never writes to any bootstrap input or
the donor blend. It prints a `KIMODO_IDLE_SOURCE_JSON=` summary and writes its self-validated `kimodo-idle-source/1`
report beneath `--temp-root` (default `game/temp/kimodo-idle-source/`).

[`calibrated_setup.py`](kimodo_pipeline/calibrated_setup.py) owns the setup-parity gate (KIMODO-SET-002) shared by
idle-source staging, workspace composition, and idle clip export: the three bootstrap inputs must match one approved
character baseline, the opened setup's constraint and `_RET` helper calibration must match the approved setup, and
the character target rig is resolved from its Root binding onto `Kimodo_Source` (or named with `--target-rig`).

## Independent Motion Frame Oracles

From the repository root, use Blender 5.3.0 and a Python environment with Kimodo 1.0.0, PyTorch and SciPy:

```sh
blender --background --factory-startup --python-exit-code 1 \
  --python tools/animation-pipeline/commands/regenerate_motion_frame_oracles.py -- blender
python tools/animation-pipeline/commands/regenerate_motion_frame_oracles.py kimodo
```

The commands read the committed MotionBricks poses and the approved source action (verified against its SHA-256).
They replace the compressed expected outputs in `integration-tests/fixtures/motion-frame-oracles/`, independently
of the C# runtime. Use them only for intentional fixture updates, record upstream versions and source identity,
and rerun the MotionBricks and reviewed-motion-source integration tests. They do not reproduce historical
playtest recordings or verify displayed motion quality.

## Verification Boundary

The MotionBricks exporters (`export_motionbricks.py` and `export_motionbricks_frames.py`) validate complete
structural, finite-data and applicable provenance bindings before publication. They write a sibling temporary JSON,
read it back and validate it, then atomically replace the destination. Validation, write or replace failure preserves
the previous destination and removes staging. Atomicity is per destination, not a transaction across both exports;
this is not a power-loss durability guarantee. No source animation is saved or rebaked.

Run their focused publication checks with:

```bash
python tools/animation-pipeline/tests/test_motionbricks_publication.py
```

Use the tool-owned focused checks appropriate to the changed command or contract. Behavioural expectations must use
synthetic fixtures; production motion data are structural, provenance, and immutability inputs, not behavioural
oracles.

[spec]: ../../specs/tooling/kimodo-pipeline/locomotive-animation-set.md
[canonical-source]: ../../game/assets/animations/source/kimodo_source.blend
[locomotion-template]: ../../game/assets/animations/source/locomotion/locomotion_template.blend
[locomotion-source]: ../../game/assets/animations/source/locomotion/locomotion.blend
[feminine-set]: ../../game/assets/animations/packs/default/locomotion/feminine/README.md
[feminine-bundle]: ../../game/assets/animations/packs/default/locomotion/feminine/landing-ablation-01.json
[bootstrap-paths]: kimodo_pipeline/bootstrap_paths.py
[bootstrap-workspace]: kimodo_pipeline/bootstrap_workspace.py
[bootstrap-extraction]: kimodo_pipeline/bootstrap_extraction.py
[idle-source-bootstrap]: kimodo_pipeline/idle_source_bootstrap.py

### Native Source Rests

`export_motionbricks.py` publishes `poses.json` with the canonical SOMA skeleton identity (`names`, `parents`,
`rest`), the Blender source rests (`blenderRest`) and the input hashes (`sources`). It carries no samples and no
target-helper, LOOK or target-rest calibration rows, and takes no arguments (see
[MotionBricks Exports](#motionbricks-exports)). To derive each native source skeleton from the reviewed F6 donor's
named rest/bind table and the current imported body, from the checkout root run:

```sh
godot-mono --headless --xr-mode off --path game -s "$(realpath tools/animation-pipeline/native-rest/derive.gd)"
```

Pass `-- /absolute/output/directory` to compare without replacing runtime resources. The destination must exist.
