---
title: Animation Variant Format
---

# Animation Variant Format

## Purpose

This page defines the normative [ANIM-003](index.md) authoring and validation contract for artists, tooling
implementers and reviewers. [Catalogue composition](index.md#character-catalogue-and-layering) defines selection.

## Variant Descriptor

1. Author one UTF-8 JSON object per selectable variant, beside its playable clip or reviewed set. There is no
   required descriptor filename. A directory or a descriptor's presence does not select it.
2. Each descriptor has these fields:
   - `schema`: `anim003_variant_v1`.
   - `category`: `idle` or `locomotion`; the directory does not determine the category.
   - `id`: a lowercase identifier matching `[a-z][a-z0-9_]*`; the catalogue key is the ordinal (`category`, `id`) pair.
   - `tags`: a non-empty array of short words for agents, each matching `[a-z][a-z0-9_-]*`.
   - `description`: a short visible account of the motion, in observer wording, without ids or tags.
3. An `idle` descriptor requires `clip`, a native Godot `Animation` resource on the reference skeleton.
   A `locomotion` descriptor requires `set`, an unchanged [ANIM-002](../002-locomotive-animation-set/index.md)
   reviewed set. Only the field appropriate to the category is allowed. Unknown or repeated fields are errors.
4. Authoring provenance is separate from runtime metadata. Descriptors and payloads remain outside ignored
   authoring and source directories.

## Path Resolution

1. An add or replace declaration in the installed template chain names a specific descriptor by its canonical `res://`
   resource path. The eligible template and descriptor owners are base content and the active optional
   [CORE-008](../../core/008-content-pack-resolution/index.md) content pack. Neither selection nor validation
   scans directories, searches other roots or infers a variant from a filename.
2. A descriptor outside `res://content/` belongs to base content (`res://`). A descriptor within
   `res://content/<id>/` belongs to that pack and is eligible only when that pack is active. This namespace is
   not part of base ownership even though it sits below `res://`. Preserve the descriptor's owner through
   template installation; do not rebase inherited base references into the active pack or the target scene.
3. `clip` and `set` are paths relative to the descriptor's owning content root, never relative to the caller.
   Reject absolute paths, resource URLs, non-canonical segments and any path whose resolved target is outside that
   owner. The playable clip or set must be in the descriptor's directory. Its required runtime payload files,
   including reviewed-set companions, stay within the same owner. Do not change the ANIM-002 set format or its
   internal path rules to enforce this boundary.

## Validator

1. Use the same validation for catalogue loading and the [headless validator][validator]. Validation never writes
   authored files. Standalone descriptor checks do not modify template selection.
2. Reject malformed JSON; wrong schema or category; missing, blank or invalid required values; empty tags;
   unknown or wrong-category fields; missing, invalid or out-of-owner references; an unloadable `Animation`;
   and sets rejected by ANIM-002. Validate resolved ownership, not just a path's text prefix.
3. Given an authored character template, validate the composed catalogue, including operation conflicts, default
   targets and skeleton compatibility without starting a character scene. Report invalid defaults and
   incompatibility as in ANIM-003 TR10–11. Those findings fail the command only under `--require-compatible`;
   malformed content and invalid operations always fail.
4. Diagnostics identify the affected owner and descriptor path, field or operation and category/id when available.
   For composition conflicts, identify both declarations where applicable.

## Installed Fixture

Before converting production defaults, prove one representative NPC using the real
[CHAR-002](../../character/002-character-root/index.md) template chain and
[CORE-005](../../core/005-scene-installer-system/index.md) installer. Use the existing CORE-008 active-content
rules, without changing its integration-test startup bypass.

1. Use test-only base-owned template declarations selecting the shipped `idle/sad` and `locomotion/feminine`
   descriptors. First verify that both are inherited on the installed character and bind their base payloads.
2. Install a derived template with declarations owned by an active fixture content pack. It adds a distinct
   fixture-owned idle and deliberately replaces an inherited idle using a fixture-owned descriptor with the same
   category/id. Verify both fixture payloads resolve inside that pack, the replacement is the sole entry at its
   key, and the inherited base gait is unchanged.
3. Keep the fixture out of shipped/default content. An injected directory context or standalone loader exercise
   can test validation failures, but does not satisfy this installed proof.

## Author Documentation

The [author guide][guide] covers descriptor fields and examples, explicit selection in template scene text,
layering, independent defaults, content ownership, metadata wording, [clip authoring](authoring.md), the validator
and JSON export inclusion. It explains that a pack author publishes each selected variant's payload under its owning
root and that idles start on a settled pose and loop cleanly. It links back to this normative format.

## Acceptance Criteria

1. **ANIM-003-VAR-AC-001:** Shipped and fixture descriptors pass the shared validator. Each rejection class in
   the validator fails with a diagnostic. Cover wrong owners, including inactive packs and a base path into
   `res://content/`, and missing, escaping or malformed payloads.
2. **ANIM-003-VAR-AC-002:** Installed fixture evidence identifies declaration, descriptor and payload ownership
   before and after explicit replacement and shows the inherited gait still binds the base reviewed set.
   Record this proof before editing production template selections.
3. **ANIM-003-VAR-AC-003:** Using the guide, an author can produce and validate a fixture-equivalent variant
   without code or Godot editor changes. The guide covers the topics above, and its examples use this contract.

[guide]: ../../../game/assets/animations/README.md
[validator]: ../../../tools/animation-pipeline/README.md#animation-variant-validation
