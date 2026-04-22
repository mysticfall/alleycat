using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Animation binding that drives the standing-crouching seek clip through an
/// <c>AnimationNodeTimeSeek</c> and (optionally) travels the enclosing
/// <c>AnimationNodeStateMachine</c> to a configured target state while the owning
/// <see cref="PoseState"/> is active.
/// </summary>
[GlobalClass]
public partial class StandingCrouchingSeekAnimationBinding : AnimationBinding
{
    private const float RestHeadHeightFloor = 1e-3f;
    private const float RatioFloor = 1e-3f;

    private bool _warnedMissingTree;
    private bool _warnedMissingSeekPath;
    private bool _warnedMissingPlayback;

    /// <summary>
    /// Full parameter path of the <see cref="AnimationNodeTimeSeek"/> <c>seek_request</c>
    /// property inside the AnimationTree (for example
    /// <c>parameters/StandingCrouching/TimeSeek/seek_request</c>).
    /// </summary>
    [Export]
    public StringName SeekRequestParameter
    {
        get;
        set;
    } = new();

    /// <summary>
    /// Parameter path for the root state-machine playback object (for example
    /// <c>parameters/playback</c>).
    /// </summary>
    [Export]
    public StringName PlaybackParameter
    {
        get;
        set;
    } = new("parameters/playback");

    /// <summary>
    /// Name of the AnimationTree state-machine state that must be active while the owning
    /// <see cref="PoseState"/> is active.
    /// </summary>
    [Export]
    public StringName TargetStateName
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
    /// <remarks>
    /// Computed as:
    /// <c>pose_blend = clamp((rest.Y - current.Y) / (RestHeadHeight * FullCrouchDepthRatio), 0, 1)</c>.
    /// Non-positive values are floored to avoid divide-by-zero and NaN propagation.
    /// </remarks>
    [Export]
    public float FullCrouchDepthRatio
    {
        get;
        set;
    } = 0.375f;

    /// <inheritdoc />
    public override void Apply(AnimationTree tree, PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (tree is null)
        {
            if (!_warnedMissingTree)
            {
                GD.PushWarning(
                    $"{nameof(StandingCrouchingSeekAnimationBinding)}.{nameof(Apply)} called with null AnimationTree; skipping.");
                _warnedMissingTree = true;
            }

            return;
        }

        float poseBlend = ComputePoseBlend(
            context.HeadTargetRestTransform.Origin.Y,
            context.HeadTargetTransform.Origin.Y,
            context.RestHeadHeight,
            FullCrouchDepthRatio);

        WriteSeekRequest(tree, poseBlend);
        MaybeTravel(tree);
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

    private void WriteSeekRequest(AnimationTree tree, float poseBlend)
    {
        if (SeekRequestParameter.IsEmpty)
        {
            if (!_warnedMissingSeekPath)
            {
                GD.PushWarning(
                    $"{nameof(StandingCrouchingSeekAnimationBinding)}.{nameof(SeekRequestParameter)} is empty; skipping seek writes.");
                _warnedMissingSeekPath = true;
            }

            return;
        }

        float seekTime = Mathf.Clamp(poseBlend, 0f, 1f) * ClipDurationSeconds;
        tree.Set(SeekRequestParameter, seekTime);
    }

    private void MaybeTravel(AnimationTree tree)
    {
        if (PlaybackParameter.IsEmpty || TargetStateName.IsEmpty)
        {
            return;
        }

        AnimationNodeStateMachinePlayback? playback =
            tree.Get(PlaybackParameter).As<AnimationNodeStateMachinePlayback>();
        if (playback is null)
        {
            if (!_warnedMissingPlayback)
            {
                GD.PushWarning(
                    $"{nameof(StandingCrouchingSeekAnimationBinding)} could not resolve playback object at '{PlaybackParameter}'.");
                _warnedMissingPlayback = true;
            }

            return;
        }

        playback.Travel(TargetStateName);
    }

    /// <summary>
    /// Pure travel-decision helper exposed for unit coverage of the unconditional travel
    /// semantics introduced after the Standing → Crouching → Standing round-trip regression.
    /// </summary>
    /// <param name="currentTargetName">
    /// The AnimationTree state-machine state the binding wants to travel to this tick.
    /// </param>
    /// <param name="lastTravelled">
    /// The target previously travelled to (unused by the unconditional semantic but retained so
    /// tests can explicitly show round-trip state no longer influences travel requests).
    /// </param>
    /// <returns><see langword="true"/> when a travel should be requested this tick.</returns>
    public static bool ShouldTravel(string? currentTargetName, string? lastTravelled)
    {
        _ = lastTravelled;
        return !string.IsNullOrEmpty(currentTargetName);
    }
}
