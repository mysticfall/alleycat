---
id: CHAR-002
title: Character Root
---

# Character Root

## Requirement

Define `AlleyCat.Character.Character` as the required `CharacterBody3D` root node for all character scenes and the
concrete implementation of the current humanoid `ICharacter` contract.

## Goal

Ensure every character scene has one stable gameplay root with explicit template-authored capability wiring, so
dependent systems can consume character capabilities without guessing whether a `Character` node or component path
exists.

## User Requirements

1. Player and NPC humanoid characters remain interoperable across hands, eyes, voice, locomotion, IK, and animation.
2. Gameplay systems can treat a character as one embodied humanoid actor rather than resolving independent subsystems.
3. Contributors can inspect a character scene and identify the root `Character` node used by gameplay and installers.
4. Character scene import settings create the required gameplay root instead of relying on later ad-hoc insertion.
5. Contributors can inspect final role templates and see the required eyes, locomotion, voice, left hand, and right
   hand capability references on the template root.
6. Character capability wiring fails clearly when the scene root or required humanoid capability is missing, invalid,
   or unrebasable.
7. Each character asset owns a stable identity for conversation context, while voice retains independent local
   attribution identity regardless of player or NPC role.
8. Gameplay systems can treat every character as an eyes holder and visual subject with discoverable, authored
   appearance cues.
9. Shared character templates provide a whole-character cue, while Ally NPC, Ally player, and Vadim describe their own
   appearances.
10. NPC role templates visibly bind production navigation to their locomotion-capable character root, while player
    templates remain free of AI navigation.
11. Invalid visual-cue ownership fails clearly when a character publishes or explicitly refreshes its authored cues.
12. Character composition exposes configured senses through the ordinary component projection; NPC templates provide
    visual and hearing senses without a bespoke perception component.

## Technical Requirements

1. `AlleyCat.Character.ICharacter` is the canonical interface for the current character kind: a fully embodied humanoid
   character.
2. `ICharacter` must follow the CORE-003 holder trait pattern and aggregate required holder traits for current humanoid
   capability groups:
   - `IHasHands` from BODY-001.
   - `IEyesHolder` from BODY-004.
   - `IHasVoice` from BODY-006.
   - `ILocomotive` from CTRL-001.
   - `IVisualSubject` from BODY-004.
3. `ICharacter` must also remain an `IComponentHolder`, with deterministic component iteration inherited from CORE-003.
4. The concrete Godot type must be named `AlleyCat.Character.Character`.
5. Consumers should depend on `ICharacter` by default. Code that must reference the concrete type from a conflicting
   context should use a local alias, such as `using CharacterNode = AlleyCat.Character.Character;`.
6. `AlleyCat.Character.Character` must inherit from `CharacterBody3D` and implement `ICharacter`.
7. Every installed or runtime character scene root node must be an `AlleyCat.Character.Character` node.
8. Character source asset imports must set the imported scene root script to
   `res://src/Character/Character.cs`, or to an equivalent UID-backed reference to that script.
9. Character source asset imports must keep the imported root type compatible with `CharacterBody3D`; for example,
   `nodes/root_type="CharacterBody3D"` with `nodes/root_script` set to the `Character.cs` script.
10. The root `Character` node owns explicit required capability references as the source of truth for the current
    humanoid capability set:
    - Eyes.
    - Locomotion.
    - Voice.
    - Left hand.
    - Right hand.
11. Final role templates used for runtime installation, including player and NPC templates, must author all required
    capability references on the template root before installation succeeds.
12. Partial reusable base templates may omit role-specific capability references, such as `Voice`, only when they are
    consumed by final role templates that complete the required root-reference contract.
13. `Character.RefreshComponents()` remains voice-required for installed and final characters; missing `Voice` on an
    installed character root is a validation failure, not a runtime relaxation.
14. Required capability references must not be discovered by hard-coded installer topology scans or stored in an
    exported generic authoring list such as legacy `ComponentNodes`.
15. `IComponentHolder.Components` on `Character` is a deterministic projection of the explicit required capability
    references and configured `ISense` components for generic component and trait consumers.
16. The component projection must be holder-defined and stable; recursive implicit component discovery must not be the
    default component collection strategy.
17. Character installers must target the scene root as the authoritative `Character` instance.
18. Character installers transfer and rebase template-authored capability references onto the imported target root
    during installation.
19. C# installer logic is limited to generic reference rebase and validation boundaries; reusable capability wiring
    remains authored in templates rather than encoded as installer topology.
20. Character installers must validate or refresh root `Character` capability references before dependent subsystem
    installers consume character capabilities.
21. Character installers must fail clearly when invoked for a character scene whose root is not the required
    `ICharacter` and `CharacterBody3D` root.
22. Installer validation must fail clearly when required root traits or capability references are missing, wrong,
    duplicate where unique, or unrebasable.
23. Character installers should use the root `Character` as the dependency hub where this reduces node-path coupling.
24. The contract does not introduce optional future character kinds or non-humanoid trait sets.
25. Character roots that should appear in scene context must be authored into the Godot `Actors` group.
26. `Actors` is reserved for strict character discovery; every member must implement `ICharacter`.
27. Items and other non-character nodes must not be added to `Actors`.
28. Character identity semantics defer normatively to CORE-009. `Character` implements `IIdentifiable`: `Id` is a local
    lower `snake_case` identifier, `Type` is `char`, and external or cross-object context uses canonical `FullId`.
29. After CORE-005 target-scene precedence resolves the final `Character.Id`, installation must assign every
     character-owned Voice local `Id` to that exact value. This creates canonical `voice:<character-id>` identity while
     preserving the raw local voice ID solely as operational attribution, not authenticated character provenance.
     Template placeholder voice IDs must be valid lower `snake_case`; installers must replace them and validate the
     resulting voice identity at the final installation boundary before exposure to scene consumers.
30. `Voiceprint` is a listener-recognition key and must not be used as proof that a voice belongs to a character.
31. The lowest shared male/female character bases must author `CharacterCardContextSource`, `Actors` membership, and
    `VisualSubjects` membership. Both bases must be valid `IVisualSubject` scan members. Higher role templates and
    concrete scenes must not add redundant compensation; BODY-004 normatively owns cue and scan details.
32. Under CTX-001, `CharacterCardContextSource` must publish canonical character identity as exactly
    `{ FullId: subject.FullId }`, not a bare local `Id`.
33. The concrete `Character` root owns a validated, read-only published visual-cue collection for its `IVisualSubject`
    role. Its visual-cue topology is immutable after publication until an explicit refresh.
34. `Character.RefreshComponents()` must perform provider-side nearest-provider cue-ownership validation when it
    publishes or explicitly refreshes the collection. It must fail clearly for invalid ownership; EyesBehaviour must
    not reconcile or filter ownership while scanning. Character installation must preserve, rebase, and validate
    template-authored visual-cue references, including non-empty and ordinally unique IDs per provider.
35. The lowest shared reference female and male character templates each author exactly one `StaticVisualCue` with ID
    `body` at `Head/BodyVisualCue` (a sibling of the existing `Viewpoint`); its generic template may use placeholder
    description content.
36. Ally NPC, Ally player, and Vadim assets override the inherited `body` cue template with character-specific
     appearance descriptions rather than replacing the shared cue topology.
37. Final NPC role templates compose `LocomotiveNavigation` as their production navigation consumer. Its explicit actor
    reference targets the root, which supplies both `Node3D` identity and the `ILocomotive` facade.
38. Player and shared player-base templates do not compose `LocomotiveNavigation`; player locomotion remains tracker
    driven through CTRL-001. NPC installation validates the rebased navigation actor before processing, and fails
    clearly when the binding is absent or is not both `Node3D` and `ILocomotive`.
39. Production installer and playtest composition bind navigation through `INavigation`, the explicit actor `Node3D`,
    and `ILocomotive`, without requiring `DirectTransformNavigation` fields. That component remains the deterministic
    test and diagnostic baseline, not the NPC production component.
40. `Character.Components` deliberately includes every configured `ISense` in deterministic holder order. A component
    that is both a required embodied capability and a configured sense appears once.
41. Final NPC role templates configure Eyes and Hearing senses under AI-006. Character and Body production code must not
    depend on Mind, although scene composition may place Mind beneath Character.
42. `CharacterPerception`, `MindStimulus`, and their bespoke installer or scene wiring must not exist.

## In Scope

- `ICharacter` as the canonical fully embodied humanoid character contract.
- `AlleyCat.Character.Character` as the concrete `CharacterBody3D` root node type.
- Required aggregation of current humanoid holder traits.
- Character source asset import settings that attach `Character.cs` as the imported scene root script.
- Template-authored root references for eyes, locomotion, voice, left hand, and right hand capabilities on final role
  templates.
- Partial reusable base templates that may omit role-specific references only when role templates complete the contract.
- Deterministic `Components` projection from explicit required capability references and configured senses.
- Installer validation, reference rebase, refresh, and dependency-hub usage for character scene assembly.
- Name conflict and alias guidance for the concrete `Character` type.
- Character-root membership in the `Actors` group for SCN-001 scene-context discovery.
- Asset-owned CORE-009 character identity and character-owned voice-ID installation for operational attribution.
- Lowest-shared-base character-card context wiring.
- `ICharacter` aggregation of the BODY-004 visual observer and visual subject roles.
- Validated, template-authored whole-character visual-cue references and character-specific description overrides.
- NPC-only `LocomotiveNavigation` composition through the character root's `Node3D` and `ILocomotive` contracts.
- NPC Eyes and Hearing composition through the ordinary component projection under AI-006.
- `ICharacter` aggregation of the BODY-004 visual-subject role and eyes-holder capability.
- Validated, published template-authored whole-character visual-cue references, explicit refresh, and
  character-specific description overrides.

## Out Of Scope

- Non-humanoid character contracts or alternate future character kinds.
- Optional capability groups not required by the current fully embodied humanoid scope.
- Replacing BODY, CTRL, IK, or speech subsystem contracts with character-root-specific APIs.
- Exact scene-node names for art, mesh, or imported visual roots.
- Final component ordering beyond deterministic holder-defined ordering required by CORE-003.
- Migration support for legacy no-root or near-root character scenes beyond clearly failing validation.
- Optional capability discovery systems beyond the explicit required humanoid capability references.
- Item or non-human actor discovery through `Actors`.
- A Vadim player asset; this slice uses a lightweight alternate female-player identity fixture for configurability.

## Acceptance Criteria

### User Requirements

1. Player and NPC humanoid scenes expose one identifiable root `Character` node for gameplay and installer consumption.
2. Systems that need hands, eyes, voice, or locomotion can consume an `ICharacter` without hard-coded component paths.
3. Imported character scenes instantiate with `Character` as the actual scene root, not as a near-root child.
4. Missing or invalid scene roots produce clear installer validation failures.
5. Final role templates expose required eyes, locomotion, voice, left hand, and right hand references on the template
   root for contributor inspection.
6. Missing, wrong, duplicate, or unrebasable required humanoid capabilities produce clear validation failures.
7. Partial reusable base templates may omit role-specific `Voice` only when the final role templates that consume them
   author `Voice` before runtime installation.
8. Role-explicit Ally player, Ally NPC, and Vadim NPC assets retain their own local identities, while an alternate
   female-player fixture demonstrates configurable identity independent of voice attribution.
9. Every character exposes a discoverable whole-character `body` cue that can describe its appearance through an eyes
   holder.
10. Ally player, Ally NPC, and Vadim return their own authored appearance descriptions rather than generic placeholder
    text.
11. Final NPC templates expose root-bound production navigation, while player templates contain no AI navigation and
    retain tracker-driven locomotion.
12. Invalid template-authored cue ownership produces a clear character publication or refresh failure.
13. NPCs expose visual and hearing senses through ordinary character component composition.

### Technical Requirements

1. `ICharacter` exists in `AlleyCat.Character` and represents only the current fully embodied humanoid character kind.
2. `ICharacter` extends or otherwise normatively aggregates `IComponentHolder`, `IHasHands`, `IEyesHolder`, `IHasVoice`,
   and `ILocomotive`.
3. `AlleyCat.Character.Character` inherits from `CharacterBody3D`, implements `ICharacter`, and is the scene root.
4. The concrete type is referenced directly only where needed; conflicting contexts use local aliases.
5. Installed and final role `Character` roots expose explicit required capability references for eyes, locomotion,
   voice, left hand, and right hand.
6. Character source asset imports set `nodes/root_script` to `res://src/Character/Character.cs` or its UID reference.
7. Character source asset imports keep a `CharacterBody3D`-compatible root type.
8. Final role templates author required capability references on the root, and installers transfer or rebase them onto
   the imported target root.
9. Partial reusable base templates are valid only as inputs to final role templates that complete any omitted
   role-specific references before installation.
10. Character scene installers fail clearly when the scene root is not the required `ICharacter` and `CharacterBody3D`.
11. `Character.RefreshComponents()` and installer validation fail clearly when `Voice` is missing from an installed or
    final character root.
12. `Character.Components` is a deterministic projection of explicit required capability references and configured
    `ISense` components, not an exported `ComponentNodes` list or recursive topology scan.
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
19. Shared male/female bases each author one `CharacterCardContextSource`, `Actors` membership, and `VisualSubjects`
    membership; each is a valid `IVisualSubject` scan member and higher layers do not compensate redundantly.
20. `CharacterCardContextSource` returns only the canonical `FullId` entry with value `subject.FullId`, not bare `Id`.
21. `ICharacter` normatively aggregates `IEyesHolder` and `IVisualSubject` from BODY-004; `IVisualObserver` does not
    exist.
22. Character roots expose validated published visual-cue references through a read-only collection; installation
    preserves or rebases those authored references, and published cue topology is immutable until explicit refresh.
23. Shared reference female and male templates each contain exactly one `StaticVisualCue` with ID `body` at
    `Head/BodyVisualCue`.
24. `Character.RefreshComponents()` validates its published cue list at publication and explicit refresh, including
    non-empty and ordinally unique IDs and nearest-provider ownership. Invalid ownership fails clearly at that
    boundary, while EyesBehaviour scanning does not reconcile or filter it.
25. Ally player, Ally NPC, and Vadim retain the shared `body` cue topology and provide character-specific template
    overrides.
26. NPC composition tests prove `LocomotiveNavigation` binds the installed root as both `Node3D` and `ILocomotive` only
    after rebase and validation. Installer and playtest composition use `INavigation` without production dependence on
    `DirectTransformNavigation`; direct-consumer baseline tests remain valid.
27. Player-template inspection proves neither final player roles nor shared player bases install `LocomotiveNavigation`.
28. Composition tests verify final NPC roles configure Eyes and Hearing, every configured `ISense` appears exactly once
    in deterministic `Character.Components` order, and required holder traits remain unchanged.
29. Dependency and composition tests verify Character production code has no Mind dependency, scene composition may
    place Mind beneath Character, and no `CharacterPerception`, `MindStimulus`, or bespoke wiring remains.

## References

- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [CORE-005: Scene Installer System](../../core/005-scene-installer-system/index.md)
- [CORE-009: Identifiable Identity](../../core/009-identifiable-identity/index.md)
- [CHAR-001: Character Skeleton Profile](../001-character-skeleton/index.md)
- [AI-004: Lore And Backstory Source Compilation](../../ai/004-lore-backstory/index.md)
- [AI-006: Percept-Based Sensing And Attention](../../ai/006-character-perception-and-attention/index.md)
- [BODY-001: Hands](../../body/001-hands/index.md)
- [BODY-004: Eyes](../../body/004-eyes/index.md)
- [BODY-006: Voice Component](../../body/006-voice/index.md)
- [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md)
- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
- [CTX-001: Contextual Information API](../../context/001-contextual-information-api/index.md)
