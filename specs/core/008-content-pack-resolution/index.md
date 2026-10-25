---
id: CORE-008
title: Content Pack Resolution
---

# Content Pack Resolution

## Requirement

The platform must resolve current content identity, root paths, and the selected content's spoken language, then start
the game from either built-in fallback content or an optional content pack with a safe start-scene fallback.

## Goal

Enable a public build that launches without any additional content while exposing a generic current-content context for
systems that need to load content-relative resources. Pack selection — an explicit launch request or the selection
manifest default — chooses which optional pack to start. Separately, a pack's per-pack manifest may declare that pack's
start scene, overriding the conventional `start.tscn` file, and may declare exactly one spoken language, so the game
starts the author's chosen entry scene and gameplay speech automatically follows the pack selected at startup while
English remains the default. CORE remains decoupled from specific content domains while failing fast when an explicitly
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
8. Gameplay that reads the current content context every frame or per percept stays responsive without repeated
   resolution work, and opted-in debug logging reports content resolution once rather than on every read.
9. A player or operator can bypass any configured or requested content pack via a launch switch and start with the
   built-in default content. When no content pack is configured or requested, behaviour is unchanged.
10. When the selected content declares a spoken language, gameplay speech recognition and NPC speech synthesis follow
    it automatically, without player, operator, or scene-editor configuration; built-in fallback content speaks
    English.
11. A selected pack whose manifest is malformed or declares an unsupported spoken language reports a clear, actionable
    startup failure instead of silently running in another spoken language.
12. After a one-time multilingual backend setup, selecting an English or Korean content pack across game launches
    requires no Docker configuration changes, container restart, or container recreation.
13. A pack author can declare the pack's start scene in the per-pack manifest instead of shipping a scene at the fixed
    `start.tscn` path, and the game starts the declared scene whenever that pack is selected — whether requested
    through a launch argument or loaded as the manifest default.
14. A declared start scene takes priority over a conventional `start.tscn` in the same pack, and packs that declare no
    start scene keep starting from their conventional `start.tscn`, so existing packs are unaffected.

## Technical Requirements

1. Optional content packs live under the fixed directory `res://content/`.
2. The built-in fallback content identity is the fixed id `default`, with content root `res://`.
3. A content pack is a subdirectory identified by `pack-id` at `res://content/<pack-id>/`. Its content identity is
   `<pack-id>` and its content root is `res://content/<pack-id>/`.
4. Content identity is mandatory for every resolved current-content context.
5. CORE must expose a generic current-content context API containing at least the current content id, root path, and
   effective spoken language. Consumers can combine the root path with domain-specific relative paths without adding
   domain-specific roots to CORE.
6. A content pack's conventional start scene is the fixed file `start.tscn` at
   `res://content/<pack-id>/start.tscn`. A pack may override the convention through its per-pack manifest (Technical
   Requirements 25 to 27).
7. A manifest resource `res://content/manifest.tres` of Godot `Resource` type `ContentManifest` exposes
   `DefaultPackId` (string). A missing manifest means no default pack is declared.
8. The resolver must check that `res://content/manifest.tres` exists before loading it, so a missing content directory
   or manifest is handled without Godot missing-resource errors. It may log a normal informational message.
9. A core service `ContentResolver` provides `string ResolveStartScenePath(string fallbackStartScenePath)` that
   returns, in priority order:
    1. The start scene of the pack requested through the `--content-pack <id>` or `--content-pack=<id>` game
       executable argument, if present.
    2. The start scene of the manifest default pack (`DefaultPackId`), if declared and that scene exists.
    3. The supplied `fallbackStartScenePath`.
    - When `AlleyCat.Testing.RuntimeContext.IsIntegrationTest()` is true, steps 1 and 2 are skipped and the fallback
      is returned.
10. The current-content context must resolve to the same selected identity and root as start-scene resolution, except
    integration-test runs resolve to `default` at `res://`.
11. If the start scene of an explicitly requested pack — declared or conventional — is missing, resolution must throw
    an `InvalidOperationException`. If the manifest default pack's start scene is missing, resolution must return the
    supplied fallback.
12. The `Game` node consumes `ContentResolver`, passing its own `FallbackStartScene` export as the fallback value. The
    resolver must not hardcode the fallback path.
13. Argument parsing must accept both `--content-pack <id>` and `--content-pack=<id>` forms.
14. CORE APIs must not expose lore-specific roots such as `lore`; lore and other consumers derive their own relative
    paths from the generic content root.
15. `GetCurrentContentContext` must resolve the current-content context once per resolver instance and cache it behind
    lock-guarded lazy initialisation, because its inputs are fixed for the process lifetime: the command-line pack
    request, the skip switch, the manifest default pack read at construction, and the integration-test flag set
    before the Godot process spawns. The DI singleton makes the cache process-wide during gameplay. A resolution
    failure must throw without caching so the next call retries, and the integration-test bypass is unchanged. The
    resolution and start-scene debug logs must be guarded with `IsEnabled(LogLevel.Debug)` per CORE-007, keeping
    their templated messages and rendering the `contentPackSkipped` field alongside the existing fields.
16. The skip switch literal is `--no-content-pack`, defined as the constant `ContentPaths.SkipContentPackSwitch`. It
    must be passed as a user argument after Godot's `--` separator (for example
    `godot-mono --path game -- --no-content-pack`) and resolved from `OS.GetCmdlineUserArgs()`, matching how the
    game's other custom user-arg switches (`--no-ai`, `--skip-splash`) are resolved. The switch must not activate
    when passed before the `--` separator.
17. When the skip switch is present, both start-scene resolution and current-content resolution must skip the
    requested pack and the manifest default pack (steps 1 and 2 of the requirement 9 priority order), mirroring the
    integration-test bypass: start-scene resolution returns the supplied fallback and current-content resolution
    returns `ContentContext.Default` (`default` at `res://`), and no pack start scene is probed. The switch takes
    precedence over an explicit `--content-pack` request and the manifest default pack without reporting a conflict.
    With no pack requested or configured, resolution is unchanged.
18. When the skip switch is active, resolution must emit one Information-level log notice that configured content
    packs are skipped, so an operator can determine from the logs why a configured pack did not load.
19. The selection manifest (`ContentManifest` at `res://content/manifest.tres`) remains the only pack-selection
    contract and carries no language or start-scene metadata. Each content pack may additionally declare metadata
    through a distinct optional per-pack manifest resource `ContentPackManifest` (Godot `Resource`) at
    `res://content/<pack-id>/manifest.tres`, exposing an optional `Language` string export that defaults to `en`
    (Technical Requirements 20 to 24) and an optional `StartScene` string export that defaults to empty (Technical
    Requirements 25 to 29).
20. Language declaration is optional and backward-compatible. The resolver must check that the per-pack manifest path
    exists before loading it: a missing per-pack manifest, a missing `res://content/` directory, or an omitted or
    blank `Language` value resolves to `en` as a normal startup path without Godot missing-resource errors or
    warnings.
21. Declared language values are normalised before use: outer whitespace is trimmed, matching is case-insensitive,
    and a tag maps to the spoken-language code of its primary sublanguage (for example `en-GB` resolves to `en`).
    The supported set is evidence-gated against the deployed speech backends and is initially `en` and `ko`; the
    context stores the normalised lowercase code. Unsupported primary sublanguages are rejected per Technical
    Requirement 23 rather than being silently remapped.
22. The effective language belongs to the selected content and follows the Technical Requirement 9 selection order:
    the requested pack, otherwise the manifest default pack, otherwise built-in fallback content. Built-in fallback
    content, the integration-test bypass, and `--no-content-pack` all resolve `en`. The language resolves once
    together with the context and is cached under the same process-lifetime lazy initialisation as Technical
    Requirement 15.
23. A per-pack manifest that exists but cannot be loaded as `ContentPackManifest`, or a declared language whose
    primary sublanguage is unsupported, must fail content resolution with an actionable exception naming the pack id,
    the manifest path, the declared value, and the supported set. The failure reuses the Technical Requirement 15
    failure semantics: it throws without caching so the next call retries, and the game reports a startup failure
    instead of silently speaking another language. The same unloadable-manifest failure also fails start-scene
    resolution, including for a default pack candidate (Technical Requirement 28).
24. Language metadata stays infrastructure-independent: packs never declare speech models, endpoints, or credentials,
    and CORE performs no mapping from language to infrastructure beyond the Technical Requirement 21 normalisation.
25. The per-pack manifest exposes an optional start-scene declaration through an
    `[Export(PropertyHint.File, "*.tscn")] string StartScene` property that defaults to empty. Start-scene
    declaration lives only on the per-pack manifest; the selection manifest contract is unchanged (Technical
    Requirement 7).
26. A declared `StartScene` value is trimmed before use. A blank value resolves to the conventional `start.tscn`
    (Technical Requirement 6), so packs that declare nothing are unaffected. A value beginning with `res://` is used
    as-is. Any other value is pack-relative and resolves against the pack root: `scenes/intro.tscn` resolves to
    `res://content/<pack-id>/scenes/intro.tscn`.
27. An explicitly declared start scene takes priority over a conventional `start.tscn` in the same pack, even when
    both exist. The resolver must read the per-pack manifest — existence check, then load — before probing a
    candidate pack's start scene, for both the requested and the default pack candidate, because the manifest
    determines the probe path. This read does not validate `Language`; language validation remains part of
    current-content resolution for the selected pack (Technical Requirements 21 to 23).
28. A declared start scene that resolves to a missing file behaves exactly like a missing conventional `start.tscn`
    (Technical Requirement 11): a requested pack throws `InvalidOperationException`; a default pack candidate returns
    the supplied fallback. Because the manifest read precedes the probe (Technical Requirement 27), a per-pack
    manifest that exists but cannot be loaded as `ContentPackManifest` throws for a default pack candidate as well,
    regardless of whether that pack's start scene exists. The integration-test bypass and the `--no-content-pack`
    skip read no per-pack manifest and probe no start scene (Technical Requirements 9 and 17).
29. A start-scene declaration changes only which scene path is probed and started. `ContentContext` identity, root,
    and language semantics are unchanged, and start-scene resolution and current-content resolution continue to
    agree on the selected pack (Technical Requirement 10).

## In Scope

- Content identity and root path conventions for `default` and optional packs.
- Generic current-content context API with no lore-specific paths.
- Optional content root location and pack directory layout (`res://content/<pack-id>/start.tscn` convention).
- `ContentManifest` resource contract with `DefaultPackId`.
- Optional per-pack `ContentPackManifest` resource with spoken-language metadata: existence-checked loading,
  normalisation, supported-set validation, and selected-content language exposure.
- Per-pack start-scene declaration through `ContentPackManifest.StartScene`: accepted path forms, priority over the
  conventional `start.tscn`, manifest-read-before-probe ordering, and failure semantics.
- `ContentResolver` resolution algorithm and its integration-test bypass via `RuntimeContext`.
- Missing manifest handling before Godot resource loading.
- Command-line argument handling for `--content-pack`.
- Skip-switch contract (`--no-content-pack` as a user argument after Godot's `--` separator), bypassing requested and
  manifest-default packs.
- `Game` node wiring of the resolver against its `FallbackStartScene` export.
- Once-per-instance current-content resolution caching and level-guarded resolution debug logging.

## Out Of Scope

- Authoring tooling, packaging, or distribution of content packs.
- General validation, signing, or versioning of content pack manifests beyond the spoken-language checks above.
- Content-specific gameplay behaviour inside any start scene.
- User-facing UI for selecting a content pack or language at runtime.
- In-process pack or language switching; language metadata applies to the pack selected at startup.
- Automatic translation of content into the declared language.

## Acceptance Criteria

1. A public build with no `res://content/` directory launches using the `Game` node `FallbackStartScene` fallback
   start scene (User Requirement 1, Technical Requirements 8 and 12).
2. A public build with no `res://content/manifest.tres` logs a normal startup message, does not emit a Godot
   missing-resource error, and launches the fallback start scene (User Requirement 2, Technical Requirement 8).
3. With a manifest declaring `DefaultPackId` and no `--content-pack` argument, the resolver returns the manifest default
   pack's start scene when it exists (User Requirement 3, Technical Requirement 9).
4. A pack requested via the `--content-pack` argument overrides the default pack when its start scene exists
   (User Requirement 4, Technical Requirement 9).
5. The current-content context reports `default` with root `res://` when fallback content is active, and `<pack-id>`
   with root `res://content/<pack-id>/` when an optional pack is active (User Requirement 7, Technical Requirements 2,
   3, 4, 5, and 10).
6. Non-lore consumers can load content-relative resources by combining the current root with their own relative paths,
   without depending on lore-specific CORE API fields (User Requirement 7, Technical Requirements 5 and 14).
7. A requested pack missing its start scene, declared or conventional, reports a startup error instead of returning
   the fallback (User Requirement 5, Technical Requirement 11).
8. A manifest default pack missing its start scene, declared or conventional, returns the supplied fallback
   (Technical Requirement 11).
9. When `AlleyCat.Testing.RuntimeContext.IsIntegrationTest()` is true, resolution returns the fallback and current
   content is `default` regardless of installed content or arguments (User Requirement 6, Technical Requirements 9
   and 10).
10. Both `--content-pack <id>` and `--content-pack=<id>` are accepted and produce the equivalent requested pack
    (Technical Requirement 13).
11. Tests verify the current-content context resolves once per resolver instance and is reused on later calls, that a
    resolution failure is not cached and is retried on the next call, and that resolution debug logging is skipped when
    Debug is filtered (User Requirement 8, Technical Requirement 15).
12. When `--no-content-pack` is passed as a user argument after the `--` separator and a manifest default pack is
    configured, start-scene resolution returns the supplied fallback and the current-content context resolves to
    `default` at `res://`, and no pack start scene is probed (User Requirement 9, Technical Requirements 16
    and 17).
13. When `--no-content-pack` is combined with an explicit `--content-pack` request, the skip switch takes precedence:
    resolution returns the fallback start scene and the `default` content context without reporting a conflict
    (User Requirement 9, Technical Requirement 17).
14. When no content pack is requested or configured, `--no-content-pack` leaves resolution results unchanged (User
    Requirement 9, Technical Requirement 17).
15. The skip switch does not activate when passed before the `--` separator (Technical Requirement 16).
16. Resolution emits one Information-level notice when the skip switch is active, and the Debug resolution templates
    render the `contentPackSkipped` field (Technical Requirements 15 and 18).
17. The current-content context exposes the effective spoken language alongside identity and root: a pack whose
    manifest declares `en` resolves `en`, a pack declaring `ko` resolves `ko`, and an explicit `--content-pack`
    request overrides the manifest default pack with the language following the requested pack (User Requirement 10,
    Technical Requirements 19, 21, and 22).
18. Packs without a per-pack manifest, manifests omitting `Language`, the built-in fallback content,
    integration-test runs, and `--no-content-pack` all resolve `en` without missing-resource errors (User
    Requirement 10, Technical Requirements 20 and 22).
19. A per-pack manifest that exists but cannot be loaded reports a startup failure naming the pack and manifest path
    instead of silently resolving `en` (User Requirement 11, Technical Requirement 23).
20. A declared language outside the supported set reports a startup failure naming the pack, the declared value, and
    the supported set (User Requirement 11, Technical Requirements 21 and 23).
21. Case and regional variants of a supported language (for example `en-GB`) normalise to the supported code
    (Technical Requirement 21).
22. Pack selection and its language require no configuration beyond the selection manifest, launch arguments, and
    per-pack manifests; packs carry no speech-model, endpoint, or credential metadata (User Requirement 12,
    Technical Requirements 19 and 24; end-to-end runtime evidence is defined in SPCH-003).
23. A pack whose per-pack manifest declares `StartScene` starts the declared scene whenever it is selected — through
    `--content-pack` or as the manifest default — with no conventional `start.tscn` present (User Requirement 13,
    Technical Requirements 25, 26, and 27).
24. A declared start scene takes priority over a conventional `start.tscn` in the same pack, and a blank or omitted
    `StartScene` keeps resolving the conventional `start.tscn`, so existing packs are unaffected (User Requirement 14,
    Technical Requirements 26 and 27).
25. Each accepted path form resolves as specified: a pack-relative value resolves against the pack root
    (`scenes/intro.tscn` → `res://content/<pack-id>/scenes/intro.tscn`) and a `res://`-prefixed value is used as-is
    (User Requirement 13, Technical Requirement 26).
26. A declared start scene that resolves to a missing file produces the same outcome as a missing conventional
    `start.tscn`: a requested pack throws `InvalidOperationException` and a manifest default pack returns the
    supplied fallback (User Requirement 5, Technical Requirements 11 and 28).
27. A default pack candidate whose per-pack manifest exists but cannot be loaded as `ContentPackManifest` fails
    resolution even when that pack's start scene is missing, because the manifest is read before the probe
    (Technical Requirements 27 and 28).
28. Integration-test runs and `--no-content-pack` read no per-pack manifest and probe no start scene, regardless of
    any declared `StartScene` (User Requirements 6 and 9, Technical Requirement 28).
29. With a start-scene override active, the current-content context reports the same id, root, and language as
    without the override, and start-scene and current-content resolution agree on the selected pack (User
    Requirement 7, Technical Requirements 10 and 29).

## References

### Implementation

- @game/src/Core/Content/ContentResolver.cs
- @game/src/Core/Content/ContentManifest.cs
- @game/src/Core/Content/ContentPackManifest.cs
- @game/src/Core/Content/ContentContext.cs
- @game/src/Core/Content/ContentPaths.cs
- @game/src/Game.cs

### Related Specs

- [CORE-001: Global Singleton](../../core/001-global-scene/index.md)
- [SPCH-003: Transcriber Component](../../speech/003-transcription/index.md)
- [SPCH-007: Supertonic Speech Generator Component](../../speech/007-supertonic-speech-generator/index.md)
