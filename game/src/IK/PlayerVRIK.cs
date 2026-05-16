using System.Globalization;
using System.Text;
using AlleyCat.Body;
using AlleyCat.IK.Pose;
using AlleyCat.UI;
using AlleyCat.XR;
using Godot;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.IK;

/// <summary>
/// Runtime bridge that drives player IK targets from XR tracking data.
/// </summary>
[GlobalClass]
public partial class PlayerVRIK : CharacterIK
{
    private const float HeightEpsilon = 1e-4f;
    private static readonly StringName _rightHandBoneName = new("RightHand");
    private static readonly StringName _leftHandBoneName = new("LeftHand");
    private static readonly StringName _rightLowerArmBoneName = new("RightLowerArm");
    private static readonly StringName _leftLowerArmBoneName = new("LeftLowerArm");

    /// <summary>
    /// When enabled, shows per-side hip clamp residuals in the debug overlay during play tests.
    /// </summary>
    [ExportGroup("Debug")]
    [Export]
    public bool HipClampDebugOutputEnabled
    {
        get;
        set;
    }

    /// <summary>
    /// When enabled, shows current hip and reference positions for standing-reference tuning.
    /// </summary>
    [Export]
    public bool HipPositionReferenceDebugOutputEnabled
    {
        get;
        set;
    }

    /// <summary>
    /// When enabled, shows forward/back seam instrumentation for clamp, limited-head, and XR-origin correlation.
    /// </summary>
    [Export]
    public bool HipForwardBackSeamDebugOutputEnabled
    {
        get;
        set;
    }

    /// <summary>
    /// When enabled, shows general pose-state animation debug lines in the debug overlay.
    /// </summary>
    [Export]
    public bool AnimationDebugOutputEnabled
    {
        get;
        set;
    }

    /// <summary>
    /// Pose state machine driven from the XR bridge. When set, <c>_Process</c> builds a
    /// <see cref="PoseStateContext"/> per tick and invokes <see cref="PoseStateMachine.Tick"/>
    /// so hip reconciliation and animation bindings observe the same snapshot as the modifier
    /// pipeline. Leave unset to disable the pose-state layer entirely.
    /// </summary>
    [ExportGroup("Pose")]
    [Export]
    public PoseStateMachine? PoseStateMachine
    {
        get;
        set;
    }

    /// <summary>
    /// Bone name used to resolve the hip-bone index supplied to the <see cref="PoseStateContext"/>.
    /// </summary>
    [ExportGroup("Skeleton")]
    [Export]
    public StringName HipBoneName
    {
        get;
        set;
    } = new("Hips");

    /// <summary>
    /// Resolved hip-bone index supplied to the pose-state context. <c>-1</c> when unresolved.
    /// </summary>
    public int HipBoneIndex
    {
        get;
        private set;
    } = -1;

    private IXROrigin? _origin;
    private IXRCamera? _camera;
    private bool _isBound;
    private bool _isDrivingDebugOverlay;
    private float _worldScale = 1.0f;
    private Vector3 _mostRecentOriginCompensationDelta = Vector3.Zero;

    private readonly FingerCollisionShapeMirror _rightFingerCollisionShapeMirror = new("Right");
    private readonly FingerCollisionShapeMirror _leftFingerCollisionShapeMirror = new("Left");
    private readonly PoseStateContextBuilder _poseContextBuilder = new();
    private readonly StringBuilder _debugMessageBuilder = new();

    /// <inheritdoc />
    public override void _Ready()
    {
        base._Ready();
        ConfigurePlayerBodyCollisionExceptions();
        ConfigureGeneratedTargetProxyCollisionExceptionsDeferred();
    }

    /// <inheritdoc />
    protected override bool CanProcessProviderTargets => _isBound;

    /// <inheritdoc />
    protected override bool CanProcessPhysicalActuators => _isBound;

    /// <inheritdoc />
    protected override void BeforeProviderTargetProcessing()
    {
        if (_origin is IXROrigin origin)
        {
            origin.OriginNode.GlobalTransform = GlobalTransform;
        }
    }

    /// <inheritdoc />
    protected override void BeforeHandTargetActuators()
    {
        DynamicPhysicalRig? rig = ResolveConfiguredPhysicalRig();
        if (rig is null)
        {
            return;
        }

        rig.SyncProxyBodiesToPhysics();
        _rightFingerCollisionShapeMirror.Sync(rig, _rightHandBoneName, ResolvedRightHandIKTarget);
        _leftFingerCollisionShapeMirror.Sync(rig, _leftHandBoneName, ResolvedLeftHandIKTarget);
    }

    /// <inheritdoc />
    protected override void AfterProviderTargetProcessing(Skeleton3D skeleton, double delta)
    {
        PoseStateMachine? stateMachine = PoseStateMachine;
        if (stateMachine is null || !stateMachine.Active)
        {
            ClearHipDebugMessage();
            return;
        }

        PoseStateContext context = BuildPoseStateContext(skeleton, delta);
        PoseStateMachineTickResult tickResult = stateMachine.Tick(context);
        ApplyHeadSolveTargetTransform(tickResult.LimitedHeadTargetTransform);
        UpdateHipDebugMessage(tickResult.Context, tickResult);
    }

    /// <inheritdoc />
    protected override void AfterEndStage(Skeleton3D skeleton, double delta)
    {
        if (_origin is null || _camera is null)
        {
            return;
        }

        Transform3D compensatedOriginTransform = ComputeCompensatedOriginTransform(
            skeleton,
            _camera.CameraNode,
            _origin.OriginNode);

        _origin.OriginNode.GlobalTransform = compensatedOriginTransform;
    }

    /// <inheritdoc />
    protected override Transform3D GetDefaultHeadTargetTransform()
        => _camera is null
            ? ResolvedHeadIKTarget?.GlobalTransform ?? Transform3D.Identity
            : _camera.CameraNode.GlobalTransform * ViewpointLocalInverseTransform;

    /// <inheritdoc />
    // HipBoneIndex may remain -1 when the skeleton does not expose a hips bone; consumers
    // (for example HipReconciliationModifier) already guard for the unresolved case.
    protected override void EnsureSubclassResolvedNodes(Skeleton3D skeleton)
        => HipBoneIndex = skeleton.FindBone(HipBoneName);

    /// <summary>
    /// Resolves XR runtime abstractions through global services once and calibrates world scale.
    /// </summary>
    public virtual bool BindToXRServices()
    {
        XRManager xrManager = Game.Instance.GetRequiredService<XRManager>();
        IXRRuntime runtime = xrManager.Runtime;
        return BindToXRRuntime(runtime.Origin, runtime.Camera);
    }

    /// <summary>
    /// Binds XR origin and camera abstractions once and calibrates world scale.
    /// </summary>
    public bool BindToXRRuntime(IXROrigin origin, IXRCamera camera)
    {
        if (_isBound)
        {
            return true;
        }

        _origin = origin;
        _camera = camera;
        EnsureResolvedNodes();
        ConfigureGeneratedTargetProxyCollisionExceptionsDeferred();

        CalibrateWorldScaleOnce();
        _isBound = true;
        return true;
    }

    private void ConfigureGeneratedTargetProxyCollisionExceptionsDeferred()
        => _ = CallDeferred(nameof(ConfigureGeneratedTargetProxyCollisionExceptions));

    private void CalibrateWorldScaleOnce()
    {
        IXROrigin origin = _origin ?? throw new InvalidOperationException("PlayerVRIK origin not bound.");
        IXRCamera camera = _camera ?? throw new InvalidOperationException("PlayerVRIK camera not bound.");

        Node3D originNode = origin.OriginNode;

        Skeleton3D skeleton = GetResolvedSkeleton();

        Transform3D headBoneRest = skeleton.GetBoneGlobalRest(HeadBoneIndex);
        Transform3D restViewpoint = headBoneRest * ViewpointLocalTransform;

        float avatarRestViewpointHeight = Mathf.Abs(restViewpoint.Origin.Y);
        float xrCameraHeight = Mathf.Abs(originNode.ToLocal(camera.CameraNode.GlobalPosition).Y);

        bool calibrated = TryCalibrateWorldScale(
            avatarRestViewpointHeight,
            xrCameraHeight,
            HeightEpsilon,
            origin.WorldScale,
            out float calibratedScale);

        if (!calibrated)
        {
            GD.PushWarning(
                $"Skipping XR world-scale calibration due to near-zero height values (avatar={avatarRestViewpointHeight:F5}, xr={xrCameraHeight:F5}).");
            _worldScale = origin.WorldScale;
            return;
        }

        origin.WorldScale = calibratedScale;
        _worldScale = calibratedScale;
    }

    /// <summary>
    /// Solves the compensated XR origin transform that aligns the current physical head target
    /// to the virtual head pose while preserving the physical-head local offset under the prior
    /// origin transform.
    /// </summary>
    public Transform3D ComputeCompensatedOriginTransform(
        Skeleton3D skeleton,
        Camera3D camera,
        Node3D origin)
    {
        Transform3D physicalHeadPose = camera.GlobalTransform * ViewpointLocalInverseTransform;
        Transform3D virtualHeadPose = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(HeadBoneIndex);
        Transform3D localPose = origin.GlobalTransform.Inverse() * physicalHeadPose;

        return virtualHeadPose * localPose.Inverse();
    }

    private static bool TryCalibrateWorldScale(
        float avatarRestViewpointHeight,
        float xrCameraHeight,
        float minimumHeight,
        float currentWorldScale,
        out float calibratedScale)
    {
        if (avatarRestViewpointHeight <= minimumHeight || xrCameraHeight <= minimumHeight)
        {
            calibratedScale = currentWorldScale;
            return false;
        }

        float candidateScale = avatarRestViewpointHeight / xrCameraHeight;
        if (!float.IsFinite(candidateScale) || candidateScale <= minimumHeight)
        {
            calibratedScale = currentWorldScale;
            return false;
        }

        calibratedScale = candidateScale;
        return true;
    }

    private void UpdateHipDebugMessage(PoseStateContext context, PoseStateMachineTickResult tickResult)
    {
        string? animationDebugMessage = AnimationDebugOutputEnabled
            ? tickResult.ActiveState?.BuildAnimationDebugMessage(context)
            : null;

        if (!HipClampDebugOutputEnabled
            && !HipPositionReferenceDebugOutputEnabled
            && !HipForwardBackSeamDebugOutputEnabled
            && string.IsNullOrEmpty(animationDebugMessage))
        {
            ClearHipDebugMessage();
            return;
        }

        StringBuilder messageBuilder = _debugMessageBuilder;
        _ = messageBuilder.Clear();

        if (HipClampDebugOutputEnabled)
        {
            AppendHipClampDebugLine(messageBuilder, tickResult.ResidualHipOffset);
        }

        if (HipPositionReferenceDebugOutputEnabled)
        {
            if (tickResult.HipLocalPosition is not Vector3 hipLocalPosition || tickResult.ActiveState is null)
            {
                AppendHipPositionReferenceUnavailableLine(messageBuilder);
            }
            else
            {
                if (messageBuilder.Length > 0)
                {
                    _ = messageBuilder.Append('\n');
                }

                HipLimitFrame limitFrame = tickResult.ActiveState.BuildHipLimitFrame(context);
                AppendHipPositionReferenceDebugLine(
                    messageBuilder,
                    hipLocalPosition,
                    limitFrame.ReferenceHipLocalPosition,
                    context.RestHeadHeight);
            }
        }

        if (HipForwardBackSeamDebugOutputEnabled)
        {
            AppendHipForwardBackSeamDebugLine(
                messageBuilder,
                context,
                tickResult,
                _mostRecentOriginCompensationDelta);
        }

        if (!string.IsNullOrEmpty(animationDebugMessage))
        {
            AppendDebugLineSeparator(messageBuilder);
            _ = messageBuilder.Append(animationDebugMessage);
        }

        _isDrivingDebugOverlay = this.SetDebugMessage(messageBuilder.ToString());
    }

    private void ClearHipDebugMessage()
    {
        if (!_isDrivingDebugOverlay)
        {
            return;
        }

        this.ClearDebugMessage();
        _isDrivingDebugOverlay = false;
    }

    private static void AppendHipClampDebugLine(StringBuilder messageBuilder, Vector3 residualHipOffset)
    {
        float left = Mathf.Max(-residualHipOffset.X, 0.0f);
        float right = Mathf.Max(residualHipOffset.X, 0.0f);
        float down = Mathf.Max(-residualHipOffset.Y, 0.0f);
        float up = Mathf.Max(residualHipOffset.Y, 0.0f);
        float forward = Mathf.Max(-residualHipOffset.Z, 0.0f);
        float back = Mathf.Max(residualHipOffset.Z, 0.0f);

        _ = messageBuilder.Append("Hip Clamp U:");
        _ = messageBuilder.Append(FormatFloat(up));
        _ = messageBuilder.Append(" D:");
        _ = messageBuilder.Append(FormatFloat(down));
        _ = messageBuilder.Append(" L:");
        _ = messageBuilder.Append(FormatFloat(left));
        _ = messageBuilder.Append(" R:");
        _ = messageBuilder.Append(FormatFloat(right));
        _ = messageBuilder.Append(" F:");
        _ = messageBuilder.Append(FormatFloat(forward));
        _ = messageBuilder.Append(" B:");
        _ = messageBuilder.Append(FormatFloat(back));
    }

    private static void AppendHipPositionReferenceUnavailableLine(StringBuilder messageBuilder)
    {
        AppendDebugLineSeparator(messageBuilder);

        _ = messageBuilder.Append("Hip Pos Ref: unavailable");
    }

    private static void AppendHipPositionReferenceDebugLine(
        StringBuilder messageBuilder,
        Vector3 hipLocalPosition,
        Vector3 referenceHipLocalPosition,
        float restHeadHeight)
    {
        Vector3 divergence = hipLocalPosition - referenceHipLocalPosition;

        _ = messageBuilder.Append("Hip Pos Cur:");
        _ = messageBuilder.Append(FormatVector3(hipLocalPosition));
        _ = messageBuilder.Append(" Ref:");
        _ = messageBuilder.Append(FormatVector3(referenceHipLocalPosition));
        _ = messageBuilder.Append(" Δ:");
        _ = messageBuilder.Append(FormatVector3(divergence));
        _ = messageBuilder.Append(" Δn:");

        if (Mathf.Abs(restHeadHeight) <= HeightEpsilon || !float.IsFinite(restHeadHeight))
        {
            _ = messageBuilder.Append("n/a");
            return;
        }

        _ = messageBuilder.Append(FormatVector3(divergence / restHeadHeight));
    }

    private static void AppendHipForwardBackSeamDebugLine(
        StringBuilder messageBuilder,
        PoseStateContext context,
        PoseStateMachineTickResult tickResult,
        Vector3 originCompensationDelta)
    {
        AppendDebugLineSeparator(messageBuilder);

        Vector3 appliedHipDelta = Vector3.Zero;
        if (tickResult.HipLocalPosition is Vector3 hipLocalPosition && tickResult.ActiveState is not null)
        {
            HipLimitFrame limitFrame = tickResult.ActiveState.BuildHipLimitFrame(context);
            appliedHipDelta = hipLocalPosition - limitFrame.ReferenceHipLocalPosition;
        }

        Vector3 desiredHipDelta = appliedHipDelta + tickResult.ResidualHipOffset;
        Vector3 limitedHeadDelta = Vector3.Zero;
        bool limitedHeadActive;
        if (tickResult.LimitedHeadTargetTransform is Transform3D limitedHeadTargetTransform)
        {
            limitedHeadActive = true;
            limitedHeadDelta = limitedHeadTargetTransform.Origin - context.HeadTargetTransform.Origin;
        }
        else
        {
            limitedHeadActive = false;
        }

        _ = messageBuilder.Append("Hip Seam Zapp:");
        _ = messageBuilder.Append(FormatFloat(appliedHipDelta.Z));
        _ = messageBuilder.Append(" Zdes:");
        _ = messageBuilder.Append(FormatFloat(desiredHipDelta.Z));
        _ = messageBuilder.Append(" Zres:");
        _ = messageBuilder.Append(FormatFloat(tickResult.ResidualHipOffset.Z));
        _ = messageBuilder.Append(" LH:");
        _ = messageBuilder.Append(limitedHeadActive ? 'Y' : 'N');
        _ = messageBuilder.Append(" ΔLH:");
        _ = messageBuilder.Append(FormatVector3(limitedHeadDelta));
        _ = messageBuilder.Append(" ΔOrg:");
        _ = messageBuilder.Append(FormatVector3(originCompensationDelta));
    }

    private static void AppendDebugLineSeparator(StringBuilder messageBuilder)
    {
        if (messageBuilder.Length > 0)
        {
            _ = messageBuilder.Append('\n');
        }
    }

    private static string FormatVector3(Vector3 value)
        => $"({FormatFloat(value.X)},{FormatFloat(value.Y)},{FormatFloat(value.Z)})";

    private static string FormatFloat(float value)
        => value.ToString("F3", CultureInfo.InvariantCulture);

    private PoseStateContext BuildPoseStateContext(Skeleton3D skeleton, double delta)
    {
        // Head target rest in world space: head-bone global rest multiplied by the viewpoint
        // marker's local transform inside the head bone. Matches the calibration reference used
        // by CalibrateWorldScaleOnce.
        Transform3D headBoneRest = skeleton.GetBoneGlobalRest(HeadBoneIndex);
        Transform3D headTargetRestTransform = skeleton.GlobalTransform * headBoneRest * ViewpointLocalTransform;

        // Current IK target transforms in world space.
        Transform3D headTargetTransform = ResolvedHeadIKTarget?.GlobalTransform ?? Transform3D.Identity;
        Transform3D rightHandTargetTransform = ResolvedRightHandIKTarget?.GlobalTransform ?? Transform3D.Identity;
        Transform3D leftHandTargetTransform = ResolvedLeftHandIKTarget?.GlobalTransform ?? Transform3D.Identity;
        Transform3D rightFootTargetTransform = ResolvedRightFootIKTarget?.GlobalTransform ?? Transform3D.Identity;
        Transform3D leftFootTargetTransform = ResolvedLeftFootIKTarget?.GlobalTransform ?? Transform3D.Identity;

        _poseContextBuilder.HeadTargetTransform = headTargetTransform;
        _poseContextBuilder.HeadTargetRestTransform = headTargetRestTransform;
        _poseContextBuilder.RightHandTargetTransform = rightHandTargetTransform;
        _poseContextBuilder.LeftHandTargetTransform = leftHandTargetTransform;
        _poseContextBuilder.RightFootTargetTransform = rightFootTargetTransform;
        _poseContextBuilder.LeftFootTargetTransform = leftFootTargetTransform;
        _poseContextBuilder.WorldScale = _worldScale;
        _poseContextBuilder.Skeleton = skeleton;
        _poseContextBuilder.AnimationTree = PoseStateMachine?.AnimationTree;
        _poseContextBuilder.HipBoneIndex = HipBoneIndex;
        _poseContextBuilder.HeadBoneIndex = HeadBoneIndex;
        _poseContextBuilder.Delta = delta;
        _poseContextBuilder.ClearAuxiliarySignals();

        return _poseContextBuilder.Build();
    }

    private void ConfigurePlayerBodyCollisionExceptions()
    {
        if (GetParent() is not CharacterBody3D playerBody)
        {
            return;
        }

        // TODO: This self-collision suppression is a temporary workaround for the current IK target rig.
        // PlayerVRIK may not be the right long-term owner once collision responsibilities are separated cleanly.
        AddBidirectionalCollisionException(playerBody, ResolvedHeadIKTarget);
        AddBidirectionalCollisionException(playerBody, ResolvedRightHandIKTarget);
        AddBidirectionalCollisionException(playerBody, ResolvedLeftHandIKTarget);
        AddBidirectionalCollisionException(playerBody, ResolvedRightFootIKTarget as PhysicsBody3D);
        AddBidirectionalCollisionException(playerBody, ResolvedLeftFootIKTarget as PhysicsBody3D);
    }

    private void ConfigureGeneratedTargetProxyCollisionExceptions()
    {
        DynamicPhysicalRig? rig = ResolveConfiguredPhysicalRig();
        if (rig is null)
        {
            return;
        }

        if (!rig.Enabled)
        {
            return;
        }

        if (rig.GeneratedProxyCount == 0)
        {
            ConfigureGeneratedTargetProxyCollisionExceptionsDeferred();
            return;
        }

        AddGeneratedHandProxyCollisionExceptions(ResolvedRightHandIKTarget, rig, _rightHandBoneName, _rightLowerArmBoneName);
        AddGeneratedHandProxyCollisionExceptions(ResolvedLeftHandIKTarget, rig, _leftHandBoneName, _leftLowerArmBoneName);
    }

    private DynamicPhysicalRig? ResolveConfiguredPhysicalRig()
        => PhysicalRig is not null && IsInstanceValid(PhysicalRig) ? PhysicalRig : null;

    private static void AddGeneratedHandProxyCollisionExceptions(
        AnimatableBody3D? handTarget,
        DynamicPhysicalRig rig,
        StringName handBoneName,
        StringName lowerArmBoneName)
    {
        if (handTarget is null)
        {
            return;
        }

        AddCollisionExceptionsForGeneratedBone(handTarget, rig, handBoneName);
        AddCollisionExceptionsForGeneratedBone(handTarget, rig, lowerArmBoneName);
        AddCollisionExceptionsForGeneratedBodies(handTarget, rig.GetGeneratedFingerProxyBodiesForHand(handBoneName));
    }

    private static void AddCollisionExceptionsForGeneratedBone(PhysicsBody3D handTarget, DynamicPhysicalRig rig, StringName boneName)
        => AddCollisionExceptionsForGeneratedBodies(handTarget, rig.GetGeneratedProxyBodiesForBone(boneName));

    private static void AddCollisionExceptionsForGeneratedBodies(PhysicsBody3D handTarget, IReadOnlyList<PhysicsBody3D> proxyBodies)
    {
        foreach (PhysicsBody3D proxyBody in proxyBodies)
        {
            AddBidirectionalCollisionException(handTarget, proxyBody);
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<HandDynamicInteractionShape> ResolveRightHandDynamicInteractionShapes()
        => ResolveHandDynamicInteractionShapes(_rightHandBoneName);

    /// <inheritdoc />
    protected override IReadOnlyList<HandDynamicInteractionShape> ResolveLeftHandDynamicInteractionShapes()
        => ResolveHandDynamicInteractionShapes(_leftHandBoneName);

    private IReadOnlyList<HandDynamicInteractionShape> ResolveHandDynamicInteractionShapes(StringName handBoneName)
    {
        BodyColliderProfile? colliderProfile = ResolveConfiguredPhysicalRig()?.ColliderProfile;
        if (colliderProfile is null)
        {
            return [];
        }

        IReadOnlyList<BodyColliderShapeDescriptor> descriptors = colliderProfile.QueryShapeDescriptorsForBone(handBoneName);
        if (descriptors.Count == 0)
        {
            return [];
        }

        var queryShapes = new HandDynamicInteractionShape[descriptors.Count];
        for (int index = 0; index < descriptors.Count; index += 1)
        {
            BodyColliderShapeDescriptor descriptor = descriptors[index];
            queryShapes[index] = new HandDynamicInteractionShape(
                descriptor.Shape,
                descriptor.LocalTransform,
                descriptor.Disabled);
        }

        return queryShapes;
    }

    private static void AddBidirectionalCollisionException(PhysicsBody3D source, PhysicsBody3D? other)
    {
        if (other is null || source == other)
        {
            return;
        }

        source.AddCollisionExceptionWith(other);
        other.AddCollisionExceptionWith(source);
    }

    private sealed class FingerCollisionShapeMirror(string sideName)
    {
        private readonly List<MirroredFingerCollisionShape> _mirroredShapes = [];
        private DynamicPhysicalRig? _mirroredRig;
        private int _mirroredGeneratedProxyCount;

        public void Sync(DynamicPhysicalRig rig, StringName handBoneName, AnimatableBody3D? handTarget)
        {
            if (handTarget is null || !IsInstanceValid(handTarget))
            {
                Clear();
                return;
            }

            if (NeedsRebuild(rig))
            {
                IReadOnlyList<GeneratedProxyCollisionShape> sourceShapes = rig.GetGeneratedFingerProxyCollisionShapesForHand(handBoneName);
                if (sourceShapes.Count == 0)
                {
                    Clear();
                    return;
                }

                Rebuild(rig, handTarget, sourceShapes);
            }

            SyncTransforms(handTarget);
        }

        private bool NeedsRebuild(DynamicPhysicalRig rig)
            => _mirroredShapes.Count == 0
               || !ReferenceEquals(_mirroredRig, rig)
               || _mirroredGeneratedProxyCount != rig.GeneratedProxyCount
               || !HasValidMirrors();

        private bool HasValidMirrors()
        {
            foreach (MirroredFingerCollisionShape mirroredShape in _mirroredShapes)
            {
                if (!IsInstanceValid(mirroredShape.SourceShape)
                    || !IsInstanceValid(mirroredShape.MirrorShape))
                {
                    return false;
                }
            }

            return true;
        }

        private void Rebuild(DynamicPhysicalRig rig, AnimatableBody3D handTarget, IReadOnlyList<GeneratedProxyCollisionShape> sourceShapes)
        {
            Clear();

            for (int index = 0; index < sourceShapes.Count; index += 1)
            {
                GeneratedProxyCollisionShape sourceShape = sourceShapes[index];
                CollisionShape3D mirrorShape = new()
                {
                    Name = $"Generated{sideName}FingerMovementCollisionShape_{index:D2}",
                    Shape = sourceShape.Shape,
                    Disabled = sourceShape.Disabled,
                };
                mirrorShape.SetMeta(IKTargetAnimatableActuator.GeneratedMovementCollisionShapeMetaKey, true);
                handTarget.AddChild(mirrorShape);
                _mirroredShapes.Add(new MirroredFingerCollisionShape(sourceShape.SourceShape, mirrorShape));
            }

            _mirroredRig = rig;
            _mirroredGeneratedProxyCount = rig.GeneratedProxyCount;
        }

        private void SyncTransforms(AnimatableBody3D handTarget)
        {
            Transform3D handWorldInverse = ResolveNodeGlobalTransform(handTarget).AffineInverse();

            foreach (MirroredFingerCollisionShape mirroredShape in _mirroredShapes)
            {
                if (!IsInstanceValid(mirroredShape.SourceShape) || !IsInstanceValid(mirroredShape.MirrorShape))
                {
                    continue;
                }

                mirroredShape.MirrorShape.Disabled = mirroredShape.SourceShape.Disabled;
                mirroredShape.MirrorShape.Transform = handWorldInverse * ResolveNodeGlobalTransform(mirroredShape.SourceShape);
                if (mirroredShape.MirrorShape.IsInsideTree())
                {
                    mirroredShape.MirrorShape.ForceUpdateTransform();
                }
            }

            if (handTarget.IsInsideTree())
            {
                handTarget.ForceUpdateTransform();
            }
        }

        private void Clear()
        {
            foreach (MirroredFingerCollisionShape mirroredShape in _mirroredShapes)
            {
                if (IsInstanceValid(mirroredShape.MirrorShape))
                {
                    mirroredShape.MirrorShape.QueueFree();
                }
            }

            _mirroredShapes.Clear();
            _mirroredRig = null;
            _mirroredGeneratedProxyCount = 0;
        }

    }

    private readonly record struct MirroredFingerCollisionShape(
        CollisionShape3D SourceShape,
        CollisionShape3D MirrorShape);

    private static Transform3D ResolveNodeGlobalTransform(Node3D node)
        => node.GetParent() is Node3D parent ? parent.GlobalTransform * node.Transform : node.GlobalTransform;
}
