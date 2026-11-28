# Animation Packs

This guide is for anyone adding idle stances or gaits to AlleyCat characters, including external content-pack
authors. You need a text editor, Blender and a checkout of the game; you do not need to change code or open the Godot
editor. The normative contract is [Animation Pack Format][pack-format], with clip production defined in
[Animation Clip Authoring][authoring].
Where this guide and those pages differ, the specifications win.

[pack-format]: ../../../specs/animation/003-animation-catalogue/pack-format.md
[authoring]: ../../../specs/animation/003-animation-catalogue/authoring.md

## What a Pack Is

An animation pack is one `pack.json` file listing entries. Each entry is either an **idle** (a stance, played as a
native Godot `Animation` clip on the reference skeleton) or a **locomotion** gait (an ANIM-002 reviewed set file).
Characters combine packs through their templates, and NPCs pick entries by id and tags, while other characters only
ever see the entry's description.

Default content ships one pack, [`default`](packs/default/pack.json). Its idle clips and
[reviewed feminine set](packs/default/locomotion/feminine/README.md) live under `packs/default/`.
Shared MotionBricks calibration and source-rest scenes live under `motionbricks/`.
Standalone reference animations under `reference/female/` are not catalogue entries. Blender sources and review
material live under `source/`, which Godot ignores; AnimationTree templates stay with the character templates.

## Manifest Fields

A manifest is a UTF-8 JSON object with exactly these top-level fields:

| Field | Meaning |
| --- | --- |
| `schema` | Always `anim003_pack_v1`. |
| `id` | Lowercase pack id (`a-z`, digits, `_`, starting with a letter), unique among a character's packs. |
| `entries` | An ordered array of entries. Order matters: the first compatible entry of a category is the fallback. |

Every entry has these fields:

| Field | Meaning |
| --- | --- |
| `id` | A lowercase identifier, unique per category within the pack. |
| `category` | `idle` or `locomotion`. |
| `tags` | A non-empty array of short lowercase words (`a-z`, digits, `_`, `-`) that NPCs use to choose. |
| `description` | A short visible account of the motion, as an observer would describe it. |
| `clip` | Idle only: the path of the native `Animation` clip (`.tres`). |
| `set` | Locomotion only: the path of an ANIM-002 reviewed set file. |

Unknown fields, a missing or blank required field, an empty `tags` array, an unknown `category` and a repeated entry
id within one category are all errors. A future format change uses a new `schema` value.

This is a complete idle-only pack, equivalent to the repository's content-pack test fixture:

```json
{
  "schema": "anim003_pack_v1",
  "id": "fixture",
  "entries": [
    {
      "id": "slump",
      "category": "idle",
      "tags": ["tired", "slumped"],
      "description": "stands slumped, weight sagging onto one hip",
      "clip": "idle/slump.tres"
    }
  ]
}
```

A locomotion entry looks like this (from the `default` pack):

```json
{
  "id": "feminine",
  "category": "locomotion",
  "tags": ["relaxed", "feminine"],
  "description": "walks with an easy, relaxed feminine gait",
  "set": "assets/animations/packs/default/locomotion/feminine/set.json"
}
```

## Path Resolution

`clip` and `set` paths are relative to the pack's **content root**. They are never `res://` paths or absolute paths,
and they may not contain `..`.

- For an external content pack rooted at `res://content/my_pack/`, a manifest at
  `res://content/my_pack/assets/animations/packs/my_pack/pack.json` resolves
  `"clip": "assets/animations/packs/my_pack/idle/slump.tres"` to
  `res://content/my_pack/assets/animations/packs/my_pack/idle/slump.tres`.
- A manifest anywhere else belongs to default content, whose root is `res://`. The `default` pack's
  `"clip": "assets/animations/packs/default/idle/sad.tres"` loads `res://assets/animations/packs/default/idle/sad.tres`.

For new external packs, use `assets/animations/packs/<pack-id>/pack.json` relative to the external content pack's
own root, not the base game's assets tree. Keep its clips nearby (for example in `idle/`). The manifest path on
the pack node is authoritative; existing layouts remain valid and need not migrate.

A content pack cannot reach outside its own root. If it adds a gait, it must ship that reviewed locomotion set (the
set file and the source files it references) inside its own content root, and point `set` at that copy.

## Pack Nodes, Enabled and Priority

A character template has one `AnimationCatalogue` node with one `AnimationPack` child node per pack. Each pack node
has three properties:

- `Manifest`: the `res://` path of the `pack.json`.
- `Enabled`: whether the pack contributes. A derived template disables an inherited pack by overriding this to
  `false`, instead of deleting the node.
- `Priority`: an integer. Packs apply in ascending priority; packs with equal priority apply in ordinal pack-id
  order. Scene-tree order does not matter. By convention, base templates use lower priorities than the templates
  derived from them.

When two applied packs contain an entry with the same category and id, the later-applied pack's entry replaces the
earlier one, takes the later pack's place in the fallback order, and the game logs one line naming both packs. Two
enabled pack nodes using the same pack `id` are an error and stop the character's catalogue from being built.

To extend `default`, write a pack with its own `id` and add a pack node with a higher `Priority` to your template. An
entry with the same id as a `default` entry (such as idle `sad`) overrides it; new ids add entries.

## Template Defaults

The `AnimationCatalogue` node exports `DefaultIdle` and `DefaultLocomotion`. A character uses, in order:

1. the NPC's own choice, once it has made one;
2. the template default;
3. the first compatible entry of that category in catalogue order (packs in application order, then manifest order).

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

Author the motion on the SOMA `Kimodo_Source` rig in a copy of the character's calibrated setup, and save the
animated source as `idle/<variant>.blend` under its animation source directory. In Blender, the meshed
reference rig in that setup follows `Kimodo_Source`, so what you see there is what the clip will contain. Export it:

```sh
python3 tools/animation-pipeline/commands/export_idle_clip.py kimodo \
  --character-root game/assets/animations/source/reference/female \
  --source game/assets/animations/source/reference/female/idle/sad.blend \
  --action kimodo_m0a3dyf0 \
  --skeleton-scene res://assets/characters/reference/female/reference_female.blend \
  --check-skeleton-scene res://assets/characters/reference/male/reference_male.blend \
  --output game/assets/animations/packs/default/idle/sad.tres
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
  --output game/content/my_pack/assets/animations/packs/my_pack/idle/my_idle.tres
```

The control rig itself is never exported.

Both paths only write the clip; they never edit `pack.json`. Your source blends are never modified, and a failed
export leaves an existing clip untouched. Re-exporting over an existing clip keeps its resource id, so scenes and packs
that reference it stay valid. Then watch the clip on the character in the game, because Godot playback is the final
visual check.

## Validating a Pack

Validate packs with the headless validator before using them. It runs the same validation the game uses when it
loads a catalogue, never edits files, and names the pack, entry, field and path of every problem. Build the game once
first (`dotnet build AlleyCat.sln`), then run from the repository root:

```sh
godot-mono --headless --xr-mode off --path game \
  --script "$PWD/tools/animation-pipeline/godot/commands/validate_animation_pack.gd" -- \
  --pack=res://content/my_pack/assets/animations/packs/my_pack/pack.json \
  --template=res://assets/characters/templates/reference_female/reference_female_npc.tscn
```

- `--pack=` may repeat. Its paths resolve by the location rule above.
- `--content-root=<res:// directory>` resolves every `--pack` as a content pack rooted at that directory, for a pack
  you have not installed under `res://content/` yet.
- `--template=` may repeat. For each template the validator also reports which entries override which, duplicate
  pack ids, and whether each idle clip binds to that character's skeleton. Your pack applies at the priority of a
  template pack node that already references it, otherwise above all of the template's packs, or at `--priority=`.
- `--require-compatible` also fails when an entry cannot bind to a template's skeleton.

The command exits with 0 when the packs are valid, 1 when they are not, and 2 for bad arguments. It ends with an
`ANIMATION_PACK_VALIDATION_JSON=` summary line.

An idle clip is compatible with a character when every track on a body bone that the SOMA rig maps (hips, spine,
limbs, fingers, head) resolves on that character's skeleton. Tracks on other bones, such as breasts or twist bones,
are optional and do nothing where the bone is missing, so a clip authored on the female reference also plays on the
male reference.

## Exporting Builds

Pack manifests and reviewed sets are plain JSON, which Godot does not export by default. Any export preset must add
`*.json` to its non-resource include filter (or an equivalent filter covering every pack and set file), or the
catalogue will be missing from the build.
