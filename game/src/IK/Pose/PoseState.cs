using AlleyCat.Control;
using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Abstract Resource base describing a pose state in the VRIK pose state machine.
/// </summary>
/// <remarks>
/// Each state couples its own AnimationTree control and hip reconciliation profile as a single
/// per-state responsibility, per the IK-004 contract. Subclasses override the lifecycle hooks
/// selectively; all hooks are no-ops by default.
/// </remarks>
[GlobalClass]
public abstract partial class PoseState : Resource, IPoseState
{
    private bool _warnedMissingPlayback;

    /// <summary>
    /// Unique identifier for this state (for example <c>Standing</c>).
    /// </summary>
    [Export]
    public StringName Id
    {
        get;
        set;
    } = new();

    string IPoseState.Id => Id.ToString();

    /// <summary>
    /// Hip reconciliation profile bound to this state.
    /// </summary>
    [Export]
    public HipReconciliationProfile? HipReconciliation
    {
        get;
        set;
    }

    /// <summary>
    /// Parameter path for the root AnimationTree state-machine playback object.
    /// </summary>
    [Export]
    public StringName PlaybackParameter
    {
        get;
        set;
    } = new("parameters/playback");

    /// <summary>
    /// Name of the AnimationTree steady-state node owned by this pose state.
    /// </summary>
    [Export]
    public StringName AnimationStateName
    {
        get;
        set;
    } = new();

    /// <summary>
    /// Invoked once when this state is selected as the initial state so it can seed any
    /// state-owned AnimationTree playback.
    /// </summary>
    public virtual void Start(AnimationTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        AnimationNodeStateMachinePlayback? playback = ResolvePlayback(tree);
        if (playback is null || AnimationStateName.IsEmpty)
        {
            return;
        }

        playback.Start(AnimationStateName, true);
    }

    /// <summary>
    /// Invoked once when the state becomes active.
    /// </summary>
    /// <param name="context">Current pose-state context snapshot.</param>
    public virtual void OnEnter(PoseStateContext context)
    {
        // No-op by default.
    }

    /// <summary>
    /// Invoked once when the state is exiting in favour of a different state.
    /// </summary>
    /// <param name="context">Current pose-state context snapshot.</param>
    public virtual void OnExit(PoseStateContext context)
    {
        // No-op by default.
    }

    /// <summary>
    /// Invoked each tick while the state is active.
    /// </summary>
    /// <param name="context">Current pose-state context snapshot.</param>
    public virtual void OnUpdate(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        AnimationTree? tree = context.AnimationTree;
        if (tree is null || !IsAnimationStateActive(context))
        {
            return;
        }

        ApplyAnimation(context);
    }

    /// <summary>
    /// Resolves locomotion permissions contributed by this pose state.
    /// </summary>
    public virtual LocomotionPermissions GetLocomotionPermissions(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return LocomotionPermissions.RotationOnly;
    }

    /// <summary>
    /// Builds an optional debug-overlay line describing this state's current animation-facing
    /// state for play tests. Return <see langword="null"/> to omit animation debug output.
    /// </summary>
    public virtual string? BuildAnimationDebugMessage(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return null;
    }

    /// <summary>
    /// Returns the current AnimationTree state-machine node from the supplied context, or empty
    /// when playback is not available.
    /// </summary>
    protected StringName GetCurrentAnimationState(PoseStateContext context)
        => context.AnimationTree is null ? new StringName() : GetCurrentAnimationState(context.AnimationTree);

    /// <summary>
    /// Returns whether this state's authored steady-state AnimationTree node is currently active
    /// in the supplied context.
    /// </summary>
    protected bool IsAnimationStateActive(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return AnimationStateName.IsEmpty
               || PlaybackParameter.IsEmpty
               || GetCurrentAnimationState(context) == AnimationStateName;
    }

    /// <summary>
    /// Resolves the configured AnimationTree playback object for subclasses.
    /// </summary>
    protected AnimationNodeStateMachinePlayback? ResolvePlayback(AnimationTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        if (PlaybackParameter.IsEmpty)
        {
            return null;
        }

        AnimationNodeStateMachinePlayback? playback =
            tree.Get(PlaybackParameter).As<AnimationNodeStateMachinePlayback>();
        if (playback is null)
        {
            WarnMissingPlayback();
        }

        return playback;
    }

    /// <summary>
    /// Returns the current AnimationTree state-machine node, or empty when playback is not
    /// available.
    /// </summary>
    protected StringName GetCurrentAnimationState(AnimationTree tree) =>
        ResolvePlayback(tree)?.GetCurrentNode() ?? new StringName();

    /// <summary>
    /// Hook for subclasses to drive AnimationTree parameters while their steady-state node is
    /// active.
    /// </summary>
    /// <param name="context">Current pose-state context snapshot.</param>
    protected virtual void ApplyAnimation(PoseStateContext context)
    {
        // No-op by default.
    }

    /// <summary>
    /// Resolves the per-tick hip-reconciliation output for this state.
    /// </summary>
    public virtual HipReconciliationTickResult? ResolveHipReconciliation(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HipReconciliationProfileResult? profileResult = HipReconciliation?.ComputeHipResult(context);
        return profileResult is null ? null : ApplyHipReconciliation(context, profileResult);
    }

    /// <summary>
    /// Applies this state's default hip-limit behaviour to the supplied profile result.
    /// </summary>
    protected virtual HipReconciliationTickResult ApplyHipReconciliation(
        PoseStateContext context,
        HipReconciliationProfileResult profileResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(profileResult);

        HipLimitFrame limitFrame = BuildHipLimitFrame(context);
        Transform3D skeletonGlobalTransform = context.Skeleton?.GlobalTransform ?? Transform3D.Identity;

        return ApplyHipLimitFrame(
            profileResult,
            limitFrame,
            context.RestHeadHeight,
            skeletonGlobalTransform);
    }

    /// <summary>
    /// Resolves the per-tick hip-limit reference and envelope for this state.
    /// </summary>
    public virtual HipLimitFrame BuildHipLimitFrame(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new HipLimitFrame
        {
            ReferenceHipLocalPosition = ResolveDefaultHipLocalReference(context),
        };
    }

    /// <summary>
    /// Applies the supplied limit frame to a profile result and derives any limited head target.
    /// </summary>
    public static HipReconciliationTickResult ApplyHipLimitFrame(
        HipReconciliationProfileResult profileResult,
        HipLimitFrame limitFrame,
        float restHeadHeight,
        Transform3D skeletonGlobalTransform)
    {
        ArgumentNullException.ThrowIfNull(profileResult);
        ArgumentNullException.ThrowIfNull(limitFrame);

        Vector3 desiredFinalHipOffset = profileResult.DesiredHipLocalPosition - limitFrame.ReferenceHipLocalPosition;
        Vector3 appliedHipLocalPosition = limitFrame.AbsoluteBounds?.ClampPosition(profileResult.DesiredHipLocalPosition)
            ?? (limitFrame.OffsetEnvelope.HasValue
                ? limitFrame.ReferenceHipLocalPosition
                  + limitFrame.OffsetEnvelope.Value.ClampOffset(desiredFinalHipOffset, restHeadHeight)
                : profileResult.DesiredHipLocalPosition);
        Vector3 appliedFinalHipOffset = appliedHipLocalPosition - limitFrame.ReferenceHipLocalPosition;

        Transform3D? limitedHeadTargetTransform = appliedFinalHipOffset.IsEqualApprox(desiredFinalHipOffset)
            ? null
            : profileResult.HeadTargetLimit?.CreateLimitedHeadTargetTransform(
                appliedHipLocalPosition,
                skeletonGlobalTransform);

        return new HipReconciliationTickResult
        {
            AppliedHipLocalPosition = appliedHipLocalPosition,
            DesiredFinalHipOffset = desiredFinalHipOffset,
            AppliedFinalHipOffset = appliedFinalHipOffset,
            LimitedHeadTargetTransform = limitedHeadTargetTransform,
        };
    }

    /// <summary>
    /// Resolves the default hip-reference position from the hip bone rest pose when available.
    /// </summary>
    protected static Vector3 ResolveDefaultHipLocalReference(PoseStateContext context)
    {
        Skeleton3D? skeleton = context.Skeleton;
        int hipBoneIndex = context.HipBoneIndex;

        return skeleton is not null && hipBoneIndex >= 0 && hipBoneIndex < skeleton.GetBoneCount()
            ? skeleton.GetBoneGlobalRest(hipBoneIndex).Origin
            : Vector3.Zero;
    }

    private void WarnMissingPlayback()
    {
        if (_warnedMissingPlayback)
        {
            return;
        }

        GD.PushWarning(
            $"{GetType().Name} could not resolve playback object at '{PlaybackParameter}'.");
        _warnedMissingPlayback = true;
    }
}
