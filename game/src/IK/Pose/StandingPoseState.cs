using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Concrete pose state representing the standing-to-crouching continuum.
/// </summary>
/// <remarks>
/// The standing pose family owns the full standing-to-crouching range through a single framework-
/// level state. The state itself scrubs the shared <c>StandingCrouching</c> AnimationTree state
/// across that continuum, while
/// <see cref="HeadTrackingHipProfile"/> provides the matching hip reconciliation behaviour.
/// </remarks>
[GlobalClass]
public partial class StandingPoseState : PoseState
{
    private const float RestHeadHeightFloor = 1e-3f;
    private const float RatioFloor = 1e-3f;

    private bool _warnedMissingSeekPath;

    /// <summary>
    /// Canonical identifier used by <see cref="StandingPoseState"/>.
    /// </summary>
    public static readonly StringName DefaultId = new("Standing");

    /// <summary>
    /// Default steady-state AnimationTree node used by the standing continuum.
    /// </summary>
    public static readonly StringName DefaultAnimationStateName = new("StandingCrouching");

    /// <summary>
    /// Full parameter path of the <see cref="AnimationNodeTimeSeek"/> <c>seek_request</c>
    /// property inside the AnimationTree.
    /// </summary>
    [Export]
    public StringName SeekRequestParameter
    {
        get;
        set;
    } = new();

    /// <summary>
    /// Duration in seconds of the standing-crouching seek clip.
    /// </summary>
    [Export]
    public float ClipDurationSeconds
    {
        get;
        set;
    } = 1.0f;

    /// <summary>
    /// Full-crouch head-descent ratio relative to <see cref="PoseStateContext.RestHeadHeight"/>.
    /// </summary>
    [Export]
    public float FullCrouchDepthRatio
    {
        get;
        set;
    } = 0.375f;

    /// <summary>
    /// Initialises the state and seeds <see cref="PoseState.Id"/> with <see cref="DefaultId"/>.
    /// </summary>
    public StandingPoseState()
    {
        Id = DefaultId;
        AnimationStateName = DefaultAnimationStateName;
    }

    /// <summary>
    /// Computes the normalised 0..1 standing-crouching pose blend from the rest and current head Y
    /// values, normalised by rest head height.
    /// </summary>
    public static float ComputePoseBlend(
        float restHeadY,
        float currentHeadY,
        float restHeadHeight,
        float fullCrouchDepthRatio)
    {
        float safeRestHeadHeight = restHeadHeight > RestHeadHeightFloor
            ? restHeadHeight
            : 1f;
        float safeFullCrouchDepthRatio = fullCrouchDepthRatio > RatioFloor
            ? fullCrouchDepthRatio
            : RatioFloor;
        float safeFullCrouchDepthMetres = safeRestHeadHeight * safeFullCrouchDepthRatio;

        float descent = restHeadY - currentHeadY;
        float ratio = descent / safeFullCrouchDepthMetres;
        return float.IsFinite(ratio) ? Mathf.Clamp(ratio, 0f, 1f) : 0f;
    }

    /// <inheritdoc />
    public override void Start(AnimationTree tree)
    {
        base.Start(tree);
        WriteSeekRequest(tree, poseBlend: 0f);
    }

    /// <inheritdoc />
    protected override void ApplyAnimation(PoseStateContext context)
    {
        if (context.AnimationTree == null)
        {
            return;
        }

        float poseBlend = ComputePoseBlend(
            context.HeadTargetRestTransform.Origin.Y,
            context.HeadTargetTransform.Origin.Y,
            context.RestHeadHeight,
            FullCrouchDepthRatio);

        WriteSeekRequest(context.AnimationTree, poseBlend);
    }

    private void WriteSeekRequest(AnimationTree tree, float poseBlend)
    {
        if (SeekRequestParameter.IsEmpty)
        {
            if (!_warnedMissingSeekPath)
            {
                GD.PushWarning(
                    $"{nameof(StandingPoseState)}.{nameof(SeekRequestParameter)} is empty; skipping seek writes.");
                _warnedMissingSeekPath = true;
            }

            return;
        }

        float seekTime = Mathf.Clamp(poseBlend, 0f, 1f) * ClipDurationSeconds;
        tree.Set(SeekRequestParameter, seekTime);
    }
}
