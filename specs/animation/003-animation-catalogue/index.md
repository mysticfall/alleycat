---
id: ANIM-003
title: Animation Catalogue
---

# Animation Catalogue

## Requirement

Provide one catalogue for variant animations (idle and locomotion). Content authors define entries in JSON animation
packs. Character templates compose those packs additively, and runtime systems, agents and observers resolve entries
through per-category views. Transitions between entries run through MotionBricks bridges from the realised pose.

## Goal

Replace the scattered, hardcoded idle and locomotion wiring with one consistent, data-driven contract. A character
can then carry several idles and gaits, switch between them naturally, and let NPCs choose and notice them. External
pack authors can also add content without editing code or the Godot editor.

## User Requirements

1. A character can stand in any idle and move in any locomotion variant from its catalogue, with natural transitions
   between idles and between idle and movement. The body does not visibly snap, freeze or jerk at a transition.
2. Changing direction mid-transition (a new idle request, or starting or stopping movement) redirects motion smoothly
   from wherever the body currently is, without first finishing the old transition.
3. Every character starts in a sensible idle and gait. An NPC can choose a different stance or gait in character,
   and keeps that choice until it chooses again.
4. An NPC knows which stances and gaits it can adopt and which it currently has. A request for one it cannot adopt
   is refused in character.
5. An NPC that looks at another NPC learns its visible stance or gait at first look, and then notices when it
   changes, as a plain visible account. It does not see internal identifiers or tags. It is not told about simply
   starting or stopping movement, or about an unchanged NPC it looks at again.
6. Male and female reference characters each have at least one usable idle and gait.
7. External content authors can add or extend animation packs for their characters using JSON files and Blender, then
   validate them. They do not need to edit code or use the Godot editor, and no repository content is generated for
   them.
8. Player VRIK and pose behaviour is unchanged. Catalogue idles never play on the player, and built-in pose clips are
   not catalogue entries.

## Technical Requirements

### Catalogue Scope

1. The catalogue covers variant animations only: categories `idle` and `locomotion`. Built-in VRIK and player pose
   AnimationTree clips ([IK-004](../../ik/004-vrik-pose-state-machine/index.md)) are not catalogue entries, are not
   listed to agents, and are not owned by this spec.
2. No runtime path special-cases an animation named `idle`, reads a hardcoded idle clip, or loops a fixed SOMA idle
   sample. All idle playback resolves through the idle view (TR9).

### Entries and Packs

3. Every entry has a shared core: `id`, `category`, `tags`, `description` and its owning pack. Entry identity is the
   pair (`category`, `id`). Each category adds its own payload: an idle entry references one native clip, and a
   locomotion entry references one ANIM-002 reviewed set. The [Animation Pack Format](pack-format.md) page is the
   normative public authoring contract for the JSON schema, path resolution, the validator, the fixture pack and the
   author documentation.
4. An animation pack is a named bundle of entries, such as `default` or `combat`, defined by one `pack.json`. The
   pack loader takes a [CORE-008](../../core/008-content-pack-resolution/index.md) `ContentContext`, and paths inside
   the pack resolve against that context's root.
   - **Production:** the context comes from the location of `pack.json`. A manifest under `res://content/<id>/` gets
     that content pack's context. Any other location gets `ContentContext.Default` (`default` at `res://`).
   - **Tests:** tests may inject a context ([Animation Pack Format](pack-format.md#path-resolution)).
   Content packs are never generated in this repository.
5. ANIM-002 reviewed set files are referenced unchanged. A locomotion entry adds catalogue identity around a set; it
   does not copy, rewrite or extend the set schema.

### Character Catalogue and Layering

6. Each character has one catalogue node with one child pack node per animation pack. A pack node references one
   `pack.json` and exports `Enabled` and an integer `Priority`. Derived templates add pack nodes, or disable inherited
   ones by overriding `Enabled`, under [CHAR-002](../../character/002-character-root/index.md) TR50–51 template-chain
   installation. Packs never come from a single array that inheritance would replace.
7. The catalogue applies enabled packs additively in ascending `Priority` order, with ties broken by ordinal pack id.
   Scene-tree child order is not part of the contract. Convention: base templates use lower priorities than the
   templates derived from them.
8. On an entry collision, the later-applied pack's entry replaces the earlier one, and one log message names the
   category, entry id, overriding pack and overridden pack. Packs of equal `Priority` follow the same rule, in the
   ordinal pack-id order from TR7, and write the same log message. A pack id used by two enabled pack nodes is a
   different case: it fails catalogue construction clearly. The catalogue is built once per character before first
   use and stays fixed for the session.

### Category Views and Defaults

9. Each category has its own view API over the shared catalogue.
   - **Idle view:** resolves an entry to its native clip.
   - **Locomotion view:** resolves an entry to an ANIM-002 reviewed set, loaded by the existing set loader.
   - Consumers use a view, not raw entries or file paths. A view lists only entries compatible with the character
     (TR11).
10. Each category's effective entry resolves in this order: agent preference (TR22), then the character template
    default, then the catalogue fallback. The catalogue node exports one default id per category. Derived templates
    override it like any exported value. The catalogue fallback is the first compatible entry of that category in
    catalogue order. Catalogue order lists packs in application order (TR7) and each pack's entries in manifest order.
    An overriding entry takes its overriding pack's position, and the overridden entry leaves the order. A category with
    no compatible entry fails clearly when a consumer requires it. A preference or default naming an unknown or
    incompatible entry is ignored with a clear log message, and resolution continues with the next source.
11. An idle entry is compatible with a character when every clip track on a SOMA-mapped bone (a bone in the ANIM TR4
    semantic body map) resolves on that character's native skeleton. Tracks on bones outside the SOMA map, such as
    breasts or twist bones, are optional. On a skeleton without those bones they are safe no-ops, matching the
    absent-bone reuse rule in the [Kimodo setup contract][kimodo-reuse] (item 6). Body family is not part of the check,
    so the female `sad` clip is usable on the male skeleton even though the male skeleton lacks `breast_l` and
    `breast_r`. A clip with any unresolved SOMA-mapped track is incompatible. A locomotion entry is compatible when its
    set loads and the character can present it under [ANIM](../index.md) TR4. Incompatible entries are excluded from
    that character's views, with one log message per entry.

### Playback Rig

12. Locomotion always plays through SOMA: generated samples go to the hidden source skeleton and are presented through
    `RetargetModifier3D`, under ANIM TR4.
13. Idles are native clips on the reference character skeleton (`GeneralSkeleton`), played without runtime
    retargeting. During native idle:
    - A runtime skeleton modifier placed first on the reference skeleton is the only presented writer for every bone
      the idle clip keys. IK and the modifiers after it keep their order.
    - The NPC AnimationTree keeps running, because it also drives eye blink and gaze
      ([VISION-001](../../vision/001-eyes/index.md)) and hand poses. The idle modifier overwrites its output on
      clip-keyed bones before presentation.
    - `RetargetModifier3D` is inactive throughout.
    - Bones the clip does not key keep the output of their other writers.
    Catalogue idles never play on the player. The player's tracked idle stays owned by VRIK
    ([IK-004](../../ik/004-vrik-pose-state-machine/index.md)). Player templates declare no idle default. They still
    carry packs and a locomotion default, because `PlayerMotionBricks` consumes the locomotion view.
14. The ANIM TR5 native→SOMA conversion is used only to feed MotionBricks bridges. It supplies realised context and
    converts idle target windows (TR16). Converted target frames are not realised observations and never enter
    realised-pose history.

### Transitions

15. **Transition Matrix:** idle→idle, idle→locomotion and locomotion→idle transitions are required. Each runs as a
    MotionBricks bridge from actual realised context. Locomotion→locomotion variant switching follows
    ANIM-002 TR4's variant-change rules. It is a required, tested mechanism, but its visual proof with a second set is
    deferred (Out Of Scope).
    - **Transition-Started Event:** when a transition starts, the subject raises `(subject, category, entry, kind)`.
      The kind is one of idle→idle, idle→locomotion, locomotion→idle or locomotion variant switch. An interruption
      raises a new event only when its target differs from the current one. Consumers may filter by kind or target;
      the [Mind Integration](mind-integration.md#observation-of-changes) observation compares the target with the
      last observed entry, so plain movement start and stop are ignored. First-look and re-focus observations come
      from focus acquisition, not from this event.
16. **Idle Target:** an idle's transition target is its start. Frame 0 and the frames after it form the MotionBricks
    target window: at least the interface's four consecutive 30 Hz frames, converted under TR14. Entries have no
    authored entry or exit fields.
17. **Idle Exit:** an idle may be exited at any frame. Context comes from the realised-pose reader. There is no
    foot-phase synchronisation and no wait for a loop boundary.
18. **Settle Blending:** to avoid a velocity stop at the seam, a bridge into an idle applies an ease-out time-warp
    over the final part of its playback, so motion settles into the clip's frame 0. A bridge starting from an idle
    applies an ease-in at its start. This is a scoped exception to the ANIM TR2 time-warp restriction. It covers only
    bridges into and out of catalogue idles. It never covers locomotion source sampling, or the CTRL-001 player settle
    into tracked idle, which ANIM TR3 still governs. Durations and curves are tunable; current values are a 0.2 s
    ease-in and a 0.33 s ease-out.
19. **Bridge Body Contract:**
    - The bridge carries the source hips, including lateral sway into and out of an idle. It does not pin the hips
      laterally.
    - Bones outside the SOMA map (such as `Root`, breasts and forearm twists) are blended through the bridge from
      their realised values to the target values, never snapped.
    - At the bridge-to-clip seam, the pose stays continuous with the clip's frame 0 within a declared tunable
      tolerance.
20. **Interruption and Latency:**
    - **Interruption:** a new request during an idle or arrival bridge abandons the old target. The current bridge
      keeps playing until the replacement arrives; the pose never freezes while it waits. The replacement starts from
      the realised pose, with a short velocity blend from the outgoing motion. The blend starts from the presented
      pose: a blend still running into the interrupted motion keeps running inside the new one, including when a stop
      or redirect interrupts an early departure. The blend duration is tunable; the current value is about 0.27 s
      (8 frames). Interruption has no fallback animation policy.
    - **Latency:** catalogue-idle bridges do not use the ANIM TR3 presented re-anchoring. A replacement bridge
      starts at the time already elapsed since its realised context, and blends velocity from the outgoing motion.
    - **Staleness:** a result whose context is older than a tunable maximum age (currently 0.5 s) is regenerated from
      fresh realised context. Superseded results are discarded, under the ANIM-002 TR4 request-ownership rules.
21. **Controllers:** NAV-001 and CTRL-001 request idle targets and locomotion sets through the views, not through fixed
    data paths. Controllers decide *when* to request arrival, release and destinations. TR20 governs *how* bridges
    involving a catalogue idle replace one another:
    - **Arriving Destination:** a destination that arrives during an idle bridge or an arrival bridge leaves that
      bridge playing until the replacement is installed. The pose never freezes.
    - **Departure:** a departure from native idle, or from a bridge that is playing, follows the TR20 latency and
      staleness rules. It starts at its elapsed time, blends velocity from the outgoing motion, and is regenerated
      when its context is older than the maximum age.
    - **Player:** the player never uses catalogue idles (TR13), so this does not change the CTRL-001 settle into
      tracked idle.
    The ANIM-002 TR6 idle boundary is delivered by this section.

### Agent and Observer Integration

22. The [Mind Integration](mind-integration.md) page is normative for these Mind integrations:
    - the in-character `change_stance(id)` and `change_gait(id)` tools, which set the agent preference without
      parking;
    - the static catalogue prompt fragment;
    - the own stance/gait scene-status projector line;
    - the NPC-to-NPC observation of stance and gait, at first look and on change.

### Authoring

23. The [Animation Clip Authoring](authoring.md) page is normative for producing idle clips. Both the Kimodo and Rigify
    paths must end in a native clip on the reference skeleton. Locomotion sets continue to use the reviewed-source
    ingestion of the [Kimodo tooling contract](../../tooling/kimodo-pipeline/locomotive-animation-set.md). Clip tools
    produce clips only. Authors maintain the metadata, and the validator enforces it.

### Content

24. Default content (`res://`) ships one animation pack, `default`, at
    `game/assets/animations/packs/default/pack.json`.
    - It holds idle `sad`, a native clip authored from the reference female idle source and published at
      `assets/animations/packs/default/idle/sad.tres` under that content root.
    - Its `entries` array lists locomotion `feminine`, whose `set` is
      `assets/animations/packs/default/locomotion/feminine/set.json` under that root. Keep the ANIM-002 set,
      approved source bundle and angular metadata together.
    - The female and male reference base templates both add the `default` pack and set the locomotion default. NPC
      role templates set the idle default, and player role templates declare none (TR13).
    - The male template must have at least one compatible idle and locomotion entry (TR11).
25. Catalogue native clips (TR13) are the only idle source. Idle playback and its tooling have none of the following:
    - an `idle` animation library on the AnimationPlayer of `reference_female_base.tscn` or
      `reference_male_base.tscn`;
    - a MotionBricks `catalogue.json` or other idle catalogue under `game/assets/animations/motionbricks/`;
    - a `clips` section in `poses.json`, which publication validation rejects;
    - a hardcoded export clip list;
    - processed or runtime idle packages.
    Idle sources are staged and authored only through [Animation Clip Authoring](authoring.md).
26. Catalogue JSON must ship in exported builds. No export presets exist yet. Any future export preset must include
    `*.json` in its non-resource include filter, or an equivalent filter that covers every pack and set file.

### Validation

27. Automated checks cover these areas:
    - schema and path validation;
    - layering order, collision overrides (including equal-priority ordering by pack id), duplicate pack-id failure
      and `Enabled` overrides;
    - default resolution order;
    - skeleton-binding compatibility, including the male template;
    - acceptance of referenced sets by the ANIM-002 loader;
    - removal of the idle special case.
    Transition checks cover the four-frame target window, any-frame exit, settle easing, interruption with a fresh
    realised context, and rejection of stale results. Transition checks use the `default` pack's `sad` and
    `feminine`. Idle→idle also needs the second idle, `slump`, from the content-pack fixture at
    `game/tests/animation/catalogue/fixture/`. Pack checks also run on the
    [test packs](pack-format.md#fixture-pack): the idle-only content-pack fixture, and the default-context extension
    pack.
28. Actual-rig temporal evidence on both reference characters, in Godot native playback, shows each required
    transition, including an interruption. Numerical seam measurements are diagnostic and do not replace the user's
    judgement of the motion.

## In Scope

- Entry core, JSON pack format, pack layering through template-chain nodes, category views and defaults.
- Native idle playback, SOMA locomotion through views, and the bridge transition and interruption contract.
- Kimodo and Rigify idle-clip authoring contracts, the validator, the fixture pack and author documentation.
- The `default` pack's `sad` and `feminine` entries, and catalogue clips as the only idle source (TR25).
- Mind tools, prompt fragment, status line and NPC-to-NPC observation under [Mind Integration](mind-integration.md).

## Out Of Scope

- Visual proof of locomotion→locomotion switching with a second reviewed set. The switching mechanism and its
  tests remain required.
- Action animations, gestures, layered or additive animation, and VRIK or player pose clips. Built-in pose clips,
  including the standalone `Idle.tres`, are untouched.
- Final tuning values for settle and velocity-blend durations, curves and seam tolerances.
- Player observation of NPC changes, and NPC observation of the player's stance or gait.
- New idle or locomotion content beyond the `sad` and `feminine` entries.

## Acceptance Criteria

### User Requirement Acceptance

1. **ANIM-003-UAC-001 (UR1–2):** Actual-rig captures on both reference characters show idle→idle, idle→locomotion and
   locomotion→idle transitions without visible snap, freeze or velocity stop. They also show an interruption that
   redirects from the current pose.
2. **ANIM-003-UAC-002 (UR3–5):** Evidence is split between a live-LLM session and a deterministic end-to-end test:
   - **Live session:** an NPC starts in its template defaults, chooses and uses a stance and a gait in character, keeps
     them, and sees its own state in scene status. A second NPC receives only the entry description when it first
     looks at a static NPC, and again when it sees a change.
   - **Deterministic end-to-end test:** a request for an unknown id is refused in character, with the preference
     unchanged. The live session does not cover refusal.
3. **ANIM-003-UAC-003 (UR6–8):** The male and female references each resolve a usable idle and gait. Using only JSON
   and clips:
   - the idle-only fixture defines a content-pack-shaped pack;
   - the extension test pack extends `default` with an override and a new locomotion entry.
   Both pass validation. VRIK and pose behaviour is unchanged.

### Technical Requirement Acceptance

1. **ANIM-003-TAC-001 (TR1–11):** Tests verify:
   - the entry schema, production derivation of the content context from the `pack.json` location, and path
     resolution through an injected test context;
   - additive layering in priority order, override logging, equal-priority collisions resolved by ordinal pack id
     with the same log message, duplicate pack-id failure and `Enabled` overrides along a template chain;
   - resolution order across preference, template default and fallback, including the fallback position of an
     overriding entry;
   - skeleton-binding compatibility, including SOMA-mapped binding of `sad` on the male skeleton with optional
     breast tracks as no-ops, and exclusion of a clip with an unresolved SOMA-mapped track;
   - acceptance of referenced sets by the ANIM-002 loader.
   A code audit confirms no `idle` special case remains.
2. **ANIM-003-TAC-002 (TR12–21):** Tests and traces confirm these behaviours:
   - that catalogue idles never play on the player, and that the player template declares no idle default;
   - native idle ownership: a deliberately conflicting AnimationTree pose does not reach presented clip-keyed bones;
     bones the clip leaves unkeyed (for example eye blink and gaze) keep the AnimationTree's output; and
     `RetargetModifier3D` stays inactive throughout;
   - target windows converted from at least four frames of clip start;
   - any-frame exit from realised context;
   - settle easing applied only to catalogue-idle bridges, never to the CTRL-001 player settle;
   - hips carry, with non-SOMA bone blending;
   - seam continuity within tolerance;
   - interruption that keeps the current bridge playing until the replacement starts from the realised pose with a
     velocity blend;
   - replacement start at the time elapsed since context, regeneration of results older than the maximum context
     age, and discard of superseded results;
   - transition-started events with the correct kind, and a new event on interruption only when the target differs;
   - controllers resolving through the views.
   Actual-rig evidence under TR28 confirms the motion.
3. **ANIM-003-TAC-003 (TR22–26):** [Mind Integration](mind-integration.md), [authoring](authoring.md) and
   [author guide](pack-format.md#acceptance-criteria) (PACK-AC-003) acceptance pass. The default pack contains
   `sad` and `feminine` at the documented paths, and an audit of scenes, data and tooling finds none of the idle
   sources TR25 prohibits. The export include-filter requirement is recorded.
4. **ANIM-003-TAC-004 (TR27–28):** The validator and test-pack checks under [Animation Pack Format](pack-format.md)
   (PACK-AC-001–002) pass. Actual-rig evidence is independently reviewed.

## Implementation Status

The catalogue is delivered and verified on the delivery candidate.

- **Runtime:** `game/src/Animations/` holds the catalogue node with child pack nodes (`Enabled`, `Priority`), the
  idle and locomotion views, default resolution and TR11 compatibility. Native idle playback uses
  `NativeIdleModifier`, placed first on the reference skeleton. `MotionBricksNavigation` runs the idle↔idle and
  idle↔locomotion bridges, interruption with velocity blending, and stale-context regeneration.
- **Content:** the `default` pack at `game/assets/animations/packs/default/pack.json` holds `sad` and `feminine`.
  Test packs live under `game/tests/animation/catalogue/`, and the [author guide][author-guide] is under
  `game/assets/animations/`.
- **Tooling:** `tools/animation-pipeline/commands/export_idle_clip.py` exports idle clips on the Kimodo (`kimodo`)
  and Rigify (`rig`) paths through `godot/commands/build_native_clip.gd`. The pack validator command is
  `godot/commands/validate_animation_pack.gd`.
- **Mind:** `change_stance` and `change_gait`, the `Stances And Gaits` prompt section, the own stance and gait status
  line, and `AnimationChangePerception` with first-look and re-focus observations are in place.
- **Removal:** the idle sources TR25 prohibits are gone from scenes, data and tooling.
- **Evidence:** the full integration suite passes 1631 of 1632 tests; the one failure is an intermittent test outside
  this work that passes three runs out of three on its own. Unit tests pass 2492 of 2492, framework tests pass 111
  with 1 skipped, and Python tests pass 185 of 185. Actual-rig evidence for TR28 on the male reference is accepted,
  and the live MIND-AC-004 session passes three runs out of three.

Known limits:

- Locomotion→locomotion switching has mechanism tests only; there is no second reviewed set for visual proof (Out Of
  Scope).
- A character without a catalogue renders an empty `Stances And Gaits` section.
- The change observation's scheduling importance (0.2) is provisional tuning.
- Peak acceleration on the locomotion→arrival bridge (7.03, against 6.07 for walking in the same measurement) is
  accepted as pre-existing.

## References

- [Animation](../index.md)
- [ANIM-002: Locomotive Conditioning Sources](../002-locomotive-animation-set/index.md)
- [Animation Pack Format](pack-format.md)
- [Animation Clip Authoring](authoring.md)
- [Mind Integration](mind-integration.md)
- [CORE-008: Content Pack Resolution](../../core/008-content-pack-resolution/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [NAV-001: NPC Navigation](../../nav/001-npc-navigation/index.md)
- [CTRL-001: Player Locomotion](../../ctrl/001-locomotion/index.md)
- [Kimodo Motion Sources](../../tooling/kimodo-pipeline/locomotive-animation-set.md)

[kimodo-reuse]: ../../tooling/kimodo-pipeline/locomotive-animation-set.md#character-setup-and-calibration-contract
[author-guide]: ../../../game/assets/animations/README.md
