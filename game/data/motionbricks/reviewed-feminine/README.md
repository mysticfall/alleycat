# Shared Feminine Source Set

Normal CTRL/NAV source data; no activation switches are required.

`set.json` binds all eight roles to `landing-ablation-01.json`, a maintained Blender export of the complete
1200-frame, 30 Hz `source_feminine_landing_ablation_01` action. Durable source/authoring, descriptor, user approval
and provenance are under `game/assets/characters/animations/source/locomotion/locomotion_feminine_landing_ablation_01*`.
Source SHA256: `3da77b8ab373dc503b789b8377d3023ea2496eab34086ef91e400a31f68de80a`.

## Selections

| Role | Inclusive Usable/Reference Range | Half-Open Repetition | Period |
| --- | --- | --- | --- |
| forward | 46–106 | [58,94) | 36 |
| backward | 196–256 | [207,246) | 39 |
| left_noncross | 346–406 | [346,376) | 30 |
| right_noncross | 496–556 | [496,526) | 30 |
| left_cross | 646–706 | [646,676) | 30 |
| right_cross | 796–856 | [796,826) | 30 |
| left_turn | 946–1006 | [946,1006) | 60 |
| right_turn | 1096–1156 | [1096,1156) | 60 |

`turn-angular-reference.json` binds actual exported facing/physical references and typed source landmarks.
Left: 947→951→961→1001; right: 1103→1107→1120→1152. Toe peaks are same-foot annotations, not additional endpoint
promises. Preserve chronology and the authored late reversal; neither turn is assumed to span exactly 90°.

## Publication and Limits

Use the [maintained ingestion/set publishers](../../../../tools/kimodo-pipeline/README.md#reviewed-source-ingestion).
Export new bundles first. Validate bundle/set/angular metadata together, then coordinate fixed set/angular
replacement with consumers stopped and rollback available. Single-file atomic replacement is not multi-file
hot publication.

Source stepping/foot-sliding mitigation is user accepted; initial twitch is deferred. Sources remain non-cyclic
and raw repetitions `EXPERIMENTAL_NOT_ADMITTED`: these labels describe quality admission, not activation flags.
No seamless-loop, sole-contact, generated-motion naturalness or no-slip claim follows.
