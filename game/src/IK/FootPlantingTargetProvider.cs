using AlleyCat.Control.Locomotion;
using AlleyCat.Core.Logging;
using AlleyCat.Rigging;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.IK;

/// <summary>One foot's IK-003 Technical Requirement 24 foot-planting record of one physics tick.</summary>
/// <param name="Tick">Physics tick the target was resolved on.</param>
/// <param name="Side">Anatomical side.</param>
/// <param name="PlantedStateAvailable">Whether a same-tick ANIM-002 planted state was available.</param>
/// <param name="MetadataPlanted">ANIM-002 planted state of this foot under the Planted Combination Rule.</param>
/// <param name="Phase">Pin phase after this tick's step.</param>
/// <param name="Pin">Planar world pin (meaningful while <see cref="FootPlantingPhase.Pinned" />).</param>
/// <param name="AnimatedTarget">Same-tick animated world transform of the foot bone.</param>
/// <param name="AnimatedBall">World ball point of the animated foot.</param>
/// <param name="Target">World foot target handed to <see cref="CharacterIK" />.</param>
/// <param name="TargetBall">World ball point of <paramref name="Target" />.</param>
/// <param name="ReachUsage">Upper-leg-to-target distance over the chain rest length.</param>
/// <param name="ReachBlendOutStarted">Whether this tick released the pin because it left the leg's reach.</param>
/// <param name="ContinuityReset">Whether this tick forgot the pin after a long gap or a teleport before stepping.</param>
/// <param name="PostIKBall">Post-IK world ball point captured inside <see cref="Skeleton3D.SignalName.SkeletonUpdated" />.</param>
/// <param name="PostIKCaptured">Whether <paramref name="PostIKBall" /> was captured on <paramref name="Tick" />.</param>
/// <param name="CapturedHeading">Acquired world anatomical heading in radians.</param>
/// <param name="AnimatedHeading">Current animated world anatomical heading in radians.</param>
/// <param name="TargetHeading">Requested world anatomical heading in radians.</param>
/// <param name="PostIKHeading">Final post-IK world anatomical heading in radians.</param>
public readonly record struct FootPlantingTickDiagnostic(
    ulong Tick,
    LimbSide Side,
    bool PlantedStateAvailable,
    bool MetadataPlanted,
    FootPlantingPhase Phase,
    Vector3 Pin,
    Transform3D AnimatedTarget,
    Vector3 AnimatedBall,
    Transform3D Target,
    Vector3 TargetBall,
    float ReachUsage,
    bool ReachBlendOutStarted,
    bool ContinuityReset,
    Vector3 PostIKBall,
    bool PostIKCaptured,
    float CapturedHeading = float.NaN,
    float AnimatedHeading = float.NaN,
    float TargetHeading = float.NaN,
    float PostIKHeading = float.NaN);

/// <summary>
/// IK-003 animation-driven foot planting for one foot: a value-only foot-target provider. It plants the foot exactly
/// while the ANIM-002 planted state of the same-tick pose says so, pins the ball joint (the toe bone's rest-pose origin
/// in foot space) horizontally, and holds its anatomical world heading about that ball. It never moves the
/// actor, the skeleton, or any target; <see cref="CharacterIK" /> applies the returned target.
/// </summary>
/// <remarks>
/// <para>Both toe bones (<c>LeftToes</c> and <c>RightToes</c>) and this leg's bones are required rig data. A rig missing
/// any of them is a bind error: it is logged once and this provider refuses, so neither foot is planted. When no
/// same-tick planted state is available the foot is treated as swinging, so no stale state is ever applied.</para>
/// <para>The pin state advances only when queried inside <see cref="CharacterIK" />'s foot-provider modifier stage, at
/// most once per physics tick. Queries from anywhere else, including another character's stage, return a
/// non-advancing preview.</para>
/// <para>Skipped ticks continue the pin, but a gap longer than <see cref="MaxContinuationGapSeconds" /> or skeleton
/// travel beyond the leg's rest length since the last resolved tick (a teleport while IK was off) forgets it first,
/// as <see cref="ResetPin" /> does, so the foot pins at its current animated position.</para>
/// </remarks>
[GlobalClass]
public partial class FootPlantingTargetProvider : IKTargetIntentProvider
{
    private readonly FootPlantingSolver _solver = new();
    private Skeleton3D? _skeleton;
    private Skeleton3D? _connectedSkeleton;
    private int _upperLeg = -1;
    private int _foot = -1;
    private Vector3 _ballInFoot;
    private Vector3 _upperToLowerRest;
    private Vector3 _lowerToFootRest;
    private bool _resolutionAttempted;
    private ulong _lastTick;
    private IKTargetIntentResolution _lastResolution;

    /// <summary>Anatomical side of the planted foot.</summary>
    [Export]
    public LimbSide Side
    {
        get; set;
    }

    /// <summary>Skeleton whose same-tick animated pose is planted.</summary>
    [Export]
    public Skeleton3D? Skeleton
    {
        get; set;
    }

    /// <summary>Source of the same-tick ANIM-002 planted state.</summary>
    [Export]
    public PlantedStateSource? PlantedStateSource
    {
        get; set;
    }

    /// <summary>Duration of the blend from a released pin back to the animated target (tunable).</summary>
    [Export(PropertyHint.Range, "0,1,0.01,suffix:s")]
    public float BlendOutSeconds
    {
        get => (float)_solver.BlendOutSeconds;
        set => _solver.BlendOutSeconds = Math.Max(value, 0f);
    }

    /// <summary>Distance short of full leg extension beyond which a pin is out of reach (tunable).</summary>
    [Export(PropertyHint.Range, "0,0.1,0.001,suffix:m")]
    public float ReachMarginMetres
    {
        get => _solver.ReachMarginMetres;
        set => _solver.ReachMarginMetres = Math.Max(value, 0f);
    }

    /// <summary>
    /// Longest gap since the last resolved tick over which the pin is continued; a longer gap resets it (tunable).
    /// </summary>
    [Export(PropertyHint.Range, "0,2,0.01,suffix:s")]
    public float MaxContinuationGapSeconds
    {
        get => (float)_solver.MaxContinuationGapSeconds;
        set => _solver.MaxContinuationGapSeconds = Math.Max(value, 0f);
    }

    /// <summary>Whether planting bound to the skeleton: it has both toe bones and this leg's bones.</summary>
    public bool IsPlantingAvailable
    {
        get; private set;
    }

    /// <summary>Ball joint in foot-local space, resolved from the toe bone's rest pose.</summary>
    public Vector3 BallInFoot => _ballInFoot;

    /// <summary>The latest tick's record; post-IK fields are filled inside the skeleton update.</summary>
    public FootPlantingTickDiagnostic LastDiagnostic
    {
        get; private set;
    }

    /// <summary>Number of pins released because they left the leg's reach.</summary>
    public int ReachBlendOutCount
    {
        get; private set;
    }

    /// <summary>Number of pins forgotten after a long gap or a teleport since the last <see cref="ResetStatistics" />.</summary>
    public int ContinuityResetCount
    {
        get; private set;
    }

    /// <summary>Largest reach usage of any applied target since the last <see cref="ResetStatistics" />.</summary>
    public float MaxReachUsage
    {
        get; private set;
    }

    /// <summary>Raised inside <see cref="Skeleton3D.SignalName.SkeletonUpdated" /> with each tick's complete record.</summary>
    public event Action<FootPlantingTickDiagnostic>? TickRecorded;

    /// <summary>Forgets the pin so the next planted tick pins at the current animated position (spawn or teleport).</summary>
    public void ResetPin() => _solver.Reset();

    /// <summary>Resets <see cref="ReachBlendOutCount" />, <see cref="ContinuityResetCount" />, and <see cref="MaxReachUsage" />.</summary>
    public void ResetStatistics()
    {
        ReachBlendOutCount = 0;
        ContinuityResetCount = 0;
        MaxReachUsage = 0f;
    }

    /// <inheritdoc />
    public override IKTargetIntent GetTargetIntent()
        => TryGetTargetIntent(out IKTargetIntentResolution resolution)
            ? resolution.Intent
            : new IKTargetIntent(Transform3D.Identity, 0f);

    /// <inheritdoc />
    public override bool TryGetTargetIntent(out IKTargetIntentResolution resolution)
    {
        if (!TryResolve())
        {
            resolution = default;
            return false;
        }

        ulong tick = Engine.GetPhysicsFrames();
        if (tick == _lastTick)
        {
            resolution = _lastResolution;
            return true;
        }

        Skeleton3D skeleton = _skeleton!;
        Transform3D skeletonWorld = skeleton.GlobalTransform;
        Transform3D animatedFoot = skeletonWorld * skeleton.GetBoneGlobalPose(_foot);
        Vector3 legRoot = skeletonWorld * skeleton.GetBoneGlobalPose(_upperLeg).Origin;
        // IK-003 TR14: the ankle, upper joint and reach limit must share the same current world frame.
        // These rest vectors are skeleton-space; the world basis applies container/actor scale once.
        float worldChainLength = (skeletonWorld.Basis * _upperToLowerRest).Length()
            + (skeletonWorld.Basis * _lowerToFootRest).Length();
        PlantedStateSource? source = PlantedStateSource;
        PlantedFeetState state = default;
        bool available = source is not null && IsInstanceValid(source) && source.TryGetPlantedState(out state);
        bool metadataPlanted = available && (Side == LimbSide.Left ? state.Left : state.Right);

        // Skipped ticks keep the pins and continue from the latest planted state over the elapsed time, so a planted
        // foot never jumps to its animation because the pipeline missed a tick; the solver resets instead after a long
        // gap or a teleport.
        var input = new FootPlantingInput(
            metadataPlanted,
            animatedFoot,
            _ballInFoot,
            legRoot,
            worldChainLength,
            _lastTick == 0 ? 0d : (double)(tick - _lastTick) / Engine.PhysicsTicksPerSecond,
            skeletonWorld.Origin);
        if (!FootTargetResolutionStage.IsActiveFor(skeleton))
        {
            // Outside this skeleton's CharacterIK foot-provider stage the pose need not be this tick's final animated pose, so the
            // query neither advances the pin state nor caches its answer (IK-003 Technical Requirement 8).
            resolution = new IKTargetIntentResolution(
                new IKTargetIntent(_solver.Preview(input).Target, 1f),
                ShouldApplyTargetTransform: true);
            return true;
        }

        FootPlantingOutput output = _solver.Step(input);

        if (output.ReachBlendOutStarted)
        {
            ReachBlendOutCount++;
        }

        if (output.ContinuityReset)
        {
            ContinuityResetCount++;
        }

        MaxReachUsage = Math.Max(MaxReachUsage, output.ReachUsage);
        LastDiagnostic = new FootPlantingTickDiagnostic(
            tick,
            Side,
            available,
            metadataPlanted,
            output.Phase,
            output.Pin,
            animatedFoot,
            output.AnimatedBall,
            output.Target,
            output.TargetBall,
            output.ReachUsage,
            output.ReachBlendOutStarted,
            output.ContinuityReset,
            Vector3.Zero,
            false,
            output.CapturedHeading,
            output.AnimatedHeading,
            output.TargetHeading);

        _lastTick = tick;
        _lastResolution = new IKTargetIntentResolution(new IKTargetIntent(output.Target, 1f), ShouldApplyTargetTransform: true);
        resolution = _lastResolution;
        return true;
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        DisconnectSkeleton();
        base._ExitTree();
    }

    private bool TryResolve()
    {
        Skeleton3D? skeleton = Skeleton;
        if (skeleton is null || !IsInstanceValid(skeleton))
        {
            return false;
        }

        if (ReferenceEquals(skeleton, _skeleton) && _resolutionAttempted)
        {
            return IsPlantingAvailable;
        }

        _skeleton = skeleton;
        _resolutionAttempted = true;
        string prefix = Side == LimbSide.Left ? "Left" : "Right";
        _upperLeg = skeleton.FindBone($"{prefix}UpperLeg");
        int lowerLeg = skeleton.FindBone($"{prefix}LowerLeg");
        _foot = skeleton.FindBone($"{prefix}Foot");
        int toe = skeleton.FindBone($"{prefix}Toes");
        int otherToe = skeleton.FindBone(Side == LimbSide.Left ? "RightToes" : "LeftToes");
        IsPlantingAvailable = _upperLeg >= 0 && lowerLeg >= 0 && _foot >= 0 && toe >= 0 && otherToe >= 0;
        if (!IsPlantingAvailable)
        {
            if (GameLoggerResolver.TryResolve(out ILogger<FootPlantingTargetProvider>? logger) && logger is not null)
            {
                logger.LogError(
                    "Foot planting cannot bind the {Side} foot to skeleton '{Skeleton}': the rig must have both LeftToes and RightToes and this leg's UpperLeg, LowerLeg and Foot bones; neither foot is planted.",
                    Side,
                    skeleton.Name.ToString());
            }

            return false;
        }

        Transform3D footRest = skeleton.GetBoneGlobalRest(_foot);
        _ballInFoot = (footRest.AffineInverse() * skeleton.GetBoneGlobalRest(toe)).Origin;
        Vector3 upperRest = skeleton.GetBoneGlobalRest(_upperLeg).Origin;
        Vector3 lowerRest = skeleton.GetBoneGlobalRest(lowerLeg).Origin;
        _upperToLowerRest = lowerRest - upperRest;
        _lowerToFootRest = footRest.Origin - lowerRest;
        _lastTick = 0;
        _solver.Reset();
        ConnectSkeleton(skeleton);
        return true;
    }

    private void ConnectSkeleton(Skeleton3D skeleton)
    {
        DisconnectSkeleton();
        skeleton.SkeletonUpdated += OnSkeletonUpdated;
        _connectedSkeleton = skeleton;
    }

    private void DisconnectSkeleton()
    {
        if (_connectedSkeleton is not null && IsInstanceValid(_connectedSkeleton))
        {
            _connectedSkeleton.SkeletonUpdated -= OnSkeletonUpdated;
        }

        _connectedSkeleton = null;
    }

    private void OnSkeletonUpdated()
    {
        Skeleton3D? skeleton = _connectedSkeleton;
        if (skeleton is null || LastDiagnostic.Tick != Engine.GetPhysicsFrames() || LastDiagnostic.PostIKCaptured)
        {
            return;
        }

        Transform3D postIKFoot = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(_foot);
        Vector3 postIKBall = postIKFoot * _ballInFoot;
        Vector3 anatomicalDirection = postIKFoot.Basis * _ballInFoot;
        anatomicalDirection.Y = 0f;
        LastDiagnostic = LastDiagnostic with
        {
            PostIKBall = postIKBall,
            PostIKCaptured = true,
            PostIKHeading = anatomicalDirection.LengthSquared() < 1e-10f
                ? float.NaN
                : Mathf.Atan2(-anatomicalDirection.X, -anatomicalDirection.Z)
        };
        TickRecorded?.Invoke(LastDiagnostic);
    }
}
