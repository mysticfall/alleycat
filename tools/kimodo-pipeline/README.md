# Kimodo Pipeline

This tool owns Kimodo bootstrap, materialisation, packaging, publication, and validation. Its normative contract is
[Kimodo Motion Sources](../../specs/tooling/kimodo-pipeline/locomotive-animation-set.md).

MotionBricks runtime requirements are defined by the [shared animation contract](../../specs/animation/index.md).
The package/publication commands below are not prerequisites for that playtest. Its minimal read-only exports are
documented separately below; source-preservation and calibration safeguards remain in force.

## MotionBricks Exports

For artist-authored and reviewed actions, use [Reviewed Source Ingestion](#reviewed-source-ingestion).
Source generation and artistic review are external to these export and publication commands.

The minimal reference-Female runtime data can be reproduced from the repository root:

```sh
blender --background --factory-startup --python-exit-code 1 \
  --python tools/kimodo-pipeline/export_motionbricks.py
"$KIMODO_PYTHON" tools/kimodo-pipeline/export_motionbricks_frames.py
```

`KIMODO_PYTHON` is a Python interpreter with the approved Kimodo installation and its dependencies available.
The first command reads the calibrated setup and `idle/sad.blend` action `kimodo_m0a3dyf0`.
It hashes inputs before/after and never saves a `.blend`. The second exports only named
`SOMASkeleton77.global_rot_offsets`, not evaluation motions. Outputs are `game/data/motionbricks/poses.json` and
`model-frames.json`; the small authored `catalogue.json` selects the sad variant's idle stage. Existing
outputs are replaced deliberately during provisioning; evidence directories are never touched.

Catalogue stages name an action, normalised source speed and zero-based four-frame `entry`/`exit` indices at
30 Hz. Normal CTRL/NAV use the catalogue for idle; moving and turn roles use the shared reviewed source set
published below. The externally licensed SOMA FP32 ONNX model is not included. See the
[runtime launch instructions](../../game/src/Motion/README.md#launch).

## Reviewed Source Ingestion

`ingest_reviewed_source.py` is the maintained **Blender action → full ordered source → runtime-reader** path for
ANIM-002. It does not modify the idle `MotionCatalogue` or wire player/NPC clocks. Prerequisites:
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
  --python tools/kimodo-pipeline/ingest_reviewed_source.py -- \
  --descriptor PATH/descriptor.json --review PATH/review.json \
  --calibration game/data/motionbricks/poses.json --destination PATH/reviewed-source.json
```

The self-contained `anim002_reviewed_source_v1` JSON includes the original descriptor text/hash, approval, tool/source
provenance, full samples, and derived metadata. Publication validates before writing, fsyncs/read-validates a staged
JSON, then atomically replaces **one destination**; failures preserve prior bytes. Source/descriptor/review/calibration
and evidence paths cannot be destinations. Inputs are hashed before/after. There is no multi-file transaction promise.

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
See [the durable feminine playtest set](../../game/data/motionbricks/reviewed-feminine/README.md) for selections,
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
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s tools/kimodo-pipeline/tests -p test_reviewed_source.py
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s tools/kimodo-pipeline/tests -p test_reviewed_set.py
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s tools/kimodo-pipeline/tests -p test_reviewed_set_source_alias.py
```

## Package Layout

Every runtime package is character-owned and variant-scoped. For a character `<character>` with set
`<set>` (`locomotion` or `idle`) and variant `<variant>`:

```text
game/assets/characters/<character>/animations/
├── processed/<set>/                     # processed set directory
│   ├── <set>_<variant>.blend            # authoring carrier (LFS asset, set-level)
│   └── <variant>/                       # variant metadata (index, metrics, provenance, descriptors)
│       └── provenance/
│           ├── per_foot_planted_intervals_v1.json                    # retained planted intervals (both sets)
│           └── per_foot_planted_interval_derivation_report_v1.json   # derivation report for those intervals
└── runtime/<set>/<variant>/             # runtime package root
    ├── <set>_<variant>.tres             # AnimationLibrary with every animation embedded
    ├── <set>_<variant>_catalogue.json   # catalogue schema v4 (no clip-path fields)
    └── per_foot_planted_intervals_v1.json  # locomotion only; idle embeds its intervals in the catalogue
```

Animations have exactly one published representation: an entry in the package `AnimationLibrary`, resolved as
`(library resource, animation identity)`. No standalone clip resources, clip directory, or clip file paths exist
anywhere in the package, catalogue, or planted intervals; interval `identity` fields carry the library animation
identity. Catalogue schema v4 declares clip inventory, root-motion properties, phase events, and stitch mappings; the
idle catalogue additionally embeds its planted intervals in `per_foot_planted_intervals`. Packaging and validation
reject any other top-level field. Generated catalogues, planted intervals, and descriptors are pipeline-owned and must
not be hand-edited. Variant metadata uses runtime package descriptor schema v3.
Catalogue durations and embedded animation lengths conform to the processed index frame math
(frames at the declared fps) within `1e-6` seconds. The current transaction and runtime configuration publish one
selected variant per character and set. Publication refuses a different additional variant before changing durable
content. Variant-neutral metadata and alternative compatible fixtures do not enable additional production publication
or live switching.

The mandatory `publish_validated_packages.py` path, implemented by `kimodo_pipeline/validated_publication.py`, composes
Python descriptor validation with Godot package validation. Python and C# independently fail closed on schema,
identities/bindings, and hashes. Synthetic fixtures own isolated behavioural checks. Real selected-package checks own
delivered-content behaviour, while package publication owns structural, binding, hash, and immutability checks.

`godot/lib/animation_pose_sampler.gd` is the shared forward-kinematics sampler for tooling that measures poses from a
packaged or imported `AnimationLibrary` without a live skeleton. Any bone channel without an animation track samples the
bone's rest pose, and tracked positions are scaled by the skeleton's motion scale as the engine does.

## Planted Intervals

`per_foot_planted_intervals_v1.json` (schema version 1, contract `anim002_per_foot_planted_intervals_v1`) lists, for
every clip and both feet, contiguous `planted` and `swing` segments in source-clip seconds that cover the whole clip,
plus a `derivation` block naming the tool and version, the role policy identity and digest, the detection tolerances,
the ball joints, the skeleton sampled, and the canonical SHA-256 of the runtime library it was derived from.

`godot/commands/derive_planted_intervals.gd` (tool version 3) derives them from a runtime `AnimationLibrary` sampled
on an imported `GeneralSkeleton`. Planted means weight-bearing: the ball point (the `LeftToes`/`RightToes` rest-pose
origin in skeleton space) is down and effectively stationary. The Root bone carries root motion, so skeleton space is
the ground frame, and a ball sliding along the ground faster than the speed tolerance is swing; authored slides and
low shuffles are accepted as swing content and are not pinned. Every tolerance and heuristic lives in
[`config/planted_interval_role_policy_v1.json`](config/planted_interval_role_policy_v1.json) (policy schema 3):

- **`detection`:** ball height above that foot's own low-percentile baseline in the clip and windowed planar ball
  speed, classified with enter/exit hysteresis into candidate planted and swing runs.
- **`structures` and `capabilities`:** each capability names a role structure and, where the structure orders the
  feet, its `lead_foot`. For transitions this is the leading foot named by the role suffix: the foot in swing at the
  clip's locomotion-side boundary (the end of an entry, the start of an exit). So `i2f_l` (`entry_forward_left`) has
  lead foot `left`: its right foot steps first and its left foot is in swing at the end; `f2i_l` has its left foot in
  swing at the start. A structure declares `sequences`, the planted and swing sequence of the lead and trail feet for
  each loop intent it accepts (`looping`, fitted circularly, and `finite`, fitted linearly), and optionally
  `locomotion_boundary` (`start` or `end`: the lead foot must be in swing there), `first_swing` (`lead` or `trail`:
  whose first swing starts first), `maximum_swing_overlap_s`, and `planted_phase_offset_range` (a circular cycle
  fraction within `[0, 0.5]`). Entries (`locomotion_boundary: end`, `first_swing: trail`) start with both feet
  planted, step with the trail foot, and end with the lead foot swinging into the cycle; exits
  (`locomotion_boundary: start`, `first_swing: lead`) start with the lead foot swinging and end with both feet
  planted; confined manoeuvres (`first_swing: lead`) accept looping and finite clips; idle is planted throughout.
- **`structure_fit`:** planted gaps shorter than the minimum join the surrounding swing; the structure's expected
  swings are the strongest motion episodes (planar travel plus weighted peak height); every other episode is merged
  into its planted neighbours only when its travel and peak height are within the absorption limits. The weakest step
  must reach the minimum score and outscore the strongest merged episode by the policy ratio. The fitted sequences,
  the locomotion-side lead swing, the first-swing order, and the cross-foot limits must match the structure. A
  transition whose lead foot is planted at its locomotion-side boundary is one refusal, reported instead of the
  per-foot sequence mismatches it implies.
- **`verification`:** independent displacement checks the classifier does not use. Before each planted start the ball
  descends by at least the minimum within the arrival window; after each planted end it rises by the minimum or moves
  the minimum leave distance within the lift window; inside each planted interval its planar drift stays within the
  cap. Finite clip edges have no arrival or lift to verify.

A clip that cannot be fitted, or whose intervals fail verification (over-drift, no descent, no lift, or a merged run
beyond the absorption limits), is refused and reported, and no interval file is written. The report (`--report=`,
contract `anim002_per_foot_planted_interval_derivation_report_v1`) records every clip's segments and refusals and,
per foot, each boundary's kind and cause (`height` or `speed`; the fit only selects among signal crossings), the
ground-level slide at every speed-caused boundary, the steps, merged gaps and absorbed episodes of the fit, and every
interval's verification measurements. It contains no paths, comparisons, or timestamps, so re-deriving the same
library with the same policy and skeleton reproduces it byte for byte, and `intervals_sha256` binds it to the
serialised interval document it explains (empty when none was written). Publication retains it as
`provenance/per_foot_planted_interval_derivation_report_v1.json` next to the retained intervals. It is durable
provenance only: no runtime package, descriptor binding, or reader consumes it, so it is hash-neutral for runtime.

Looping clips, including side-steps and pivots authored to loop seamlessly, are fitted circularly; finite clips are
fitted linearly. An interval that crosses the loop seam is one interval for fitting, verification, and reporting; the
published segments split it at the clip boundary, so a looping clip's first and last segments of the same state are
two halves of one interval, neither is subject to a minimum length, and the seam is not a boundary. Every state change
is placed half a sample (`dt/2`) before the first sample of the new state, wrapped into the clip for looping clips: a
change at sample 0 is published and reported at `L - dt/2`, and the seam split leaves a half-sample tail of the new
state at the end of the clip.

Derivation samples the processed set-level authoring carrier's skeleton (for example
`processed/locomotion/locomotion_sad.blend`), which is the rig the package was retargeted to and packaged from. The
same library derived on `reference_female.blend` matches it within two 60 Hz samples (`0.0333 s`); on
`reference_male.blend` boundaries move by up to `0.167 s`, because a different leg geometry changes the ball
trajectory near the speed tolerance. `--compare=<intervals.json>` compares a derivation (for example on another
skeleton via `--skeleton=`) against a reference document; boundaries are compared per foot and circularly for
looping clips, and `--compare-tolerance-s=<seconds>` defaults to `1e-6`.

Packaging requires a `planted_intervals` declaration (`input`, durable `retained_input`, and `storage`:
`package_file` with `runtime_path` for locomotion, `catalogue_field` for idle) and refuses intervals whose schema, tool
version, policy digest, or runtime-library digest is stale, or whose clips, feet, or coverage disagree with the
package. The digest binds intervals to library bytes, and saving a library assigns new subresource identifiers, so
intervals must be derived from the exact library that is published: either the preserved published library
(`--preserve-runtime-library`) or a library staged with `--stage-library-only` and reused with
`--runtime-library-source=<res path>`. The runtime reader (`PerFootPlantedIntervals`) applies the same binding when it
validates intervals against a locomotion catalogue or loads a validated idle catalogue: character (from the package
path), package, variant, every clip's role, capability, loop intent, and duration, and the canonical library digest.

## Repository Source Assets

Durable authoring inputs split into two families: the three repository-fixed `.blend` artefacts under
`game/assets/characters/animations/source/` (shared by every character) and each character's own authored inputs beneath
its character asset root (the calibrated retarget setup and the animated idle sources). Generated workspaces, processed
carriers, runtime resources, metadata, and reports are derivatives and cannot replace a source merely because they
contain similar data. The [authoritative specification][spec] owns their preservation and retirement requirements.

### Canonical Clean Rig

[`game/assets/characters/animations/source/kimodo_source.blend`][canonical-source] is the repository-fixed clean
`Kimodo_Source` rig used by bootstrap and workspace composition. It contains no actions, meshes, or materials.
[`bootstrap_paths.py`][bootstrap-paths] owns the path; [`bootstrap_workspace.py`][bootstrap-workspace] owns and enforces
the exact approved baseline digest. Preserve the file byte-for-byte. Replace it only through deliberate baseline
re-approval with coordinated path, validation, and test updates.

### Character-Neutral Locomotion Template

[`game/assets/characters/animations/source/locomotion/locomotion_template.blend`][locomotion-template] contains the pose
references, root waypoints, and neutral reference actions used for bootstrap workspace composition.
[`bootstrap_paths.py`][bootstrap-paths] owns this repository-fixed path, and
[`bootstrap_workspace.py`][bootstrap-workspace] owns and enforces its approved baseline digest. Preserve it
byte-for-byte. Replacement requires baseline re-approval and coordinated composition, validation, and test updates.

### Monolithic Locomotion Input

[`game/assets/characters/animations/source/locomotion/locomotion.blend`][locomotion-source] is the read-only monolithic
bootstrap and extraction input. It carries source actions and semantic segments together with character and template
content. [`bootstrap_extraction.py`][bootstrap-extraction] owns this input and must never save over it. It is not a
runtime package. Retire it only after bootstrap extraction and every reproducibility and provenance dependency has an
approved replacement.

### Per-Character Authored Inputs

Each character owns two protected input families beneath its character asset root
(KIMODO-SRC-006): the calibrated retarget setup at `animations/source/kimodo_retarget_setup.blend` and the animated
idle sources at `animations/source/idle/<variant>.blend` (for example the reference female's `sad.blend`). They are
authored inputs, not repository-fixed assets and not derivatives. [`bootstrap_paths.py`][bootstrap-paths] owns their
path derivation; idle sources are created only through [`idle_source_bootstrap`][idle-source-bootstrap] (below), which
stages a byte-verified copy of the character's approved setup with the donor idle action appended, and refuses to
overwrite an existing idle source. The pipeline never modifies them.

Repository bootstrap and character input paths must report a clear error when a required input is missing; they must not
silently select a generated derivative. Source validation covers structure, path ownership, provenance, and read-only
or baseline byte integrity. Keep those checks separate from synthetic behavioural fixtures and real selected-package
delivery checks. Exact digest values remain in the owning implementation rather than this README.

## Environment

Run commands from the repository root. Blender-hosted commands require `blender`. External GDScript runs with
`godot-mono --path game --script "$PWD/tools/kimodo-pipeline/godot/commands/<script>.gd"`; this preserves game `res://`.
External tooling has no `.gd.uid` sidecars.

For Blender's project-local Python dependencies, run:

```sh
python3 tools/kimodo-pipeline/bootstrap_blender_dependencies.py
```

Set `KIMODO_BLENDER_PYTHON_DEPS` to place those dependencies outside the default ignored directory when required.

## Commands

Use these supported entry points:

```sh
blender --background --factory-startup --python tools/kimodo-pipeline/commands/retarget_animations.py -- \
    --package locomotion --variant sad --character-root game/assets/characters/reference/female \
    --root-lateral-axis 0 --workspace <materialised-workspace-or-staged-copy>
blender --background --factory-startup --python tools/kimodo-pipeline/commands/retarget_animations.py -- \
    --package idle --variant sad --character-root game/assets/characters/reference/female \
    --idle-source-action <idle_action>
blender --background --factory-startup --python tools/kimodo-pipeline/commands/idle_source_bootstrap.py -- \
    --character-root game/assets/characters/reference/female --variant sad \
    --donor-blend <donor.blend> --donor-action <idle_action>
python3 tools/kimodo-pipeline/commands/prepare_validated_publication_manifest.py --help
python3 tools/kimodo-pipeline/commands/publish_validated_packages.py --help
```

Both retarget invocations name the character explicitly: `--character-root` is required (the pipeline assumes no
default character), `--root-lateral-axis` is the character's Root local lateral-location axis (required whenever the
selected locomotion roles derive the precision temporal reversal, `0` for the reference female), and the target rig is
resolved from the opened content or named with `--target-rig`. Locomotion opens the character's authoring workspace
(or a staged materialised copy, bound with `--materialisation-report`); the idle package accepts no workspace or source
override — it always bakes the character's idle source at `animations/source/idle/<variant>.blend` with an explicit
`--idle-source-action`.

`idle_source_bootstrap` creates one per-character idle source. It is the only writer of `animations/source/idle/`:
`--character-root`, `--variant`, `--donor-blend`, and `--donor-action` are required (`--setup`, `--temp-root`,
`--repository-root`, and `--approved-character-baselines` exist for disposable tests). It resolves the character's
Gate-1 approved baseline, copies the character setup byte-verified, appends only the donor's animated Kimodo-space
idle action (admission requires an animated loop: more than one distinct frame, Kimodo-space pose-bone channels,
finite values), reopens the staged blend to verify setup calibration parity, inert-rig absence, and the
exactly-one-action object set, and publishes transactionally to the derived idle-source path. An existing idle source
is a protected authoring input: the command refuses to overwrite one, and it never writes to any bootstrap input or
the donor blend. It prints a `KIMODO_IDLE_SOURCE_JSON=` summary and writes its self-validated `kimodo-idle-source/1`
report beneath `--temp-root` (default `game/temp/kimodo-idle-source/`).

`retarget_animations.py` writes the set-level authoring blend and the variant-scoped metadata directory. Before any
bake output, the idle package passes the setup-parity gate (KIMODO-SET-002): the idle source's setup bytes must match
the approved character baseline and its constraint/helper fingerprints must match the approved calibration, and
generated idle provenance records the idle source path, action, and digest (`idle_source_binding`). The manifest
preparer requires each staged variant's `provenance/per_foot_planted_intervals_v1.json` and takes
`--stage-root`, `--processed-root`, `--runtime-root`, `--legacy-root`, and `--output`; it does not mutate production
roots and refuses a different additional variant when the durable set already contains the selected variant.

External `configure_kimodo_import.gd`, `package_kimodo_runtime.gd`, `validate_kimodo_runtime_package.gd`, and
`derive_planted_intervals.gd` accept either `--descriptor=<descriptor.json>` or
`--package=<locomotion|idle>` plus `--variant=<variant>` (and optionally `--character=<character>`) as user arguments
after the `--` separator, as in `--script <script>.gd -- --package=idle --variant=sad`. The scripts read only
`OS.get_cmdline_user_args()`, so arguments placed before `--` are ignored and the package silently defaults to
`locomotion` with variant `sad`. Use the absolute-path invocation in **Environment**. Configuration and packaging write
package artefacts; run them only against disposable stages until the validated publication transaction is ready.

`configure_kimodo_import.gd` disables `save_to_file` externalisation globally and for every action — including stale
sidecar blocks — and fails when any residual `enabled=true` survives.

Every `package_kimodo_runtime.gd` invocation must pass exactly one explicit, disposable
`--stage-root=res://temp/<repair>/stage/runtime/<set>/<variant>`. The packager rejects missing, empty, unsafe, or
malformed stage roots before it changes any durable root. Stage roots must remain under `res://temp`, end in
`runtime/<set>/<variant>`, and not traverse symbolic links. Preflight also refuses externalising import
configurations, variant/path disagreement, and frame-math disagreement between the processed index, catalogue, and
staged library. Packaging only prepares and validates that stage; the generated descriptor
intentionally contains durable production resource paths, which only `publish_validated_packages.py` may replace
through the required cross-package transaction.

## Publication Safety

`publish_validated_packages.py` is **destructive**. Use it only after the two disposable package stages and their
descriptors have passed preflight. It accepts only `--manifest <manifest.json>` and
`--journal-directory <journal-directory>` and performs the production-safe, journalled multi-package transaction.

The manifest contains exactly `locomotion` in `planted-interval-file-locomotion` mode and `idle` in
`catalogue-planted-interval-idle` mode. Both retain hash-bound planted intervals in their processed provenance;
locomotion also publishes them as a package file and idle embeds them in its catalogue. Do not use the publisher for a
partial package update or unreviewed staging output.

Each staged processed package must carry its content-owned
`provenance/stitch-release-intent-v1.json` inside its variant metadata directory. Manifest preparation strictly
validates and transports those opaque role edges; generic tooling does not invent them. Idle's declaration has an
empty `stitch_edges` array.

For a metadata-only repair, copy both current processed packages byte-for-byte to disposable paths under `game/temp`;
add or update only the package-owned intent; derive each package's planted intervals from its published library into
the staged `provenance/per_foot_planted_intervals_v1.json` **and write the fresh derivation report alongside it in the
same invocation**; prepare the two-package manifest; package both stages with
`--preserve-runtime-library`; and publish them with `publish_validated_packages.py`. Do not run source repair,
materialisation, or retargeting. Use full provenance regeneration instead if any processed animation, index, metrics,
source/action provenance, retarget binding, or materialisation carrier is changed or invalid.

For each package, the derivation arguments must target the same staged variant provenance directory (replace
`<repair>`, `<set>`, and `<variant>` with the selected stage, package, and variant):

```text
--output=res://temp/<repair>/stage/processed/<set>/<variant>/provenance/per_foot_planted_intervals_v1.json
--report=res://temp/<repair>/stage/processed/<set>/<variant>/provenance/per_foot_planted_interval_derivation_report_v1.json
```

Remove the copied interval and report files from that staged directory before deriving, and stop on derivation
failure. Before manifest preparation, require both fresh files and verify the report's `intervals_sha256` against
the SHA-256 of the staged interval file's bytes. The manifest's `processed_stage` must name the processed package
directory containing both files; packaging/preflight descriptors must select those staged intervals. Manifest
preparation does not validate the report binding. Directing `--report` only to a logs directory leaves a copied stale
report in the publication stage. Keep command logs separately; copy the fresh staged report to logs only if needed.
This pairing applies to full provenance regeneration too. Historical `republish.sh` scripts under `game/temp/` are
task scratch artefacts, not maintained publication entry points; follow this staged recipe instead.

Staged publication bytes move only through binary-safe channels: `ResourceSaver`/`ResourceLoader` for libraries,
`DirAccess.copy_absolute` for UID sidecars, and `shutil.copy2`/`os.replace` for stage transport. Text writes target
JSON artefacts only.

Runtime-library SHA-256 bindings use deterministic `ResourceSaver` text while excluding only the path-owned top-level
`uid="uid://..."` annotation that Godot adds when importing the resource at its durable path. Producer and validators
apply the same canonicalisation; subresource IDs and all animation payload bytes remain covered.

## Verification Boundary

The MotionBricks exporters (`export_motionbricks.py` and `export_motionbricks_frames.py`) validate complete
structural, finite-data and applicable provenance bindings before publication. They write a sibling temporary JSON,
read it back and validate it, then atomically replace the destination. Validation, write or replace failure preserves
the previous destination and removes staging. Atomicity is per destination, not a transaction across both exports;
this is not a power-loss durability guarantee. No source animation is saved or rebaked.

Run their focused publication checks with:

```bash
python tools/kimodo-pipeline/tests/test_motionbricks_publication.py
```

Use the tool-owned focused checks appropriate to the changed command or contract. Behavioural expectations must use
synthetic fixtures; production motion and planted-interval data are structural, provenance, and immutability inputs,
not behavioural oracles. Clean-clone reproducibility is proven from an isolated cold-import project with no UID
registration or import-cache state (`test_cold_import_project_loads_the_package_without_uid_registration`).

[spec]: ../../specs/tooling/kimodo-pipeline/locomotive-animation-set.md
[canonical-source]: ../../game/assets/characters/animations/source/kimodo_source.blend
[locomotion-template]: ../../game/assets/characters/animations/source/locomotion/locomotion_template.blend
[locomotion-source]: ../../game/assets/characters/animations/source/locomotion/locomotion.blend
[bootstrap-paths]: kimodo_pipeline/bootstrap_paths.py
[bootstrap-workspace]: kimodo_pipeline/bootstrap_workspace.py
[bootstrap-extraction]: kimodo_pipeline/bootstrap_extraction.py
[idle-source-bootstrap]: kimodo_pipeline/idle_source_bootstrap.py

### Native Source Rests

`export_motionbricks.py` publishes canonical samples, source layout and Blender source rests, not target-helper,
LOOK or target-rest calibration rows. Its CLI is unchanged. To derive each native source skeleton from the reviewed
F6 donor's named rest/bind table and the current imported body, from the checkout root run:

```sh
godot-mono --headless --xr-mode off --path game -s "$(realpath tools/kimodo-pipeline/native-rest/derive.gd)"
```

Pass `-- /absolute/output/directory` to compare without replacing runtime resources. The destination must exist.
