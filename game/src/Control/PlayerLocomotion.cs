using AlleyCat.Common;
using Godot;

namespace AlleyCat.Control;

/// <summary>
/// Concrete locomotion component for the reference player.
/// </summary>
[GlobalClass]
public partial class PlayerLocomotion : LocomotionBase
{
    private const float RootMotionSpeedEpsilon = 1e-4f;
    private static readonly StringName _playbackParameter = new("parameters/playback");
    private static readonly StringName _walkingAnimationStateName = new("Walking");
    private static readonly StringName _standingAnimationStateName = new("StandingCrouching");

    private CharacterBody3D? TargetCharacterBodyResolved
    {
        get;
        set;
    }

    private AnimationTree? AnimationTreeResolved
    {
        get;
        set;
    }

    private Node3D? RootMotionReferenceResolved
    {
        get;
        set;
    }
    private Vector2 _movementInput;
    private Vector2 _rotationInput;
    private double _snapTurnCooldownRemainingSeconds;
    private bool _warnedMissingAnimationBlendParameter;
    private bool _warnedRootMotionFallback;
    private bool _warnedUnsupportedAnimationBlendParameterType;

    /// <summary>
    /// Character body moved by this locomotion component.
    /// </summary>
    [Export]
    public CharacterBody3D? TargetCharacterBodyNode
    {
        get;
        set;
    }

    /// <summary>
    /// Animation tree used for blend driving and root motion extraction.
    /// </summary>
    [Export]
    public AnimationTree? AnimationTree
    {
        get;
        set;
    }

    /// <summary>
    /// Transform reference that resolves authored root motion into world space.
    /// </summary>
    [Export]
    public Node3D? RootMotionReference
    {
        get;
        set;
    }

    /// <summary>
    /// Optional animation parameter path driven by locomotion blend input.
    /// </summary>
    [Export]
    public StringName AnimationBlendParameter
    {
        get;
        set;
    } = new();

    /// <summary>
    /// Locomotion turn mode.
    /// </summary>
    [Export]
    public TurnMode TurnMode
    {
        get;
        set;
    } = TurnMode.Snap;

    /// <summary>
    /// Maximum planar movement speed multiplier.
    /// </summary>
    [Export(PropertyHint.Range, "0,10,0.01,or_greater")]
    public float MovementSpeedMultiplier
    {
        get;
        set;
    } = 1.5f;

    /// <summary>
    /// Rotation speed multiplier.
    /// </summary>
    [Export(PropertyHint.Range, "0,20,0.01,or_greater")]
    public float RotationSpeedMultiplier
    {
        get;
        set;
    } = 1.0f;

    /// <summary>
    /// Snap-turn increment in degrees.
    /// </summary>
    [Export(PropertyHint.Range, "0,180,0.1,or_greater")]
    public float SnapTurnAngleDegrees
    {
        get;
        set;
    } = 45f;

    /// <summary>
    /// Snap-turn cooldown duration in seconds.
    /// </summary>
    [Export(PropertyHint.Range, "0,5,0.01,or_greater")]
    public float SnapTurnCooldownSeconds
    {
        get;
        set;
    } = 0.25f;

    /// <summary>
    /// Stick magnitude threshold used to trigger snap turns.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float SnapTurnActivationThreshold
    {
        get;
        set;
    } = 0.5f;

    /// <summary>
    /// Smooth-turn sensitivity in radians per second at full input before the rotation multiplier is applied.
    /// </summary>
    [Export(PropertyHint.Range, "0,20,0.01,or_greater")]
    public float SmoothTurnSensitivity
    {
        get;
        set;
    } = 2.5f;

    /// <summary>
    /// Speed value at which the locomotion animation blend reaches full walk.
    /// </summary>
    [Export(PropertyHint.Range, "0.001,10,0.01,or_greater")]
    public float AnimationBlendThreshold
    {
        get;
        set;
    } = 1.0f;

    /// <summary>
    /// AnimationTree top-level state that is allowed to contribute locomotion root motion.
    /// </summary>
    [Export]
    public StringName RootMotionAnimationStateName
    {
        get;
        set;
    } = _walkingAnimationStateName;

    /// <summary>
    /// Symmetric deadzone applied to movement and turn axes.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float InputDeadzone
    {
        get;
        set;
    } = 0.15f;

    /// <inheritdoc />
    public override void _Ready()
    {
        base._Ready();
        TargetCharacterBodyResolved = TargetCharacterBodyNode ?? this.RequireNode<CharacterBody3D>("..");
        AnimationTreeResolved = AnimationTree ?? this.RequireNode<AnimationTree>("../AnimationTree");
        RootMotionReferenceResolved = RootMotionReference ?? this.RequireNode<Node3D>("../Female_export");

        SetPhysicsProcess(true);
    }

    /// <inheritdoc />
    public override void SetMovementInput(Vector2 input) => _movementInput = ApplyDeadzone(input, InputDeadzone);

    /// <inheritdoc />
    public override void SetRotationInput(Vector2 input) => _rotationInput = ApplyDeadzone(input, InputDeadzone);

    /// <inheritdoc />
    public override void _PhysicsProcess(double delta)
    {
        if (delta <= 0d)
        {
            return;
        }

        LocomotionPermissions permissions = GetCurrentLocomotionPermissions();

        ApplyRotation(delta, permissions);
        Vector2 locomotionBlendInput = GetLocomotionBlendInput(permissions);
        Vector3 desiredVelocity = ComputeDesiredPlanarVelocity(locomotionBlendInput);
        UpdateLocomotionAnimationState(locomotionBlendInput);
        UpdateAnimationBlend(locomotionBlendInput);
        Vector3 planarVelocity = ResolvePlanarVelocity(delta, desiredVelocity);

        CharacterBody3D targetCharacterBody = GetTargetCharacterBody();
        targetCharacterBody.Velocity = new Vector3(
            planarVelocity.X,
            targetCharacterBody.Velocity.Y,
            planarVelocity.Z);

        _ = targetCharacterBody.MoveAndSlide();
    }

    private void ApplyRotation(double delta, LocomotionPermissions permissions)
    {
        float yawDelta = TurnMode switch
        {
            TurnMode.Smooth => ComputeSmoothYawDelta(
                _rotationInput.X,
                delta,
                RotationSpeedMultiplier,
                SmoothTurnSensitivity,
                InputDeadzone),
            TurnMode.Snap => ApplySnapTurn(delta),
            _ => throw new InvalidOperationException($"Unsupported {nameof(TurnMode)} value '{TurnMode}'."),
        };

        yawDelta = ApplyRotationPermissions(yawDelta, permissions);

        if (Mathf.IsZeroApprox(yawDelta))
        {
            return;
        }

        ApplyYawRotation(yawDelta);
    }

    private float ApplySnapTurn(double delta)
    {
        float yawDelta = ComputeSnapYawDelta(
            _rotationInput.X,
            Mathf.DegToRad(SnapTurnAngleDegrees),
            SnapTurnActivationThreshold,
            InputDeadzone,
            ref _snapTurnCooldownRemainingSeconds,
            delta);

        if (!Mathf.IsZeroApprox(yawDelta))
        {
            _snapTurnCooldownRemainingSeconds = SnapTurnCooldownSeconds;
        }

        return yawDelta;
    }

    private Vector3 ComputeDesiredPlanarVelocity(Vector2 locomotionBlendInput)
    {
        if (locomotionBlendInput.IsZeroApprox())
        {
            return Vector3.Zero;
        }

        Vector3 localDirection = new(locomotionBlendInput.X, 0f, -locomotionBlendInput.Y);
        Vector3 worldDirection = (GetMovementBasis() * localDirection).Normalized();
        return worldDirection * (MovementSpeedMultiplier * locomotionBlendInput.Length());
    }

    private Vector2 GetLocomotionBlendInput(LocomotionPermissions permissions)
        => ApplyMovementPermissions(_movementInput, permissions);

    private Vector3 ResolvePlanarVelocity(double delta, Vector3 desiredVelocity)
    {
        if (desiredVelocity.LengthSquared() <= RootMotionSpeedEpsilon * RootMotionSpeedEpsilon)
        {
            return Vector3.Zero;
        }

        if (AnimationTreeResolved is null || RootMotionReferenceResolved is null)
        {
            return desiredVelocity;
        }

        if (!IsRootMotionStateActive())
        {
            return desiredVelocity;
        }

        Vector3 rootMotionDelta = AnimationTreeResolved.GetRootMotionPosition();
        Vector3 worldRootMotionVelocity = RootMotionReferenceResolved.GlobalBasis * rootMotionDelta / (float)delta;
        worldRootMotionVelocity = new Vector3(worldRootMotionVelocity.X, 0f, worldRootMotionVelocity.Z);

        if (worldRootMotionVelocity.LengthSquared() <= RootMotionSpeedEpsilon * RootMotionSpeedEpsilon)
        {
            WarnRootMotionFallbackOnce();
            return desiredVelocity;
        }

        return worldRootMotionVelocity * MovementSpeedMultiplier;
    }

    private void UpdateAnimationBlend(Vector2 locomotionBlendInput)
    {
        if (AnimationTreeResolved is null || AnimationBlendParameter.IsEmpty)
        {
            return;
        }

        float safeThreshold = Mathf.Max(AnimationBlendThreshold, 1e-3f);
        float blend = Mathf.Clamp(locomotionBlendInput.Length() / safeThreshold, 0f, 1f);

        Variant currentValue = AnimationTreeResolved.Get(AnimationBlendParameter);
        if (currentValue.VariantType == Variant.Type.Nil)
        {
            if (!_warnedMissingAnimationBlendParameter)
            {
                GD.PushWarning(
                    $"{nameof(PlayerLocomotion)} could not resolve animation blend parameter '{AnimationBlendParameter}'. " +
                    "Locomotion movement still runs, but walk blending remains blocked until the animation tree is reconciled.");
                _warnedMissingAnimationBlendParameter = true;
            }

            return;
        }

        if (currentValue.VariantType is Variant.Type.Float or Variant.Type.Int)
        {
            AnimationTreeResolved.Set(AnimationBlendParameter, blend);
            return;
        }

        if (currentValue.VariantType == Variant.Type.Vector2)
        {
            AnimationTreeResolved.Set(AnimationBlendParameter, locomotionBlendInput);
            return;
        }

        if (_warnedUnsupportedAnimationBlendParameterType)
        {
            return;
        }

        GD.PushWarning(
            $"{nameof(PlayerLocomotion)} resolved animation blend parameter '{AnimationBlendParameter}' " +
            $"with unsupported type '{currentValue.VariantType}'.");
        _warnedUnsupportedAnimationBlendParameterType = true;
    }

    private void UpdateLocomotionAnimationState(Vector2 locomotionBlendInput)
    {
        AnimationNodeStateMachinePlayback? playback = ResolvePlayback();
        if (playback is null)
        {
            return;
        }

        StringName currentNode = playback.GetCurrentNode();
        bool hasMovementInput = !locomotionBlendInput.IsZeroApprox();

        if (currentNode == _standingAnimationStateName && hasMovementInput)
        {
            playback.Travel(_walkingAnimationStateName);
            return;
        }

        if (currentNode == _walkingAnimationStateName && !hasMovementInput)
        {
            playback.Travel(_standingAnimationStateName);
        }
    }

    private bool IsRootMotionStateActive()
    {
        if (RootMotionAnimationStateName.IsEmpty)
        {
            return false;
        }

        AnimationNodeStateMachinePlayback? playback = ResolvePlayback();
        return playback is not null && playback.GetCurrentNode() == RootMotionAnimationStateName;
    }

    private AnimationNodeStateMachinePlayback? ResolvePlayback()
        => AnimationTreeResolved?.Get(_playbackParameter).As<AnimationNodeStateMachinePlayback>();

    private void WarnRootMotionFallbackOnce()
    {
        if (_warnedRootMotionFallback)
        {
            return;
        }

        GD.PushWarning(
            $"{nameof(PlayerLocomotion)} did not receive locomotion root motion from the current animation tree. " +
            "Falling back to direct planar velocity until locomotion animation-tree reconciliation is completed.");
        _warnedRootMotionFallback = true;
    }

    private static Vector2 ApplyDeadzone(Vector2 input, float deadzone)
        => new(
            ApplyDeadzone(input.X, deadzone),
            ApplyDeadzone(input.Y, deadzone));

    private CharacterBody3D GetTargetCharacterBody()
        => TargetCharacterBodyResolved
            ?? throw new InvalidOperationException($"{nameof(PlayerLocomotion)} target body is not available before _Ready.");

    /// <summary>
    /// Resolves the world-space basis used to convert local movement input into planar velocity.
    /// </summary>
    protected virtual Basis GetMovementBasis() => GetTargetCharacterBody().GlobalBasis;

    /// <summary>
    /// Applies yaw rotation to the controlled character body.
    /// </summary>
    protected virtual void ApplyYawRotation(float yawDelta) => GetTargetCharacterBody().RotateY(yawDelta);

    private static float ApplyDeadzone(float input, float deadzone)
        => Mathf.Abs(input) >= Mathf.Clamp(deadzone, 0f, 1f) ? input : 0f;

    private static float ComputeSmoothYawDelta(
        float inputX,
        double delta,
        float rotationSpeedMultiplier,
        float smoothTurnSensitivity,
        float inputDeadzone)
    {
        float filteredInput = ApplyDeadzone(inputX, inputDeadzone);
        float scaledDelta = (float)delta;
        return Mathf.IsZeroApprox(filteredInput)
            ? 0f
            : -filteredInput * rotationSpeedMultiplier * smoothTurnSensitivity * scaledDelta;
    }

    private static float ComputeSnapYawDelta(
        float inputX,
        float snapTurnAngleRadians,
        float activationThreshold,
        float inputDeadzone,
        ref double cooldownRemainingSeconds,
        double delta)
    {
        cooldownRemainingSeconds = Math.Max(0d, cooldownRemainingSeconds - delta);

        float filteredInput = ApplyDeadzone(inputX, inputDeadzone);
        return cooldownRemainingSeconds > 0d || Mathf.Abs(filteredInput) < activationThreshold
            ? 0f
            : -Mathf.Sign(filteredInput) * snapTurnAngleRadians;
    }
}
