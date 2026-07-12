---
id: CORE-008
title: Content Pack Resolution
---

# Content Pack Resolution

## Requirement

The platform must resolve and start the game from an optional content directory `res://content/`, so the game runs
standalone or with an optional content pack, selecting the start scene by priority order with a safe fallback.

## Goal

Enable a public build that launches without any additional content, while allowing an optional content pack to declare
its start scene through a manifest and override the default. This keeps the core platform decoupled from any specific
content set while still failing fast when an explicitly requested pack cannot start.

## User Requirements

1. The game launches and plays standalone when no optional content directory is present, using the built-in fallback
   start scene.
2. A missing optional manifest launches with the built-in fallback start scene and is reported as a normal startup
   path, not as a Godot missing-resource error.
3. A configured default content pack starts the game with that pack's start scene when no explicit pack is requested.
4. A player or operator can request a specific content pack via a launch argument and have it take priority over the
   default pack.
5. Explicitly requesting a pack that cannot provide its start scene reports a startup failure instead of silently
   falling back.
6. Integration-test runs always use the built-in fallback start scene regardless of any installed content or
   arguments, keeping test fixtures deterministic.

## Technical Requirements

1. The content root is a fixed directory `res://content/`.
2. A content pack is a subdirectory identified by `pack-id` at `res://content/<pack-id>/`. Its start scene is the
   fixed file `start.tscn` at `res://content/<pack-id>/start.tscn`.
3. A manifest resource `res://content/manifest.tres` of Godot `Resource` type `ContentManifest` exposes
   `DefaultPackId` (string). A missing manifest means no default pack is declared.
4. The resolver must check that `res://content/manifest.tres` exists before loading it, so a missing content directory
   or manifest is handled without Godot missing-resource errors. It may log a normal informational message.
5. A core service `ContentResolver` provides `string ResolveStartScenePath(string fallbackStartScenePath)` that
   returns, in priority order:
    1. The start scene of the pack requested through the `--content-pack <id>` or `--content-pack=<id>` game
       executable argument, if present.
    2. The start scene of the default pack (`DefaultPackId`), if declared and its `start.tscn` exists.
    3. The supplied `fallbackStartScenePath`.
    - When `AlleyCat.Testing.RuntimeContext.IsIntegrationTest()` is true, steps 1 and 2 are skipped and the fallback
      is returned.
6. If an explicitly requested pack is missing `start.tscn`, resolution must throw an `InvalidOperationException`. If
   the default pack is missing `start.tscn`, resolution must return the supplied fallback.
7. The `Game` node consumes `ContentResolver`, passing its own `FallbackStartScene` export as the fallback value. The
   resolver must not hardcode the fallback path.
8. Argument parsing must accept both `--content-pack <id>` and `--content-pack=<id>` forms.

## In Scope

- Content root location and pack directory layout (`res://content/<pack-id>/start.tscn`).
- `ContentManifest` resource contract with `DefaultPackId`.
- `ContentResolver` resolution algorithm and its integration-test bypass via `RuntimeContext`.
- Missing manifest handling before Godot resource loading.
- Command-line argument handling for `--content-pack`.
- `Game` node wiring of the resolver against its `FallbackStartScene` export.

## Out Of Scope

- Authoring tooling, packaging, or distribution of content packs.
- Validation, signing, or versioning of content pack manifests.
- Content-specific gameplay behaviour inside any start scene.
- User-facing UI for selecting a content pack at runtime.

## Acceptance Criteria

1. A public build with no `res://content/` directory launches using the `Game` node `FallbackStartScene` fallback
   start scene (User Requirement 1, Technical Requirements 4 and 7).
2. A public build with no `res://content/manifest.tres` logs a normal startup message, does not emit a Godot
   missing-resource error, and launches the fallback start scene (User Requirement 2, Technical Requirement 4).
3. With a manifest declaring `DefaultPackId` and no `--content-pack` argument, the resolver returns the default pack's
   `start.tscn` when it exists (User Requirement 3, Technical Requirement 5).
4. A pack requested via the `--content-pack` argument overrides the default pack when its `start.tscn` exists
   (User Requirement 4, Technical Requirement 5).
5. A requested pack missing `start.tscn` reports a startup error instead of returning the fallback
   (User Requirement 5, Technical Requirement 6).
6. A default pack missing `start.tscn` returns the supplied fallback (Technical Requirement 6).
7. When `AlleyCat.Testing.RuntimeContext.IsIntegrationTest()` is true, resolution returns the fallback regardless of
   installed content or arguments (User Requirement 6, Technical Requirement 5).
8. Both `--content-pack <id>` and `--content-pack=<id>` are accepted and produce the equivalent requested pack
   (Technical Requirement 8).

## References

### Implementation

- @game/src/Core/Content/ContentResolver.cs
- @game/src/Core/Content/ContentManifest.cs
- @game/src/Core/Content/ContentPaths.cs
- @game/src/Game.cs

### Related Specs

- [CORE-001: Global Singleton](../../core/001-global-scene/index.md)
