# Shared Motion Runtime

CTRL and NAV normally use the same [feminine source set](../../data/motionbricks/reviewed-feminine/README.md).
No source/controller activation flags are needed. NPC idles are native catalogue clips (ANIM-003); moving and
stationary-turn roles come from `ReviewedRoleSampler`, not fixed prototype walk windows.

## Files and Attribution

- `ReviewedMotionSource`: calibrated full chronological poses, physical references and explicit raw repetitions.
- `ReviewedRoleSampler`: eight-role source binding, phase, raw SE2 accumulation and source-preserving placement.
- `StationaryTurnReference`: hash/bounds/angle-bound primary and recovery landmarks; joint origins are not contact.
- `SomaMotionData`/`SomaModelFrames`: canonical poses/calibration and source-authoritative BONES frame conversion.
- `SomaInference`: C# adaptation of NVIDIA MotionBricks `model/soma/kernels.py`, conditioning masks in
  `model/kernels.py` and endpoint transforms in `utils/pose_utils.py`.
- `NativeSomaRig`: absolute canonical source writer and post-modifier target readback.
- `RealisedPoseHistory`: four 30 Hz context samples from displayed post-modifier observations.
- `SomaInferenceWorker`: per-consumer handle with latest-request ownership and codec state over the shared
  `SomaNativeSession` backend (CORE-011), which the Game-owned shared model registry loads once per model key and
  disposes at shutdown.

Adapted MotionBricks software: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES, Apache-2.0.
The original licence is retained in `LICENSE-MotionBricks`. This is modified software, not an official NVIDIA
implementation. Source: <https://github.com/NVlabs/MotionBricks>. Models are separately licensed under the NVIDIA
Open Model Agreement and locally provisioned. No evaluation-only upstream motion samples are distributed.

## Launch

Provision the [model](../../models/motionbricks/README.md), calibration and model-frame exports using
the [maintained tooling](../../../tools/animation-pipeline/README.md). Ordinary gameplay executes no Python or Blender.
Runtime JSON is self-contained; embedded source/evidence paths are provenance, not runtime dependencies.

```sh
dotnet build AlleyCat.sln -warnaserror
godot-mono --xr-mode off --path game res://assets/testing/navigation/navigation_test.tscn -- --no-ai
```

Use normal Godot asset import, never recovery mode. Neither a runtime source migration nor a copied import cache
is a licence to regenerate source motion. `MOTIONBRICKS_MODEL` optionally overrides the local FP32 model path.

## Controller Boundaries

[Player Motion](../Control/PLAYER-MOTION.md) owns current stick intent, source-guided travel and source-landmark
stationary turns. NAV samples the Godot NavigationAgent3D route with measured source rates and native-only
handoff, including request-local overrun handling. Finite terminal turns service both ordered primary cues
per allocation, with source-derived angular mapping, before arrival. There is no procedural actor yaw repair,
custom routing, predicted-physics admission or output foot correction.

Generated root translation uses `MoveAndCollide`. History comes from actual post-`SkeletonUpdated` state,
not generated endpoints. Cancellation/replacement invalidates ownership and stale results. Live XR/VRIK and
non-root pose placement retain their existing authority. Native calls are not forcibly interrupted.
See [Navigation Recording](../Navigation/Recording/README.md) for feedback and truthful failure records.

## Remaining Validation

This is calibrated Female content, not a complete multi-variant quality admission. The source improvement is
accepted with initial twitch deferred. Raw repetitions, generated gait, contact, inference holds and headset
comfort remain provisional. Four target poses do not promise a whole displayed step or fixed native duration.
Known native shutdown and physical-input/rendered-origin risks remain open. Mechanism tests and synthetic
endpoint fixtures do not certify real-model motion or visual correctness.
