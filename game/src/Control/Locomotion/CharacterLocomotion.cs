using AlleyCat.Interaction.Hands;
using Godot;

namespace AlleyCat.Control.Locomotion;

/// <summary>
/// Concrete locomotion component for a character rig.
/// </summary>
[GlobalClass]
public partial class CharacterLocomotion : LocomotionBase
{
    private const int RootMotionConsumptionPhysicsPriority = 100;
    private static readonly StringName _playbackParameter = HandPoseAnimationTreePaths.GetNestedStateMachinePlaybackParameter();
    private static readonly StringName _legacyRootPlaybackParameter = new("parameters/playback");
    private static readonly StringName _stateMachineStartNodeName = new("Start");
    private static readonly StringName _walkingAnimationStateName = new("Walking");
    private static readonly StringName _standingAnimationStateName = new("Idle");

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
    private bool _warnedMissingAnimationBlendParameter;
    private bool _warnedUnsupportedAnimationBlendParameterType;
    private bool _warnedMissingAnimationTurnBlendParameter;
    private bool _warnedUnsupportedAnimationTurnBlendParameterType;

    /// <summary>Initialises root-motion consumption after default-priority AnimationTree physics evaluation.</summary>
    public CharacterLocomotion()
    {
        ProcessPhysicsPriority = RootMotionConsumptionPhysicsPriority;
    }

    /// <summary>
    /// Character body moved by this locomotion component.
    /// </summary>
    [Export]
    public CharacterBody3D? TargetCharacterBodyNode
    {
        get;
        set
        {
            field = value;
            TargetCharacterBodyResolved = null;
            TryResolveRuntimeReferences();
        }
    }

    /// <summary>
    /// Animation tree used for blend driving and root motion extraction.
    /// </summary>
    [Export]
    public AnimationTree? AnimationTree
    {
        get;
        set
        {
            field = value;
            AnimationTreeResolved = null;
            TryResolveRuntimeReferences();
        }
    }

    /// <summary>
    /// Transform reference that resolves authored root motion into world space.
    /// </summary>
    [Export]
    public Node3D? RootMotionReference
    {
        get;
        set
        {
            field = value;
            RootMotionReferenceResolved = null;
            TryResolveRuntimeReferences();
        }
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
    /// Optional animation parameter path driven by signed turn input.
    /// </summary>
    [Export]
    public StringName AnimationTurnBlendParameter
    {
        get;
        set;
    } = new();

    /// <summary>
    /// Default idle state used when no pose-specific locomotion animation source is active.
    /// </summary>
    [Export]
    public StringName IdleAnimationStateName
    {
        get;
        set;
    } = _standingAnimationStateName;

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
        TryResolveRuntimeReferences();
        if (RootMotionReferenceResolved is null)
        {
            if (!IsInsideTree())
            {
                throw new InvalidOperationException(
                    $"{GetType().Name} '{Name}' requires exported {nameof(RootMotionReference)} to be assigned; assign the character root motion reference or install a character module that binds it.");
            }

            GD.PushWarning(
                $"{GetType().Name} '{Name}' is waiting for exported {nameof(RootMotionReference)} to be bound by a character module.");
        }
    }

    private void TryResolveRuntimeReferences()
    {
        if (TargetCharacterBodyResolved is not null && !IsInstanceValid(TargetCharacterBodyResolved))
        {
            TargetCharacterBodyResolved = null;
        }

        if (AnimationTreeResolved is not null && !IsInstanceValid(AnimationTreeResolved))
        {
            AnimationTreeResolved = null;
        }

        if (RootMotionReferenceResolved is not null && !IsInstanceValid(RootMotionReferenceResolved))
        {
            RootMotionReferenceResolved = null;
        }

        TargetCharacterBodyResolved ??= TargetCharacterBodyNode ?? (IsInsideTree() ? GetParentOrNull<CharacterBody3D>() : null);
        AnimationTreeResolved ??= AnimationTree ?? (IsInsideTree() ? GetNodeOrNull<AnimationTree>("../AnimationTree") : null);
        RootMotionReferenceResolved ??= RootMotionReference;

        SetPhysicsProcess(TargetCharacterBodyResolved is not null && AnimationTreeResolved is not null && RootMotionReferenceResolved is not null);
    }

    /// <inheritdoc />
    public override void Move(Vector2 input)
    {
        _movementInput = ApplyRadialDeadzone(input, InputDeadzone);
        PublishAnimationControls();
    }

    /// <inheritdoc />
    public override void Rotate(Vector2 input)
    {
        _rotationInput = ApplyRadialDeadzone(input, InputDeadzone);
        PublishAnimationControls();
    }

    /// <inheritdoc />
    public override void _PhysicsProcess(double delta)
    {
        if (delta <= 0d)
        {
            return;
        }

        LocomotionPermissions permissions = GetCurrentLocomotionPermissions();
        LocomotionStateTarget locomotionStateTarget = PublishAnimationControls(permissions);

        RootMotionSample rootMotion = ResolveRootMotionSample(delta, permissions, locomotionStateTarget);
        if (!Mathf.IsZeroApprox(rootMotion.YawDelta))
        {
            ApplyYawRotation(rootMotion.YawDelta);
        }

        CharacterBody3D targetCharacterBody = GetTargetCharacterBody();
        targetCharacterBody.Velocity = new Vector3(
            rootMotion.PlanarVelocity.X,
            targetCharacterBody.Velocity.Y,
            rootMotion.PlanarVelocity.Z);

        _ = targetCharacterBody.MoveAndSlide();
    }

    private void PublishAnimationControls()
    {
        if (AnimationTreeResolved is null)
        {
            return;
        }

        _ = PublishAnimationControls(GetCurrentLocomotionPermissions());
    }

    private LocomotionStateTarget PublishAnimationControls(LocomotionPermissions permissions)
    {
        Vector2 locomotionBlendInput = GetLocomotionBlendInput(permissions);
        float turnBlendInput = GetTurnBlendInput(permissions);
        LocomotionStateTarget locomotionStateTarget = ResolveLocomotionStateTarget();
        bool hasLocomotionIntent = !locomotionBlendInput.IsZeroApprox()
            || !Mathf.IsZeroApprox(turnBlendInput);
        UpdateLocomotionAnimationState(hasLocomotionIntent, locomotionStateTarget);
        UpdateAnimationBlend(AnimationBlendParameter, locomotionBlendInput, ref _warnedMissingAnimationBlendParameter, ref _warnedUnsupportedAnimationBlendParameterType);
        UpdateAnimationBlend(AnimationTurnBlendParameter, turnBlendInput, ref _warnedMissingAnimationTurnBlendParameter, ref _warnedUnsupportedAnimationTurnBlendParameterType);
        return locomotionStateTarget;
    }

    private Vector2 GetLocomotionBlendInput(LocomotionPermissions permissions)
        => ApplyMovementPermissions(_movementInput, permissions);

    private float GetTurnBlendInput(LocomotionPermissions permissions)
        => !permissions.RotationAllowed
            ? 0f
            : ComputeSmoothTurnBlend(
                 _rotationInput.X,
                 RotationSpeedMultiplier,
                 SmoothTurnSensitivity);

    private RootMotionSample ResolveRootMotionSample(
        double delta,
        LocomotionPermissions permissions,
        LocomotionStateTarget locomotionStateTarget)
    {
        if (AnimationTreeResolved is null || RootMotionReferenceResolved is null)
        {
            return default;
        }

        if (!IsRootMotionStateActive(locomotionStateTarget, out _))
        {
            return default;
        }

        Vector3 rootMotionDelta = GetRootMotionPositionDelta();
        Vector3 planarVelocity = Vector3.Zero;
        if (permissions.MovementAllowed && rootMotionDelta.IsFinite())
        {
            Vector3 worldRootMotionVelocity = GetRootMotionReferenceBasis() * rootMotionDelta / (float)delta;
            if (worldRootMotionVelocity.IsFinite())
            {
                planarVelocity = new Vector3(worldRootMotionVelocity.X, 0f, worldRootMotionVelocity.Z);
            }
        }

        float rootYawDelta = GetRootMotionYawDelta();
        float yawDelta = permissions.RotationAllowed && float.IsFinite(rootYawDelta)
            ? rootYawDelta
            : 0f;

        return new RootMotionSample(planarVelocity, yawDelta);
    }

    private void UpdateAnimationBlend(
        StringName parameter,
        Variant desiredValue,
        ref bool warnedMissing,
        ref bool warnedUnsupported)
    {
        if (AnimationTreeResolved is null || parameter.IsEmpty)
        {
            return;
        }

        StringName resolvedBlendParameter = ResolveAnimationParameter(parameter);
        Variant currentValue = AnimationTreeResolved.Get(resolvedBlendParameter);
        if (currentValue.VariantType == Variant.Type.Nil)
        {
            if (!warnedMissing)
            {
                GD.PushWarning(
                    $"{nameof(CharacterLocomotion)} could not resolve animation blend parameter '{parameter}'. " +
                    "Locomotion still runs, but blending remains blocked until the animation tree is reconciled.");
                warnedMissing = true;
            }

            return;
        }

        if (currentValue.VariantType is Variant.Type.Float or Variant.Type.Int)
        {
            float scalar = desiredValue.VariantType == Variant.Type.Vector2
                ? Mathf.Clamp(desiredValue.AsVector2().Length() / Mathf.Max(AnimationBlendThreshold, 1e-3f), 0f, 1f)
                : desiredValue.AsSingle();
            AnimationTreeResolved.Set(resolvedBlendParameter, scalar);
            return;
        }

        if (currentValue.VariantType == Variant.Type.Vector2 && desiredValue.VariantType == Variant.Type.Vector2)
        {
            AnimationTreeResolved.Set(resolvedBlendParameter, desiredValue.AsVector2());
            return;
        }

        if (currentValue.VariantType == Variant.Type.Vector2
            && desiredValue.VariantType is Variant.Type.Float or Variant.Type.Int)
        {
            float movementMagnitude = Mathf.Clamp(
                _movementInput.Length() / Mathf.Max(AnimationBlendThreshold, 1e-3f),
                0f,
                1f);
            AnimationTreeResolved.Set(
                resolvedBlendParameter,
                new Vector2(Mathf.Clamp(desiredValue.AsSingle(), -1f, 1f), movementMagnitude));
            return;
        }

        if (warnedUnsupported)
        {
            return;
        }

        GD.PushWarning(
            $"{nameof(CharacterLocomotion)} resolved animation blend parameter '{parameter}' " +
            $"with unsupported type '{currentValue.VariantType}'.");
        warnedUnsupported = true;
    }

    private void UpdateLocomotionAnimationState(
        bool hasLocomotionIntent,
        LocomotionStateTarget locomotionStateTarget)
    {
        AnimationNodeStateMachinePlayback? playback = ResolvePlayback();
        if (playback is null)
        {
            return;
        }

        StringName currentNode = playback.GetCurrentNode();
        StringName targetNode = hasLocomotionIntent
            ? locomotionStateTarget.MovementStateName
            : locomotionStateTarget.IdleStateName;

        if (currentNode == _stateMachineStartNodeName)
        {
            playback.Start(targetNode, reset: true);
            return;
        }

        if (currentNode == locomotionStateTarget.IdleStateName && hasLocomotionIntent)
        {
            playback.Travel(locomotionStateTarget.MovementStateName);
            return;
        }

        if (currentNode == locomotionStateTarget.MovementStateName && !hasLocomotionIntent)
        {
            playback.Travel(locomotionStateTarget.IdleStateName);
        }
    }

    private bool IsRootMotionStateActive(
        LocomotionStateTarget locomotionStateTarget,
        out StringName rootMotionStateName)
    {
        rootMotionStateName = locomotionStateTarget == GetDefaultLocomotionStateTarget()
            ? RootMotionAnimationStateName
            : locomotionStateTarget.MovementStateName;

        if (rootMotionStateName.IsEmpty)
        {
            return false;
        }

        AnimationNodeStateMachinePlayback? playback = ResolvePlayback();
        return playback is not null && playback.GetCurrentNode() == rootMotionStateName;
    }

    private LocomotionStateTarget ResolveLocomotionStateTarget()
        => GetLocomotionStateTarget() ?? GetDefaultLocomotionStateTarget();

    private LocomotionStateTarget GetDefaultLocomotionStateTarget()
        => new(IdleAnimationStateName, _walkingAnimationStateName);

    private AnimationNodeStateMachinePlayback? ResolvePlayback()
    {
        return AnimationTreeResolved is null
            ? null
            : AnimationTreeResolved.Get(_playbackParameter).As<AnimationNodeStateMachinePlayback>()
               // Compatibility for legacy/simple state-machine-only rigs where the state machine is the tree root.
               ?? AnimationTreeResolved.Get(_legacyRootPlaybackParameter).As<AnimationNodeStateMachinePlayback>();
    }

    private StringName ResolveAnimationParameter(StringName parameter)
    {
        if (AnimationTreeResolved is null || parameter.IsEmpty)
        {
            return parameter;
        }

        Variant value = AnimationTreeResolved.Get(parameter);
        if (value.VariantType != Variant.Type.Nil)
        {
            return parameter;
        }

        StringName nestedParameter = HandPoseAnimationTreePaths.GetNestedStateMachineParameter(parameter.ToString());
        Variant nestedValue = AnimationTreeResolved.Get(nestedParameter);
        return nestedValue.VariantType == Variant.Type.Nil ? parameter : nestedParameter;
    }

    private static Vector2 ApplyDeadzone(Vector2 input, float deadzone)
        => new(
            ApplyDeadzone(input.X, deadzone),
            ApplyDeadzone(input.Y, deadzone));

    private static Vector2 ApplyRadialDeadzone(Vector2 input, float deadzone)
    {
        if (!input.IsFinite())
        {
            return Vector2.Zero;
        }

        float threshold = Mathf.Clamp(deadzone, 0.0f, 0.999f);
        float length = input.Length();
        if (length <= threshold)
        {
            return Vector2.Zero;
        }

        float remappedLength = Mathf.Clamp((length - threshold) / (1.0f - threshold), 0.0f, 1.0f);
        return input * (remappedLength / length);
    }

    private CharacterBody3D GetTargetCharacterBody()
        => TargetCharacterBodyResolved
            ?? throw new InvalidOperationException($"{nameof(CharacterLocomotion)} target body is not available before _Ready.");

    /// <summary>
    /// Resolves the current locomotion root-motion position delta from the animation runtime.
    /// </summary>
    protected virtual Vector3 GetRootMotionPositionDelta() => AnimationTreeResolved?.GetRootMotionPosition() ?? Vector3.Zero;

    /// <summary>
    /// Resolves the current animation-owned root-motion yaw delta.
    /// </summary>
    protected virtual float GetRootMotionYawDelta()
    {
        Quaternion rotation = GetRootMotionRotation();
        return !float.IsFinite(rotation.X)
            || !float.IsFinite(rotation.Y)
            || !float.IsFinite(rotation.Z)
            || !float.IsFinite(rotation.W)
            ? 0f
            : rotation.GetEuler().Y;
    }

    /// <summary>
    /// Resolves the current animation-owned root-motion rotation delta.
    /// </summary>
    protected virtual Quaternion GetRootMotionRotation() => AnimationTreeResolved?.GetRootMotionRotation() ?? Quaternion.Identity;

    /// <summary>
    /// Resolves the world-space basis used to convert authored root motion into world-space velocity.
    /// </summary>
    protected virtual Basis GetRootMotionReferenceBasis() => RootMotionReferenceResolved?.GlobalBasis ?? Basis.Identity;

    /// <summary>
    /// Applies yaw rotation to the controlled character body.
    /// </summary>
    protected virtual void ApplyYawRotation(float yawDelta) => GetTargetCharacterBody().RotateY(yawDelta);

    private static float ApplyDeadzone(float input, float deadzone)
        => Mathf.Abs(input) >= Mathf.Clamp(deadzone, 0f, 1f) ? input : 0f;

    private static float ComputeSmoothTurnBlend(
        float inputX,
        float rotationSpeedMultiplier,
        float smoothTurnSensitivity)
    {
        return Mathf.IsZeroApprox(inputX)
            ? 0f
            : Mathf.Clamp(inputX * rotationSpeedMultiplier * smoothTurnSensitivity, -1f, 1f);
    }

    private readonly record struct RootMotionSample(Vector3 PlanarVelocity, float YawDelta);
}
