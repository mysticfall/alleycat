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
inspector-readable source of truth for player and NPC wiring, while installers copy, rebase, and validate authored
template contents without requiring generators to know internal node placement.

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
14. The role installer scene root is `RigRoleTemplateSceneInstaller` from `AlleyCat.Rigging.Installation`,
     implements `ISceneInstaller.Install`, loads the configured template as a `PackedScene`, resolves target and
     template character skeletons once, creates template
     context, and delegates to child installers.
15. The role installer scene root does not copy or install the visual/import root or any other template node itself.
16. The role installer scene root does not own placement properties such as `InstallMode`, `SourcePath`, or
    `TargetParentPath`.
17. Role templates are authoritative exemplars for serialisable exported node references, component wiring, and reusable
    player/NPC topology.
18. Child installers copy explicit template subtrees from `context.TemplateRoot` into the target actual scene, such as
     `game/assets/characters/reference/ally.tscn`, without requiring the actual scene root to be template-aware.
19. Child installers own source subtree path, target parent path or resolver, selected-node versus
     selected-node-children mode, binding, metadata, and idempotency.
20. Child installers in role installer scenes do not expose `PackedScene Template` or equivalent template/resource
     asset properties, including animation tree root overrides; they select and clone from the template provided by the
     role installer scene root.
21. Optional skeleton path configuration lives only on the role installer scene root; child installer scenes do not
     serialise skeleton source paths, `TargetSkeleton`, or equivalent per-child skeleton target configuration.
22. Child installers in role installer scenes do not expose or serialise per-child skeleton assignments; any child that
     needs target or template skeletons consumes them from the role installer root's install context.
23. `AlleyCat.Rigging.Installation` template subtree installers expose context-driven source/target placement only.
    They do not expose standalone `PackedScene` fallback properties or manual skeleton assignment properties.
24. Exported node references copied from role templates are rebased from the template root to the target scene root when
     they refer to corresponding imported model, skeleton, or target-subtree nodes.
25. Reused template subtrees are reconciled against the current template on reinstall: missing nested descendants are
    added and template-authored references are refreshed, while installer-owned output and sibling installer output are
    preserved.
26. Rebase and validation logic may recognise required target boundaries, but reusable wiring remains authored in the
     role templates rather than hard-coded in C#.
27. Legacy metadata bindings may remain for migration or escape-hatch cases, but role templates are the preferred
     primary wiring mechanism.
28. The mirror room instances actual runtime player and ally scenes, not template-only sources.
29. Integration and visual verification scenes use specialised minimal fixtures where appropriate; production role or
      mirror-room scenes are used only when testing production assembly.
30. Base template content remains role-neutral; player-only VRIK, pose, and hip reconciliation stay out of base/NPC
      templates, and NPC `CharacterIK`/provider setup stays out of base/player templates.
31. The player template owns player animation-tree root selection by overriding the inherited `AnimationTree` resource;
      player subsystem installers bind that authored node/resource instead of loading hidden role resources from C#.
32. Animation tree templates live under `game/assets/characters/templates/animation/`.
33. `game/assets/characters/reference/female/animations/` remains source/reference animation-library data.

## In Scope

- Canonical humanoid skeleton-profile definition and hierarchy.
- Reference character profile resource alignment.
- Normative bone naming for dependent systems.
- Portable character contract specifying required node structure and interfaces.
- Constraints preventing hard-coded dependencies on specific character instances.
- Template-backed installer modules for character runtime topology that must remain visible and reviewable.
- Role scene topology and ownership for player/NPC reference character scenes.
- Flattened template layout under `game/assets/characters/templates/`.
- Complete template-only reference female source scenes and their installer-scene bindings.
- Removal of obsolete wrapper scenes under `game/assets/characters/reference/female/`.
- Separation between template-only reference female sources, role installer scenes, and actual runtime role scenes.
- Template-authoritative exported node references, rebase, and validation for copied character wiring.
- Reconciliation for reused template subtrees, including nested descendant installation and reference refresh.
- Mirror-room and verification-scene consumption rules for actual scenes versus specialised fixtures.

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
        `game/assets/characters/reference/ally.tscn`.
    - Actual player and ally scenes derive from or instance the source `.blend` directly and include the matching role
        installer scene.
    - Actual role scene roots do not own `RigRoleTemplateSceneInstaller` directly and do not use active base-scene
        inheritance from template-only role sources.
    - `npc_installer.tscn` and `player_installer.tscn` use the template-aware role coordinator as their root and
        reference the matching template-only role source through exported `PackedScene Template`.
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
    - `AlleyCat.Rigging.Installation` template subtree installers have no standalone `PackedScene` fallback API or
        manual skeleton assignment API; missing template root or skeleton context produces a clear installation failure.
    - Mirror room instances actual player and ally scenes, not template-only sources.
    - Integration and visual verification scenes use specialised minimal fixtures unless they test production assembly.
    - Base and role templates preserve the split between non-role setup, player-only VRIK/pose/hip reconciliation, and
        NPC `CharacterIK`/provider setup.
    - The player template serialises player animation-tree root selection, and `PlayerRigInstaller` only validates and
        binds the already-installed/template-authored `AnimationTree`.
    - Animation tree templates and reference animation-library data use their documented directories.

## References

- @game/assets/characters/reference/skeleton_profiles/skeleton_profile_makehuman.tres
- @game/assets/characters/templates/reference_female/reference_female_base.tscn
- @game/assets/characters/templates/reference_female/reference_female_npc.tscn
- @game/assets/characters/templates/reference_female/reference_female_player.tscn
- @game/assets/characters/templates/installers/
- @game/assets/characters/templates/installers/npc_installer.tscn
- @game/assets/characters/templates/installers/player_installer.tscn
- @game/assets/characters/templates/animation/
- @game/assets/characters/reference/player.tscn
- @game/assets/characters/reference/ally.tscn
- @game/assets/characters/reference/female/animations/
- [CORE-005: Scene Installer System](../../core/005-scene-installer-system/index.md)
