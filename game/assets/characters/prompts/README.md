# Shared NPC Prompt Stacks

- `current_scene.tres` binds the `own-stance-and-gait` projection, so every `AgenticMind` that uses it needs an
  `OwnAnimationSceneStatusProjector` direct child (the NPC templates author one); for a character without an animation
  catalogue the projection is empty and its section renders nothing.
