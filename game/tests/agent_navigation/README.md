# Agent Navigation Test Scene

Reference-character scene for the AgenticMind coordinate-navigation tools: a 16 m × 16 m navigable floor, a
reference-male companion, and labelled minimap landmarks at known world coordinates. The reference-female subject is
not authored in the scene: the integration tests add a minimal test-only subject (see Subject below).

## Layout

| Node | Kind | World (x, z) | Notes |
| --- | --- | --- | --- |
| `Subject` | `char:reference_female`, added by the tests | (1.0, 1.5) | Faces bearing 200°. |
| `Companion` | `char:reference_male` (NPC template) | (−1.0, −3.5) | Faces the subject. |
| `Landmarks/Crate` | Obstacle, 1 m box | (4.5, −2.0) | Labelled "Crate". |
| `Landmarks/Lamp` | Obstacle, post | (4.5, 4.0) | Labelled "Lamp". |
| `Landmarks/Bench` | Obstacle, 1.6 m × 0.5 m | (−2.5, 4.5) | Labelled "Bench". |
| `Landmarks/Rug` | Walkable floor marking | (−2.5, −1.0) | Labelled "Rug". |
| `Landmarks/Wall` | Obstacle, 3 m × 0.3 m | (1.0, −1.2) | Unlabelled. |

Bearings are compass bearings: 0° is map up (−Z), 90° is right (+X), clockwise.

## Subject

`integration-tests/src/Mind/AI/AgentNavigationSubject.cs` builds the subject in test code and adds it to the scene
root before tree entry. It keeps only what the navigation tests need: the `reference_female.blend` body (model and
`AnimationPlayer`), `MotionBricksNavigation`, a perception-free `AgenticMind`, the actor `MiniMapMarker`, a collision
sphere, and an inert `NPCCharacterInstaller` whose presence makes `Character` mount MotionBricks through its
production hook. Its component projection exposes navigation only, so voice, hearing, eyes, hands, IK, perceptions
and gaze cannot affect the tests.

## Files

- `agent_navigation.tscn`: the scene (companion, minimap markers, rug, environment).
- `arena.tscn`: the `NavigationRegion3D` with the floor and obstacle colliders.
- `arena_navmesh.tres`: the baked navigation mesh.
- `bake_navmesh.gd`: rebakes the navigation mesh after `arena.tscn` changes, snapping the flat arena's walkable
  surface to the floor top (y = 0) so NavigationAgent3D's 3D waypoint and arrival distances match the character:

  ```bash
  godot-mono --headless --path game --xr-mode off -s res://tests/agent_navigation/bake_navmesh.gd
  ```

`integration-tests/src/Mind/AI/CoordinateNavigationWalkIntegrationTests.cs` drives the production `walk_to`,
`turn_to` and `stop` tools on the subject (AI-015 TR-20) and writes per-frame traces to
`res://temp/agent-nav-tools/walk-tests/`.

The coordinates above are also declared in
`integration-tests/src/Mind/AI/Navigation/CoordinateNavigationGate.cs` (the AI-015 TR-21 task set); the scene check in
`CoordinateNavigationFinalGateTests.cs` fails if they drift.
