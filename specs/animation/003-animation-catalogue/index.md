---
id: ANIM-003
title: Animation Catalogue
---

# Animation Catalogue

## Requirement

Provide one catalogue for variant animations (idle and locomotion). Content authors describe individual variants
in JSON. Character templates explicitly select and compose variants across content roots. Runtime systems, agents
and observers resolve entries through per-category views, and transitions run from the realised pose.

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
7. External content authors can author and validate individual variants with JSON and Blender, and explicitly
   select them alongside inherited variants, without editing code or using the Godot editor. Installing content
   does not itself make its variants available to a character.
8. Player VRIK and pose behaviour is unchanged. Catalogue idles never play on the player, and built-in pose clips are
   not catalogue entries.

## Technical Requirements

### Catalogue Scope

1. The catalogue covers variant animations only: categories `idle` and `locomotion`. Built-in VRIK and player pose
   AnimationTree clips ([IK-004](../../ik/004-vrik-pose-state-machine/index.md)) are not catalogue entries, are not
   listed to agents, and are not owned by this spec.
2. No runtime path special-cases an animation named `idle`, reads a hardcoded idle clip, or loops a fixed SOMA idle
   sample. All idle playback resolves through the idle view (TR9).

### Variant Entries

3. Each selected entry comes from one JSON descriptor beside its typed payload. Entry identity is (`category`,
   `id`), not owner, directory or gender. The same id may appear in different categories. The
   [Animation Variant Format](variant-format.md) is normative for descriptors, paths and validation.
4. Only explicit references select descriptors from base content or the active CORE-008 optional pack. Runtime
   payloads resolve within the descriptor's own content root, not the installed character's root. Neither content
   activation nor directory discovery adds entries.
5. ANIM-002 reviewed set files are referenced unchanged. A locomotion entry adds catalogue identity around a set; it
   does not copy, rewrite or extend the set schema.

### Character Catalogue and Layering

6. Each character has one catalogue from its [CHAR-002](../../character/002-character-root/index.md) TR50–51
   template chain. Each contributing template authors a distinct `AnimationVariantLayer` directly under
   `AnimationCatalogue`, with its own `Operations` array. A derived template must not override that array.
   Use [CORE-005](../../core/005-scene-installer-system/index.md) installation and target-scene precedence.
7. Apply layers from base to most derived, in each layer's declared order. A layer acts at most once on each key:
   - **Add:** select a descriptor for an absent key and append the new entry.
   - **Remove:** name an inherited key and remove it; other entries retain their relative order.
   - **Replace:** name an inherited key and select a descriptor with that key. Remove the inherited entry and
     append the replacement, using the replacing layer's order.
   A derived layer with no operation on a key inherits it unchanged.
8. A colliding add, multiple operations on one key in one layer, an absent remove/replace target or a mismatched
   replacement key fails catalogue construction. Do not silently replace, deduplicate or publish a partial
   catalogue. Build it once per character before first use and keep it fixed for the session.

### Category Views and Defaults

9. Each category has its own view API over the shared catalogue.
   - **Idle view:** resolves an entry to its native clip.
   - **Locomotion view:** resolves an entry to an ANIM-002 reviewed set, loaded by the existing set loader.
   - Consumers use a view, not raw entries or file paths. A view lists only entries compatible with the character
     (TR11).
10. Each category's effective entry resolves in this order: agent preference (TR22), then the character template
    default, then the catalogue fallback. A template may inherit, override or clear a category default without
    changing the selected entries, and selecting an entry does not set a default. Fallback is the first compatible
    entry of that category in catalogue order (TR7). A category with no compatible entry fails clearly when a
    consumer requires it. A preference or default naming an unknown or incompatible entry is ignored with a clear
    log message, and resolution continues with the next source.
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
    inherit catalogue entries and a locomotion default, because `PlayerMotionBricks` consumes the locomotion view.
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

24. Both reference base templates explicitly select `idle/sad` and `locomotion/feminine`, in that order, and
    set the locomotion default to `feminine`. Both NPC role templates set the idle default to `sad`; the shipped
    female player template declares none (TR13). Descriptors sit beside `idle/sad/sad.tres` and
    `locomotion/feminine/set.json` under `game/assets/animations/`. Keep the ANIM-002 set, approved source bundle and
    angular metadata together.
25. Catalogue native clips (TR13) are the only idle source. Idle playback and its tooling have none of the following:
    - an `idle` animation library on the AnimationPlayer of `reference_female_base.tscn` or
      `reference_male_base.tscn`;
    - a MotionBricks `catalogue.json` or other idle catalogue under `game/assets/animations/motionbricks/`;
    - a `clips` section in `poses.json`, which publication validation rejects;
    - a hardcoded export clip list;
    - processed or runtime idle packages.
    Idle sources are staged and authored only through [Animation Clip Authoring](authoring.md).
26. Catalogue JSON must ship in exported builds. No export presets exist yet. Any future export preset must include
    `*.json` in its non-resource include filter, or an equivalent filter covering descriptors, sets and their
    required JSON companions.

### Validation

27. Pass the [installed fixture proof](variant-format.md#installed-fixture) before changing production selections.
    For delivery, verify validation, composition, defaults, compatibility and the unchanged playback inputs (TAC-001
    and TAC-004). Prompt and tools use only compatible selected entries (TR9 and TR22).
    Descriptors and explicit template operations are the only selection format, including for external authors.
    Ship no aggregate selection manifests, loaders, nodes, path aliases, compatibility branches or obsolete
    documentation, examples, tooling, tests or fixtures. CORE-008 content packs are unchanged.
    Transition checks cover the four-frame target window, any-frame exit, settle easing, interruption with a fresh
    realised context and rejection of stale results. Use `sad`, `feminine` and a second fixture idle (`slump`)
    for idle→idle.
28. Actual-rig temporal evidence on both reference characters, in Godot native playback, shows each required
    transition, including an interruption. Numerical seam measurements are diagnostic and do not replace the user's
    judgement of the motion.

## In Scope

- Variant descriptors, template-chain selection and layering, category views and defaults.
- Native idle playback, SOMA locomotion through views, and the bridge transition and interruption contract.
- Kimodo and Rigify idle-clip authoring contracts, the validator, installed fixture and author documentation.
- Shipped `sad` and `feminine` entries, and catalogue clips as the only idle source (TR25).
- Mind tools, prompt fragment, status line and NPC-to-NPC observation under [Mind Integration](mind-integration.md).

## Out Of Scope

- Visual proof of locomotion→locomotion switching with a second reviewed set. The switching mechanism and its
  tests remain required.
- Action animations, gestures, layered or additive animation, and VRIK or player pose playback changes. Built-in
  clips such as `player-pose/Idle.tres` remain outside the catalogue regardless of their storage location.
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
3. **ANIM-003-UAC-003 (UR6–8):** Both reference characters resolve a usable idle and gait. The author-guide and
   installed fixture checks show that an author can compose individual variants with inherited entries and that
   an installed but unselected variant is not available. VRIK and pose behaviour are unchanged.

### Technical Requirement Acceptance

1. **ANIM-003-TAC-001 (TR1–11):** Tests verify:
   - explicit references and descriptor-owned payload resolution (VAR-AC-001–002);
   - inherited add/remove/replace, deterministic order, invalid operations (TR8) and the same id in both categories;
   - preference, default and fallback precedence; inherited, overridden and cleared defaults independent of
     selection; and unknown or incompatible targets, including a default whose entry was removed;
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
3. **ANIM-003-TAC-003 (TR22–26):** [Mind Integration](mind-integration.md), [authoring](authoring.md) and the
   [author guide](variant-format.md#acceptance-criteria) (VAR-AC-003) pass acceptance. A deterministic test shows
   prompt listing and tool acceptance for compatible selected variants only, with refusal for removed or unselected
   variants and the replacement listed once. Both reference templates resolve the documented defaults and order.
   Audit that TR25's prohibited idle sources are absent and that the export include-filter requirement is recorded.
4. **ANIM-003-TAC-004 (TR27–28):** Record VAR-AC-001–002 and audit runtime, scenes, assets, fixtures, tests,
   tooling and docs for a single selection format. Compare installed native idle clip identity,
   track bindings and sampled output, and reviewed gait set identity, eight role bindings, angular metadata and
   sampler outputs against the storage baseline. Review actual-rig evidence independently.

## Implementation Status

The shipped templates select individual variants. Focused checks verify installed inheritance, cross-root addition
and replacement, defaults, validation, selected prompt and tool visibility, and retained native idle and reviewed
gait inputs. These checks do not establish visual quality, new generation, live LLM behaviour or headset operation.

- **Runtime:** native idle playback and MotionBricks bridges are delivered. Individual variant selection changes
  catalogue composition, not playback, bridge or visibility behaviour.
- **Mind:** `change_stance` and `change_gait`, the `Stances And Gaits` prompt section, the own stance and gait status
  line, and `AnimationChangePerception` with first-look and re-focus observations are in place.
- **Removal:** the idle sources TR25 prohibits are gone from scenes, data and tooling. Variant descriptors and
  template operations are the only selection format.

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
- [Animation Variant Format](variant-format.md)
- [Animation Clip Authoring](authoring.md)
- [Mind Integration](mind-integration.md)
- [CORE-008: Content Pack Resolution](../../core/008-content-pack-resolution/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [NAV-001: NPC Navigation](../../nav/001-npc-navigation/index.md)
- [CTRL-001: Player Locomotion](../../ctrl/001-locomotion/index.md)
- [Kimodo Motion Sources](../../tooling/kimodo-pipeline/locomotive-animation-set.md)

[kimodo-reuse]: ../../tooling/kimodo-pipeline/locomotive-animation-set.md#character-setup-and-calibration-contract
