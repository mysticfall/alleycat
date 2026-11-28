# Animation Variants

This guide is for anyone adding idle stances or gaits to AlleyCat characters, including external content-pack
authors. You can author clips in Blender and edit descriptors and template scenes in a text editor, without
changing code or opening the Godot editor. See [Animation Variant Format][variant-format] and
[Animation Clip Authoring][authoring] for the normative contracts.

[variant-format]: ../../../specs/animation/003-animation-catalogue/variant-format.md
[authoring]: ../../../specs/animation/003-animation-catalogue/authoring.md
[layers]: ../../../specs/animation/003-animation-catalogue/index.md#character-catalogue-and-layering
[content-packs]: ../../../docs/content-packs.md
[female-base]: ../characters/templates/reference_female/reference_female_base.tscn
[reviewed-set]: ../../../specs/tooling/kimodo-pipeline/locomotive-animation-set.md#source-production-contract

1. Publish a native idle clip or an ANIM-002 reviewed gait and write its [descriptor](#variant-descriptors).
2. Add, remove or replace entries in your [character template](#selecting-variants-in-template-scenes).
3. [Validate](#validating-variants) the descriptor and the composed template before playing or exporting.

## Category-First Storage

The reference base templates select [`idle/sad`](idle/sad/variant.json) and
[`locomotion/feminine`](locomotion/feminine/variant.json), in that order. Each descriptor lives beside its payload.

- `idle/sad/` holds the native NPC stance exported from `idle/sad/source/sad.blend`, action `kimodo_m0a3dyf0`,
  using the calibrated female setup in `authoring/reference/female/`.
- `locomotion/feminine/` keeps `set.json`, the reviewed source export `landing-ablation-01.json` and
  `turn-angular-reference.json` together. The [gait guide](locomotion/feminine/README.md) traces the reviewed
  action and protected records in the adjacent `source/`.
- `motionbricks/` is shared runtime rig data: canonical calibration, model frame offsets and source-rest scenes.
- `player-pose/`, `hand-pose/` and `eyes/` are built-in assets, not ANIM-003 catalogue entries.
- Dedicated Blender inputs and review files sit in ignored `source/` children beside their variant or category.
  Shared rigs, setups, generator inputs and workspace templates stay in ignored `authoring/`. Runtime does not
  load either authoring location. AnimationTree templates and imported models stay with the character assets;
  dedicated test fixtures stay with their tests.

### Player Pose Provenance

The [female model importer][female-import] owns all twelve external `player-pose/` clips. The
[female generator config][female-generator] selects `player-pose/source/animations_rigify.blend` upstream,
with the same exact actions. This traces the configured inputs, not a replay of model generation.
The pose state machine still uses its existing subset; the other exports remain model library members.

| Player Pose Clips (`.tres`) | Model and Configured Rigify Actions |
| --- | --- |
| `All Fours`, `All Fours-enter`, `All Fours-forward` | `All Fours-loop`, `All Fours-enter`, `All Fours-forward-loop` |
| `Crouch`, `Crouch-enter`, `Crouch-seek` | `Crouch-loop`, `Crouch-enter`, `Crouch-seek` |
| `Idle` | `Idle-loop` (not the NPC `sad` stance) |
| `Kneel`, `Kneel-enter`, `Kneel-seek` | `Kneel-loop`, `Kneel-enter`, `Kneel-seek` |
| `Sit`, `Sit-enter` | `Sit-loop`, `Sit-enter` |

The unreferenced standalone `Walk Forward.tres` was removed. The model and forearm-twist fixture keep their own
embedded `Walk Forward` animations, and `Walk Forward-loop` remains in `authoring/reference/female/animations.blend`.

### Hand and Eye Provenance

- `hand-pose/Grab-ball-40.tres`, `Grab-pipe-10.tres` and `Reset.tres` are historical female model exports of
  the identically named actions. The configured upstream input `authoring/reference/female/animations.blend`
  contains all three actions and `Walk Forward-loop`. The current model embeds its same-named hand clips
  separately. The ball and pipe clips serve their respective grab points; optical tracking also samples
  `Grab-pipe-10.tres` for flexion and `Reset.tres` for neutral. The hand tree's `Reset` key resolves to
  the embedded animation, not this standalone neutral sample.
- `hand_fist.tres`, `hand_thumb_opposition.tres` and `hand_thumb_spread.tres` in `hand-pose/` retain their
  historical female model export keys and configured UID/fallback targets. Their names match the historical
  import keys, but the current model and configured inputs lack these actions. A fresh model library does not
  contain them, no independent runtime reader was found, and earlier upstream sources are unknown.
- `eyes/eyes.tres` is the legacy Godot library loaded by the female tracking fixture: `eyes_blink.tres`,
  `eyes_right_left.tres` and `eyes_up_down.tres` supply `Eyes Blink`, `Eyes Right Left` and `Eyes Up Down`,
  respectively. No upstream Blender action is established for these resources. Production eye curves are
  generated by the [character importer][eye-import] from each model's own topology and morphs, not from the
  fixture library.

[female-import]: ../characters/reference/female/reference_female.blend.import
[female-generator]: ../characters/reference/female/reference_female.json
[eye-import]: ../characters/import/character_eye_animation_import.gd

## Variant Descriptors

Write one UTF-8 JSON object per variant. `id` starts with a lowercase letter, followed by lowercase letters,
digits or underscores. Each tag starts with a lowercase letter and can also contain digits, underscores or
hyphens. There must be at least one tag and a non-blank `description`. Extra or repeated fields are errors.
Identity is `(category, id)`: the same id can occur in both categories, but not twice in one composed category.

Shipped `idle/sad/variant.json`, next to the native `Animation` resource:

```json
{
  "schema": "anim003_variant_v1",
  "category": "idle",
  "id": "sad",
  "tags": ["sad", "slumped", "downcast"],
  "description": "stands with slumped shoulders and a lowered gaze, shifting weight listlessly",
  "clip": "assets/animations/idle/sad/sad.tres"
}
```

Shipped `locomotion/feminine/variant.json`, next to the [reviewed set][reviewed-set]:

```json
{
  "schema": "anim003_variant_v1",
  "category": "locomotion",
  "id": "feminine",
  "tags": ["relaxed", "feminine"],
  "description": "walks with an easy, relaxed feminine gait",
  "set": "assets/animations/locomotion/feminine/set.json"
}
```

## Path Resolution

In a scene, `Descriptor` is an explicit canonical `res://` path. In that JSON descriptor, `clip` or `set` is a
forward-slash path relative to the descriptor's **owning content root**, not to the scene or variant directory.
Do not use absolute paths, `res://`, `.` or `..` in payload paths. A symlink cannot cross ownership.

- Base descriptors resolve payloads under `res://`, excluding `res://content/`.
- For an active [content pack][content-packs] at `res://content/my_pack/`, a descriptor there with
  `"clip": "assets/animations/idle/quiet/quiet.tres"` resolves to
  `res://content/my_pack/assets/animations/idle/quiet/quiet.tres`. Put the descriptor beside `quiet.tres`.
- A pack's gait descriptor must point to a set beside it in the same pack, with the reviewed role bundles
  and angular metadata under that owner. To reuse the built-in gait, inherit or select the **base descriptor**
  instead of pointing a pack-owned descriptor at the base payload.

## Selecting Variants in Template Scenes

Put one `AnimationVariantLayer` node directly under `AnimationCatalogue` in each contributing template scene.
Use a distinct child name; do not override an inherited layer's `Operations`. [Layers][layers] apply in template
ancestry order, then in the array's written order. Installation does not discover variants from files on disk.

This `.tscn` fragment assumes a local template derived from a shipped NPC role. It adds `idle/quiet` and replaces
inherited `idle/sad`. Prepare your own `quiet` and `sad` descriptors in the paths shown; the replacement must
declare `category: idle` and `id: sad`. Insert the declarations into the corresponding external resource,
subresource and node sections of your scene. Choose unused resource ids.

```text
[ext_resource type="Script" path="res://src/Animations/AnimationVariantLayer.cs" id="cat_layer"]
[ext_resource type="Script" path="res://src/Animations/AnimationVariantOperation.cs" id="cat_op"]

[sub_resource type="Resource" id="AddQuiet"]
script = ExtResource("cat_op")
Kind = 0
Descriptor = "res://content/my_pack/assets/animations/idle/quiet/variant.json"

[sub_resource type="Resource" id="ReplaceSad"]
script = ExtResource("cat_op")
Kind = 2
Category = "idle"
EntryID = "sad"
Descriptor = "res://content/my_pack/assets/animations/idle/sad/variant.json"

[node name="LocalVariants" type="Node" parent="AnimationCatalogue"]
script = ExtResource("cat_layer")
Operations = [SubResource("AddQuiet"), SubResource("ReplaceSad")]
```

- **Add (`Kind = 0`):** use only `Descriptor`. Its key must not already exist; it goes at the end.
- **Remove (`Kind = 1`):** use only `Category` and `EntryID`. To remove rather than replace `sad` above, change
  `ReplaceSad` to `Kind = 1` and delete its `Descriptor` line.
- **Replace (`Kind = 2`):** use `Category`, `EntryID` and `Descriptor`. It removes the inherited key and appends
  the new entry. Two operations on one key in the same layer are invalid, as is removing or replacing an absent key.

For a shipped example of `Add`, see `BaseVariants` in the [female base scene][female-base].

### External Content Workflow

Install the pack under `game/content/<pack-id>/` with a valid start scene as described in the
[content-pack guide][content-packs]. Put the local template beside your character scene and configure the
character's installer to use it ([CHAR-002](../../../specs/character/002-character-root/index.md)). Publish the
variant and payload under the pack root, then explicitly select it in that template. Launch or validate with the
pack active. Installing a pack does not change other characters' catalogues or select any of its files by itself.

## Template Defaults

The `AnimationCatalogue` node exports `DefaultIdle` and `DefaultLocomotion`. A character uses, in order:

1. the NPC's own choice, once it has made one;
2. the template default;
3. the first compatible entry of that category in the composed catalogue order.

In your derived template, omit either property to inherit it, write an entry id to override it, or write `""`
to clear it. To set `quiet` as the idle default and leave the gait default inherited, put this node override
above the `LocalVariants` child. If the scene already has that override node, edit it instead of adding another:

```text
[node name="AnimationCatalogue" parent="."]
DefaultIdle = "quiet"
```

Set defaults on the catalogue node, not in `Operations`. Removing an entry does not clear the default.

A default that names an unknown or incompatible entry is ignored with a log line. Base templates set the locomotion
default; NPC role templates set the idle default; player templates declare no idle default, because catalogue idles
never play on the player.

## Writing Descriptions and Tags

- **Descriptions** are the only thing other characters perceive when an NPC changes stance or gait. Write an external,
  visible account in the third person without a subject: "stands with slumped shoulders and a lowered gaze", "walks
  with an easy, relaxed gait". Do not write in the first person, and do not mention ids, tags or feelings that are
  not visible.
- **Tags** help the NPC itself choose. Use a few short lowercase words about mood or manner (`sad`, `relaxed`,
  `weary`). Observers never see them.

## Authoring Idle Clips

Every idle clip is a native Godot `Animation` resource on the reference character skeleton, with bone tracks bound
to `%GeneralSkeleton:<bone>`. A good idle:

- **starts on a settled pose**, because the game blends into frame 0 and the frames just after it;
- **loops cleanly**, with the last frame flowing back into the first;
- has no entry or exit markers; the game can leave an idle at any frame.

There are two authoring paths, both run by the Kimodo pipeline tool. See its
[Native Idle Clips](../../../tools/animation-pipeline/README.md#native-idle-clips) section for every option.

### Kimodo Path

Author the motion on the SOMA `Kimodo_Source` rig in a copy of the character's calibrated setup. The built-in
idle source belongs at `idle/<variant>/source/<variant>.blend`, separate from the shared setup. In Blender the
meshed reference rig follows `Kimodo_Source`, so the export matches the preview. From the repository root, replay
the shipped stance into scratch and compare it with the published clip:

```sh
python3 tools/animation-pipeline/commands/export_idle_clip.py kimodo \
  --character-root game/assets/animations/authoring/reference/female \
  --source game/assets/animations/idle/sad/source/sad.blend \
  --action kimodo_m0a3dyf0 \
  --skeleton-scene res://assets/characters/reference/female/reference_female.blend \
  --check-skeleton-scene res://assets/characters/reference/male/reference_male.blend \
  --compare res://assets/animations/idle/sad/sad.tres \
  --temp-root game/temp/animation-guide/idle-stage \
  --output game/temp/animation-guide/sad.tres
```

Add `--preview-blend game/temp/<name>/preview.blend` to save the prepared setup copy with your action bound, for
review in the Blender interface.

### Rigify Path

Build a Rigify (or other) control rig over the reference skeleton, and make the reference armature follow the
control rig's deform bones with bone constraints (for example Copy Transforms). Animate the control rig, then export
the reference armature's deform bones:

```sh
python3 tools/animation-pipeline/commands/export_idle_clip.py rig \
  --blend path/to/authoring.blend --control-rig rig --target Female --action my_idle \
  --skeleton-scene res://assets/characters/reference/female/reference_female.blend \
  --output game/content/my_pack/assets/animations/idle/my_idle/my_idle.tres
```

The control rig itself is never exported.

Both paths write the clip, not its descriptor. The tools leave source blends unchanged and preserve the existing
clip on failed export. Re-exporting over a clip keeps its UID. Check the result on the target character in Godot;
the validator checks bindings, not appearance.

## Validating Variants

Build and import the project, then run the [headless validator][validator] from the repository root. This checks
both shipped descriptors and all shipped role templates:

```sh
godot-mono --headless --xr-mode off --path game \
  --script "$PWD/tools/animation-pipeline/godot/commands/validate_animation_variants.gd" -- \
  --variant=res://assets/animations/idle/sad/variant.json \
  --variant=res://assets/animations/locomotion/feminine/variant.json \
  --template=res://assets/characters/templates/reference_female/reference_female_npc.tscn \
  --template=res://assets/characters/templates/reference_male/reference_male_npc.tscn \
  --template=res://assets/characters/templates/reference_female/reference_female_player.tscn \
  --require-compatible
```

- Use at least one `--variant=` or `--template=`; both are repeatable. A standalone descriptor check does not add
  that variant to any template. A template check validates its on-disk inherited selection and reports bindings,
  incompatibilities and invalid defaults, without starting a character scene or running inference.
- For optional content, add `--content-pack=my_pack` **before** the `--` separator and pass the pack's actual
  `res://content/my_pack/...` resource paths after it. The pack must resolve through normal startup rules.
- Exit `0` means valid; non-strict checks can still report incompatibilities or invalid defaults. Exit `1` means
  invalid content or composition, or those findings under `--require-compatible`. Exit `2` means bad usage or
  the validator script could not load its command. Read the `ANIMATION_VARIANT_VALIDATION_JSON=` summary for
  `valid`, `passed`, `errors`, `incompatible` and `invalid_defaults`.

An idle clip is compatible with a character when every track on a body bone that the SOMA rig maps (hips, spine,
limbs, fingers, head) resolves on that character's skeleton. Tracks on other bones, such as breasts or twist bones,
are optional and do nothing where the bone is missing, so a clip authored on the female reference also plays on the
male reference.

## Exporting Builds

Any export preset must add `*.json` to its non-resource include filter, or use an equivalent filter covering
all descriptors, reviewed sets and their required JSON companions. Do not include ignored authoring inputs.

[validator]: ../../../tools/animation-pipeline/README.md#animation-variant-validation
