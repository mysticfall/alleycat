---
id: CORE-005
title: Scene Installer System
---

# Scene Installer System

## Requirement

Establish a general, composable installer pattern for setting up and configuring scene modules through visible
template-backed installation. Template scenes are the authoritative, inspector-readable source for serialisable node
references and reusable topology. Installers copy, rebase, and validate authored template contents instead of encoding
primary scene topology in C#.

## Goal

Provide a contract for composing scene setup logic through modular installers. The contract ensures:

- Installers are reusable where they share the same explicit context contract.
- Root coordinators do not require intimate knowledge of module-internal node placement.
- Module installers encapsulate domain-specific setup, such as IK rigs, animation players, and AI controllers.
- Template authors can inspect and maintain node references in scenes rather than reverse-engineering C# topology.
- The pattern supports runtime and editor-debug installation for testing and scene management.
- Character rig portability is achieved as a specific application of this general pattern.

## User Requirements

1. Developers can define module-specific installers, such as IK or animation installers, that handle setup for
     their own domain using template scenes/resources to expose what they install.
2. A root installer can delegate installation tasks to child/module installers through a general interface.
3. Template-provider root installers do not need to know internal node placement rules for each delegated module.
4. Installers produce editor-visible output where appropriate by copying and rebasing template contents, so scenes
   remain inspectable and testable.
5. Generated character rigs do not depend on hard-coded references to concrete character meshes or node names.
6. The system avoids opaque runtime-only scene management as the only observable configuration mechanism.
7. Installer/module scenes must expose what they install through visible template scenes and resources. Character
     modules such as IK modifiers, IK targets, physical rig, animation tree, eyes, locomotion, hands, and attachments
     live in inspectable templates.
8. Character role scenes keep player and NPC installation separate while sharing the same explicit character-template
   context shape.
9. Contributors can inspect role templates as exemplars for player and NPC wiring before running installation.
10. Template edits to authored installer-owned topology, including new, moved, or renamed nodes and changed node
    references, propagate to installed player/NPC scenes without C# installer source changes when they stay within
    authored installer boundaries.
11. Player and NPC role templates can be used as drop-in replacements for scenes derived from them, including nested
    descendants under reused template subtrees.

## Technical Requirements

1. The core installer contract lives in `AlleyCat.Core.Installer` and accepts an explicit typed installation target
   context through `ISceneInstaller<TContext>`.
2. Template scenes are the source of truth for serialisable exported node references and reusable scene topology.
3. Module installers encapsulate setup for their domain by copying authored subtrees/resources from template scenes,
   including placement, ordering, and validation rules.
4. Composition delegates to ordered module installers without knowing module-internal topology. Character role
   composition is explicit on the character role coordinator.
5. Installers must be additive and safe to compose with other installers that target the same scene root.
6. Installer logic must avoid absolute paths and concrete character names such as `Female`.
7. Installers use template-authored exported references, component binding, or relative lookup from the installation
   context as the preferred wiring flow.
8. Legacy metadata bindings may remain as an escape hatch for non-serialisable or migration-only wiring, but they are
   not the preferred primary authoring mechanism.
9. Production module installers must not hide required node inventories in opaque hard-coded C# creation lists when
     those inventories define reusable scene topology.
10. The system supports runtime execution for lightweight instances, tests, and prototyping.
11. The system supports editor-tool or asset-operation debug execution without a separate bake-mode API.
12. Role installer scene roots implement `ISceneInstaller.Install`, export a `PackedScene Template`, instantiate the
     template source, resolve target-scene shared dependencies, create typed template context, and delegate installation
     to child installers.
13. Template-provider root installers must not copy or install template scene nodes themselves.
14. Template-provider root installers must not own subtree placement properties such as install mode, source path, or
     target parent path.
15. Child installers select and clone subtrees from the root-provided template context using explicit source and target
     placement rules.
16. Child installers own source subtree paths relative to the typed template context's template root, target parent
     paths or resolvers, selected-node versus selected-node-children mode, binding, metadata, and idempotency.
17. Child installers in the role-installer scene flow must not expose a `PackedScene Template` or equivalent template or
     resource asset property, including animation tree root overrides; only the role installer scene root owns the
     template asset.
18. Child installers may rebase exported node references copied from template subtrees so references authored against
    the template root target the corresponding imported model, skeleton, or target subtree in the installed scene.
19. Installed runtime nodes whose lifecycle callbacks may read exported node references must not enter the live scene
    tree with stale template-local or null references when those references can be copied and rebased first.
20. Subtree copy operations build source-to-runtime reference mappings before adding copied nodes to the live tree for
     same-subtree references and same-batch sibling references. Existing-node reuse refreshes exported references from
     the template source before dependent installation continues.
21. Reused template subtrees are reconciled against the current template on reinstall: missing nested descendants are
    added and template-authored references are refreshed, while installer-owned output and sibling installer output are
    preserved.
22. Rebase logic is limited to template-root to target-root boundary translation and validation; it must not become the
      primary place where reusable topology or reference wiring is encoded.
23. Character role installer roots use the `AlleyCat.Rigging.Installation` APIs, resolve target and
    template character skeletons once, and expose them through `RigInstallationContext`; auto-resolution by convention
    is allowed, with optional skeleton paths configured
    only on the role root.
24. Role-flow child installer scenes consume skeletons from typed context and must not serialise skeleton source paths,
    `TargetSkeleton`, or equivalent per-child skeleton configuration.
25. Template subtree installers are context-only consumers. They do not provide standalone `PackedScene` fallback APIs,
       and skeleton-dependent child installers fail clearly when invoked without `RigInstallationContext`.
26. Copy operations preserve Godot ownership, installer metadata, idempotency, sibling installer output, and role split.
27. Child installers, not template-provider root installers, copy visual/import roots or other selected template
       subtrees when the role flow requires them.
28. Character rig assembly uses the installer pattern by delegating IK, animation, collider, and related setup.
29. Character role installers prevent role leakage: player-only VRIK, pose, and hip reconciliation do not install into
       NPC/base outputs, and NPC `CharacterIK`/provider setup does not install into player/base outputs.
30. The generic context remains minimal: target root and metadata namespace only. Template roots, skeletons, and other
       installer-specific dependencies live in typed derived contexts rather than type-erased service dictionaries.
31. Automatic runtime installation is owned by root/coordinator installers. Child subsystem installer bases do not
       define competing auto-install switches.
32. Tool installers that support editor execution expose `InstallInEditor` as an exported editor trigger and
        `InstallNowInEditor` as the imperative helper used by editor workflows and tests.
33. Editor undo semantics are coarse: undo clears installer-owned generated output and redo reinstalls it. Exact prior
        generated node identity is not guaranteed across undo/redo.
34. Character rig installer composition is split into explicit player/NPC role installer scenes. Shared base template
    content is provided by the role-owned template scene; misleading generic base-installer assets are removed rather
    than advertising character-context-only children as generic composites.
35. Player-specific animation-tree root selection is authored in the player role/template layer. Player subsystem
        installers bind the already-installed `AnimationTree` and fail clearly if the template did not provide a tree
        root; they do not load hidden player animation resources from C#.

## In Scope

- `ISceneInstaller<TContext>` interface definition and minimal generic `SceneInstallationContext`.
- Module-specific installer implementations for domains such as IK, animation, AI, and item setup.
- Explicit root/coordinator installers for domains that need to delegate ordered module setup.
- Guidelines for writing installers that avoid hard-coded node paths and support composability.
- Contracts for runtime and editor-debug installation workflows.
- Exported editor install trigger and helper workflow.
- Character rig portability as a use case of the installer pattern.
- Base-vs-role character rig installer split, with template-aware role installer scenes for player/NPC assembly.
- Template-scene context creation and propagation through `AlleyCat.Rigging.Installation` from a role installer
  scene root to child installers.
- Character skeleton dependency resolution and propagation from a role installer scene root to child installers.
- Template-provider root installer delegation through `ISceneInstaller.Install` without root-owned node copying.
- Explicit child-installer subtree copy operations, including selected-node and selected-node-children copy modes.
- Exported node-reference rebase from template-root references to the corresponding target-root nodes.
- Lifecycle-safe reference copy and rebase before installed nodes enter the live tree where Godot lifecycle timing
  permits it.
- Validation that required template-authored references are present or fail clearly when a template omits them.
- Copy semantics for ownership, installer metadata, idempotency, sibling installer output, and role split.
- Template-backed module scenes/resources for reusable topology that must remain inspectable.
- Reconciliation for reused template subtrees, including nested descendant installation and reference refresh.

## Out Of Scope

- Specific IK solver algorithms or animation blending techniques.
- Character mesh generation or weight painting workflows.
- Gameplay logic that varies by entity type (e.g., combat behaviour, item abilities).
- Automatic discovery of installers via reflection or scanning (explicit composition is required).
- Custom persistence of installer configurations outside standard Godot scene saving/loading mechanisms.
- Real-time modification of installed scenes during gameplay, unless a module explicitly defines it.
- Replacing template-authored exported references with metadata binding as the normal authoring path.

## Acceptance Criteria

### User Requirements
1. Module-specific installers can be created without exposing internal placement rules to the root installer.
2. A root installer can delegate to multiple module installers via a common interface.
3. Installer output is inspectable through visible templates and when run in an editor-debug context.
4. Generated character rigs avoid concrete character names in installed scene structure outside reference fixtures.
5. Installers do not rely on runtime-only scene manipulation as the only observable outcome.
6. Player and NPC role scenes remain separately installable with role-specific child installer sets.
7. Blank imported character scenes can be assembled from role templates without contributors editing C# topology.
8. Contributors can inspect player and NPC role templates to understand exported reference wiring.
9. Template edits within authored installer boundaries, including new, moved, or renamed nodes and changed node
   references, appear in installed player/NPC scenes without C# installer source changes.
10. Nested descendants added under reused template subtrees are installed automatically.

### Technical Requirements
1. A core installer interface is defined in `AlleyCat.Core.Installer` with an explicit installation target context.
2. At least two module installers implement the core interface and encapsulate domain-specific setup.
3. Character role installers orchestrate `RigSceneInstaller` children explicitly; generic installer composition
   does not require a built-in composite node type.
4. Installers use relative paths, template-authored exported references, or component binding to modify the target.
5. Installer logic avoids hard-coded strings like `Female` except in reference-only fixtures.
6. Installer logic has no bake-mode branch or stable-ID requirement; idempotency uses installer node path/name metadata
   when metadata is needed.
7. Character assembly uses installer delegation without hard-coded knowledge of module internals.
8. Reusable production modules expose required topology through template scenes/resources rather than opaque C# node
   inventory creation.
9. Role installer scene roots implement `ISceneInstaller.Install`, export `PackedScene Template`, instantiate the
   configured template source, resolve target and template skeletons for character scenes, create
   `RigInstallationContext`, and pass it to child installers.
10. Role installer scene roots do not copy template nodes themselves and do not own install mode, source path, or target
     parent path properties.
11. Child installers copy selected template subtrees using source paths relative to their typed template context, target
     parent paths or resolvers, and selected-node or selected-node-children copy mode.
12. Child installers in the role-installer scene flow do not expose `PackedScene Template` or equivalent template or
      resource asset properties in their classes or scene serialisation, including animation tree root overrides.
13. Character role-flow child installers do not expose or serialise manual skeleton assignment properties; tests verify
     they bind skeleton-dependent subsystems from the root-provided install context.
14. Template subtree installers expose no standalone template asset fallback API and fail clearly without a typed
    template/character context.
15. Copy operations preserve Godot ownership, installer metadata, idempotency, sibling installer output, and role split.
16. Copied template subtrees preserve non-empty exported node references where the referenced target exists.
17. Rebase logic maps template-root exported node references to corresponding target-root imported model, skeleton, or
     target-subtree nodes and fails clearly when a required reference cannot be mapped.
18. Tests verify lifecycle-sensitive installed nodes do not enter the live tree with stale template-local or null
    exported references when those references can be copied and rebased before `AddChild`.
19. Tests verify same-subtree and same-batch sibling exported references are mapped before lifecycle callbacks can cache
     them, and existing-node reuse refreshes exported references from the template source before validation passes.
20. Tests verify reused template subtrees receive missing nested descendants and refreshed exported references while
    preserving installer-owned output and sibling installer output.
21. Tests or review confirm hard-coded topology in C# is limited to validation or rebase boundaries, not primary wiring.
22. Tests or review confirm legacy metadata bindings are not presented or used as the preferred authoring flow.
23. Tests or review confirm player-only VRIK/pose/hip reconciliation is absent from NPC/base output, and NPC
         `CharacterIK`/provider setup is absent from player/base output.
24. Unit tests verify the generic context has no template root or service dictionary API, typed context interfaces are
       present, and auto-install is not duplicated by rig subsystem bases.
25. Role installer roots resolve target and template skeletons once, with optional skeleton path configuration only on
    the role root.
26. Tests or scene review confirm role installer child serialisation contains no `Female/GeneralSkeleton`-style skeleton
    source path, `TargetSkeleton`, or equivalent per-child skeleton target configuration.
27. Editor-capable installers expose `InstallInEditor` and route editor execution through `InstallNowInEditor`.
28. Editor undo clears installer-owned generated output and redo reinstalls it; stable generated node identity is not
         required.
29. Actual character scenes include the matching role installer scene, while the role installer scene root owns
           template-aware composition and keeps base setup separated from player/NPC role setup.
30. Tests or review confirm character-context-only composition is represented by explicit role installer scenes, not an
         obsolete generic base asset.
31. Tests or review confirm player animation-tree root selection is serialised in the player template/role-owned data,
         while `PlayerRigInstaller` contains no hard-coded player animation resource load.

## References
- `@game/src/Core/Installer/` — Installer system types
- `@game/src/Core/Installer/TemplateSceneReferenceRebaser.cs` — Template-root to target-root reference rebasing
- [CORE-003: Component/Trait System](../003-component-system/index.md) — Potential component-holder integration
- [CHAR-001: Character Skeleton Profile](../../character/001-character-skeleton/index.md)
- [Character Generator](../../tooling/character-generator/index.md)
