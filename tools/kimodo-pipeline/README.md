# Kimodo Pipeline

This tool owns Kimodo bootstrap, materialisation, packaging, publication, and validation. Its normative contract is
[Kimodo Locomotive Animation-Set Pipeline](../../specs/tooling/kimodo-pipeline/locomotive-animation-set.md).

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
