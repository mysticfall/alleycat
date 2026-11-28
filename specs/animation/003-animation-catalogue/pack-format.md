---
title: Animation Pack Format
---

# Animation Pack Format

## Purpose

This page is the normative public authoring contract for [ANIM-003](index.md) animation packs. It is written for
external pack authors, implementers of the catalogue loader and validator, and reviewers. Runtime layering, views and
transitions are defined on the [ANIM-003 index](index.md).

## Pack Manifest

1. A pack is one UTF-8 JSON file, `pack.json`, authorable without the Godot editor. JSON is the only metadata format.
2. Top-level fields:
   - `schema`: the format version, `anim003_pack_v1`.
   - `id`: the pack id, a lowercase identifier unique within a character's catalogue.
   - `entries`: an ordered array of entries. Manifest order is used by the catalogue fallback.
3. Entry fields shared by all categories:
   - `id`: a lowercase identifier, unique per category within the pack.
   - `category`: either `idle` or `locomotion`.
   - `tags`: a non-empty array of short lowercase words for agents. The prompt lists tags, but observers never see
     them.
   - `description`: a short external, visible account of the motion, written as an observer would describe it. Do
     not use first-person wording, ids or tags. Observers receive only this text.
4. Category payloads:
   - `idle` requires `clip`, the path of a native Godot `Animation` resource on the reference skeleton.
   - `locomotion` requires `set`, the path of a reviewed set file in the unchanged ANIM-002 format.
5. Unknown top-level or entry fields are validation errors. A schema change requires a new `schema` version.

```json
{
  "schema": "anim003_pack_v1",
  "id": "default",
  "entries": [
    {
      "id": "sad",
      "category": "idle",
      "tags": ["sad", "slumped"],
      "description": "stands with slumped shoulders and a lowered gaze",
      "clip": "assets/animations/packs/default/idle/sad.tres"
    },
    {
      "id": "feminine",
      "category": "locomotion",
      "tags": ["relaxed", "feminine"],
      "description": "walks with an easy, relaxed feminine gait",
      "set": "assets/animations/packs/default/locomotion/feminine/set.json"
    }
  ]
}
```

The example shows the shape, not final authored wording.

## Path Resolution

1. `clip` and `set` are relative paths under the owning content root, and never `res://` or absolute paths. Paths
   that escape the root (`..`) are errors.
2. The pack loader takes a [CORE-008](../../core/008-content-pack-resolution/index.md) `ContentContext` and combines
   that context's root with each relative path. It does not hardcode roots.
   - **Production:** the context comes from the location of `pack.json`. Under `res://content/<id>/` it is that
     content pack's context. Anywhere else it is `ContentContext.Default` (`default` at `res://`).
   - **Tests:** tests may inject a context, so a fixture can stand in for a content pack without being installed
     under `res://content/`.
3. Default content places packs at `assets/animations/packs/<pack-id>/pack.json` under `res://`. For a new external
   content pack, the same path relative to that pack's **own content root** is recommended, not a path in the base
   game's assets tree. The pack node's explicit manifest path is authoritative; existing layouts need not migrate.

## Validator

1. One validation implementation serves both catalogue loading and a manually invoked headless validator command. The
   command is owned under `tools/` per [Developer Tool Layout](../../tooling/index.md) and documented in its README.
2. It rejects:
   - malformed JSON, the wrong `schema` or unknown fields;
   - missing or blank required fields, an empty `tags` array or a bad `category`;
   - duplicate entry ids within a category;
   - paths that are absolute, escape the root or point to missing files;
   - a `clip` that is not a loadable `Animation`;
   - a `set` that the existing ANIM-002 set loader does not accept.
3. Given a character template, it also reports skeleton-binding compatibility per entry ([ANIM-003](index.md) TR11),
   layering collisions with the overriding pack (equal priorities ordered by pack id), and duplicate pack ids.
4. Diagnostics name the pack, entry, field and path, and never modify authored files.

## Fixture Pack

Integration tests run inside the `game/` project. Test packs therefore live with the other `res://`-loadable test
fixtures, under `game/tests/animation/catalogue/` (loaded as `res://tests/animation/catalogue/`). They are never placed
under `game/content/` and are never shipped in or loaded by the default pack. The contract is proven in two parts.

1. **Content-pack fixture:** `game/tests/animation/catalogue/fixture/` contains a fixture pack with idle entries only.
   Its `Animation` resources live inside the fixture directory. Tests inject a content-pack context rooted at that
   directory. This proves content-pack-shaped resolution: every `clip` path is relative to the injected root and
   loads from there. Schema and validator cases that need no locomotion entry also use this context. The fixture
   provides at least one idle entry that differs from the default pack's, currently `slump`, so that
   [ANIM-003](index.md) TR27 can prove idle→idle.
2. **Default-context extension:** locomotion entry resolution, and a pack that extends `default`, are proven under
   `ContentContext.Default`, with paths relative to `res://`.
   - The `default` pack's `feminine` entry resolves `assets/animations/packs/default/locomotion/feminine/set.json`.
   - An extension test pack at `game/tests/animation/catalogue/extension/pack.json` gets `ContentContext.Default`
     through the production location rule. Applied at a higher priority than `default`, it overrides a `default`
     entry id, and adds a new locomotion id whose `set` is a `res://`-relative reviewed-set path.
   - Validator cases for a `set` path use this context.
3. Tests load every test pack through the same loader and validator as default content. They cover a valid pass and
   one failing case for each rejection class in Validator item 2.
4. In a real content pack, locomotion entries reference reviewed sets inside that pack's own content root. Path
   Resolution item 1 forbids reaching outside it.

## Author Documentation

1. The [author-facing guide][guide] lives at `game/assets/animations/README.md`. It covers:
   - the manifest fields and an example;
   - path resolution and the recommended layout under each external content pack's own root;
   - pack nodes, `Enabled`, `Priority` and overrides;
   - template defaults;
   - description and tag writing guidance;
   - the authoring paths in [Animation Clip Authoring](authoring.md);
   - the validator command;
   - the `*.json` export include filter;
   - that a content pack must ship its own reviewed locomotion sets inside its content root.
2. The guide states that idles should start on a settled pose and loop cleanly. It links back to this page as the
   normative contract.

## Acceptance Criteria

1. **ANIM-003-PACK-AC-001:** The default pack, the content-pack fixture and the extension test pack pass validation.
   Every rejection class fails with a diagnostic naming the pack, entry, field and path.
2. **ANIM-003-PACK-AC-002:** In production, the loader derives `ContentContext.Default` for manifests outside
   `res://content/` and a content pack's context for manifests under `res://content/<id>/`. Paths resolve against that
   context's root. The idle-only fixture resolves its `clip` paths, and loads its `Animation` resources, through a
   content-pack context injected at its directory. Under `ContentContext.Default`, the `default` pack's `feminine`
   entry and the extension pack's new locomotion entry resolve their sets, and the extension pack's override applies
   with its log message.
3. **ANIM-003-PACK-AC-003:** The author guide exists, covers every listed topic and links to this page. Using only the
   guide, an author can produce a valid fixture-equivalent pack without being required to use the recommended paths.

[guide]: ../../../game/assets/animations/README.md
