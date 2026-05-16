using Godot;

namespace AlleyCat.IK;

/// <summary>
/// World-space IK target intent requested by an <see cref="IKTargetIntentProvider" />.
/// </summary>
/// <param name="WorldTransform">Target transform in world space.</param>
/// <param name="DesiredInfluence">Desired influence for the corresponding IK modifier.</param>
public readonly record struct IKTargetIntent(Transform3D WorldTransform, float DesiredInfluence = 1.0f);
