---
id: CHAR-002
title: Character Root
---

# Character Root

## Requirement

Define `AlleyCat.Character.Character` as the common `CharacterBody3D` root implementation of the current humanoid
`ICharacter` contract, with `AlleyCat.Character.PlayerCharacter` as the required derived root for the player role.

## Goal

Ensure every character scene has one stable, role-appropriate gameplay root with explicit template-authored capability
wiring, so dependent systems can consume character capabilities without guessing whether a compatible character root
or component path exists.

## User Requirements

1. Player and NPC humanoid characters remain interoperable across hands, eyes, voice, navigation, IK, and animation,
   while each role retains its own movement implementation.
2. Gameplay systems can treat a character as one embodied humanoid actor rather than resolving independent subsystems.
3. Contributors can inspect a character scene and identify its role-appropriate, `Character`-derived root used by
   gameplay and installers.
4. Character scene import settings create the required gameplay root instead of relying on later ad-hoc insertion.
5. Contributors can inspect final role templates and see shared eyes, voice, hands, and animation infrastructure plus
   the role-appropriate movement capability on the template root.
6. Character capability wiring fails clearly when the scene root or required humanoid capability is missing, invalid,
   or unrebasable.
7. Each character asset owns a stable identity for conversation context, while voice retains independent local
   attribution identity regardless of player or NPC role.
8. Gameplay systems can treat every character as a vision holder and visual subject with discoverable, authored
   appearance cues.
9. Shared character templates provide a whole-character cue, while Ally NPC, Ally player, and Vadim describe their own
   appearances.
10. NPC role templates visibly bind production `INavigation`; player templates visibly bind player `ILocomotion` and
    keep optional navigation disabled until explicitly activated.
11. Invalid visual-cue ownership fails clearly when a character publishes or explicitly refreshes its authored cues.
12. Character composition exposes configured senses through the ordinary component projection; NPC templates provide
    visual and hearing senses without a bespoke perception component.
13. Downstream systems use the character's authored voice capability as the authoritative voice for that character.

## Technical Requirements

1. `AlleyCat.Character.ICharacter` is the canonical interface for the current fully embodied humanoid character and
   remains an `INavigator`.
2. `ICharacter` must follow the CORE-003 holder trait pattern and aggregate required holder traits for current humanoid
   capability groups:
    - `IHasHands` from INTR-003.
    - `IHasVision` from VISION-001.
    - `IHasHearing` from SPCH-006.
    - `IHasVoice` from SPCH-005.
    - `INavigator` from NAV-001.
    - `IVisualSubject` from VISION-001.
    - `ISpatial` from VISION-001 (`AlleyCat.Core`), inherited transitively through `IVisualSubject`.
      It must not aggregate `ILocomotive`; locomotion is a player-role capability aggregated by
      `AlleyCat.Character.IPlayer`.
3. `ICharacter` must also remain an `IComponentHolder`, inheriting CORE-003 deterministic component iteration and its
   local component-only `IServiceProvider` contract through the holder traits.
4. The common concrete Godot root type must be named `AlleyCat.Character.Character`; the concrete player root type must
   be named `AlleyCat.Character.PlayerCharacter`.
5. Consumers should depend on `ICharacter` by default. Code that must reference `Character` from a conflicting context
   should use a local alias, such as `using CharacterNode = AlleyCat.Character.Character;`.
6. `AlleyCat.Character.Character` must inherit from `CharacterBody3D` and implement `ICharacter`.
   `AlleyCat.Character.PlayerCharacter` must inherit from `Character` and implement `IPlayer`.
7. Every installed or runtime character scene root must be a `Character` instance or a role-appropriate derived type.
   Production NPC roots must use `Character`; the production player root must use `PlayerCharacter`.
8. Character source asset imports must set the imported scene root script to
   `res://src/Character/Character.cs`, or to an equivalent UID-backed reference to that script.
9. Character source asset imports must keep the imported root type compatible with `CharacterBody3D`; for example,
   `nodes/root_type="CharacterBody3D"` with `nodes/root_script` set to the `Character.cs` script.
10. The root `Character` node owns shared actor, eyes, voice, hands, and animation infrastructure. Its explicit required
    capability references are the source of truth for the current humanoid capability set.
11. `IComponentHolder.Components` must project those references in deterministic role-appropriate order:
    - NPC: Eyes, Navigation, Voice, Hearing, Left, Right.
    - Player: Eyes, Locomotion, Navigation, Voice, Left, Right.
12. Final NPC role templates must author production `INavigation`. Final player role templates must author player
    `ILocomotion` and `PlayerNavigation`, with `PlayerNavigation` disabled by default. Both roles must author the shared
    Eyes, Voice, Left hand, and Right hand references before installation succeeds.
13. Partial reusable base templates may omit role-specific capability references, such as `Voice`, only when they are
    consumed by final role templates that complete the required root-reference contract.
14. `Character.RefreshComponents()` remains voice-required for installed and final characters; missing `Voice` on an
    installed character root is a validation failure, not a runtime relaxation.
15. Required capability references must not be discovered by hard-coded installer topology scans or stored in an
    exported generic authoring list such as legacy `ComponentNodes`.
16. `IComponentHolder.Components` on `Character` is a deterministic projection of the explicit required capability
    references and configured `ISense` components for generic component and trait consumers.
17. The component projection must be holder-defined and stable; recursive implicit component discovery must not be the
    default component collection strategy.
18. Character installers must target the scene root as the authoritative `Character` instance, including derived
    role-specific roots.
19. Character installers transfer and rebase template-authored capability references onto the imported target root
    during installation.
20. C# installer logic is limited to generic reference rebase and validation boundaries; reusable capability wiring
    remains authored in templates rather than encoded as installer topology.
21. Character installers must validate or refresh capability references on the `Character`-derived root before
    dependent subsystem installers consume character capabilities.
22. Character installers must fail clearly when a character scene root does not satisfy all common root contracts:
    `ICharacter`, `CharacterBody3D`, and `Character` or a role-appropriate derived type.
23. Installer validation must fail clearly when required root traits or capability references are missing, wrong,
    duplicate where unique, or unrebasable.
24. Character installers should use the `Character`-derived root as the dependency hub where this reduces node-path
    coupling.
25. The contract does not introduce optional future character kinds or non-humanoid trait sets.
26. Character roots that should appear in scene context must be authored into the Godot `Actors` group.
27. `Actors` is reserved for strict character discovery; every member must implement `ICharacter`.
28. Items and other non-character nodes must not be added to `Actors`.
29. Character identity semantics defer normatively to CORE-009. `Character` implements `IIdentifiable`: `Id` is a local
    lower `snake_case` identifier, `Type` is `char`, and external or cross-object context uses canonical `FullId`.
30. After CORE-005 target-scene precedence resolves the final `Character.Id`, installation must assign every
    character-owned Voice local `Id` to that exact value. This creates canonical `voice:<character-id>` identity while
    preserving the raw local voice ID solely as operational attribution, not authenticated character provenance.
    Template placeholder voice IDs must be valid lower `snake_case`; installers must replace them and validate the
    resulting voice identity at the final installation boundary before exposure to scene consumers.
31. `Voiceprint` is a listener-recognition key and must not be used as proof that a voice belongs to a character.
32. The lowest shared male/female character bases must author `Actors` membership and `VisualSubjects` membership.
    Both bases must be valid `IVisualSubject` scan members. Higher role templates and concrete scenes must not add
    redundant compensation; VISION-001 normatively owns cue and scan details.
33. Session render contexts receive characters as raw `ICharacter` values whose template surface is curated by the
    [TMPL-001](../../templating/001-templating-system/index.md) member-access policy: templates can read exactly the
    canonical `FullId`, never a bare local `Id`.
34. The concrete `Character` root owns a validated, read-only published visual-cue collection for its `IVisualSubject`
    role. Its visual-cue topology is immutable after publication until an explicit refresh.
35. `Character.RefreshComponents()` must perform provider-side nearest-provider cue-ownership validation when it
    publishes or explicitly refreshes the collection. It must fail clearly for invalid ownership; EyesBehaviour must
    not reconcile or filter ownership while scanning. Character installation must preserve, rebase, and validate
    template-authored visual-cue references, including non-empty and ordinally unique IDs per provider.
36. The lowest shared reference female and male character templates each author exactly one `StaticVisualCue` with ID
    `body` at `Head/BodyVisualCue` (a sibling of the existing `Viewpoint`); its generic template may use placeholder
    description content.
37. Ally NPC, Ally player, and Vadim assets override the inherited `body` cue template with character-specific
    appearance descriptions rather than replacing the shared cue topology.
38. `AlleyCat.Character.IPlayer` extends `ICharacter` and `ILocomotive`.
    `AlleyCat.Character.PlayerCharacter : Character, IPlayer` is the required concrete player root and composes player
    `ILocomotion` plus optional `INavigation` that is disabled by default.
39. Final NPC role templates compose `NPCNavigation` as production `INavigation` and contain no `ILocomotive`, player
    `CharacterLocomotion`, or `ILocomotion`. NPC navigation owns its root-producing locomotion graph region and coherent
    actor motor path.
40. Player templates compose tracker/stick-driven `CharacterLocomotion` as `ILocomotion` by default. Their
    `PlayerNavigation` activates only explicitly and yields to non-neutral manual movement as defined by CTRL-001.
41. NPC and player movement implementations remain separate. They may share only small, clearly neutral infrastructure;
    character composition must not introduce a shared scheduler or aggregate role-agnostic movement-response model.
42. `DirectTransformNavigation` remains the deterministic test and diagnostic baseline, not the NPC production movement
    implementation.
43. `Character.Components` deliberately includes every configured `ISense` in deterministic holder order. A component
    that is both a required embodied capability and a configured sense appears once.
44. Final NPC role templates configure Vision and Hearing senses under AI-006. Character and modality or delivery
    domains must not depend on Mind, although scene composition may place Mind beneath Character.
45. `CharacterPerception`, `MindStimulus`, and their bespoke installer or scene wiring must not exist.
46. The Character component projection is the sole authored voice source for downstream systems. Mind must not export,
    author, or duplicate an output-voice reference; tools resolve the character-owned voice from the supplied
    `ICharacter`.
47. After `RefreshComponents()` commits its replacement projection, Character must expose the committed state through a
    Core-level component-projection refresh contract and publish its notification. Character remains Mind-free;
    subscribers own any downstream rebinding.

## In Scope

- `ICharacter` as the canonical fully embodied humanoid `INavigator` contract.
- `IPlayer : ICharacter, ILocomotive` as the player-role capability composition.
- `AlleyCat.Character.Character` as the common concrete `CharacterBody3D` root type and production NPC root.
- `AlleyCat.Character.PlayerCharacter : Character, IPlayer` as the required production player root.
- Required aggregation of current humanoid holder traits.
- Character source asset import settings that attach `Character.cs` as the imported scene root script.
- Template-authored shared capability references and deterministic role-appropriate movement ordering on final role
  templates.
- Partial reusable base templates that may omit role-specific references only when role templates complete the contract.
- Deterministic `Components` projection from explicit required capability references and configured senses.
- Installer validation, reference rebase, refresh, and dependency-hub usage for character scene assembly.
- Name conflict and alias guidance for the concrete `Character` type.
- Character-root membership in the `Actors` group for SCN-001 scene-context discovery.
- Asset-owned CORE-009 character identity and character-owned voice-ID installation for operational attribution.
- Character component projection as the sole authored downstream voice source.
- Lowest-shared-base `Actors` and `VisualSubjects` membership wiring.
- `ICharacter` aggregation of the VISION-001 vision-holder and visual-subject roles.
- NPC-default `INavigation` and player-default `ILocomotion` composition with separate movement implementations.
- NPC Vision and Hearing composition through the ordinary component projection under AI-006.
- A Core-level post-commit component-projection refresh contract for downstream consumers.
- Validated, published template-authored whole-character visual-cue references, explicit refresh, and
  character-specific description overrides.

## Out Of Scope

- Non-humanoid character contracts or alternate future character kinds.
- Optional capability groups not required by the current fully embodied humanoid scope.
- Replacing embodied-component, CTRL, IK, or speech subsystem contracts with character-root-specific APIs.
- Exact scene-node names for art, mesh, or imported visual roots.
- Alternative component orders beyond the deterministic NPC and player orders defined by this specification.
- Migration support for legacy no-root or near-root character scenes beyond clearly failing validation.
- Optional capability discovery systems beyond the explicit required humanoid capability references.
- Item or non-human actor discovery through `Actors`.
- A Vadim player asset; this slice uses a lightweight alternate female-player identity fixture for configurability.

## Acceptance Criteria

### User Requirement Acceptance

1. Player and NPC humanoid scenes expose one identifiable, `Character`-derived root for gameplay and installer
   consumption; production NPC roots use `Character`, while the production player root uses `PlayerCharacter`.
2. Systems that need shared humanoid capabilities or role-appropriate movement can consume the character root without
   hard-coded component paths.
3. Imported character source scenes instantiate with `Character` as the actual root, not as a near-root child; final
   production scenes retain a root type appropriate to their NPC or player role.
4. Missing or invalid scene roots produce clear installer validation failures.
5. Final role templates expose shared eyes, voice, and hand references plus NPC `INavigation` or player `ILocomotion`
   and disabled-by-default `PlayerNavigation` on the template root for contributor inspection.
6. Missing, wrong, duplicate, or unrebasable required humanoid capabilities produce clear validation failures.
7. Partial reusable base templates may omit role-specific `Voice` only when the final role templates that consume them
   author `Voice` before runtime installation.
8. Role-explicit Ally player, Ally NPC, and Vadim NPC assets retain their own local identities, while an alternate
   female-player fixture demonstrates configurable identity independent of voice attribution.
9. Every character exposes a discoverable whole-character `body` cue that can describe its appearance through a vision
   holder.
10. Ally player, Ally NPC, and Vadim return their own authored appearance descriptions rather than generic placeholder
    text.
11. Final NPC templates expose production `INavigation`; player templates expose tracker/stick-driven `ILocomotion` and
    disabled-by-default optional navigation.
12. Invalid template-authored cue ownership produces a clear character publication or refresh failure.
13. NPCs expose visual and hearing senses through ordinary character component composition.
14. Speech and other downstream systems use the voice authored on the Character component projection, with no duplicate
    Mind-authored output voice.

### Technical Requirement Acceptance

1. `ICharacter` exists in `AlleyCat.Character` and represents only the current fully embodied humanoid character kind.
2. `ICharacter` extends or otherwise normatively aggregates `IComponentHolder`, `IHasHands`, `IHasVision`,
   `IHasHearing`, `IHasVoice`, `IVisualSubject`, and `INavigator`; it does not aggregate `ILocomotive`.
3. `AlleyCat.Character.Character` inherits from `CharacterBody3D` and implements `ICharacter`.
   `AlleyCat.Character.PlayerCharacter` inherits from `Character`, implements `IPlayer`, and is the required production
   player root; production NPC roots use `Character`.
4. Concrete root types are referenced directly only where needed; conflicting contexts use local aliases.
5. Installed and final `Character`-derived role roots expose explicit shared capability references and the movement
   references required for their role.
6. Character source asset imports set `nodes/root_script` to `res://src/Character/Character.cs` or its UID reference.
7. Character source asset imports keep a `CharacterBody3D`-compatible root type.
8. Final role templates author required capability references on the root, and installers transfer or rebase them onto
   the imported target root.
9. Partial reusable base templates are valid only as inputs to final role templates that complete any omitted
   role-specific references before installation.
10. Character scene installers fail clearly when the root does not satisfy all common root contracts: `ICharacter`,
    `CharacterBody3D`, and `Character` or a role-appropriate derived type.
11. `Character.RefreshComponents()` and installer validation fail clearly when `Voice` is missing from an installed or
    final character root.
12. `Character.Components` projects explicit required references and configured `ISense` components in the exact role
    order: NPC Eyes, Navigation, Voice, Hearing, Left, Right; player Eyes, Locomotion, Navigation, Voice, Left, Right.
    It does not use an exported `ComponentNodes` authoring list or recursive topology scan.
13. C# installer logic stays within generic rebase and validation boundaries instead of hard-coding capability topology.
14. Validation paths cover root identity, import root script settings, reference refresh or rebase, and clear failures.
15. The implementation preserves existing subsystem contracts instead of moving hands, eyes, voice, or locomotion APIs
    into the character root.
16. Character roots intended for scene context are members of `Actors`, and no item or non-`ICharacter` node is accepted
    as valid `Actors` membership.
17. `Character` implements CORE-009 `IIdentifiable`: its local lower `snake_case` `Id` has `Type` `char`, and
    cross-object context uses canonical `FullId`.
18. After target-scene precedence, installation assigns every character-owned Voice local `Id` to the final exact
    `Character.Id`, validates canonical `voice:<character-id>` before identity exposure, and replaces only valid lower
    `snake_case` template placeholder IDs. The raw local voice ID remains operational attribution rather than
    authenticated provenance; Voiceprint remains recognition metadata and is not used to establish character ownership.
19. Shared male/female bases each author `Actors` membership and `VisualSubjects` membership; each is a valid
    `IVisualSubject` scan member and higher layers do not compensate redundantly.
20. Render context exposes characters as raw `ICharacter` values whose template surface is curated by the TMPL-001
    member-access policy: only the canonical `FullId` resolves, never a bare local `Id`.
21. `ICharacter` normatively aggregates `IHasVision` and `IVisualSubject` from VISION-001 and `IHasHearing` from
    SPCH-006. `IVisualSubject` inherits the VISION-001 `AlleyCat.Core.ISpatial` contract, so `ICharacter` exposes the
    read-only `GlobalTransform` trait transitively; `IVisualObserver` does not exist.
22. Character roots expose validated published visual-cue references through a read-only collection; installation
    preserves or rebases those authored references, and published cue topology is immutable until explicit refresh.
23. Shared reference female and male templates each contain exactly one `StaticVisualCue` with ID `body` at
    `Head/BodyVisualCue`.
24. `Character.RefreshComponents()` validates its published cue list at publication and explicit refresh, including
    non-empty and ordinally unique IDs and nearest-provider ownership. Invalid ownership fails clearly at that
    boundary, while EyesBehaviour scanning does not reconcile or filter it.
25. Ally player, Ally NPC, and Vadim retain the shared `body` cue topology and provide character-specific template
    overrides.
26. Interface and root-type tests prove `IPlayer : ICharacter, ILocomotive` and
    `PlayerCharacter : Character, IPlayer`; production player composition uses `PlayerCharacter`, binds player
    `ILocomotion`, and keeps optional `INavigation` disabled until explicit activation.
27. NPC composition tests prove production NPC roles use `NPCNavigation` as `INavigation` without an `ILocomotive`,
    player `CharacterLocomotion`, or `ILocomotion` dependency, and that player roles do not install `NPCNavigation`.
    Direct-consumer baseline tests remain valid.
28. Source searches and composition tests prove no shared NPC/player scheduler, aggregate role-agnostic response model,
    or old compatibility path remains.
29. Composition tests verify final NPC roles configure Vision and Hearing, every configured `ISense` appears exactly
    once in deterministic `Character.Components` order, and required holder traits resolve those components.
30. Dependency and composition tests verify Character production code has no Mind dependency, scene composition may
    place Mind beneath Character, and no `CharacterPerception`, `MindStimulus`, or bespoke wiring remains.
31. `ICharacter` inherits CORE-003 `IComponentHolder` service-provider semantics, including local component-only
    resolution and the specified zero-, one-, and multiple-match outcomes.
32. Composition tests verify the Character projection is the sole authored voice source and Mind has no exported or
    duplicated output-voice reference.
33. Composition tests verify a post-commit component-projection refresh contract is available without introducing a
    Character-to-Mind dependency.

## Implementation Status

The implementation conforms to the role-root architecture: production NPC roots use `Character`, while production
player composition uses `PlayerCharacter : Character, IPlayer`, `CharacterLocomotion` through `ILocomotion`, and
disabled-by-default `PlayerNavigation`. NPC composition uses `NPCNavigation` through `INavigation` and contains no
player `CharacterLocomotion` or `ILocomotion`.

## References

- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [CORE-005: Scene Installer System](../../core/005-scene-installer-system/index.md)
- [CORE-009: Identifiable Identity](../../core/009-identifiable-identity/index.md)
- [CHAR-001: Character Skeleton Profile](../001-character-skeleton/index.md)
- [AI-004: Lore And Backstory Source Compilation](../../ai/004-lore-backstory/index.md)
- [AI-006: Percept-Based Sensing And Attention](../../ai/006-character-perception-and-attention/index.md)
- [AI-001: Mind Component](../../ai/001-mind/index.md)
- [AI-002: Agent Runtime](../../ai/002-agent-runtime/index.md)
- [INTR-003: Hands](../../interaction/003-hands/index.md)
- [VISION-001: Eyes](../../vision/001-eyes/index.md)
- [SPCH-005: Voice Component](../../speech/005-voice/index.md)
- [SPCH-006: Hearing Component](../../speech/006-hearing/index.md)
- [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md)
- [TMPL-001: Templating System](../../templating/001-templating-system/index.md)
- [NAV-001: NPC Navigation](../../nav/001-npc-navigation/index.md)
- [ANIM-002: Locomotive Animation Set](../../animation/002-locomotive-animation-set/index.md)
- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
