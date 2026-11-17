# MotionBricks Model Setup

Provision the separately licensed NVIDIA SOMA FP32 model as `onnx/mmm_v6_soma_fp32.onnx` in the root project's
`game/models/motionbricks/` directory. Obtain it from <https://github.com/NVlabs/MotionBricks> under the NVIDIA Open
Model Agreement; the runtime's Apache-2.0 licence does not license these weights. Never commit model payloads.

Copy the model from your upstream checkout; do not move or modify that source. The root project owns this local copy.
OpenChamber's `setupWorktree` links each worktree's `onnx` directory to
`$ROOT_PROJECT_PATH/game/models/motionbricks/onnx`, matching Supertonic's shared-payload convention.
Provision the root payload before creating worktrees; existing worktrees need the same link installed once.
Do not link the root payload back into a worktree or copy weights per worktree.

The exported `MotionBricksNavigation.ModelPath` defaults to `res://models/motionbricks/onnx/mmm_v6_soma_fp32.onnx`.
A non-blank `MOTIONBRICKS_MODEL` overrides it; unset, empty or whitespace values use the exported property.
Both paths accept `res://` or an absolute native path and are globalised on the Godot scene thread before worker IO.
Missing files fail explicitly; there is no developer-specific fallback. This is source-checkout playtest setup, not
an exported-PCK deployment contract. See [Runtime Setup](../../src/Motion/README.md#launch).
