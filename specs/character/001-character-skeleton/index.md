---
id: CHAR-001
title: Character Skeleton Profile
---

# Character Skeleton Profile

## Requirement

All characters in AlleyCat use Godot's built-in `SkeletonProfileHumanoid` as their skeleton
profile, ensuring standardised humanoid bone naming and hierarchy across the project.

## Goal

Keep character rigs interoperable across IK, retargeting, animation, and tooling by standardising
on a single humanoid skeletal contract. Define the portable character contract that specifies
the required node structure, properties, and interfaces for game-compatible characters.
Character rig portability is achieved through the Scene Installer System (CORE-005). Role templates are the
inspector-readable source of generic player and NPC defaults and topology, while explicitly serialised configuration in
a higher-layer target character scene takes precedence for reused properties. Installers copy, rebase, and validate
authored template contents without requiring generators to know internal node placement.

## User Requirements

1. Character animation and pose behaviour should remain consistent across supported characters.
2. Features that depend on humanoid anatomy (IK, speech facial mapping) should work without
    per-character skeleton schema rewrites.
3. Game systems must be able to instantiate and interact with any character using only the
     defined portable character contract, without depending on specific node names or mesh
     references except for reference-only fixtures and tests.
4. Contributors can inspect role templates to understand player and NPC wiring before installation.
5. Blank imported character scenes can be assembled into working player or NPC scenes from the matching role template.
6. Template edits to authored player/NPC topology, including new, moved, or renamed nodes and changed node references,
   propagate to installed scenes without C# source changes when they stay within authored installer boundaries.
7. Player and NPC templates can replace scenes derived from them, including nested descendants under reused template
   subtrees.
8. Generated characters receive shared role/runtime capabilities without a visible duplicate reference character or
   duplicate skeleton from the imported base scene.
9. Generated and actual character scenes can select a character-specific body collider profile from the root role
   installer, without expanding or directly editing inherited child installer internals.
10. Character-specific values explicitly authored in an actual target scene survive automatic and repeated role
    installation, while values not explicitly overridden continue to refresh from the role template.
11. The mirror room uses the production Vadim character scene and retains Vadim's authored identity and voice settings.

## Technical Requirements

1. Character skeleton profiles must use Godot `SkeletonProfileHumanoid` naming and hierarchy.
2. The reference profile must remain aligned with `SkeletonProfileHumanoid` without incompatible
    overrides.
3. Downstream systems must treat the documented bone hierarchy as the canonical integration
    contract.
4. The portable character contract requires:
    - Visual/import root node with character metadata
    - Skeleton node following `SkeletonProfileHumanoid` hierarchy
    - AnimationPlayer node with standard animation tree structure
    - Attachment points for viewpoint, hand anchors, and other gameplay systems
    - Dynamic physical rig with collider profile matching character shape
    - Eye blend-shape pathways with per-character binding support
5. Character template assets are flattened under `game/assets/characters/templates/`; non-human template families are
   not part of the current contract.
6. Installer scenes live under `game/assets/characters/templates/installers/`.
7. Consolidated template-only reference female source scenes live under
   `game/assets/characters/templates/reference_female/`:
   - `reference_female_base.tscn` — complete common authored source setup for both roles.
   - `reference_female_npc.tscn` — complete working NPC template that extends the base template as appropriate.
   - `reference_female_player.tscn` — complete working player template that extends the base template as appropriate.
8. Actual runtime role scenes are separate from template-only sources:
   - `game/assets/characters/reference/player.tscn`
   - `game/assets/characters/reference/ally.tscn`
   - `game/assets/characters/reference/vadim.tscn`
9. Obsolete wrapper scenes under `game/assets/characters/reference/female/` are removed:
   - `reference_female.tscn`
   - `reference_female_npc.tscn`
10. Actual runtime scenes and template-only source scenes instance the source `.blend` directly where a visual/import
    root is needed, rather than deriving from the removed wrapper scenes.
11. Actual player and ally scenes derive from or instance the source `.blend` directly and include the matching role
    installer scene. They do not own `RigRoleTemplateSceneInstaller` directly as an extra wrapper.
12. `game/assets/characters/templates/installers/npc_installer.tscn` has the template-aware installer type as its root
    and references `templates/reference_female/reference_female_npc.tscn` through exported `PackedScene Template`.
13. `game/assets/characters/templates/installers/player_installer.tscn` has the template-aware installer type as its
     root and references `templates/reference_female/reference_female_player.tscn` through exported
     `PackedScene Template`.
14. When a role template extends or inherits from an imported base character scene, the role installer root configures
     exported `PackedScene TemplateBaseline` to that imported base scene.
15. `npc_installer.tscn` and `player_installer.tscn` set `TemplateBaseline` to
     `res://assets/characters/reference/female/reference_female.blend`.
16. Baseline-aware template subtree installers skip root or selected-node content that is equivalent to
     `TemplateBaseline`; they copy only nodes/components explicitly added by the role template above the imported base.
17. Baseline-aware installation prevents generated scenes from receiving copied reference-female base subtrees or
     duplicate skeletons while preserving role-authored additions.
18. The role installer scene root is `RigRoleTemplateSceneInstaller` from `AlleyCat.Rigging.Installation`,
      implements `ISceneInstaller.Install`, loads the configured template as a `PackedScene`, resolves target and
      template character skeletons once, creates template
      context, and delegates to child installers.
19. The role installer scene root does not copy or install the visual/import root or any other template node itself.
20. The role installer scene root does not own placement properties such as `InstallMode`, `SourcePath`, or
     `TargetParentPath`.
21. Role templates are authoritative exemplars for generic serialisable exported node references, component wiring, and
     reusable player/NPC topology, subject to explicitly local target-scene configuration precedence from CORE-005.
22. Child installers copy explicit template subtrees from `context.TemplateRoot` into the target actual scene, such as
      `game/assets/characters/reference/ally.tscn`, without requiring the actual scene root to be template-aware.
23. Child installers own source subtree path, target parent path or resolver, selected-node versus
      selected-node-children mode, binding, metadata, and idempotency.
24. Child installers in role installer scenes do not expose `PackedScene Template` or equivalent template/resource
       asset properties, including animation tree root overrides. They select and clone from the template provided by
       the role installer scene root.
25. Optional character-level physical rig configuration, including a root-exposed `BodyColliderProfile`, lives on the
      role installer scene root and flows to child installers through typed installation context.
26. The role installer may carry the character-level collider profile, but the dynamic physical rig installer remains
      responsible for applying it to the copied or installed physical rig.
27. Child installers must not require actual character scenes to expand inherited dynamic physical rig installer
      internals, reach into child installer nodes, or use a generic service bag to configure collider profiles.
28. Optional skeleton path configuration lives only on the role installer scene root; child installer scenes do not
       serialise skeleton source paths, `TargetSkeleton`, or equivalent per-child skeleton target configuration.
29. Child installers in role installer scenes do not expose or serialise per-child skeleton assignments; any child that
       needs target or template skeletons consumes them from the role installer root's install context.
30. `AlleyCat.Rigging.Installation` template subtree installers expose context-driven source/target placement only.
      They do not expose standalone `PackedScene` fallback properties or manual skeleton assignment properties.
31. Exported node references copied from role templates are rebased from the template root to the target scene root when
       they refer to corresponding imported model, skeleton, or target-subtree nodes.
32. Reused template subtrees are reconciled against the current template on reinstall: missing nested descendants are
      added and template-authored references are refreshed. Installer-owned output and sibling installer output are
      preserved.
33. Rebase and validation logic may recognise required target boundaries, but reusable wiring remains authored in the
       role templates rather than hard-coded in C#.
34. Legacy metadata bindings may remain for migration or escape-hatch cases, but role templates are the preferred
       primary wiring mechanism.
35. The mirror room instances the actual runtime player and Vadim scenes, not template-only sources.
36. Integration and visual verification scenes use specialised minimal fixtures where appropriate; production role or
        mirror-room scenes are used only when testing production assembly.
37. Base template content remains role-neutral; player-only VRIK, pose, and hip reconciliation stay out of base/NPC
        templates, and NPC `CharacterIK`/provider setup stays out of base/player templates.
38. The player template owns player animation-tree root selection by overriding the inherited `AnimationTree` resource;
        player subsystem installers bind that authored node/resource instead of loading hidden role resources from C#.
39. Animation tree templates live under `game/assets/characters/templates/animation/`.
40. `game/assets/characters/reference/female/animations/` remains source/reference animation-library data.
41. Male NPC template assets live under `game/assets/characters/templates/reference_male/`; the production Vadim scene
    is `game/assets/characters/reference/vadim.tscn` and selects the male template and runtime assets.
42. Character installation follows CORE-005 target-scene precedence: explicitly local exported properties and
    `Node3D.Transform` values on reused nodes, including direct character-root exported-property copies, win over role
    template defaults. Non-overridden values refresh, and new or missing nodes receive complete template state.
43. `game/assets/characters/reference/vadim.tscn` owns `Voice.Id = "Vadim"` and
    `SpeechGenerator.VoiceOverride = "Ian.wav"`. Shared templates are not made character-specific by this requirement
    and must not encode `Ian.wav` as the shared value for this fix.
44. `TemplateBaseline` remains a topology-selection input for inherited imported content; it is not unioned into the
    local target `SceneState` used to determine property overrides.

## In Scope

- Canonical humanoid skeleton-profile definition and hierarchy.
- Reference character profile resource alignment.
- Normative bone naming for dependent systems.
- Portable character contract specifying required node structure and interfaces.
- Constraints preventing hard-coded dependencies on specific character instances.
- Template-backed installer modules for character runtime topology that must remain visible and reviewable.
- Role scene topology and ownership for player/NPC reference character scenes.
- Flattened template layout under `game/assets/characters/templates/`.
- Complete template-only female and male reference source scenes and their installer-scene bindings.
- Removal of obsolete wrapper scenes under `game/assets/characters/reference/female/`.
- Separation between template-only character sources, role installer scenes, and actual runtime role scenes.
- Template-authoritative exported node references, rebase, and validation for copied character wiring.
- Baseline-aware role template installation for templates that inherit from imported base character scenes.
- Root role installer ownership of character-level physical rig configuration, including character-specific collider
  profile selection.
- Reconciliation for reused template subtrees, including nested descendant installation and reference refresh.
- Mirror-room and verification-scene consumption rules for actual scenes versus specialised fixtures.
- Target-scene configuration precedence for character-specific exported properties and transforms.
- Male template/runtime assets and the production Vadim scene used by the mirror room.

## Out Of Scope

- Per-feature IK solver internals and tuning values.
- Character mesh/weighting authoring workflows.
- Animation-style tuning and subjective motion polish.
- Specific character art assets or visual design.
- Gameplay logic that varies by character type or role.
- Non-human character template families before they are planned.
- Active base-scene inheritance from template-only sources in actual player or ally role scenes.
- Exact final rig-tuning values for authored template content.
- Replacing role-template authored references with metadata binding as the normal wiring path.
- Persistence of arbitrary runtime mutations across role installation.
- Persistent previous-template snapshots or full value-based three-way merging.

## Acceptance Criteria

1. User Requirements:
    - Character animation and pose behaviour remains consistent across characters.
    - Humanoid-dependent features (IK, speech) work without per-character skeleton changes.
    - Game systems can instantiate characters using only the portable contract.
    - No dependence on concrete `Female` node/mesh names except in reference fixtures/tests.
    - Contributors can inspect player and NPC role templates to understand authored reference wiring.
    - Blank imported character scenes can be assembled through the matching role installer and template.
    - Template edits within authored installer boundaries propagate to installed role scenes without C# source changes.
    - Nested descendants added under reused player/NPC template subtrees are installed automatically.
    - Generated characters receive role/runtime capabilities without a visible copied reference-female base character or
        duplicate skeleton.
    - Generated and actual character scenes can select character-specific collider profiles at the root role installer
        without expanding inherited child installer internals.
    - Explicitly authored character-scene values survive automatic and repeated role installation, while non-overridden
        values continue to refresh from the role template.
    - The mirror room uses the production Vadim scene with Vadim's locally authored identity and voice settings.

2. Technical Requirements:
    - Canonical hierarchy references `SkeletonProfileHumanoid` with documented bone structure.
    - Reference profile resource path is defined and documented.
    - Portable character contract specifies all required nodes and interfaces.
    - Technical contracts verified through resource alignment and interface validation.
    - Generated characters expose required properties via exported references or
        component binding, not by replacing inherited children.
    - Template assets use the flattened `game/assets/characters/templates/` layout, with installers under
        `templates/installers/` and template-only reference female role sources under `templates/reference_female/`.
    - Template-only role scenes are complete authored source scenes, not thin shells over the imported visual scene.
    - Obsolete wrapper scenes at `game/assets/characters/reference/female/reference_female.tscn` and
        `game/assets/characters/reference/female/reference_female_npc.tscn` are removed.
    - Template-only source scenes and actual runtime scenes instance the source `.blend` directly where a visual/import
        root is needed.
    - Actual runtime role scenes exist at `game/assets/characters/reference/player.tscn` and
        `game/assets/characters/reference/ally.tscn`, with the production male NPC at
        `game/assets/characters/reference/vadim.tscn`.
    - Actual player and ally scenes derive from or instance the source `.blend` directly and include the matching role
        installer scene.
    - Actual role scene roots do not own `RigRoleTemplateSceneInstaller` directly and do not use active base-scene
        inheritance from template-only role sources.
    - `npc_installer.tscn` and `player_installer.tscn` use the template-aware role coordinator as their root and
         reference the matching template-only role source through exported `PackedScene Template`.
    - Role installer roots configure `TemplateBaseline` whenever the configured template extends or inherits from an
        imported base character scene.
    - `npc_installer.tscn` and `player_installer.tscn` serialise `TemplateBaseline` as
        `res://assets/characters/reference/female/reference_female.blend`.
    - Baseline-aware child installers skip baseline-equivalent root or selected-node content and copy only role-authored
        nodes/components above the imported base.
    - Role installer scene roots implement `ISceneInstaller.Install`, load the configured template `PackedScene`,
        resolve target and template skeletons once, create template context, and delegate to child installers.
    - Role installer scene roots do not copy template nodes themselves and do not own install mode, source path, or
        target parent path properties.
    - Child installers copy explicit subtrees from `context.TemplateRoot` into actual runtime scenes and own placement,
        copy mode, binding, metadata, and idempotency.
    - Reused template subtrees receive missing nested descendants and refreshed exported references while preserving
        installer-owned output and sibling installer output.
    - Copied role-template subtrees preserve non-empty exported node references when the referenced target exists.
    - Rebase logic maps template-root exported node references to corresponding target-root imported model, skeleton, or
        target-subtree nodes and fails clearly when a required reference cannot be mapped.
    - Role-template wiring is inspectable in scene resources; hard-coded C# topology is limited to validation or rebase
        boundaries, not primary wiring.
    - Legacy metadata bindings are not presented or used as the preferred role-template wiring flow.
    - Child installers in role installer scenes do not expose `PackedScene Template` or equivalent template/resource
        asset properties in their classes or scene serialisation, including animation tree root overrides.
    - Child installer scene serialisation contains no `Female/GeneralSkeleton`-style skeleton source path,
        `TargetSkeleton`, or equivalent per-child skeleton target configuration.
    - Child installers in role installer scenes do not expose or serialise manual skeleton assignment properties, and
        skeleton-dependent runtime subsystems bind through the root-provided install context.
    - Role installer roots carry character-level collider profile configuration through typed install context, while the
        dynamic physical rig installer applies the profile without child installer reach-in or a generic service bag.
    - Dynamic physical rig installation applies collider profile precedence as root context profile first, then child
        installer default, then copied template rig profile.
    - `AlleyCat.Rigging.Installation` template subtree installers have no standalone `PackedScene` fallback API or
        manual skeleton assignment API; missing template root or skeleton context produces a clear installation failure.
    - Male NPC templates exist under `game/assets/characters/templates/reference_male/`, and Vadim selects the male
        template and runtime assets from the production scene.
    - Mirror room instances actual player and Vadim scenes, not template-only sources.
    - Integration and visual verification scenes use specialised minimal fixtures unless they test production assembly.
    - Base and role templates preserve the split between non-role setup, player-only VRIK/pose/hip reconciliation, and
        NPC `CharacterIK`/provider setup.
    - The player template serialises player animation-tree root selection, and `PlayerRigInstaller` only validates and
        binds the already-installed/template-authored `AnimationTree`.
    - Animation tree templates and reference animation-library data use their documented directories.
    - Tests verify CORE-005 precedence preserves explicitly local reused-node exported properties,
        `Node3D.Transform`, and direct character-root exported-property copies while refreshing non-overridden values
        and fully populating new or missing nodes.
    - Tests verify Vadim retains `Voice.Id = "Vadim"` and `SpeechGenerator.VoiceOverride = "Ian.wav"` after automatic
        and repeated installation, without requiring `Ian.wav` in a shared template.
    - Tests verify `TemplateBaseline` does not contribute inherited values to local target-scene override detection.

## References

- @game/assets/characters/reference/skeleton_profiles/skeleton_profile_makehuman.tres
- @game/assets/characters/templates/reference_female/reference_female_base.tscn
- @game/assets/characters/templates/reference_female/reference_female_npc.tscn
- @game/assets/characters/templates/reference_female/reference_female_player.tscn
- @game/assets/characters/templates/reference_male/reference_male_base.tscn
- @game/assets/characters/templates/reference_male/reference_male_npc.tscn
- @game/assets/characters/templates/installers/
- @game/assets/characters/templates/installers/npc_installer.tscn
- @game/assets/characters/templates/installers/player_installer.tscn
- @game/assets/characters/templates/animation/
- @game/assets/characters/reference/player.tscn
- @game/assets/characters/reference/ally.tscn
- @game/assets/characters/reference/vadim.tscn
- @game/assets/characters/reference/female/body_collider_profile.tres
- @game/assets/characters/reference/female/reference_female.blend
- @game/assets/characters/reference/female/animations/
- @game/assets/testing/mirror_room/mirror_room.tscn
- [CORE-005: Scene Installer System](../../core/005-scene-installer-system/index.md)
