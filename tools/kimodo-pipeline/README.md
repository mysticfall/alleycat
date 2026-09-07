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
└── runtime/<set>/<variant>/             # runtime package root
    ├── <set>_<variant>.tres             # AnimationLibrary with every animation embedded
    ├── <set>_<variant>_catalogue.json   # catalogue schema v3 (no clip-path fields)
    └── authored_per_foot_contacts_v1.json  # locomotion only
```

Animations have exactly one published representation: an entry in the package `AnimationLibrary`, resolved as
`(library resource, animation identity)`. No standalone clip resources, clip directory, or clip file paths exist
anywhere in the package, catalogue, or contact evidence; contact `clip` fields carry the library animation identity.
Catalogue durations and embedded animation lengths conform to the processed index frame math
(frames at the declared fps) within `1e-6` seconds. Publication of a second variant for the same character and set is
refused until multi-variant publication is ratified.

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
blender --background --python tools/kimodo-pipeline/commands/retarget_animations.py -- --help
python3 tools/kimodo-pipeline/commands/generate_contact_evidence.py --help
python3 tools/kimodo-pipeline/commands/prepare_validated_publication_manifest.py --help
python3 tools/kimodo-pipeline/commands/publish_validated_packages.py --help
```

`retarget_animations.py` writes the set-level authoring blend and the variant-scoped metadata directory.
`generate_contact_evidence.py` writes only to a caller-supplied disposable processed stage. The manifest preparer takes
`--stage-root`, `--processed-root`, `--runtime-root`, `--legacy-root`, and `--output`; it does not mutate production
roots and refuses a second variant for the same character and set.

External `configure_kimodo_import.gd`, `package_kimodo_runtime.gd`, and
`validate_kimodo_runtime_package.gd` accept either `--descriptor=<descriptor.json>` or
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
configurations, a second variant for the same character and set, and frame-math disagreement between the processed
index, catalogue, and staged library. Packaging only prepares and validates that stage; the generated descriptor
intentionally contains durable production resource paths, which only `publish_validated_packages.py` may replace
through the required cross-package transaction.

## Publication Safety

`publish_validated_packages.py` is **destructive**. Use it only after the two disposable package stages and their
descriptors have passed preflight. It accepts only `--manifest <manifest.json>` and
`--journal-directory <journal-directory>` and performs the production-safe, journalled multi-package transaction.

The manifest contains exactly `locomotion` in `contact-evidenced-locomotion` mode and `idle` in `contact-free-idle`
mode. Locomotion retains hash-bound contact evidence. Idle explicitly carries no contact declaration or artefact. Do
not use the publisher for a partial package update or unreviewed staging output.

Each staged processed package must carry its content-owned
`provenance/stitch-release-intent-v1.json` inside its variant metadata directory. Manifest preparation strictly
validates and transports those opaque role edges; generic tooling does not invent them. Idle's declaration has an
empty `stitch_edges` array.

For a metadata-only repair, copy both current processed packages and retained locomotion contact evidence byte-for-byte
to disposable paths under `game/temp`; add or update only the package-owned intent; configure/import/package both stages;
prepare the two-package manifest; validate it; and publish it with `publish_validated_packages.py`. Do not run source
repair, materialisation, retargeting, or contact generation. Use full provenance regeneration instead if any processed
animation, index, metrics, contact evidence, source/action provenance, retarget binding, or materialisation carrier is
changed or invalid.

Staged publication bytes move only through binary-safe channels: `ResourceSaver`/`ResourceLoader` for libraries,
`DirAccess.copy_absolute` for UID sidecars, and `shutil.copy2`/`os.replace` for stage transport. Text writes target
JSON artefacts only.

## Verification Boundary

Use the tool-owned focused checks appropriate to the changed command or contract. Behavioural expectations must use
synthetic fixtures; production-authored motion and contact data are structural, provenance, and immutability inputs,
not behavioural oracles. Clean-clone reproducibility is proven from an isolated cold-import project with no UID
registration or import-cache state (`test_cold_import_project_loads_the_package_without_uid_registration`).
