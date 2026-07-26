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
7. Each character asset owns a stable identity that drives voice attribution and conversation context independently of
   its player or NPC role.
8. Gameplay systems can treat every character as both an observer of visual cues and a visual subject with discoverable,
   authored appearance cues.
9. Shared character templates provide a whole-character cue, while Ally NPC, Ally player, and Vadim describe their own
   appearances.

## Technical Requirements

1. `AlleyCat.Character.ICharacter` is the canonical interface for the current character kind: a fully embodied humanoid
   character.
2. `ICharacter` must follow the CORE-003 holder trait pattern and aggregate required holder traits for current humanoid
   capability groups:
   - `IHasHands` from BODY-001.
   - `IEyesHolder` from BODY-004.
   - `IHasVoice` from BODY-006.
   - `ILocomotive` from CTRL-001.
   - `IVisualObserver` and `IVisualSubject` from BODY-004.
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
    references for generic component and trait consumers.
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
28. `Character.Id` is asset-owned, exact, and case-sensitive at runtime; role templates must not supply a concrete
    character identity.
29. Generic character installation must set the installed character-owned `Voice.Id` to the final `Character.Id` after
    target-scene precedence is resolved.
30. `Voiceprint` is a listener-recognition key and must not be used as proof that a voice belongs to a character.
31. The lowest shared male/female character bases must author `CharacterCardContextSource` and `Actors` membership.
    Higher role templates and concrete scenes must not add redundant compensation.
32. `CharacterCardContextSource` returns exactly `{ Id: subject.Id }` under CTX-001.
33. The concrete `Character` root owns a validated, read-only visual-cue collection for its `IVisualSubject` role.
34. Character installation must preserve, rebase, and validate template-authored visual-cue references. Validation must
    enforce the BODY-004 cue contract, including non-empty and ordinally unique IDs per provider.
35. The lowest shared reference female and male character templates each author exactly one `StaticVisualCue` with ID
    `body` at `Head/BodyVisualCue` (a sibling of the existing `Viewpoint`); its generic template may use placeholder
    description content.
36. Ally NPC, Ally player, and Vadim assets override the inherited `body` cue template with character-specific
    appearance descriptions rather than replacing the shared cue topology.

## In Scope

- `ICharacter` as the canonical fully embodied humanoid character contract.
- `AlleyCat.Character.Character` as the concrete `CharacterBody3D` root node type.
- Required aggregation of current humanoid holder traits.
- Character source asset import settings that attach `Character.cs` as the imported scene root script.
- Template-authored root references for eyes, locomotion, voice, left hand, and right hand capabilities on final role
  templates.
- Partial reusable base templates that may omit role-specific references only when role templates complete the contract.
- Deterministic `Components` projection from explicit required capability references.
- Installer validation, reference rebase, refresh, and dependency-hub usage for character scene assembly.
- Name conflict and alias guidance for the concrete `Character` type.
- Character-root membership in the `Actors` group for SCN-001 scene-context discovery.
- Asset-owned stable character identity and generic installed voice-ID alignment.
- Lowest-shared-base character-card context wiring.
- `ICharacter` aggregation of the BODY-004 visual observer and visual subject roles.
- Validated, template-authored whole-character visual-cue references and character-specific description overrides.

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
- Empty voice-ID authoring validation.

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
8. Role-explicit Ally player, Ally NPC, and Vadim NPC assets retain their own identities, while an alternate
   female-player fixture demonstrates configurable identity.
9. Every character exposes a discoverable whole-character `body` cue that can describe its appearance to a visual
   observer.
10. Ally player, Ally NPC, and Vadim return their own authored appearance descriptions rather than generic placeholder
    text.

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
12. `Character.Components` is a deterministic projection of explicit required capability references, not an exported
    `ComponentNodes` authoring list or recursive topology scan.
13. C# installer logic stays within generic rebase and validation boundaries instead of hard-coding capability topology.
14. Validation paths cover root identity, import root script settings, reference refresh or rebase, and clear failures.
15. The implementation preserves existing subsystem contracts instead of moving hands, eyes, voice, or locomotion APIs
    into the character root.
16. Character roots intended for scene context are members of `Actors`, and no item or non-`ICharacter` node is accepted
    as valid `Actors` membership.
17. Character installation copies the final exact `Character.Id` to the character-owned voice through generic logic.
18. Voiceprint remains recognition metadata and is not used to establish character ownership.
19. Shared male/female bases each author one `CharacterCardContextSource` and `Actors` membership; higher layers do not
    compensate redundantly.
20. `CharacterCardContextSource` returns only the exact subject `Id` entry.
21. `ICharacter` normatively aggregates both `IVisualObserver` and `IVisualSubject` from BODY-004.
22. Character roots expose validated visual-cue references through a read-only collection, and installation preserves
    or rebases those authored references.
23. Shared reference female and male templates each contain exactly one `StaticVisualCue` with ID `body` at
    `Head/BodyVisualCue`.
24. Validation enforces the BODY-004 cue contract, including non-empty, ordinally unique IDs per character provider.
25. Ally player, Ally NPC, and Vadim retain the shared `body` cue topology and provide character-specific template
    overrides.

## References

- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [CORE-005: Scene Installer System](../../core/005-scene-installer-system/index.md)
- [CHAR-001: Character Skeleton Profile](../001-character-skeleton/index.md)
- [BODY-001: Hands](../../body/001-hands/index.md)
- [BODY-004: Eyes](../../body/004-eyes/index.md)
- [BODY-006: Voice Component](../../body/006-voice/index.md)
- [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md)
- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
- [CTX-001: Contextual Information API](../../context/001-contextual-information-api/index.md)
