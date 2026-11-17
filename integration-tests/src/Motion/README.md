# MotionBricks Verification

Current authority: [NAV-001](../../../specs/nav/001-npc-navigation/index.md),
[Shared Animation](../../../specs/animation/index.md), and CHAR-002 role separation.
Do not restore NPCIdle/NPCWalkF/PlayerTravel clip-state assumptions or endpoint quality gates.

## Focused Commands

From the repository root, after normal Godot import and game build:

```sh
dotnet test tests/AlleyCat.Tests.csproj --filter 'FullyQualifiedName~AlleyCat.Tests.Motion|FullyQualifiedName~AlleyCat.Tests.Navigation'
MOTIONBRICKS_MODEL="$PWD/game/models/motionbricks/onnx/mmm_v6_soma_fp32.onnx" \
MOTIONBRICKS_FRAMES="$PWD/game/data/motionbricks/model-frames.json" \
xvfb-run -a dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- \
 --test-class AlleyCat.IntegrationTests.Motion.MotionBricksIntegrationTests,AlleyCat.IntegrationTests.Motion.MotionBricksDeliveryIntegrationTests
```

The diagnostic inference fixture takes explicit model/frame paths; ordinary playtests use the exported model default.
No preload is required by these instructions. These integration classes are explicitly headless-compatible; visual scene
runs must use a renderer. Full integration runs are reserved for final handoff readiness, not each iteration.

## Coverage and Boundaries

- `MotionBricksDeliveryIntegrationTests`: supported Female NPC owns generated navigation, not player locomotion;
  player receives no generated navigation; real runtime data loads approved windows and named model frames without
  diagnostic paths. No scene-tree entry or native inference is needed for these resource contracts.
- `MotionBricksIntegrationTests`: independent executed Kimodo codec/conversion and Blender anatomical oracle,
  native generation, real skin-ready feedback, realised history and endpoint conversion. The independent oracle
  fixtures under `game/temp/motionbricks` are deliberately preserved test evidence, not gameplay prerequisites.
- `tests/src/Motion` and retained neutral `tests/src/Navigation`: worker ownership, stale/error handling, facade,
  routing, via passage and truthful terminal outcomes including arrival-sequence versus convergence distinction.
- Full normal-scheduling existing-scene runs under `game/temp/motionbricks/focused-final-01` cover initial idle,
  departure, sustained movement, detour, every hold, arrival and at least three seconds idle. Contact observer reads
  the final displayed target pose; it never supplies pose inputs, advances skeletons or corrects feet.
- The two obsolete ANIM-002 production-graph probes in `PreIKRuntimeCompositionOracleIntegrationTests` were retired.
  Its independent mutation/admission checks remain, with a synthetic missing-channel/post-IK-substitution refusal
  check replacing the old graph-dependent refusal fixture. This is not a MotionBricks contact oracle.
- Shared composition, perception, hand/eye/voice and player pose tests remain owned by their respective contracts,
  even when their assertions mention navigation. Their outstanding broad-suite failures are not silently waived.

Diagnostics are not suitability thresholds. Model contact confidence is not physical contact truth, and calibrated
target joint landmarks are not sole-surface contacts. Keep raw evidence, interpolation identity and limitations.
