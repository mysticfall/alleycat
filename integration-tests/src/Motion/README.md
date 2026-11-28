# MotionBricks Verification

Current authority: [NAV-001](../../../specs/nav/001-npc-navigation/index.md),
[CTRL-001](../../../specs/ctrl/001-locomotion/index.md),
[ANIM-002](../../../specs/animation/002-locomotive-animation-set/index.md), and
[TEST-001](../../../specs/testing/001-test-framework/index.md).
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
- `MotionBricksIntegrationTests`: independent Kimodo codec/conversion, native generation, real skin-ready
  feedback, realised history and endpoint conversion. `ReviewedMotionSourceIntegrationTests` compares the
  approved take's poses against independently evaluated Blender matrices. The
  [oracle fixtures](../../fixtures/motion-frame-oracles/) are test inputs, not gameplay prerequisites.
- `tests/src/Motion` and retained neutral `tests/src/Navigation`: worker ownership, stale/error handling, facade,
  routing, via passage and truthful terminal outcomes including arrival-sequence versus convergence distinction.
- The two obsolete ANIM-002 production-graph probes in `PreIKRuntimeCompositionOracleIntegrationTests` were retired.
  Its independent mutation/admission checks remain, with a synthetic missing-channel/post-IK-substitution refusal
  check replacing the old graph-dependent refusal fixture. This is not a MotionBricks contact oracle.
- Shared composition, perception, hand/eye/voice and player pose tests remain owned by their respective contracts,
  even when their assertions mention navigation. Their outstanding broad-suite failures are not silently waived.

## Real Model Loading

Only tests that verify generated motion load the real MotionBricks model, and they carry `[IsolatedProcess]` so each
runs in its own Godot process with no other session alive. Fixtures that merely contain MotionBricks nodes (templates,
role installers, real session scenes) install `NativeFreeMotion` for the test's lifetime: every
`MotionBricksNavigation` and `PlayerMotionBricks` entering the tree gets a native-free session factory before its
deferred initialisation, so production wiring stays intact without a CUDA load. Narrower fixtures may set
`SessionFactory` directly. See [`NativeFreeMotion.cs`](NativeFreeMotion.cs) and TEST-001
[Fixture Inference Systems](../../../specs/testing/001-test-framework/index.md#fixture-inference-systems).

Diagnostics are not suitability thresholds. Model contact confidence is not physical contact truth, and calibrated
target joint landmarks are not sole-surface contacts. Keep raw evidence, interpolation identity and limitations.

`game/temp` is disposable. A test must run in isolation without another test or a prior playtest creating its
inputs or output directories. Prefer tracked runtime inputs or authored fixtures, manual fixed simulation steps
and explicit completion signals. Only wait for a Godot frame where an engine-owned boundary requires it.
Authored scenarios do not replay the original playtest recordings or establish displayed motion quality.

## Self-Contained Regression Coverage

- Detour, wall stop, corner and via submission, and near-corner lookahead use dedicated navigation fixtures.
  The corner path and pacing expectations come from the authored geometry, not a recorded path.
- The local-adjustment facts cover both short translation with different facing and near-opposite facing.
  They check useful translation before the turn, bounded excursion, no repeat travel after the turn, and order
  through arrival. The separate saved-boundary and short-consumption probes are not replayed: exact saved packets,
  contact patterns and historical request timing are outside this coverage.
- Finite-turn and overrun use committed source data, manually stepped playback and controlled worker handoffs.
  They cover cue sequencing, cancellation, recovery and genuine provider failure; they do not reproduce the
  original contexts or command timestamps.
- Compatible-turn coverage uses authored changes throughout the original simulation window to test both pending
  and active ownership. A separate stale-worker fact covers incompatible replacement. The duplicate retirement
  check and the pivot/strong-step authoring preflights are not routine integration facts; those preflights do not
  have replacement contact-quality oracles.
- Positioning, repeat floor navigation, mapped lifts and collision keep their behavioural assertions without
  depending on diagnostic output. Recorder and input-trace checks read back files generated by their own fact.
- Skin feedback and actor yaw use a dedicated rig with committed poses. Departure conditioning and explicit
  raw cycles use authored conditions and the committed source. Source adoption checks the committed take identity,
  metadata and articulation, not a staging receipt.

## Independent Frame Oracles

The compressed fixtures are generated using Kimodo 1.0.0 (SOMA77 pose conversions) and Blender 5.3.0 (evaluated
matrices from the approved action and slot). They are checked in so a routine test run does not need either tool.
See the [Kimodo pipeline guide](../../../tools/animation-pipeline/README.md#independent-motion-frame-oracles) for regeneration.
