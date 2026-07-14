---
id: CORE-008
title: Content Pack Resolution
---

# Content Pack Resolution

## Requirement

The platform must resolve current content identity and root paths, then start the game from either built-in fallback
content or an optional content pack with a safe start-scene fallback.

## Goal

Enable a public build that launches without any additional content while exposing a generic current-content context for
systems that need to load content-relative resources. Optional packs can declare a start scene through a manifest and
override the default. CORE remains decoupled from specific content domains while failing fast when an explicitly
requested pack cannot start.

## User Requirements

1. The game launches and plays standalone when no optional content directory is present, using the built-in fallback
   start scene.
2. A missing optional manifest launches with the built-in fallback start scene and is reported as a normal startup
   path, not as a Godot missing-resource error.
3. A configured manifest default content pack starts the game with that pack's start scene when no explicit pack is
   requested.
4. A player or operator can request a specific content pack via a launch argument and have it take priority over the
   default pack.
5. Explicitly requesting a pack that cannot provide its start scene reports a startup failure instead of silently
   falling back.
6. Integration-test runs always use the built-in fallback start scene regardless of any installed content or
   arguments, keeping test fixtures deterministic.
7. Game systems can identify the active content set and load resources relative to its root without knowing whether the
   content is built in or supplied by an optional pack.

## Technical Requirements

1. Optional content packs live under the fixed directory `res://content/`.
2. The built-in fallback content identity is the fixed id `default`, with content root `res://`.
3. A content pack is a subdirectory identified by `pack-id` at `res://content/<pack-id>/`. Its content identity is
   `<pack-id>` and its content root is `res://content/<pack-id>/`.
4. Content identity is mandatory for every resolved current-content context.
5. CORE must expose a generic current-content context API containing at least the current content id and root path.
   Consumers can combine the root path with domain-specific relative paths without adding domain-specific roots to CORE.
6. A content pack's start scene is the fixed file `start.tscn` at `res://content/<pack-id>/start.tscn`.
7. A manifest resource `res://content/manifest.tres` of Godot `Resource` type `ContentManifest` exposes
   `DefaultPackId` (string). A missing manifest means no default pack is declared.
8. The resolver must check that `res://content/manifest.tres` exists before loading it, so a missing content directory
   or manifest is handled without Godot missing-resource errors. It may log a normal informational message.
9. A core service `ContentResolver` provides `string ResolveStartScenePath(string fallbackStartScenePath)` that
   returns, in priority order:
    1. The start scene of the pack requested through the `--content-pack <id>` or `--content-pack=<id>` game
       executable argument, if present.
    2. The start scene of the manifest default pack (`DefaultPackId`), if declared and its `start.tscn` exists.
    3. The supplied `fallbackStartScenePath`.
    - When `AlleyCat.Testing.RuntimeContext.IsIntegrationTest()` is true, steps 1 and 2 are skipped and the fallback
      is returned.
10. The current-content context must resolve to the same selected identity and root as start-scene resolution, except
    integration-test runs resolve to `default` at `res://`.
11. If an explicitly requested pack is missing `start.tscn`, resolution must throw an `InvalidOperationException`. If
    the manifest default pack is missing `start.tscn`, resolution must return the supplied fallback.
12. The `Game` node consumes `ContentResolver`, passing its own `FallbackStartScene` export as the fallback value. The
    resolver must not hardcode the fallback path.
13. Argument parsing must accept both `--content-pack <id>` and `--content-pack=<id>` forms.
14. CORE APIs must not expose lore-specific roots such as `lore`; lore and other consumers derive their own relative
    paths from the generic content root.

## In Scope

- Content identity and root path conventions for `default` and optional packs.
- Generic current-content context API with no lore-specific paths.
- Optional content root location and pack directory layout (`res://content/<pack-id>/start.tscn`).
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
   start scene (User Requirement 1, Technical Requirements 8 and 12).
2. A public build with no `res://content/manifest.tres` logs a normal startup message, does not emit a Godot
   missing-resource error, and launches the fallback start scene (User Requirement 2, Technical Requirement 8).
3. With a manifest declaring `DefaultPackId` and no `--content-pack` argument, the resolver returns the manifest default
   pack's `start.tscn` when it exists (User Requirement 3, Technical Requirement 9).
4. A pack requested via the `--content-pack` argument overrides the default pack when its `start.tscn` exists
   (User Requirement 4, Technical Requirement 9).
5. The current-content context reports `default` with root `res://` when fallback content is active, and `<pack-id>`
   with root `res://content/<pack-id>/` when an optional pack is active (User Requirement 7, Technical Requirements 2,
   3, 4, 5, and 10).
6. Non-lore consumers can load content-relative resources by combining the current root with their own relative paths,
   without depending on lore-specific CORE API fields (User Requirement 7, Technical Requirements 5 and 14).
7. A requested pack missing `start.tscn` reports a startup error instead of returning the fallback
   (User Requirement 5, Technical Requirement 11).
8. A manifest default pack missing `start.tscn` returns the supplied fallback (Technical Requirement 11).
9. When `AlleyCat.Testing.RuntimeContext.IsIntegrationTest()` is true, resolution returns the fallback and current
   content is `default` regardless of installed content or arguments (User Requirement 6, Technical Requirements 9
   and 10).
10. Both `--content-pack <id>` and `--content-pack=<id>` are accepted and produce the equivalent requested pack
    (Technical Requirement 13).

## References

### Implementation

- @game/src/Core/Content/ContentResolver.cs
- @game/src/Core/Content/ContentManifest.cs
- @game/src/Core/Content/ContentPaths.cs
- @game/src/Game.cs

### Related Specs

- [CORE-001: Global Singleton](../../core/001-global-scene/index.md)
