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
/// <para>
/// The published face intent is roll-free by construction and its origin is constructed
/// non-recursively from an independent upstream snapshot (AI-009 TR 19–22, HEAD-1): each evaluation
/// captures the native skeleton Head and Neck bone poses with the authored face/Viewpoint and pivot
/// offsets — the animation-owned upstream values the engine restores between skeleton updates, never
/// solved attachment globals — and with upstream head origin <c>h</c>, pivot position <c>p</c>, upstream
/// face rotation <c>U</c> and commanded face rotation <c>D</c> publishes
/// <c>R = D·inverse(U)</c>, <c>target = p + R·(h − p)</c>. Solved output is not an operand, so the solve
/// cannot advance its own target; the solved reads the servo still consumes (angular errors, neutral
/// re-anchoring) are explicitly k−1 feedback from the previous completed evaluation, bootstrapped with
/// marked upstream samples until the first solve completes. Without a valid pivot the origin is the
/// upstream head origin (TR 22). On rigs inside the fold family's supported authoring domain, one
/// shared per-evaluation frame — anchored to the animation-owned upstream pose — resolves BOTH the
/// coupled gaze prediction and the published intent origin for EVERY command: the frame's continuous
/// engagement blend reproduces the TR 21 orbit exactly on the neutral/upward regime and the
/// yaw-composed fold family on the downward regime, with no command gate. Node-authored rigs without a
/// skeleton keep the legacy solved-frame pivot construction, which production templates never author.
/// </para>
/// </remarks>
[GlobalClass]
public partial class OrientingController : IKTargetIntentProvider, IIKBeginEvaluable
{
    private const float DirectionLengthEpsilonSquared = 1e-8f;
    private const float ReferenceUpParallelDotLimit = 0.9999f;

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
    private IKTargetIntent _currentIntent = new(Transform3D.Identity, 0f);
    private Basis _legacyRestFaceBasis = Basis.Identity;
    private Vector3 _legacyRestHeadOrigin;
    private bool _legacyRestCaptured;

    private UpstreamBinding? _upstreamBinding;
    private ulong _evaluationSequence;
    private ulong _nodeFallbackEvaluationSequence;
    private ulong _bindingGeneration;
    private Skeleton3D? _bootstrapSkeleton;
    private ulong? _pendingEvaluation;
    private FinalFeedbackPacket? _completedFeedback;
    private Skeleton3D? _beginSkeleton;
    private bool _hasBeginAim;

    internal ulong? LastFeedbackSequence
    {
        get; private set;
    }
    internal bool LastFeedbackIsBootstrap { get; private set; } = true;
    internal Transform3D LastFeedbackHeadFrame
    {
        get; private set;
    }
    internal Vector3 LastFeedbackEyeOrigin
    {
        get; private set;
    }

    private readonly record struct FinalFeedbackPacket(
        ulong Sequence, ulong Generation, ulong SkeletonIdentity, Transform3D CaptureRoot,
        Transform3D HeadRelative, Vector3 EyeRelative, Basis FaceRelative);
    private bool _warnedInvalidUpstreamAuthoring;
    private bool _warnedFoldChainUnsupported;
    private readonly float[] _foldGridAngleScratch = new float[DownwardFoldGeometry.GridSampleCount];

    /// <summary>
    /// The most recently captured upstream snapshot consumed by the aimed-origin construction, exposed
    /// for diagnostics and independent oracles (never a second evaluation channel).
    /// </summary>
    internal OrientingUpstreamSnapshot? LastUpstreamSnapshot
    {
        get;
        private set;
    }

    /// <summary>
    /// The requested aimed-origin target of the most recent evaluation (AI-009 TR 21: "record requested
    /// and mediated targets"). The mediated target — what the head pipeline actually applies after its
    /// own mediation — is observable at the head solve-target node, which this controller does not own.
    /// </summary>
    internal Vector3 LastRequestedAimTargetGlobal
    {
        get;
        private set;
    }

    /// <summary>
    /// Read-only diagnostic (E1 park-exclusion wiring): the command the policy most recently published,
    /// in the character envelope frame. Diagnostic only — never an input to controller behaviour.
    /// </summary>
    internal OrientingAim LastPublishedAim
    {
        get;
        private set;
    }

    /// <summary>
    /// This evaluation's shared fold-family prediction frame (envelope frame), when the bound rig and
    /// neutral upstream satisfy the fold domain; otherwise null. The same frame object feeds the
    /// geometric gaze prediction and the published intent origin, so both consume one geometry.
    /// </summary>
    internal DownwardFoldPredictionFrame? LastFoldFrame
    {
        get;
        private set;
    }

    /// <summary>
    /// Machine-readable status of the downward fold construction for the most recent evaluation:
    /// <c>fold_governed</c> (downward regime, fold family fully engaged), <c>engagement_blend</c>
    /// (continuous pitch entry band), <c>orbit_path</c> (neutral/upward regime keeps the exact TR 21
    /// construction), <c>degenerate_orbit_direction</c>, <c>not_bound:&lt;reason&gt;</c>,
    /// <c>no_skeleton_snapshot</c>, or the frame-build's outside-domain reason. Diagnostic only.
    /// </summary>
    internal string LastFoldStatus
    {
        get;
        private set;
    } = "not_evaluated";

    /// <summary>
    /// The most recent fold-publication diagnostic (world-space selection record). Present for every
    /// skeleton-backed evaluation whose frame was built, including abstaining commands. Diagnostic only.
    /// </summary>
    internal DownwardFoldEvaluation? LastFoldEvaluation
    {
        get;
        private set;
    }

    /// <summary>
    /// The most recent geometric solve the bound policy performed (saturation or centring), or null when
    /// the policy held or released this evaluation. Diagnostic only — regression oracles use it to verify
    /// prediction/publication identity.
    /// </summary>
    internal GazeSaturationResult? LastPolicySolve => _policy?.LastSolveResult;

    /// <summary>
    /// The authored viewpoint (eye-centre) offset in upstream face-local coordinates as last fed to the
    /// geometric feedback. Diagnostic only.
    /// </summary>
    internal Vector3 LastViewpointOffsetEnvelope
    {
        get;
        private set;
    }

    /// <summary>
    /// Cumulative per-evaluation fold frame builds since this controller's creation (bounded runtime-cost
    /// diagnostic; each build is a fixed 33-sample FK plus domain validation).
    /// </summary>
    internal long FoldFrameBuilds
    {
        get;
        private set;
    }

    /// <summary>
    /// Binding to the authored upstream geometry (AI-009 TR 14): the skeleton, the Head bone feeding the
    /// face/Viewpoint, the pivot bone feeding NeckPivot, and the authored marker-to-bone offsets. Owned
    /// by one binding generation; rebinding or teardown invalidates it. The fold-family chain authoring
    /// (or its unsupported reason) is cached per generation and never re-discovered per evaluation.
    /// </summary>
    /// <param name="Skeleton"></param>
    /// <param name="SkeletonIdentity"></param>
    /// <param name="HeadBoneIndex"></param>
    /// <param name="PivotBoneIndex"></param>
    /// <param name="PivotMarker">Marker identity; equal offsets do not make a replacement the same binding.</param>
    /// <param name="ViewpointOffsetToBone"></param>
    /// <param name="PivotOffsetToBone"></param>
    /// <param name="FoldChain">Cached fold-family chain authoring when the rig satisfies the supported
    /// authoring domain; otherwise null.</param>
    /// <param name="FoldUnsupportedReason">First unsupported-authoring reason when <paramref name="FoldChain"/>
    /// is null.</param>
    /// <param name="Generation">Generation counter, incremented on every (re)bind and teardown.</param>
    private sealed record UpstreamBinding(
        Skeleton3D Skeleton,
        ulong SkeletonIdentity,
        int HeadBoneIndex,
        int? PivotBoneIndex,
        Node3D? PivotMarker,
        Transform3D ViewpointOffsetToBone,
        Transform3D? PivotOffsetToBone,
        DownwardFoldChainAuthoring? FoldChain,
        string? FoldUnsupportedReason,
        ulong Generation);

    /// <summary>
    /// Solved feedback sample (AI-009 TR 14): at evaluation k this is the completed evaluation k−1's
    /// solved output, read directly from final bones and transported through the current root — never
    /// the current solve. Before a matching completion this is an upstream bootstrap sample, which
    /// <paramref name="FromCompletedSolve"/> marks explicitly.
    /// </summary>
    private readonly record struct SolvedFeedbackSample(
        Transform3D HeadFrame,
        Vector3 EyeOriginWorld,
        Basis FaceBasis,
        bool FromCompletedSolve);

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

    /// <summary>
    /// Optional neck articulation marker the published intent origin pivots about (AI-009 TR 21), authored under
    /// the IK-001 template contract. While a valid pivot is assigned, the intent origin swings about it by the
    /// commanded aim rotation, driving the Neck-Spine CCDIK chain to distribute the look across the neck and upper
    /// spine. While unset or invalid, the controller publishes the intent origin at the current solved head origin.
    /// </summary>
    [Export]
    public Node3D? NeckPivot
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
        // Skeleton-backed providers are owned exclusively by the installed CharacterIK Begin boundary.
        // Keep the node-authored compatibility path for consumers without a skeleton.
        if (Viewpoint is { } viewpoint && IsInstanceValid(viewpoint)
            && ResolveBoneChain(viewpoint, out _, out _, out _) != BoneChainResult.NodeAuthored)
        {
            return;
        }

        if (_upstreamBinding is not null)
        {
            ResetServoState();
        }
        _beginSkeleton = null;
        EvaluateIntent(delta);
    }

    /// <inheritdoc />
    public void EvaluateAtBegin(Skeleton3D skeleton, double delta, ulong sequence)
    {
        _beginSkeleton = skeleton;
        _evaluationSequence = sequence;
        _pendingEvaluation = null;
        EvaluateIntent(delta);
    }

    private void EvaluateIntent(double delta)
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
            ResetServoState();
            ProvideIdleIntent();
            return;
        }

        UpstreamCaptureStatus upstreamStatus = CaptureUpstreamSnapshot(delta, out OrientingUpstreamSnapshot? upstreamSnapshot);
        if (upstreamStatus == UpstreamCaptureStatus.InvalidAuthoring)
        {
            // Missing required upstream input makes the provider inactive (AI-009 TR 17); it must never
            // reconstruct a neutral reference from solved output.
            ResetServoState();
            ProvideIdleIntentForInvalidUpstreamAuthoring();
            return;
        }

        SolvedFeedbackSample solvedFeedback = ResolveSolvedFeedback(headFrame, eyeOriginGlobalPosition, upstreamSnapshot);
        LastFeedbackHeadFrame = solvedFeedback.HeadFrame;
        LastFeedbackEyeOrigin = solvedFeedback.EyeOriginWorld;
        LastFeedbackIsBootstrap = !solvedFeedback.FromCompletedSolve;
        if (upstreamSnapshot is not null && _beginSkeleton is not null)
        {
            _pendingEvaluation = upstreamSnapshot.EvaluationSequence;
        }

        // The character envelope frame: the stable anatomical frame the command lives in (HEAD-2). Its up axis
        // is the roll reference, and root motion transports commands without changing their meaning.
        Basis envelope = ResolveEnvelopeBasis();
        Vector3 characterOrigin = _character?.GlobalTransform.Origin ?? Vector3.Zero;
        Vector3 referenceUp = ResolveReferenceUp();

        // The shared fold-family frame (one geometry for prediction AND publication) is built once per
        // evaluation from the same snapshot and envelope the feedback uses; outside its supported domain
        // the frame is null and every construction keeps the unchanged TR 21 orbit path.
        DownwardFoldPredictionFrame? foldFrame = BuildFoldFrameIfSupported(
            upstreamSnapshot, envelope, characterOrigin, out string foldStatus);
        LastFoldFrame = foldFrame;
        LastFoldStatus = foldStatus;

        Node3D? anchor = _vision.LookTarget;
        if (anchor is not null && !IsInstanceValid(anchor))
        {
            anchor = null;
        }

        OrientingAnchorState anchorState = delta == 0d && _hasBeginAim
            ? anchor is null ? OrientingAnchorState.None
                : ReferenceEquals(anchor, _lastAnchor) ? OrientingAnchorState.SameAnchor : OrientingAnchorState.NewAnchor
            : ResolveAnchorState(anchor);
        GeometricFeedbackSnapshot feedback = BuildGeometricFeedback(
            envelope, characterOrigin, anchor, solvedFeedback, upstreamSnapshot, foldFrame);
        OrientingAim aim = delta == 0d && _hasBeginAim ? LastPublishedAim : _policy.Evaluate(new OrientingEvaluation(
            delta,
            anchorState,
            upstreamSnapshot?.EvaluationSequence ?? ++_nodeFallbackEvaluationSequence,
            feedback));
        _hasBeginAim = true;

        // The command is an orientation in the envelope frame; composing it through the envelope basis yields
        // the roll-free world face intent (HEAD-2 stable-frame command — no solved-fed neutral re-anchor).
        Basis aimWorld = envelope * GeometricGazeSaturation.ComposeCommandBasis(aim.HorizontalRadians, aim.VerticalRadians);
        Basis faceIntentBasis = BuildRollFreeBasis(aimWorld.Orthonormalized(), referenceUp, envelope.Y);
        // Convert the commanded face orientation back into the solved head-bone frame through the marker's inverse
        // local rotation — the same transform relation XRHeadTargetIntentProvider publishes for the CCDIK target.
        Basis intentBasis = (faceIntentBasis * _viewpointLocalBasis.Inverse()).Orthonormalized();
        LastPublishedAim = aim;
        _currentIntent = new IKTargetIntent(
            new Transform3D(
                intentBasis,
                ResolveAimedIntentOrigin(solvedFeedback, aim, faceIntentBasis, upstreamSnapshot, foldFrame)),
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
        _beginSkeleton = null;
        _viewpointCache = null;
        _viewpointLocalInverseTransform = Transform3D.Identity;
        _viewpointLocalBasis = Basis.Identity;
        LastRequestedAimTargetGlobal = Vector3.Zero;
        LastPublishedAim = default;

        // Teardown stops providing intent so CharacterIK falls back to its safe idle (AI-009 TR 17).
        _currentIntent = new IKTargetIntent(Transform3D.Identity, 0f);
        LastFoldFrame = null;
        LastFoldEvaluation = null;
        LastFoldStatus = "not_evaluated";
        LastViewpointOffsetEnvelope = Vector3.Zero;
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
        // Upstream and feedback snapshots invalidate with the servo state (AI-009 TR 17): rebinding
        // re-resolves the binding with a fresh generation and re-enters the bootstrap marking.
        DisposeBootstrapHook();
        _upstreamBinding = null;
        LastUpstreamSnapshot = null;
        _pendingEvaluation = null;
        _completedFeedback = null;
        LastFeedbackSequence = null;
        LastFeedbackIsBootstrap = true;
        _hasBeginAim = false;
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
            _legacyRestFaceBasis = Basis.Identity;
            _legacyRestHeadOrigin = Vector3.Zero;
            _legacyRestCaptured = false;
            ResetServoState();
        }

        // The viewpoint marker is parented to the solved head frame, so mapping its world pose back through its local
        // transform yields the current solved head frame — the same relation XRHeadTargetIntentProvider uses to
        // derive the player's head target, keeping the Neck-Spine CCDIK interpretation identical.
        headFrame = viewpoint.GlobalTransform * _viewpointLocalInverseTransform;
        eyeOriginGlobalPosition = viewpoint.GlobalPosition;
        if (!_legacyRestCaptured)
        {
            // Bootstrap reference for the node-authored legacy path: the first-seen face basis, held stable for
            // the viewpoint's lifetime (never re-anchored from solved output).
            _legacyRestFaceBasis = (headFrame.Basis * _viewpointLocalBasis).Orthonormalized();
            _legacyRestHeadOrigin = headFrame.Origin;
            _legacyRestCaptured = true;
        }

        return true;
    }

    /// <summary>
    /// The character envelope frame (HEAD-2): the owning character's orthonormalised global basis — the stable
    /// anatomical frame whose up axis is the roll reference and whose coordinates the command lives in. Falls
    /// back to the world frame when the character cannot be resolved.
    /// </summary>
    private Basis ResolveEnvelopeBasis()
        => (_character?.GlobalTransform.Basis ?? Basis.Identity).Orthonormalized();

    /// <summary>
    /// Builds the geometric feedback snapshot (AI-009 TR 14) in the character envelope frame: the anchor and
    /// the k−1 solved face/eye geometry, plus the upstream snapshot geometry and this evaluation's shared
    /// fold-family frame. Node-authored rigs — which production never authors — approximate the upstream
    /// pose with the node head frame, since no engine solve exists there.
    /// </summary>
    private GeometricFeedbackSnapshot BuildGeometricFeedback(
        Basis envelope,
        Vector3 characterOrigin,
        Node3D? anchor,
        in SolvedFeedbackSample solvedFeedback,
        OrientingUpstreamSnapshot? upstreamSnapshot,
        DownwardFoldPredictionFrame? foldFrame)
    {
        Basis envelopeInverse = envelope.Inverse();
        Vector3 ToEnvelope(Vector3 worldPosition)
        {
            return envelopeInverse * (worldPosition - characterOrigin);
        }

        Basis ToEnvelopeBasis(Basis worldBasis)
        {
            return (envelopeInverse * worldBasis).Orthonormalized();
        }

        Vector3 anchorPosition = ToEnvelope(anchor?.GlobalPosition ?? solvedFeedback.EyeOriginWorld);
        Vector3 solvedEyeOrigin = ToEnvelope(solvedFeedback.EyeOriginWorld);
        Basis solvedFaceBasis = ToEnvelopeBasis(solvedFeedback.FaceBasis);
        Vector3 viewpointOffset = _upstreamBinding is { } binding
            ? binding.ViewpointOffsetToBone.Basis.Orthonormalized().Inverse() * binding.ViewpointOffsetToBone.Origin
            : _viewpointLocalBasis.Inverse() * (Viewpoint?.Transform.Origin ?? Vector3.Zero);
        LastViewpointOffsetEnvelope = viewpointOffset;

        if (upstreamSnapshot is { } snapshot)
        {
            return new GeometricFeedbackSnapshot(
                anchorPosition,
                solvedEyeOrigin,
                solvedFaceBasis,
                ToEnvelope(snapshot.HeadOriginWorld),
                ToEnvelope(snapshot.PivotPositionWorld),
                ToEnvelopeBasis(snapshot.UpstreamFaceBasisWorld),
                viewpointOffset,
                foldFrame);
        }

        Node3D? pivot = NeckPivot;
        Vector3 pivotWorld = pivot is not null && IsInstanceValid(pivot)
            ? pivot.GlobalPosition
            : solvedFeedback.HeadFrame.Origin;
        // Node-authored rigs approximate the upstream pose with the bootstrap rest frame captured at first
        // evaluation — stable, unlike the followed solved head, so the release target cannot chase the solve.
        return new GeometricFeedbackSnapshot(
            anchorPosition,
            solvedEyeOrigin,
            solvedFaceBasis,
            ToEnvelope(_legacyRestHeadOrigin),
            ToEnvelope(pivotWorld),
            ToEnvelopeBasis(_legacyRestFaceBasis),
            viewpointOffset);
    }

    /// <summary>
    /// Builds this evaluation's shared fold-family prediction frame when the binding's cached chain
    /// authoring and the captured upstream snapshot satisfy the fold domain (neutral upstream, rigid
    /// skeleton basis, valid pivot, reconstruction within tolerance). Outside the domain the returned
    /// status names the exact reason and the caller keeps the unchanged TR 21 orbit construction — an
    /// explicit outside-domain fallback, never a claim that the orbit is anatomically safe.
    /// </summary>
    private DownwardFoldPredictionFrame? BuildFoldFrameIfSupported(
        OrientingUpstreamSnapshot? upstreamSnapshot,
        Basis envelope,
        Vector3 characterOrigin,
        out string foldStatus)
    {
        if (_upstreamBinding is not { } binding)
        {
            foldStatus = "no_skeleton_binding";
            return null;
        }

        if (upstreamSnapshot is null)
        {
            foldStatus = "no_skeleton_snapshot";
            return null;
        }

        if (binding.FoldChain is not { } chain)
        {
            foldStatus = $"not_bound:{binding.FoldUnsupportedReason ?? "unknown"}";
            return null;
        }

        OrientingSettings settings = _settings
            ?? throw new InvalidOperationException("Orienting controller settings were not initialised.");
        DownwardFoldPredictionFrame? frame = chain.TryBuildFrame(
            binding.Skeleton,
            upstreamSnapshot,
            envelope,
            characterOrigin,
            settings.ComfortConeDownRadians,
            settings.EnvelopeDownRadians,
            out string? notBuiltReason);
        if (frame is null)
        {
            foldStatus = notBuiltReason ?? "not_built";
            return null;
        }

        FoldFrameBuilds++;
        foldStatus = "frame_built";
        return frame;
    }

    /// <summary>
    /// Resolves the character-stable roll reference up (AI-009 TR 19): the owning character's global basis up,
    /// falling back to world up when the character — or its up axis — cannot be resolved.
    /// </summary>
    private Vector3 ResolveReferenceUp()
    {
        Vector3 characterUp = _character?.GlobalTransform.Basis.Y ?? Vector3.Zero;
        return characterUp.LengthSquared() >= DirectionLengthEpsilonSquared
            ? characterUp.Normalized()
            : Vector3.Up;
    }

    /// <summary>
    /// Rebuilds <paramref name="faceBasis"/> with its forward axis preserved and its roll stripped against
    /// <paramref name="referenceUp"/>, so the result is roll-free by construction (AI-009 TR 19). The construction
    /// mirrors <see cref="Basis.LookingAt(Vector3, Vector3)" />: the basis Z axis is the negative forward, the
    /// right axis is the reference up crossed with Z, and the up axis is Z crossed with right. When the reference
    /// up is near-parallel to the forward axis — which the orientation envelope caps prevent — the fallback up is
    /// used defensively; when both are unusable the commanded basis is returned orthonormalised rather than
    /// publishing a degenerate basis.
    /// </summary>
    private static Basis BuildRollFreeBasis(Basis faceBasis, Vector3 referenceUp, Vector3 fallbackUp)
    {
        Vector3 forward = (-faceBasis.Z).Normalized();
        Vector3 up = IsUsableRollReferenceUp(referenceUp, forward)
            ? referenceUp.Normalized()
            : IsUsableRollReferenceUp(fallbackUp, forward)
                ? fallbackUp.Normalized()
                : Vector3.Zero;

        if (up == Vector3.Zero)
        {
            return faceBasis.Orthonormalized();
        }

        Vector3 zAxis = -forward;
        Vector3 right = up.Cross(zAxis).Normalized();
        return new Basis(right, zAxis.Cross(right), zAxis);
    }

    private static bool IsUsableRollReferenceUp(Vector3 up, Vector3 forward)
    {
        Vector3 normalised = up.Normalized();
        return normalised != Vector3.Zero && Mathf.Abs(normalised.Dot(forward)) < ReferenceUpParallelDotLimit;
    }

    /// <summary>
    /// Resolves the published intent origin. Skeleton-backed rigs use the non-recursive upstream
    /// construction (AI-009 TR 21, HEAD-1): with upstream head origin h, upstream pivot position p and
    /// upstream face rotation U from ONE captured snapshot and the commanded face rotation D,
    /// <c>R = D * inverse(U)</c> and <c>target = p + R * (h − p)</c> — solved output is not an input, so
    /// for constant upstream geometry and command the solve cannot advance the target. Without a valid
    /// pivot the published origin is the upstream head origin (TR 22). Node-authored rigs — no skeleton
    /// in the viewpoint's chain, which production templates never author — keep the legacy solved-frame
    /// pivot construction: such rigs have no engine solve for the solved operands to recurse against.
    /// When this evaluation's fold frame resolves the published command, the published origin is the
    /// SAME continuous resolution the coupled gaze prediction used — mapped to world through the frame's
    /// exact envelope transport. The frame reproduces the orbit target exactly on the neutral/upward
    /// regime and resolves the yaw-composed animation-anchored fold family on the downward regime, with
    /// no command gate; a degenerate resolution keeps the orbit target.
    /// </summary>
    private Vector3 ResolveAimedIntentOrigin(
        in SolvedFeedbackSample solvedFeedback,
        OrientingAim aim,
        Basis faceIntentBasis,
        OrientingUpstreamSnapshot? upstreamSnapshot,
        DownwardFoldPredictionFrame? foldFrame)
    {
        if (upstreamSnapshot is { } snapshot)
        {
            Vector3 orbitTarget = OrientingAimGeometry.ConstructAimedTarget(snapshot, faceIntentBasis);
            Vector3 requested = orbitTarget;
            if (foldFrame is { } frame)
            {
                requested = ResolveFoldAwareOrigin(frame, aim, orbitTarget);
            }
            else
            {
                LastFoldEvaluation = null;
            }

            LastRequestedAimTargetGlobal = requested;
            return requested;
        }

        LastFoldEvaluation = null;
        Node3D? pivot = NeckPivot;
        if (pivot is null || !IsInstanceValid(pivot))
        {
            LastRequestedAimTargetGlobal = solvedFeedback.HeadFrame.Origin;
            return solvedFeedback.HeadFrame.Origin;
        }

        Vector3 pivotPosition = pivot.GlobalPosition;
        // The node-authored reference for R is the rest face basis captured at first evaluation of this viewpoint
        // — a stable bootstrap reference, never re-anchored from solved output.
        Basis aimRotation = faceIntentBasis * _legacyRestFaceBasis.Inverse();
        Vector3 requestedLegacy = pivotPosition + (aimRotation * (solvedFeedback.HeadFrame.Origin - pivotPosition));
        LastRequestedAimTargetGlobal = requestedLegacy;
        return requestedLegacy;
    }

    /// <summary>
    /// Resolves the commanded aim's fold/orbit displacement blend and subsequent pivot yaw in the shared
    /// envelope frame, then transports that origin to world. General blended/yawed publication is not a
    /// fold-family member. A failed resolution keeps the orbit target; both paths record diagnostics.
    /// </summary>
    private Vector3 ResolveFoldAwareOrigin(
        DownwardFoldPredictionFrame frame,
        OrientingAim aim,
        Vector3 orbitTargetWorld)
    {
        bool resolved = frame.TryPredictHeadOriginDetailed(
            aim.HorizontalRadians,
            aim.VerticalRadians,
            out _,
            out DownwardFoldSelection selection,
            _foldGridAngleScratch);
        Vector3 publishedWorld = resolved
            ? frame.WorldFromEnvelope * selection.HeadOriginEnvelope
            : orbitTargetWorld;
        Vector3 spineOriginWorld = frame.WorldFromEnvelope * selection.SpineOriginEnvelope;
        Vector3 orbitReferenceWorld = frame.WorldFromEnvelope * selection.OrbitTargetEnvelope;
        LastFoldStatus = resolved ? selection.Reason : "degenerate_orbit_direction";
        LastFoldEvaluation = new DownwardFoldEvaluation(
            selection.Applied,
            selection.Reason,
            selection.AlphaIndex,
            selection.Alpha,
            selection.DirectionErrorDegrees,
            (orbitReferenceWorld - spineOriginWorld).Length() * 1000d,
            (publishedWorld - spineOriginWorld).Length() * 1000d,
            frame.Alpha0ReconstructionMillimetres,
            orbitReferenceWorld,
            spineOriginWorld,
            publishedWorld,
            resolved ? [.. _foldGridAngleScratch] : [],
            selection.SaturatedAtMaxAlpha,
            selection.DirectionOptimalIndex,
            selection.DemandExtraSteps);
        return publishedWorld;
    }

    /// <summary>Outcome of one upstream capture attempt.</summary>
    private enum UpstreamCaptureStatus
    {
        /// <summary>No skeleton anywhere in the viewpoint's authored chain: node-authored rig.</summary>
        NodeAuthored,

        /// <summary>Snapshot captured from native bone poses plus authored offsets.</summary>
        Captured,

        /// <summary>A bone-attachment chain exists but its required input is missing or invalid.</summary>
        InvalidAuthoring,
    }

    /// <summary>
    /// Captures the immutable upstream snapshot for this evaluation (AI-009 TR 14): native skeleton
    /// Head/Neck bone poses composed with the authored face/Viewpoint and pivot offsets — never solved
    /// attachment globals. Outside the engine's modifier pass these bone poses hold the animation-owned
    /// upstream values the engine restores after every skeleton update (runtime-proven by the AI-009
    /// seam probe), so the snapshot can never absorb solved output through this channel.
    /// </summary>
    private UpstreamCaptureStatus CaptureUpstreamSnapshot(double delta, out OrientingUpstreamSnapshot? snapshot)
    {
        snapshot = null;
        Marker3D? viewpoint = Viewpoint;
        if (viewpoint is null || !IsInstanceValid(viewpoint))
        {
            return UpstreamCaptureStatus.NodeAuthored;
        }

        BoneChainResult headChain = ResolveBoneChain(viewpoint, out Skeleton3D? skeleton, out int headBone, out Transform3D viewpointOffset);
        if (headChain == BoneChainResult.InvalidAuthoring)
        {
            return UpstreamCaptureStatus.InvalidAuthoring;
        }

        if (headChain == BoneChainResult.NodeAuthored || skeleton is null)
        {
            return UpstreamCaptureStatus.NodeAuthored;
        }

        int? pivotBone = null;
        Transform3D? pivotOffset = null;
        Node3D? pivot = NeckPivot;
        if (pivot is not null && IsInstanceValid(pivot))
        {
            // An invalid pivot chain is the TR 22 fallback (upstream head origin), never inactive.
            if (ResolveBoneChain(pivot, out Skeleton3D? pivotSkeleton, out int pivotBoneIndex, out Transform3D markerOffset)
                == BoneChainResult.Resolved
                && pivotSkeleton == skeleton)
            {
                pivotBone = pivotBoneIndex;
                pivotOffset = markerOffset;
            }
        }

        if (!ReferenceEquals(_upstreamBinding?.Skeleton, skeleton)
            || !ReferenceEquals(_upstreamBinding?.PivotMarker, pivot)
            || _upstreamBinding?.HeadBoneIndex != headBone
            || _upstreamBinding?.PivotBoneIndex != pivotBone
            || _upstreamBinding?.ViewpointOffsetToBone != viewpointOffset
            || _upstreamBinding?.PivotOffsetToBone != pivotOffset)
        {
            RebindUpstream(skeleton, headBone, pivotBone, viewpointOffset, pivotOffset);
        }

        UpstreamBinding binding = _upstreamBinding!;
        if (_beginSkeleton is not null && !ReferenceEquals(_beginSkeleton, skeleton))
        {
            ResetServoState();
            return UpstreamCaptureStatus.InvalidAuthoring;
        }

        Transform3D skeletonGlobal = skeleton.GlobalTransform;
        Transform3D headBoneWorld = skeletonGlobal * skeleton.GetBoneGlobalPose(binding.HeadBoneIndex);
        Vector3 pivotPosition = headBoneWorld.Origin;
        bool hasPivot = false;
        if (binding.PivotBoneIndex is { } boneIndex && binding.PivotOffsetToBone is { } pivotOffsetToBone)
        {
            pivotPosition = (skeletonGlobal * skeleton.GetBoneGlobalPose(boneIndex) * pivotOffsetToBone).Origin;
            hasPivot = true;
        }

        snapshot = new OrientingUpstreamSnapshot(
            binding.Generation,
            _beginSkeleton is null ? ++_evaluationSequence : _evaluationSequence,
            delta,
            _beginSkeleton is null ? "process" : "begin",
            binding.SkeletonIdentity,
            _character?.GlobalTransform ?? Transform3D.Identity,
            skeletonGlobal,
            headBoneWorld.Origin,
            pivotPosition,
            (headBoneWorld.Basis * viewpointOffset.Basis.Orthonormalized()).Orthonormalized(),
            hasPivot);
        LastUpstreamSnapshot = snapshot;
        return UpstreamCaptureStatus.Captured;
    }

    private void RebindUpstream(
        Skeleton3D skeleton,
        int headBone,
        int? pivotBone,
        Transform3D viewpointOffset,
        Transform3D? pivotOffset)
    {
        DisposeBootstrapHook();
        var foldChain = DownwardFoldChainAuthoring.TryBind(
            skeleton, headBone, out string? foldUnsupportedReason);
        if (foldChain is null)
        {
            LogFoldChainUnsupported(skeleton, foldUnsupportedReason);
        }

        _upstreamBinding = new UpstreamBinding(
            skeleton,
            skeleton.GetInstanceId(),
            headBone,
            pivotBone,
            NeckPivot,
            viewpointOffset,
            pivotOffset,
            foldChain,
            foldUnsupportedReason,
            _bindingGeneration + 1);
        _bindingGeneration = _upstreamBinding.Generation;
        _pendingEvaluation = null;
        _completedFeedback = null;
        LastFeedbackSequence = null;
        LastFeedbackIsBootstrap = true;
        _hasBeginAim = false;
        LastUpstreamSnapshot = null;
        LastFoldFrame = null;
        LastFoldEvaluation = null;
        LastFoldStatus = foldChain is null ? $"not_bound:{foldUnsupportedReason}" : "not_evaluated";
        // Capture final bone geometry independently of BoneAttachment3D subscriber ordering.
        skeleton.SkeletonUpdated += OnSkeletonUpdated;
        _bootstrapSkeleton = skeleton;
    }

    /// <summary>
    /// One-time per-binding diagnostic when a rig's authoring is outside the fold family's supported
    /// domain: the controller keeps its unchanged TR 21 orbit construction for that rig. This states a
    /// domain limit; it does not claim the orbit construction is anatomically safe.
    /// </summary>
    private void LogFoldChainUnsupported(Skeleton3D skeleton, string? reason)
    {
        if (_warnedFoldChainUnsupported)
        {
            return;
        }

        _warnedFoldChainUnsupported = true;
        ILogger<OrientingController>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Debug) == true)
        {
            logger.LogDebug(
                "Orienting controller '{Path}' cannot use the downward fold-family construction on skeleton "
                + "'{SkeletonName}' (reason: {Reason}); keeping the unchanged pivot-orbit construction for this rig.",
                IsInsideTree() ? GetPath() : Name,
                skeleton.Name,
                reason ?? "unknown");
        }
    }

    private void OnSkeletonUpdated()
    {
        if (_pendingEvaluation is not { } sequence || _upstreamBinding is not { } binding
            || !IsInstanceValid(binding.Skeleton))
        {
            return;
        }

        _pendingEvaluation = null;
        Transform3D root = _character?.GlobalTransform ?? Transform3D.Identity;
        Transform3D inverseRoot = root.AffineInverse();
        Transform3D head = binding.Skeleton.GlobalTransform * binding.Skeleton.GetBoneGlobalPose(binding.HeadBoneIndex);
        Transform3D face = head * binding.ViewpointOffsetToBone;
        _completedFeedback = new FinalFeedbackPacket(sequence, binding.Generation, binding.SkeletonIdentity,
            root, inverseRoot * head, inverseRoot * face.Origin,
            (root.Basis.Orthonormalized().Inverse() * face.Basis.Orthonormalized()).Orthonormalized());
    }

    private SolvedFeedbackSample ResolveSolvedFeedback(
        Transform3D nodeHead, Vector3 nodeEye, OrientingUpstreamSnapshot? upstream)
    {
        LastFeedbackSequence = null;
        if (upstream is not null && _upstreamBinding is { } binding)
        {
            Transform3D root = _character?.GlobalTransform ?? Transform3D.Identity;
            if (_completedFeedback is { } packet && packet.Generation == upstream.Generation
                && packet.SkeletonIdentity == upstream.SkeletonIdentity
                && packet.Sequence + 1 == upstream.EvaluationSequence)
            {
                LastFeedbackSequence = packet.Sequence;
                return new SolvedFeedbackSample(root * packet.HeadRelative, root * packet.EyeRelative,
                    (root.Basis.Orthonormalized() * packet.FaceRelative).Orthonormalized(), true);
            }

            Transform3D head = binding.Skeleton.GlobalTransform * binding.Skeleton.GetBoneGlobalPose(binding.HeadBoneIndex);
            Transform3D face = head * binding.ViewpointOffsetToBone;
            return new SolvedFeedbackSample(head, face.Origin, face.Basis.Orthonormalized(), false);
        }

        return new SolvedFeedbackSample(nodeHead, nodeEye,
            (nodeHead.Basis * _viewpointLocalBasis).Orthonormalized(), false);
    }

    private void DisposeBootstrapHook()
    {
        if (_bootstrapSkeleton is { } bootstrap)
        {
            bootstrap.SkeletonUpdated -= OnSkeletonUpdated;
            _bootstrapSkeleton = null;
        }
    }

    /// <summary>Result of walking a marker's authored chain up to its bone attachment.</summary>
    private enum BoneChainResult
    {
        /// <summary>No BoneAttachment3D anywhere above the marker: node-authored rig.</summary>
        NodeAuthored,

        /// <summary>A bone attachment exists but its skeleton or bone index is missing/invalid.</summary>
        InvalidAuthoring,

        /// <summary>Chain resolved to a skeleton bone with the marker's authored offset.</summary>
        Resolved,
    }

    /// <summary>
    /// Walks from a marker up its authored chain, composing local transforms, until the first
    /// BoneAttachment3D ancestor: the attachment tracks the bone pose, so its children are authored
    /// relative to the bone frame and the attachment's own local transform contributes nothing (the
    /// isolated-fixture composition proves this relation against the real template).
    /// </summary>
    private static BoneChainResult ResolveBoneChain(
        Node3D marker,
        out Skeleton3D? skeleton,
        out int boneIndex,
        out Transform3D markerOffsetToBone)
    {
        skeleton = null;
        boneIndex = -1;
        markerOffsetToBone = marker.Transform;
        for (Node? current = marker.GetParent(); current is not null; current = current.GetParent())
        {
            if (current is BoneAttachment3D attachment)
            {
                skeleton = attachment.GetParent() as Skeleton3D;
                boneIndex = attachment.BoneIdx;
                return skeleton is null || boneIndex < 0 || boneIndex >= skeleton.GetBoneCount()
                    ? BoneChainResult.InvalidAuthoring
                    : BoneChainResult.Resolved;
            }

            if (current is Node3D intermediate)
            {
                markerOffsetToBone = intermediate.Transform * markerOffsetToBone;
            }
        }

        return BoneChainResult.NodeAuthored;
    }

    private void ProvideIdleIntentForInvalidUpstreamAuthoring()
    {
        _currentIntent = new IKTargetIntent(Transform3D.Identity, 0f);
        if (_warnedInvalidUpstreamAuthoring)
        {
            return;
        }

        _warnedInvalidUpstreamAuthoring = true;
        ILogger<OrientingController>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Warning) == true)
        {
            logger.LogWarning(
                "Orienting controller '{Path}' has invalid upstream authoring (bone attachment without a "
                + "usable skeleton/bone); providing idle intent with zero influence.",
                IsInsideTree() ? GetPath() : Name);
        }
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
