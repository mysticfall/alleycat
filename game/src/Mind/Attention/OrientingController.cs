using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Logging;
using AlleyCat.IK;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.Attention;

/// <summary>
/// Mind-owned head-orientation controller that adapts the owner character's attention gaze anchor into world-space
/// head IK intent (AI-009).
/// </summary>
/// <remarks>
/// <para>
/// The controller is a pure consumer of its owner character's <see cref="IVision"/>: it reads
/// <see cref="IVision.LookTarget"/> each frame — the assigned anchor's world position only, never saccade offsets —
/// and never calls <see cref="IVision.SetLookTarget"/> or <see cref="IVision.ClearLookTarget"/>. Gaze selection
/// semantics stay wholly with the AI-007 selector; this controller owns only when and how far the head follows.
/// It is composed as a direct child of its owner <see cref="Mind"/> in NPC role templates and wired by the NPC
/// installer into <see cref="CharacterIK.HeadTargetIntentProvider"/>, driving Neck-Spine CCDIK (IK-001).
/// </para>
/// <para>
/// Decision logic — engagement modes, glance classification, hysteresis, reaction delay, rate-limited smoothing,
/// and the influence ramp — lives in the Godot-free <see cref="OrientingPolicy"/> seam. This adapter owns every
/// world-transform ↔ angle conversion and forms the closed-loop servo required by AI-009 TR 14: the aim is an
/// absolute head-orientation offset applied to a tracked <em>neutral reference frame</em>, recomputed from the
/// current solved head each frame. The neutral reference lives in the avatar's <em>face frame</em> — the solved
/// head frame with the viewpoint marker's local rotation applied — because that is the frame where the eye-neutral
/// axis is the marker's −Z forward and the comfort-cone angles are meaningful; real reference rigs author the
/// viewpoint with a 180° yaw so the raw head-bone frame's forward is +Z, not −Z. The commanded aim is converted
/// back into the head-bone frame through the marker's inverse local rotation before publication, matching the
/// <see cref="XRHeadTargetIntentProvider"/> transform semantics that the Neck-Spine CCDIK consumes. The neutral
/// reference is re-anchored to the live solved head with the applied aim rotation removed whenever the solved head
/// confirms the previously commanded intent, so animation-driven head motion carries into the reference while
/// orienting rotation does not, and the live per-axis errors fed to the policy keep reflecting where the anchor
/// actually sits relative to where the head actually points. As the head turns, the residual the policy sees and
/// the IK solving residual both shrink, converging on full centring.
/// </para>
/// </remarks>
[GlobalClass]
public partial class OrientingController : IKTargetIntentProvider
{
    private const float NeutralTrackingToleranceRadians = 0.2f;
    private const float DirectionLengthEpsilonSquared = 1e-8f;

    private Mind? _mind;
    private ICharacter? _character;
    private IComponentProjectionNotifier? _componentProjectionNotifier;
    private IVision? _vision;
    private OrientingSettings? _settings;
    private OrientingPolicy? _policy;
    private ILogger<OrientingController>? _logger;
    private bool _isReady;
    private bool _warnedMissingViewpoint;

    private Marker3D? _viewpointCache;
    private Transform3D _viewpointLocalInverseTransform = Transform3D.Identity;
    private Basis _viewpointLocalBasis = Basis.Identity;
    private Node3D? _lastAnchor;
    private Basis _neutralBasis = Basis.Identity;
    private Basis _appliedAimBasis = Basis.Identity;
    private Basis _lastIntentBasis = Basis.Identity;
    private bool _neutralCaptured;
    private IKTargetIntent _currentIntent = new(Transform3D.Identity, 0f);

    /// <summary>
    /// Avatar viewpoint marker representing the eye-centre in avatar space. The marker's parent frame is the solved
    /// head frame this controller measures and commands, matching the <see cref="XRHeadTargetIntentProvider"/>
    /// transform semantics.
    /// </summary>
    [ExportGroup("Targets")]
    [Export]
    public Marker3D? Viewpoint
    {
        get;
        set;
    }

    /// <summary>Symmetric horizontal eye comfort cone half-angle in degrees.</summary>
    [ExportGroup("Comfort Cone")]
    [Export(PropertyHint.Range, "0.01,90,0.01,or_greater")]
    public float ComfortConeHorizontalDegrees
    {
        get; set;
    } = ToDegrees(OrientingSettings.Default.ComfortConeHorizontalRadians);

    /// <summary>Upward eye comfort cone angle in degrees.</summary>
    [Export(PropertyHint.Range, "0.01,90,0.01,or_greater")]
    public float ComfortConeUpDegrees
    {
        get; set;
    } = ToDegrees(OrientingSettings.Default.ComfortConeUpRadians);

    /// <summary>Downward eye comfort cone angle in degrees.</summary>
    [Export(PropertyHint.Range, "0.01,90,0.01,or_greater")]
    public float ComfortConeDownDegrees
    {
        get; set;
    } = ToDegrees(OrientingSettings.Default.ComfortConeDownRadians);

    /// <summary>Symmetric horizontal head orientation envelope in degrees.</summary>
    [ExportGroup("Orientation Envelope")]
    [Export(PropertyHint.Range, "0.01,180,0.01,or_greater")]
    public float EnvelopeHorizontalDegrees
    {
        get; set;
    } = ToDegrees(OrientingSettings.Default.EnvelopeHorizontalRadians);

    /// <summary>Upward head orientation envelope in degrees.</summary>
    [Export(PropertyHint.Range, "0.01,180,0.01,or_greater")]
    public float EnvelopeUpDegrees
    {
        get; set;
    } = ToDegrees(OrientingSettings.Default.EnvelopeUpRadians);

    /// <summary>Downward head orientation envelope in degrees.</summary>
    [Export(PropertyHint.Range, "0.01,180,0.01,or_greater")]
    public float EnvelopeDownDegrees
    {
        get; set;
    } = ToDegrees(OrientingSettings.Default.EnvelopeDownRadians);

    /// <summary>Continuous same-anchor assignment required before sustained centring engages, in seconds.</summary>
    [ExportGroup("Timing")]
    [Export(PropertyHint.Range, "0.01,60,0.01,or_greater")]
    public float CentringDelaySeconds
    {
        get; set;
    } = (float)OrientingSettings.Default.CentringDelaySeconds;

    /// <summary>Engagement pause before the head starts toward a newly engaged aim, in seconds.</summary>
    [Export(PropertyHint.Range, "0.01,5,0.005,or_greater")]
    public float ReactionDelaySeconds
    {
        get; set;
    } = (float)OrientingSettings.Default.ReactionDelaySeconds;

    /// <summary>Horizontal head aim rate cap in degrees per second.</summary>
    [ExportGroup("Motion")]
    [Export(PropertyHint.Range, "0.01,1080,0.01,or_greater")]
    public float MaxHorizontalRateDegreesPerSecond
    {
        get; set;
    } = ToDegrees(OrientingSettings.Default.MaxHorizontalRateRadiansPerSecond);

    /// <summary>Vertical head aim rate cap in degrees per second.</summary>
    [Export(PropertyHint.Range, "0.01,1080,0.01,or_greater")]
    public float MaxVerticalRateDegreesPerSecond
    {
        get; set;
    } = ToDegrees(OrientingSettings.Default.MaxVerticalRateRadiansPerSecond);

    /// <summary>Exponential approach time constant for aim smoothing, in seconds.</summary>
    [Export(PropertyHint.Range, "0.005,5,0.005,or_greater")]
    public float AimSmoothingSeconds
    {
        get; set;
    } = (float)OrientingSettings.Default.AimSmoothingSeconds;

    /// <summary>Influence ramp rate while an anchor is assigned, per second.</summary>
    [Export(PropertyHint.Range, "0.1,20,0.1,or_greater")]
    public float InfluenceEngagePerSecond
    {
        get; set;
    } = (float)OrientingSettings.Default.InfluenceEngagePerSecond;

    /// <summary>Influence ramp rate while no anchor is assigned, per second.</summary>
    [Export(PropertyHint.Range, "0.1,20,0.1,or_greater")]
    public float InfluenceReleasePerSecond
    {
        get; set;
    } = (float)OrientingSettings.Default.InfluenceReleasePerSecond;

    /// <summary>Extra angle beyond the comfort cone required to engage saturation, in degrees.</summary>
    [ExportGroup("Hysteresis")]
    [Export(PropertyHint.Range, "0,45,0.01")]
    public float SaturationEngageMarginDegrees
    {
        get; set;
    } = ToDegrees(OrientingSettings.Default.SaturationEngageMarginRadians);

    /// <summary>Angle back inside the comfort cone required to release an engaged saturation axis, in degrees.</summary>
    [Export(PropertyHint.Range, "0,45,0.01")]
    public float SaturationReleaseMarginDegrees
    {
        get; set;
    } = ToDegrees(OrientingSettings.Default.SaturationReleaseMarginRadians);

    /// <summary>Horizontal angle the sustained aim deliberately leaves short of full centring, in degrees.</summary>
    [ExportGroup("Eccentricity")]
    [Export(PropertyHint.Range, "0,45,0.01")]
    public float ResidualEccentricityHorizontalDegrees
    {
        get; set;
    } = ToDegrees(OrientingSettings.Default.ResidualEccentricityHorizontalRadians);

    /// <summary>Vertical angle the sustained aim deliberately leaves short of full centring, in degrees.</summary>
    [Export(PropertyHint.Range, "0,45,0.01")]
    public float ResidualEccentricityVerticalDegrees
    {
        get; set;
    } = ToDegrees(OrientingSettings.Default.ResidualEccentricityVerticalRadians);

    /// <inheritdoc />
    public override IKTargetIntent GetTargetIntent() => _currentIntent;

    /// <inheritdoc />
    public override void _Ready()
    {
        _settings = CreateValidatedSettings();
        _mind = GetParent() as Mind
            ?? throw new InvalidOperationException(
                $"Orienting controller '{GetPath()}' requires a direct {nameof(Mind)} parent.");
        _character = _mind.OwningCharacter;
        _componentProjectionNotifier = _character as IComponentProjectionNotifier
            ?? throw new InvalidOperationException(
                $"Orienting controller '{GetPath()}' requires its owning character '{_character.GetType().FullName}' to implement {nameof(IComponentProjectionNotifier)}.");

        _componentProjectionNotifier.ComponentsRefreshed += OnComponentsRefreshed;
        _isReady = true;

        if (_componentProjectionNotifier.HasComponentProjection)
        {
            BindVisionAfterProjection();
        }
        else
        {
            LogWaitingForProjection();
        }
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (!_isReady || _vision is null || _policy is null)
        {
            return;
        }

        if (!double.IsFinite(delta) || delta < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delta),
                delta,
                "Orienting controller process delta must be finite and non-negative.");
        }

        if (!TryResolveHeadFrame(out Transform3D headFrame, out Vector3 eyeOriginGlobalPosition))
        {
            ProvideIdleIntent();
            return;
        }

        // The face frame is the solved head frame with the viewpoint marker's local rotation applied: its −Z axis
        // is the eye-neutral forward, so every angle the policy consumes is measured in the frame the comfort cone
        // and envelope describe, independent of the rig's head-bone forward axis convention.
        Basis solvedFaceBasis = (headFrame.Basis * _viewpointLocalBasis).Orthonormalized();
        UpdateNeutralBasis(solvedFaceBasis);

        Node3D? anchor = _vision.LookTarget;
        if (anchor is not null && !IsInstanceValid(anchor))
        {
            anchor = null;
        }

        OrientingAnchorState anchorState = ResolveAnchorState(anchor);
        (double horizontalErrorRadians, double verticalErrorRadians) = anchorState == OrientingAnchorState.None
            ? (0d, 0d)
            : ResolveAngularErrors(anchor!, eyeOriginGlobalPosition);

        OrientingAim aim = _policy.Evaluate(
            new OrientingEvaluation(delta, anchorState, horizontalErrorRadians, verticalErrorRadians));

        // The aim is an absolute yaw-then-pitch offset from the neutral face reference; composing it as a local
        // rotation of the reference basis matches the eye-neutral-axis sign convention of the fed errors, so a
        // converged aim places the anchor on the face forward axis.
        var aimBasis = Basis.FromEuler(
            new Vector3((float)aim.VerticalRadians, (float)aim.HorizontalRadians, 0f));
        Basis faceIntentBasis = (_neutralBasis * aimBasis).Orthonormalized();
        // Convert the commanded face orientation back into the solved head-bone frame through the marker's inverse
        // local rotation — the same transform relation XRHeadTargetIntentProvider publishes for the CCDIK target.
        Basis intentBasis = (faceIntentBasis * _viewpointLocalBasis.Inverse()).Orthonormalized();
        _appliedAimBasis = aimBasis;
        _lastIntentBasis = faceIntentBasis;
        _currentIntent = new IKTargetIntent(
            new Transform3D(intentBasis, headFrame.Origin),
            (float)Math.Clamp(aim.Influence, 0d, 1d));
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_componentProjectionNotifier is { } componentProjectionNotifier)
        {
            componentProjectionNotifier.ComponentsRefreshed -= OnComponentsRefreshed;
            _componentProjectionNotifier = null;
        }

        _vision = null;
        _policy = null;
        _character = null;
        _mind = null;
        _isReady = false;
        ResetServoState();
        _viewpointCache = null;
        _viewpointLocalInverseTransform = Transform3D.Identity;
        _viewpointLocalBasis = Basis.Identity;

        // Teardown stops providing intent so CharacterIK falls back to its safe idle (AI-009 TR 17).
        _currentIntent = new IKTargetIntent(Transform3D.Identity, 0f);
    }

    private OrientingSettings CreateValidatedSettings()
    {
        OrientingSettings settings = new(
            ToRadians(ComfortConeHorizontalDegrees),
            ToRadians(ComfortConeUpDegrees),
            ToRadians(ComfortConeDownDegrees),
            ToRadians(EnvelopeHorizontalDegrees),
            ToRadians(EnvelopeUpDegrees),
            ToRadians(EnvelopeDownDegrees),
            CentringDelaySeconds,
            ReactionDelaySeconds,
            ToRadians(MaxHorizontalRateDegreesPerSecond),
            ToRadians(MaxVerticalRateDegreesPerSecond),
            AimSmoothingSeconds,
            InfluenceEngagePerSecond,
            InfluenceReleasePerSecond,
            ToRadians(SaturationEngageMarginDegrees),
            ToRadians(SaturationReleaseMarginDegrees),
            ToRadians(ResidualEccentricityHorizontalDegrees),
            ToRadians(ResidualEccentricityVerticalDegrees));

        OrientingSettingsValidation validation = settings.Validate();
        return !validation.IsValid
            ? throw new InvalidOperationException(
                $"Orienting controller '{GetPath()}' has invalid orienting authoring: {validation.FailureReason}")
            : settings;
    }

    private void OnComponentsRefreshed()
    {
        if (_isReady)
        {
            BindVisionAfterProjection();
        }
    }

    private void BindVisionAfterProjection()
    {
        ICharacter character = _character
            ?? throw new InvalidOperationException("Orienting controller has no resolved owning character after activation.");
        IVision vision = character.RequireVision();
        if (ReferenceEquals(_vision, vision))
        {
            LogProjectionRefreshWithoutVisionChange();
            return;
        }

        _vision = vision;
        _policy = new OrientingPolicy(
            _settings ?? throw new InvalidOperationException("Orienting controller settings were not initialised."));
        ResetServoState();
        LogVisionBound(vision);
    }

    private void ResetServoState()
    {
        _lastAnchor = null;
        _neutralBasis = Basis.Identity;
        _appliedAimBasis = Basis.Identity;
        _lastIntentBasis = Basis.Identity;
        _neutralCaptured = false;
    }

    private bool TryResolveHeadFrame(out Transform3D headFrame, out Vector3 eyeOriginGlobalPosition)
    {
        Marker3D? viewpoint = Viewpoint;
        if (viewpoint is null || !IsInstanceValid(viewpoint))
        {
            headFrame = Transform3D.Identity;
            eyeOriginGlobalPosition = Vector3.Zero;
            return false;
        }

        if (!ReferenceEquals(_viewpointCache, viewpoint))
        {
            _viewpointCache = viewpoint;
            _viewpointLocalInverseTransform = viewpoint.Transform.Inverse();
            _viewpointLocalBasis = viewpoint.Transform.Basis.Orthonormalized();
            ResetServoState();
        }

        // The viewpoint marker is parented to the solved head frame, so mapping its world pose back through its local
        // transform yields the current solved head frame — the same relation XRHeadTargetIntentProvider uses to
        // derive the player's head target, keeping the Neck-Spine CCDIK interpretation identical.
        headFrame = viewpoint.GlobalTransform * _viewpointLocalInverseTransform;
        eyeOriginGlobalPosition = viewpoint.GlobalPosition;
        return true;
    }

    private void UpdateNeutralBasis(Basis solvedFaceBasis)
    {
        if (!_neutralCaptured)
        {
            _neutralBasis = solvedFaceBasis;
            _appliedAimBasis = Basis.Identity;
            _lastIntentBasis = solvedFaceBasis;
            _neutralCaptured = true;
            return;
        }

        if (new Quaternion(solvedFaceBasis).AngleTo(new Quaternion(_lastIntentBasis)) <= NeutralTrackingToleranceRadians)
        {
            // The solved head confirmed the previously commanded intent: re-anchor the neutral reference to the live
            // solved face orientation with the applied aim rotation removed, so animation-driven head motion carries
            // into the reference while orienting rotation does not.
            _neutralBasis = (solvedFaceBasis * _appliedAimBasis.Inverse()).Orthonormalized();
        }

        // Otherwise the solved head has not confirmed the previous intent — transit lag beyond the tolerance or
        // joint-constraint strain — so hold the reference and keep commanding the strain.
    }

    private OrientingAnchorState ResolveAnchorState(Node3D? anchor)
    {
        if (anchor is null)
        {
            _lastAnchor = null;
            return OrientingAnchorState.None;
        }

        if (ReferenceEquals(anchor, _lastAnchor))
        {
            return OrientingAnchorState.SameAnchor;
        }

        _lastAnchor = anchor;
        LogAnchorAssigned(anchor);
        return OrientingAnchorState.NewAnchor;
    }

    private (double HorizontalRadians, double VerticalRadians) ResolveAngularErrors(
        Node3D anchor,
        Vector3 eyeOriginGlobalPosition)
    {
        Vector3 localDirection = _neutralBasis.Transposed() * (anchor.GlobalPosition - eyeOriginGlobalPosition);
        float horizontalReachSquared = (localDirection.X * localDirection.X) + (localDirection.Z * localDirection.Z);
        float lengthSquared = horizontalReachSquared + (localDirection.Y * localDirection.Y);
        if (lengthSquared <= DirectionLengthEpsilonSquared)
        {
            return (0d, 0d);
        }

        // Sign convention: a positive error means a positive head rotation centres the anchor — positive horizontal
        // is yaw towards the anchor from the eye-neutral axis (a positive Godot +Y yaw turns the forward axis to the
        // left), and positive vertical is upward pitch.
        double horizontalRadians = Math.Atan2(-localDirection.X, -localDirection.Z);
        double verticalRadians = Math.Atan2(localDirection.Y, Math.Sqrt(horizontalReachSquared));
        return (horizontalRadians, verticalRadians);
    }

    private void ProvideIdleIntent()
    {
        _currentIntent = new IKTargetIntent(Transform3D.Identity, 0f);
        if (!_warnedMissingViewpoint)
        {
            _warnedMissingViewpoint = true;
            ILogger<OrientingController>? logger = TryGetLogger();
            if (logger?.IsEnabled(LogLevel.Warning) == true)
            {
                logger.LogWarning(
                    "Orienting controller '{Path}' has no resolved viewpoint; providing idle intent with zero influence.",
                    GetPath());
            }
        }
    }

    private void LogWaitingForProjection()
    {
        ILogger<OrientingController>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Debug) == true)
        {
            logger.LogDebug("Orienting controller is waiting for its owner's component projection.");
        }
    }

    private void LogVisionBound(IVision vision)
    {
        ILogger<OrientingController>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Debug) == true)
        {
            logger.LogDebug(
                "Orienting controller bound Vision component {VisionType} after component projection.",
                vision.GetType().FullName ?? vision.GetType().Name);
        }
    }

    private void LogProjectionRefreshWithoutVisionChange()
    {
        ILogger<OrientingController>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Debug) == true)
        {
            logger.LogDebug("Orienting controller observed a component projection refresh without a Vision binding change.");
        }
    }

    private void LogAnchorAssigned(Node3D anchor)
    {
        ILogger<OrientingController>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Trace) == true)
        {
            logger.LogTrace("Orienting controller observed a new look-target anchor '{AnchorPath}'.", anchor.GetPath());
        }
    }

    private ILogger<OrientingController>? TryGetLogger()
    {
        if (_logger is null && GameLoggerResolver.TryResolve(out ILogger<OrientingController>? logger))
        {
            _logger = logger;
        }

        return _logger;
    }

    private static float ToDegrees(double radians) => (float)(radians * 180d / Math.PI);

    private static double ToRadians(float degrees) => degrees * Math.PI / 180d;
}
