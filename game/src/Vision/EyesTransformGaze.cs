using Godot;

namespace AlleyCat.Vision;

/// <summary>
/// Applies the shared VISION-001 gaze rotation to transform-driven eye nodes. Each eye's authored
/// neutral local transform is captured on construction and every frame's rotation is rebuilt from
/// that neutral, so consecutive gaze updates never accumulate drift.
/// </summary>
/// <remarks>
/// Positive non-uniform scale on the eye origin, the eyes' parents, or the eyes themselves is safe:
/// parent-frame transfer uses orthonormalised rotation only, and the captured neutral basis carries
/// the eye's authored scale through verbatim. Negative scale and skew are not supported.
/// </remarks>
internal sealed class EyesTransformGaze(Node3D leftEye, Node3D rightEye)
{
    private readonly Transform3D _leftEyeNeutralLocalTransform = CaptureNeutralTransform(leftEye);
    private readonly Transform3D _rightEyeNeutralLocalTransform = CaptureNeutralTransform(rightEye);

    /// <summary>
    /// Restores both eyes' authored neutral local transforms, releasing transform-gaze ownership.
    /// </summary>
    public void RestoreNeutralTransforms()
    {
        ApplyNeutralTransform(leftEye, _leftEyeNeutralLocalTransform);
        ApplyNeutralTransform(rightEye, _rightEyeNeutralLocalTransform);
    }

    /// <summary>
    /// Applies the eye-origin-frame gaze delta to both eyes for the current frame. The delta is
    /// transferred into each eye's current parent frame and applied identically to both eyes, so
    /// they rotate as one coordinated unit without vergence.
    /// </summary>
    /// <param name="eyeOriginGlobalTransform">The world transform of the shared eye origin.</param>
    /// <param name="gazeDeltaEyeOrigin">The smoothed, clamped gaze rotation in the eye-origin frame.</param>
    public void ApplyGaze(Transform3D eyeOriginGlobalTransform, Quaternion gazeDeltaEyeOrigin)
    {
        ApplyGaze(leftEye, _leftEyeNeutralLocalTransform, eyeOriginGlobalTransform, gazeDeltaEyeOrigin);
        ApplyGaze(rightEye, _rightEyeNeutralLocalTransform, eyeOriginGlobalTransform, gazeDeltaEyeOrigin);
    }

    private static void ApplyGaze(
        Node3D eye,
        Transform3D neutralLocalTransform,
        Transform3D eyeOriginGlobalTransform,
        Quaternion gazeDeltaEyeOrigin)
    {
        if (!IsValidEye(eye))
        {
            return;
        }

        // Transfer the shared eye-origin-frame delta into the eye's current parent frame. The local
        // transform stays relative to the animated parent, so no global neutral is ever cached.
        Quaternion parentRotation = eye.GetParentOrNull<Node3D>()?.GlobalTransform.Basis.GetRotationQuaternion()
            ?? Quaternion.Identity;
        Quaternion eyeOriginRotation = eyeOriginGlobalTransform.Basis.GetRotationQuaternion();
        Quaternion parentFrameTransfer = parentRotation.Inverse() * eyeOriginRotation;
        Quaternion gazeDeltaParentFrame = parentFrameTransfer * gazeDeltaEyeOrigin * parentFrameTransfer.Inverse();

        // Pre-multiplying the neutral basis applies the gaze rotation about parent-frame axes while
        // preserving the authored neutral position, orientation relative to the delta, and scale.
        eye.Transform = new Transform3D(
            new Basis(gazeDeltaParentFrame) * neutralLocalTransform.Basis,
            neutralLocalTransform.Origin);
    }

    private static void ApplyNeutralTransform(Node3D eye, Transform3D neutralLocalTransform)
    {
        if (IsValidEye(eye))
        {
            eye.Transform = neutralLocalTransform;
        }
    }

    private static Transform3D CaptureNeutralTransform(Node3D eye)
        => eye is null
            ? throw new ArgumentNullException(nameof(eye))
            : eye.Transform;

    private static bool IsValidEye(Node3D eye)
        => GodotObject.IsInstanceValid(eye) && eye.IsInsideTree();
}
