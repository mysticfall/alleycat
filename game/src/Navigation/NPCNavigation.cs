using System.Collections.Immutable;
using AlleyCat.Common;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core.Logging;
using AlleyCat.IK;
using Godot;
using Microsoft.Extensions.Logging;

#pragma warning disable CS1591, IDE0032, IDE0046, IDE0072

namespace AlleyCat.Navigation;

public enum NPCShortBoutInvalidationReason
{
    None,
    DirectPathCollision,
    RealisedCollision,
    RealisedDivergence,
    ExitEndpointResidual,
    PlaybackWatchdog,
}

/// <summary>Read-only producer/consumer observation for an NPC animation playback interval.</summary>
public readonly record struct NPCNavigationPlaybackObservation(
    NPCAnimationStage ProgrammeStage,
    StringName EvaluatedState,
    float EvaluatedPlayPosition,
    float EvaluatedPlayLength,
    bool FiniteStateObserved,
    float FiniteStateElapsed,
    float FiniteStateDuration);

public enum NPCShortBoutTerminalOutcome
{
    None,
    Succeeded,
    FailedCollision,
    FailedDivergence,
    FailedEndpoint,
    FailedPlayback,
}

/// <summary>Read-only evidence of NPC contact contribution consumption within one movement composition frame.</summary>
public readonly record struct NPCContactLockConsumptionTrace(
    ulong PhysicsTick,
    ulong NavigationGeneration,
    string EvaluatedRole,
    float EvaluatedPhase,
    ulong LiveStatePhysicsTick,
    long LiveStateClockGeneration,
    ulong ContributionPhysicsTick,
    long ContributionClockGeneration,
    long ContributionIdentity,
    bool ContributionAuthorised,
    bool ConsumptionAttempted,
    bool Consumed);

/// <summary>Immutable finite-role lookup held only by the active runtime sampling snapshot.</summary>
internal sealed class NPCFiniteRoleSamplingSnapshot<T>(ImmutableDictionary<string, T> roles)
    where T : notnull
{
    public T Require(string role)
    {
        if (roles.TryGetValue(role, out T? track))
        {
            return track!;
        }

        throw new InvalidOperationException($"NPC vector forecast requires Root position track for '{role}'.");
    }
}

/// <summary>
/// Production NPC destination, animation-programme, root-motion and physical movement composition root.
/// </summary>
[GlobalClass]
public partial class NPCNavigation : NavigationBase
{
    [Signal]
    public delegate void TerminalConvergenceFailedEventHandler();

    [Signal]
    public delegate void TerminalConfirmationEnteredEventHandler();

    private const float TerminalForecastUncertaintyMetres = 0.014f;
    /// <summary>
    /// Shared forecast-noise band for route-relative terminal overshoot. A forecast stop within this
    /// band of the endpoint is not an overshoot: it sits at the forecast uncertainty floor, commits in-window, and
    /// remains owned by the finite vector-convergence schedule.
    /// </summary>
    public const float TerminalOvershootToleranceMetres = 0.025f;
    private const float MaximumExitPlanarResidualRate = 0.7f;
    private const float FarEstimateBlendMaterialChange = 0.1f;
    private const float FarEstimateRateMaterialChange = 0.1f;
    private const float ReleaseApproachActivationDistance = 1.2f;

    /// <summary>
    /// Safety factor converting one radian of outstanding terminal-facing turn into ordinary walking distance.
    /// The sustained walk must complete the terminal facing before the imminent release: the packaged f2i exits
    /// continue the walk pose, so a release that fires mid-turn crossfades a mixed stance and lands off the
    /// endpoint with a correction far beyond capacity.
    /// </summary>
    private const float TerminalFacingLeadSafetyFactor = 1.4f;
    public const float FiniteTransitionPlaybackRate = NPCFiniteTransitionCadence.SourceSecondsPerWallSecond;

    public const float ReversalMinimumSpeed = 0.06f;
    public const int ReversalRequiredConsecutiveFrames = 4;

    private static readonly StringName _playbackParameter = new("parameters/States/playback");
    private static readonly StringName _sustainedBlendParameter = new("parameters/States/NPCTravel/Sustained/blend_position");
    private static readonly StringName _sustainedPositionParameter = new("parameters/States/NPCTravel/Sustained/current_position");
    private static readonly StringName _playbackRateParameter = new("parameters/States/NPCTravel/Rate/scale");
    private static readonly StringName _statesNode = new("States");
    private static readonly StringName _clipNode = new("Clip");
    private static readonly StringName _sustainedNode = new("Sustained");
    private static readonly StringName _travelState = new("NPCTravel");
    private readonly NPCAnimationProgramme _programme = new();
    private readonly NPCResidualCorrectionBudget _correctionBudget = new();
    private readonly NPCReversalCounter _reversalCounter = new(ReversalMinimumSpeed, ReversalRequiredConsecutiveFrames);
    private readonly NPCTerminalReleaseScheduler _terminalScheduler = new();
    private readonly NPCVectorConvergenceExecutor _vectorConvergence = new();
    // Reused while the deterministic same/opposite imminent candidates are evaluated. Their capacities bound the
    // pass and avoid per-frame allocation on the warmed physics path.
    private readonly NPCExactImportedRootEndpointCache<Animation> _importedRootEndpointCache = new(1024);
    private readonly List<NPCCommandedReleaseProjectionInterval> _sameLaneCandidateProjection = new(96);
    private readonly List<NPCCommandedReleaseProjectionInterval> _oppositeLaneCandidateProjection = new(96);
    private readonly List<NPCVectorConvergenceInterval> _sameLaneCandidateLeadInIntervals = new(96);
    private readonly List<NPCVectorConvergenceInterval> _sameLaneCandidateMovingIntervals = new(64);
    private readonly List<NPCVectorConvergenceInterval> _oppositeLaneCandidateLeadInIntervals = new(96);
    private readonly List<NPCVectorConvergenceInterval> _oppositeLaneCandidateMovingIntervals = new(64);
    private NPCTerminalCandidateDecisionDiagnostics _terminalCandidateDecisionDiagnostics =
        NPCTerminalCandidateDecisionDiagnostics.Empty;
    private StandingLocomotionCatalogue? _catalogue;
    private bool _runtimeBindingPackageFailureLatched;
    private NPCBlendDomain? _sustainedBlendDomain;
    private NPCNavigationRoutePlan? _route;
    private Vector2 _smoothedBlend;
    private Vector2 _blendCommandAcceleration;
    private float _smoothedPlaybackRate = 1f;
    private float _rootMotionScale = 1f;
    private float _playbackRateAcceleration;
    private Vector2 _smoothedResidual;
    private Vector2 _residualAcceleration;
    private float _smoothedYawResidual;
    private float _previousAuthoredYawRate;
    private float _previousAppliedYawRate;
    private bool _finiteStateObserved;
    private float _finiteStateElapsed;
    private float _finiteStateDuration;
    private Vector3 _lastRealisedDisplacement;
    private Vector3 _previousTerminalRootAccumulator;
    private Vector3 _previousTerminalActorPosition;
    private bool _terminalApproachObserved;
    private ulong _physicsFrameGeneration;
    private readonly float[] _sustainedBlendWeights = new float[6];
    private int[] _sustainedEngineTriangleIndices = [];
    // This snapshot is rebuilt whenever the mutable runtime blend resource is activated or its seam-aligned
    // samples are changed. Projection may sample it hundreds of times per candidate, so it must not cross the
    // Godot boundary for names, topology, animations, or root tracks on every projected interval.
    private NPCSustainedBlendSnapshot? _sustainedBlendSnapshot;
    private ulong _sustainedSamplingSnapshotGeneration;
    private NPCFarRouteStoppingEstimator _farRouteStoppingEstimator;
    private NPCFarRouteEstimateKey _farRouteEstimateKey;
    private Vector2 _farRouteEstimateBlend;
    private float _farRouteEstimateRate;
    private NPCAnimationPhaseLane _farRouteEstimateLane;
    private NPCImminentCheckRetryGate _imminentCheckRetryGate;
    private int _exactTerminalCandidateChecksThisFrame;
    private int _cumulativeExactTerminalCandidateChecks;
    private NPCBoundedTerminalCheckDiagnostics _boundedTerminalCheckDiagnostics;
    private bool _releaseSelectedAtLastPublication;
    private bool _sustainedCycleAvailable;
    private bool _sustainedCycleWasArmedForObservedAdvance;
    private float _sustainedCyclePreviousPhase;
    private float _sustainedCycleCurrentPhase;
    private NPCSustainedCycleClock _sustainedCycleClock = NPCSustainedCycleClock.Reset(default);
    private float _sustainedCycleExpectedObservedAdvance;
    private Vector2 _releaseApproachBlendPoint;
    private NPCReleaseOccurrence _selectedReleaseOccurrence;
    private Vector3 _lastCollisionTravel;
    private Vector3 _lastCollisionRemainder;
    private AnimationNodeBlendSpace2D? _sustainedIntervalSamplerSpace;
    private Func<Vector2, float, Vector2, float, float, NPCCommandedReleaseAuthoredIntervalSample>?
        _sustainedIntervalSampler;
    private bool _terminalReleaseApproachActive;

    /// <summary>
    /// Frozen maximum terminal-facing activation distance for the active request and route revision. Ordinary
    /// travel starts converging its facing onto the terminal yaw once the remaining route distance cannot carry
    /// the outstanding yaw turn at the ordinary gait; freezing the maximum keeps the activation latched as the
    /// error shrinks with distance.
    /// </summary>
    private float _terminalFacingActivationDistance = ReleaseApproachActivationDistance;

    /// <summary>Accumulated sustained-travel time inside the stopping region without any imminent commitment.</summary>
    private float _routeExhaustionSeconds;

    /// <summary>Destination request generation the accumulated exhaustion clock belongs to.</summary>
    private long _routeExhaustionRequestGeneration = -1;

    /// <summary>Route revision the accumulated exhaustion clock belongs to.</summary>
    private long _routeExhaustionRouteRevision = -1;
    private bool _entryHandoffBlendActive;
    private Vector2 _entryHandoffBlendPoint;
    private float _entryHandoffPlaybackRate = 1f;
    private float _entryHandoffUnitRootSpeed;
    private int _entryHandoffPublicationFrames;
    private float _forwardLeftExitCrossfadeDuration;
    private float _forwardRightExitCrossfadeDuration;
    private float _backwardLeftExitCrossfadeDuration;
    private float _backwardRightExitCrossfadeDuration;
    private float _activeEntryCrossfadeDuration;
    private NPCShortBoutSplicePlan? _shortBout;
    private bool _shortBoutExitTransitionRequested;
    private bool _shortBoutCorrectionInitialised;
    private Vector2 _shortBoutTotalWorldCorrection;
    private Vector2 _shortBoutPublishedWorldCorrection;
    private float _shortBoutTotalYawCorrection;
    private float _shortBoutPublishedYawCorrection;
    private Vector3 _shortBoutStartWorldPosition;
    private Vector3 _shortBoutObservedAuthoredWorld;
    private Vector3 _shortBoutObservedAuthoredRoot;
    private float _shortBoutObservedAuthoredYaw;
    private bool _shortBoutExecutionInvalid;
    private bool _shortBoutFailureFinalised;
    private Vector3 _shortBoutForecastWorld;
    private Vector3 _shortBoutImportedForecastRoot;
    private float _shortBoutImportedForecastYaw;
    private Vector3 _shortBoutActualGraphForecastRoot;
    private float _shortBoutActualGraphForecastYaw;
    private Animation? _shortBoutImportedEntry;
    private Animation? _shortBoutImportedExit;
    private int _shortBoutEntryPositionTrack = -1;
    private int _shortBoutEntryRotationTrack = -1;
    private int _shortBoutExitPositionTrack = -1;
    private int _shortBoutExitRotationTrack = -1;
    private float _shortBoutPreviousEntrySourcePosition;
    private float _shortBoutPreviousExitSourcePosition;
    private readonly Transform3D[] _shortBoutPoseBefore = new Transform3D[5];
    private readonly int[] _shortBoutPoseBoneIndices = new int[5];
    private Skeleton3D? _shortBoutSkeleton;
    private bool _shortBoutCrossfadeWasActiveBeforeEvaluation;
    private bool _shortBoutPoseObservedPreviousFrame;
    private bool _shortBoutResidualSettlement;
    private Vector2 _pivotAbsorptionWorldTotal;
    private Vector2 _pivotAbsorptionPublishedWorld;
    private float _sideStepAbsorptionYawTotal;
    private float _sideStepAbsorptionPublishedYaw;
    private Vector2 _sideStepPlanarWorldTotal;
    private Vector2 _sideStepPlanarPublishedWorld;
    private Vector2 _sideStepPlanarObservedAuthoredWorld;
    private Vector3 _sideStepPlanarAnchorWorld;
    private Basis _sideStepPlanarAnchorBasis = Basis.Identity;
    private float _sideStepPlanarSnapshotMagnitude;
    private Vector2 _vectorRequestedCorrection;
    private bool _finiteExitCompletionObserved;
    private bool _terminalMotionBoundaryClosed;
    private NPCTerminalBoundaryMeasurement? _terminalBoundaryMeasurement;
    private NPCTerminalBoundaryState _terminalBoundaryState;
    private NPCTerminalConfirmation _terminalConfirmation;
    private NavigationTerminalTolerances _terminalTolerances;
    private NPCVectorConvergenceExecutionLedger _terminalVectorConvergenceLedger;
    private Vector3 _terminalCommitmentModelledRootWorld;
    private Vector3 _terminalCommitmentConsumedRootWorld;
    private float _terminalCommitmentModelledYaw;
    private float _terminalCommitmentConsumedYaw;
    private Vector3 _lastVectorConvergenceCommitmentModelledRootWorld;
    private Vector3 _lastVectorConvergenceCommitmentConsumedRootWorld;
    private float _lastVectorConvergenceCommitmentModelledYaw;
    private float _lastVectorConvergenceCommitmentConsumedYaw;

    /// <summary>
    /// A terminal occurrence belongs to an evaluated sustained child, not merely to the programme's requested
    /// state. This prevents a route replan observing the stale finite state's phase during Entry→Travel hand-off.
    /// </summary>
    private bool HasLiveSustainedState => _programme.Stage == NPCAnimationStage.SustainedTravel
        && LastEvaluatedState == _travelState
        && _sustainedCycleAvailable;

    public NPCNavigation()
    {
        PathDesiredDistance = 0.05f;
        DestinationReachedDistance = 0.05f;
        ProcessPhysicsPriority = -100;
        TreeExiting += ReleaseRuntimeSamplingSnapshot;
    }

    [ExportGroup("Required Binding")]
    [Export]
    public CharacterBody3D? Actor
    {
        get; set;
    }
    [Export]
    public AnimationTree? AnimationTree
    {
        get; set;
    }
    [Export]
    public Node3D? RootMotionReference
    {
        get; set;
    }
    [Export(PropertyHint.File, "*.json")]
    public string CataloguePath
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            _catalogue = null;
            _runtimeBindingPackageFailureLatched = false;
        }
    } =
        "res://assets/characters/reference/female/animations/runtime/locomotion/sad/locomotion_sad_catalogue.json";
    [Export] public bool PlanarMotionAllowed { get; set; } = true;
    [Export] public bool YawMotionAllowed { get; set; } = true;
    [Export]
    public bool AwaitInstallerActivation
    {
        get; set;
    }

    [ExportGroup("Programme")]
    [Export(PropertyHint.Range, "0.1,10,0.1")] public float BlendSmoothingRate { get; set; } = 6f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float AuthoredExitDistance { get; set; } = 0.475f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float TerminalCommitmentCorrectionBudget { get; set; } = 0.2f;
    [Export(PropertyHint.Range, "0,0.2,0.001")] public float PositionTolerance { get; set; } = 0.04f;
    [Export(PropertyHint.Range, "0,15,0.1")] public float YawToleranceDegrees { get; set; } = 2f;
    [Export(PropertyHint.Range, "0,0.1,0.0001")] public float TerminalPositionTolerance { get; set; } = 0.001f;
    [Export(PropertyHint.Range, "0,5,0.01")] public float TerminalYawToleranceDegrees { get; set; } = 0.1f;
    [Export(PropertyHint.Range, "0,1,0.001")] public float FarEstimateUncertaintyReserve { get; set; } = 0.05f;
    [Export(PropertyHint.Range, "0,1,0.001")] public float FarEstimateMaterialDriftDistance { get; set; } = 0.05f;

    [ExportGroup("Residual Correction")]
    [Export(PropertyHint.Range, "0,2,0.01")] public float MaximumPlanarResidualRate { get; set; } = 0.35f;
    [Export(PropertyHint.Range, "0,0.2,0.001")] public float MaximumPlanarResidualPerSegment { get; set; } = 0.08f;
    [Export(PropertyHint.Range, "0,3.14,0.01")] public float MaximumYawResidualRate { get; set; } = 1.2f;
    [Export(PropertyHint.Range, "0,6.28,0.01")] public float MaximumYawResidualPerSegment { get; set; } = Mathf.Pi;
    [Export(PropertyHint.Range, "0,30,0.1")] public float ResidualSmoothingRate { get; set; } = 10f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float LowSpeedCorrectionWeight { get; set; } = 0.2f;

    [ExportGroup("Short-Bout Splice")]
    [Export(PropertyHint.Range, "0.01,0.25,0.01")] public float ShortBoutProtectedEndpointDuration { get; set; } = 0.08f;
    [Export(PropertyHint.Range, "0.01,0.25,0.01")] public float ShortBoutCrossfadeDuration { get; set; } = 0.12f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float MaximumShortBoutCorrection { get; set; } = 0.7f;
    [Export(PropertyHint.Range, "0,4,0.01")] public float MaximumShortBoutCorrectionRate { get; set; } = 3f;
    [Export(PropertyHint.Range, "0,6.28,0.01")] public float MaximumShortBoutYawCorrectionRate { get; set; } = Mathf.Pi;

    public NPCAnimationStage ProgrammeStage => _programme.IsSustainedTravelHandoffPending
        ? NPCAnimationStage.Entry
        : _programme.Stage;
    public NPCAnimationPhaseLane PhaseLane => _programme.PhaseLane;
    public int EntryCount => _programme.EntryCount;
    public int ExitCount => _programme.ExitCount;
    public int DomainHandoffCount => _programme.DomainHandoffCount;
    public int IdleCrossingCount => _programme.IdleCrossingCount;
    public long LastDestinationRequestGeneration
    {
        get; private set;
    }
    public long LastRouteRevision
    {
        get; private set;
    }
    public float LastRouteProgress
    {
        get; private set;
    }
    public float LastRemainingDistance
    {
        get; private set;
    }
    public float CurrentRouteBrakingDistance => _route?.BrakingDistance ?? 0f;
    public Vector2 LastBlendPoint
    {
        get; private set;
    }
    public Vector2 LastPlanarResidual
    {
        get; private set;
    }
    public float LastYawResidual
    {
        get; private set;
    }

    /// <summary>Last production terminal-yaw step, exposed for deterministic route diagnostics.</summary>
    public NPCYawConvergenceStepTrace LastYawConvergenceStepTrace
    {
        get; private set;
    }
    public Vector3 LastRealisedDisplacement => _lastRealisedDisplacement;
    public NPCMovementCompositionFrame LastMovementCompositionFrame
    {
        get; private set;
    }
    public long LastSustainedClockGeneration => _sustainedCycleClock.Generation;
    public NPCTerminalBoundaryMeasurement? TerminalBoundaryMeasurement => _terminalBoundaryMeasurement;
    public NPCTerminalBoundaryState TerminalBoundaryState => _terminalBoundaryState;
    public int TerminalConfirmationFrameCount => _terminalConfirmation.ConfirmedFrameCount;
    public double LastSustainedPreviousUnwrappedPhase => _sustainedCycleClock.PreviousUnwrappedPhase;
    public double LastSustainedUnwrappedPhase => _sustainedCycleClock.CurrentUnwrappedPhase;
    public NPCReleaseOccurrence LastSelectedReleaseOccurrence
    {
        get; private set;
    }
    public Vector3 LastAuthoredRootWorldDelta
    {
        get; private set;
    }
    public Vector3 LastRequestedSyntheticWorldDelta
    {
        get; private set;
    }
    public Vector3 LastRouteTangent
    {
        get; private set;
    }
    public Vector3 LastTerminalTangent
    {
        get; private set;
    }
    public float LastAuthoredRootLongitudinal
    {
        get; private set;
    }
    public float LastRequestedSyntheticLongitudinal
    {
        get; private set;
    }
    public float LastRealisedLongitudinal
    {
        get; private set;
    }
    public float LastRealisedRouteLongitudinal
    {
        get; private set;
    }
    public bool TerminalCommitted => _programme.TerminalCommitted;
    public NPCAnimationFrame LastPublishedAnimationFrame
    {
        get; private set;
    }
    public NPCNavigationPlaybackObservation PlaybackObservation => new(
        _programme.Stage,
        LastEvaluatedState,
        LastEvaluatedPlayPosition,
        LastEvaluatedPlayLength,
        _finiteStateObserved,
        _finiteStateElapsed,
        _finiteStateDuration);
    public string LastCandidateRejectionSummary => _terminalScheduler.RejectionSummary();
    /// <summary>
    /// Gets the latest inspectable terminal-candidate evaluation, or a cleared no-candidate snapshot outside a
    /// terminal evaluation window.
    /// </summary>
    public NPCTerminalCandidateDecisionDiagnostics LastTerminalCandidateDecision
        => _terminalCandidateDecisionDiagnostics;

    /// <summary>
    /// Gets a structured, read-only evidence snapshot of the terminal model for tooling that cannot bind C#
    /// record types (for example the navigation photobooth runner). Mirrors the IK003 navigation diagnostics
    /// snapshot pattern: it allocates only when explicitly called and never mutates navigation state.
    /// </summary>
    public Godot.Collections.Dictionary GetTerminalEvidenceSnapshot()
    {
        NPCTerminalCandidateDecisionDiagnostics decision = _terminalCandidateDecisionDiagnostics;
        var outcomes = new Godot.Collections.Array();
        foreach (NavigationTerminalOutcomeRecord record in TerminalOutcomes)
        {
            outcomes.Add(new Godot.Collections.Dictionary
            {
                ["sequence"] = record.Sequence,
                ["request_id"] = record.Request.RequestID,
                ["request_generation"] = record.Request.Generation,
                ["route_revision"] = record.Revision,
                ["clock_generation"] = record.ClockGeneration,
                ["outcome"] = record.Outcome.ToString(),
                ["reason"] = record.Reason.ToString(),
                ["requested"] = DescribeOutcomeTransform(record.RequestedTransform),
                ["final"] = DescribeOutcomeTransform(record.FinalTransform),
                ["planar_error_m"] = record.Error.PlanarDistance,
                ["yaw_error_rad"] = record.Error.YawRadians,
                ["planar_tolerance_m"] = record.Tolerances.PlanarDistance,
                ["yaw_tolerance_rad"] = record.Tolerances.YawRadians,
                ["planar_margin_m"] = record.Margins.PlanarDistance,
                ["yaw_margin_rad"] = record.Margins.YawRadians,
                ["acceptance_physics_tick"] = record.AcceptancePhysicsTick,
                ["boundary_physics_tick"] = record.BoundaryPhysicsTick,
                ["terminal_physics_tick"] = record.TerminalPhysicsTick,
                ["occurrence"] = DescribeOccurrence(record.Occurrence),
                ["occurrence_available"] = record.Occurrence is { } occurrence && occurrence.IsValid,
                ["obstruction_kind"] = record.Obstruction?.BlockKind ?? string.Empty,
            });
        }

        return new Godot.Collections.Dictionary
        {
            ["terminal_release_approach_active"] = IsTerminalReleaseApproachActive,
            ["selected_release_occurrence"] = DescribeOccurrence(LastSelectedReleaseOccurrence),
            ["selected_release_occurrence_available"]
                = LastSelectedReleaseOccurrence is { } selected && selected.IsValid,
            ["selected_exit_role"] = LastSelectedExitRole,
            ["selected_release_lane"] = LastSelectedReleaseLane.ToString(),
            ["selected_release_event"] = LastSelectedReleaseEvent,
            ["target_release_phase"] = LastTargetReleasePhase,
            ["time_to_selected_release_seconds"] = LastTimeToSelectedReleaseSeconds,
            ["terminal_commit_phase"] = LastTerminalCommitPhase,
            ["terminal_commit_root_accumulator"] = LastTerminalCommitRootAccumulator,
            ["terminal_boundary_state"] = (int)TerminalBoundaryState,
            ["terminal_boundary_measurement_available"] = _terminalBoundaryMeasurement is not null,
            ["terminal_boundary_measurement"] = DescribeBoundaryMeasurement(_terminalBoundaryMeasurement),
            ["terminal_confirmation_frame_count"] = TerminalConfirmationFrameCount,
            ["terminal_outcomes"] = outcomes,
            ["terminal_candidate_decision"] = DescribeCandidateDecision(decision),
        };
    }

    private static Godot.Collections.Dictionary DescribeOutcomeTransform(NavigationOutcomeTransform transform)
        => new()
        {
            ["x"] = transform.X,
            ["y"] = transform.Y,
            ["z"] = transform.Z,
            ["yaw_rad"] = transform.YawRadians,
        };

    private static Godot.Collections.Dictionary DescribeOccurrence(NPCReleaseOccurrence? occurrence)
    {
        if (occurrence is not { } value)
        {
            return [];
        }

        return new Godot.Collections.Dictionary
        {
            ["clock_generation"] = value.ClockGeneration,
            ["cycle_ordinal"] = value.CycleOrdinal,
            ["target_phase"] = value.TargetPhase,
            ["lane"] = value.Lane.ToString(),
            ["release_event"] = value.ReleaseEvent,
            ["valid"] = value.IsValid,
        };
    }

    private static Godot.Collections.Dictionary DescribeBoundaryMeasurement(
        NPCTerminalBoundaryMeasurement? measurement)
    {
        if (measurement is not { } value)
        {
            return [];
        }

        return new Godot.Collections.Dictionary
        {
            ["request_id"] = value.Request.RequestID,
            ["request_generation"] = value.Request.Generation,
            ["route_revision"] = value.RouteRevision,
            ["clock_generation"] = value.ClockGeneration,
            ["boundary_physics_tick"] = value.BoundaryPhysicsTick,
            ["physics_tick"] = value.PhysicsTick,
            ["requested"] = DescribeOutcomeTransform(value.RequestedTransform),
            ["final"] = DescribeOutcomeTransform(value.FinalTransform),
            ["planar_error_m"] = value.Error.PlanarDistance,
            ["yaw_error_rad"] = value.Error.YawRadians,
            ["planar_tolerance_m"] = value.Tolerances.PlanarDistance,
            ["yaw_tolerance_rad"] = value.Tolerances.YawRadians,
            ["planar_margin_m"] = value.Margins.PlanarDistance,
            ["yaw_margin_rad"] = value.Margins.YawRadians,
            ["within_tolerance"] = value.IsWithinTolerance,
            ["occurrence"] = DescribeOccurrence(value.Occurrence),
        };
    }

    private static Godot.Collections.Dictionary DescribeCandidateDecision(
        NPCTerminalCandidateDecisionDiagnostics decision)
        => new()
        {
            ["has_candidate_evaluation"] = decision.HasCandidateEvaluation,
            ["reset_reason"] = decision.ResetReason ?? string.Empty,
            ["stage"] = decision.Stage.ToString(),
            ["terminal_committed"] = decision.TerminalCommitted,
            ["exit_count"] = decision.ExitCount,
            ["scheduler_commit"] = decision.SchedulerCommit,
            ["selected_candidate_index"] = decision.SelectedCandidateIndex,
            ["selected_scheduler_rejection"] = decision.SelectedSchedulerRejection.ToString(),
            ["terminal_release_selected"] = decision.TerminalReleaseSelected,
            ["same_lane_candidate"] = DescribeCandidateEvaluation(decision.SameLaneCandidate),
            ["opposite_lane_candidate"] = DescribeCandidateEvaluation(decision.OppositeLaneCandidate),
        };

    private static Godot.Collections.Dictionary DescribeCandidateEvaluation(
        NPCTerminalCandidateEvaluationDiagnostics candidate)
        => new()
        {
            ["is_evaluated"] = candidate.IsEvaluated,
            ["candidate_index"] = candidate.CandidateIndex,
            ["release_lane"] = candidate.ReleaseLane.ToString(),
            ["exit_role"] = candidate.ExitRole ?? string.Empty,
            ["release_event"] = candidate.ReleaseEvent ?? string.Empty,
            ["time_to_release_seconds"] = candidate.TimeToReleaseSeconds,
            ["projected_lead_in_ticks"] = candidate.ProjectedLeadInTicks,
            ["required_correction_world"] = candidate.RequiredCorrectionWorld,
            ["reachable_correction_world"] = candidate.ReachableCorrectionWorld,
            ["predicted_endpoint_residual_world"] = candidate.PredictedEndpointResidualWorld,
            ["predicted_endpoint_yaw_residual_rad"] = candidate.PredictedEndpointYawResidual,
            ["signed_terminal_overshoot_m"] = candidate.SignedTerminalOvershootMetres,
            ["would_overshoot_terminal"] = candidate.WouldOvershootTerminal,
            ["endpoint_serviceable"] = candidate.EndpointServiceable,
            ["yaw_compatible"] = candidate.YawCompatible,
            ["velocity_compatible"] = candidate.VelocityCompatible,
            ["contact_compatible"] = candidate.ContactCompatible,
            ["wait_compatible"] = candidate.WaitCompatible,
            ["supported_blend"] = candidate.SupportedBlend,
            ["scheduler_rejection"] = candidate.SchedulerRejection.ToString(),
        };

    /// <summary>
    /// Gets the live side-step planar remainder — the request offset minus the remaining authored clip forecast —
    /// in world planar coordinates, or zero outside an active side step.
    /// </summary>
    public Vector2 LastSideStepPlanarRemainingWorld => _sideStepPlanarWorldTotal;
    public bool IsShortBoutActive => _programme.Stage == NPCAnimationStage.ShortBout;
    public NPCShortBoutSplicePlan? ShortBoutPlan => _shortBout;
    public NPCTurnInPlacePlan? TurnInPlacePlan => _programme.TurnInPlace;
    public NPCSideStepPlan? SideStepPlan => _programme.SideStep;
    /// <summary>Gets whether a completed short bout is converging its bounded endpoint residual through settlement.</summary>
    public bool IsShortBoutResidualSettlementActive => _shortBoutResidualSettlement;
    public float LastShortBoutEntrySourcePosition
    {
        get; private set;
    }
    public float LastShortBoutExitSourcePosition
    {
        get; private set;
    }
    public float LastShortBoutBlendWeight
    {
        get; private set;
    }
    public float LastSmoothedPlaybackRate => _smoothedPlaybackRate;

    public static float FiniteTransitionSourceWallRatio => FiniteTransitionPlaybackRate;

    public float LastShortBoutEntryPlaybackRate
    {
        get; private set;
    }
    public float LastShortBoutExitPlaybackRate
    {
        get; private set;
    }
    public bool LastShortBoutSeekPublished
    {
        get; private set;
    }
    public bool LastShortBoutCrossfadeActive => _shortBoutCrossfadeWasActiveBeforeEvaluation;
    public bool LastShortBoutExitReachedEndpoint
    {
        get; private set;
    }
    public int LastShortBoutUnexpectedResetCount
    {
        get; private set;
    }
    public float LastShortBoutAggregatePoseDelta
    {
        get; private set;
    }
    public string LastShortBoutFailureReason { get; private set; } = string.Empty;
    public Vector2 LastShortBoutPlannedWorldCorrection => _shortBoutTotalWorldCorrection;
    public float LastShortBoutPlannedYawCorrection => _shortBoutTotalYawCorrection;
    public float LastShortBoutForecastResponseScale { get; private set; } = 1f;
    public Vector3 LastShortBoutForecastWorld => _shortBoutForecastWorld;
    public Vector3 LastShortBoutImportedForecastRoot => _shortBoutImportedForecastRoot;
    public float LastShortBoutImportedForecastYaw => _shortBoutImportedForecastYaw;
    public Vector3 LastShortBoutActualGraphForecastRoot => _shortBoutActualGraphForecastRoot;
    public float LastShortBoutActualGraphForecastYaw => _shortBoutActualGraphForecastYaw;
    public Vector3 LastShortBoutActualGraphRoot => _shortBoutObservedAuthoredRoot;
    public float LastShortBoutActualGraphYaw => _shortBoutObservedAuthoredYaw;
    public Vector3 LastShortBoutActualWorldDisplacement => _shortBoutObservedAuthoredWorld;
    public Vector3 LastModelledShortBoutRootDelta
    {
        get; private set;
    }
    public float LastModelledShortBoutRootYaw
    {
        get; private set;
    }
    public bool LastShortBoutDirectCollisionFree
    {
        get; private set;
    }
    public bool LastShortBoutCollisionDetected
    {
        get; private set;
    }
    public NPCShortBoutInvalidationReason LastShortBoutInvalidationReason
    {
        get; private set;
    }
    public float LastShortBoutRequestedDirectLength
    {
        get; private set;
    }
    public float LastShortBoutCumulativeRealisedDisplacement
    {
        get; private set;
    }
    public float LastShortBoutRealisedShortfall
    {
        get; private set;
    }
    public NPCShortBoutTerminalOutcome LastShortBoutTerminalOutcome
    {
        get; private set;
    }
    public int LastShortBoutTerminalFailureCount
    {
        get; private set;
    }
    public float ObjectiveReversalMinimumSpeed => _reversalCounter.MinimumSpeed;
    public int ObjectiveReversalRequiredConsecutiveFrames => _reversalCounter.RequiredConsecutiveFrames;
    public int ObjectiveReversalCount => _reversalCounter.Count;
    public bool LastObjectiveReversalConfirmed
    {
        get; private set;
    }
    public NPCResidualCorrectionScope CorrectionScope => _correctionBudget.Scope;
    public float LastRealisedPlanarCorrection => _correctionBudget.RealisedPlanar;
    public float LastRealisedYawCorrection => _correctionBudget.RealisedYaw;
    public float RemainingPlanarCorrection => _correctionBudget.RemainingPlanar;
    public float RemainingYawCorrection => _correctionBudget.RemainingYaw;
    public float ReservedTerminalPlanar => _correctionBudget.ReservedTerminalPlanar;
    public float ReservedTerminalYaw => _correctionBudget.ReservedTerminalYaw;
    public string LastSelectedExitRole { get; private set; } = string.Empty;
    public string LastSelectedReleaseEvent { get; private set; } = string.Empty;
    public NPCAnimationPhaseLane LastSelectedReleaseLane
    {
        get; private set;
    }
    public bool IsTerminalReleaseApproachActive => _terminalReleaseApproachActive;
    public float LastTargetReleasePhase
    {
        get; private set;
    }
    public float LastTimeToSelectedReleaseSeconds
    {
        get; private set;
    }
    public float LastTerminalCommitPhase
    {
        get; private set;
    }
    public Vector3 LastTerminalCommitRootAccumulator
    {
        get; private set;
    }
    public float LastTerminalSignedRemainingDistance
    {
        get; private set;
    }
    public float LastTerminalLateralError
    {
        get; private set;
    }
    public float LastTerminalRootTangentDisplacement
    {
        get; private set;
    }
    public float LastTerminalRealisedTangentDisplacement
    {
        get; private set;
    }
    public float LastTerminalResidualTangentDisplacement
    {
        get; private set;
    }
    public bool LastTerminalOvershot
    {
        get; private set;
    }
    public bool LastMovementCollided
    {
        get; private set;
    }
    public StringName LastEvaluatedState
    {
        get; private set;
    } = new();
    public StringName LastFadingFromState
    {
        get; private set;
    } = new();
    public float LastFadingFromPlayPosition
    {
        get; private set;
    }
    /// <summary>Raw evaluated fading-source length exposed for physics-tick consumers and diagnostics.</summary>
    public float LastFadingFromPlayLength
    {
        get; private set;
    }
    /// <summary>Actual outgoing ANIM-002 role while the graph is cross-fading.</summary>
    public string LastFadingFromRole
    {
        get; private set;
    } = string.Empty;
    /// <summary>Actual outgoing ANIM-002 library animation identity while the graph is cross-fading.</summary>
    public string LastFadingFromClip
    {
        get; private set;
    } = string.Empty;
    /// <summary>Normalised phase of the outgoing ANIM-002 source while the graph is cross-fading.</summary>
    public float LastFadingFromNormalisedPhase
    {
        get; private set;
    }
    public float LastEvaluatedPlayPosition
    {
        get; private set;
    }
    /// <summary>Raw evaluated graph-state length exposed for physics-tick diagnostics.</summary>
    public float LastEvaluatedPlayLength
    {
        get; private set;
    }
    public float LastEvaluatedNormalisedPhase
    {
        get; private set;
    }
    /// <summary>Previous engine-evaluated phase of the looping NPCTravel/Sustained child timeline.</summary>
    public float LastSustainedCyclePreviousPhase => _sustainedCyclePreviousPhase;
    /// <summary>Current engine-evaluated phase of the looping NPCTravel/Sustained child timeline.</summary>
    public float LastSustainedCyclePhase => _sustainedCycleCurrentPhase;
    /// <summary>Whether the looping child timeline supplied a finite, positive-cycle observation.</summary>
    public bool HasSustainedCycleObservation => _sustainedCycleAvailable;
    /// <summary>Projected child-cycle advance for the next graph evaluation at the published rate.</summary>
    public float LastSustainedCycleExpectedAdvance
    {
        get; private set;
    }
    public float LastTerminalCommitPreviousPhase
    {
        get; private set;
    }
    public float LastTerminalCommitExpectedAdvance
    {
        get; private set;
    }
    public float LastTerminalCommitPlaybackRate
    {
        get; private set;
    }
    public float LastTerminalCommitOrdinaryPlaybackRate
    {
        get; private set;
    }
    /// <summary>Actual evaluated ANIM-002 role exposed as diagnostic input only.</summary>
    public string LastEvaluatedRole { get; private set; } = string.Empty;
    public StandingLocomotionCapability? LastEvaluatedCapability
    {
        get; private set;
    }
    /// <summary>Actual evaluated ANIM-002 library animation identity exposed as diagnostic input only.</summary>
    public string LastEvaluatedClip { get; private set; } = string.Empty;
    /// <summary>NPC-only consumer for an already-authorised contact-lock contribution.</summary>
    public ContactLockCoordinator? ContactLockCoordinator
    {
        get; set;
    }
    /// <summary>Gets the most recent NPC-only contact consumption decision without affecting movement.</summary>
    public NPCContactLockConsumptionTrace LastContactLockConsumptionTrace
    {
        get; private set;
    }
    public Vector3 LastEvaluatedRootDelta
    {
        get; private set;
    }
    public float LastEvaluatedRootYaw
    {
        get; private set;
    }
    public Quaternion LastEvaluatedRootRotation
    {
        get; private set;
    } = Quaternion.Identity;
    public int PhysicalStepCount
    {
        get; private set;
    }
    public bool IsRuntimeBindingActive
    {
        get; private set;
    }
    /// <summary>
    /// Whether a terminal ANIM-002 package failure latched runtime-binding activation for this installation
    /// attempt. The unchanged failing package is not re-read, re-parsed, or re-validated; a new installation
    /// attempt (scene reload or an explicit catalogue-path change) starts fresh.
    /// </summary>
    public bool IsRuntimeBindingPackageFailureLatched => _runtimeBindingPackageFailureLatched;
    /// <summary>Terminal package-failure diagnostics reported during runtime-binding activation.</summary>
    public int RuntimeBindingPackageFailureDiagnosticCount
    {
        get; private set;
    }
    public ulong LastPublishedFrameGeneration
    {
        get; private set;
    }
    public ulong LastConsumedFrameGeneration
    {
        get; private set;
    }
    /// <summary>
    /// Enables complete per-generation movement-accounting publication for diagnostic captures, including explicit
    /// zero rows while navigation is idle. This does not alter movement, animation, routing, or contact behaviour.
    /// </summary>
    public bool CaptureCompleteMovementDiagnostics
    {
        get; set;
    }
    /// <summary>
    /// Publishes the selected candidate's allocation-bearing exact interval traces for NAV-001 evidence generation.
    /// The production policy always computes its private execution intervals; changing this property never alters
    /// behaviour.
    /// </summary>
    public bool CaptureVectorConvergenceDiagnostics
    {
        get; set;
    }
    /// <summary>Enables publication of fixed-size far/imminent exact-check counters without changing planning.</summary>
    public bool CaptureBoundedTerminalCheckDiagnostics
    {
        get; set;
    }
    /// <summary>Gets the latest bounded terminal-check counters, or zero when their publication is disabled.</summary>
    public NPCBoundedTerminalCheckDiagnostics LastBoundedTerminalCheckDiagnostics
        => CaptureBoundedTerminalCheckDiagnostics ? _boundedTerminalCheckDiagnostics : default;
    /// <summary>
    /// Test-only isolation for a raw ordinary-cycle accounting window. It suppresses terminal candidate planning so
    /// legacy exit-entry shaping cannot relabel the window as terminal approach movement. Disabled by default and
    /// never used by route execution or vector-convergence diagnostics.
    /// </summary>
    internal bool SuppressTerminalPlanningForOrdinaryCycleDiagnostics
    {
        get; set;
    }
    /// <summary>Gets whether this navigation authority uses the promoted production vector-convergence policy.</summary>
    public NPCVectorConvergenceExecutionLedger LastVectorConvergenceExecutionLedger
        => _terminalBoundaryMeasurement is not null ? _terminalVectorConvergenceLedger : _vectorConvergence.Ledger;
    public NPCVectorConvergenceResetReason LastVectorConvergenceResetReason
        => _vectorConvergence.LastResetReason;
    public Vector3 LastVectorConvergenceCommitmentModelledRootWorld
        => _terminalBoundaryMeasurement is not null
            ? _terminalCommitmentModelledRootWorld
            : _lastVectorConvergenceCommitmentModelledRootWorld;
    public Vector3 LastVectorConvergenceCommitmentConsumedRootWorld
        => _terminalBoundaryMeasurement is not null
            ? _terminalCommitmentConsumedRootWorld
            : _lastVectorConvergenceCommitmentConsumedRootWorld;
    public float LastVectorConvergenceCommitmentModelledYaw
        => _terminalBoundaryMeasurement is not null
            ? _terminalCommitmentModelledYaw
            : _lastVectorConvergenceCommitmentModelledYaw;
    public float LastVectorConvergenceCommitmentConsumedYaw
        => _terminalBoundaryMeasurement is not null
            ? _terminalCommitmentConsumedYaw
            : _lastVectorConvergenceCommitmentConsumedYaw;
    public Vector3 LastVectorConvergenceForecastAuthoredRootWorld
    {
        get; private set;
    }
    public Vector3 LastVectorConvergenceConsumedAuthoredRootWorld
    {
        get; private set;
    }
    public float LastVectorConvergenceForecastAuthoredYaw
    {
        get; private set;
    }
    public float LastVectorConvergenceConsumedAuthoredYaw
    {
        get; private set;
    }

    internal AnimationNodeBlendTree RuntimeGraphRoot
        => AnimationTree?.TreeRoot as AnimationNodeBlendTree
            ?? throw new InvalidOperationException("NPC navigation requires a runtime AnimationNodeBlendTree.");

    public override string[] _GetConfigurationWarnings()
    {
        List<string> warnings = [];
        if (Actor is null)
        {
            warnings.Add("Actor must reference the authoritative NPC CharacterBody3D.");
        }

        if (AnimationTree is null)
        {
            warnings.Add("AnimationTree must reference the NPC root-producing tree.");
        }

        if (RootMotionReference is null)
        {
            warnings.Add("RootMotionReference must reference the rig container used by authored Root motion.");
        }

        if (string.IsNullOrWhiteSpace(CataloguePath))
        {
            warnings.Add("CataloguePath must reference the ANIM-002 runtime catalogue.");
        }

        return [.. warnings];
    }

    public override void _Ready()
    {
        SetPhysicsProcess(false);
        if (!AwaitInstallerActivation)
        {
            ActivateRuntimeBinding();
        }
    }

    public void ActivateRuntimeBinding()
    {
        if (IsRuntimeBindingActive)
        {
            return;
        }

        if (_runtimeBindingPackageFailureLatched)
        {
            // A terminal package failure latched this installation attempt. Returning degraded (instead of
            // throwing) lets the installer chain complete once instead of retrying every frame; the failing
            // package is not re-read or re-validated while it remains unchanged.
            return;
        }

        if (_GetConfigurationWarnings().Length != 0)
        {
            throw new InvalidOperationException(string.Join(" ", _GetConfigurationWarnings()));
        }

        if (!AreBindingsInTree())
        {
            throw new InvalidOperationException("NPC navigation activation requires actor, AnimationTree, and RootMotionReference to be in the scene tree.");
        }

        if (!IsAnimationTreeRootResolved())
        {
            throw new InvalidOperationException(
                $"NPC navigation activation requires AnimationTree root '{AnimationTree!.RootNode}' to resolve after installer rebasing.");
        }

        if (!TryResolveRuntimeCatalogue(out StandingLocomotionCatalogue? catalogue))
        {
            return;
        }

        _catalogue = catalogue;
        _rootMotionScale = RootMotionReference!
            .RequireNode<Skeleton3D>("GeneralSkeleton")
            .MotionScale;
        if (!float.IsFinite(_rootMotionScale) || _rootMotionScale <= 0f)
        {
            throw new InvalidOperationException("NPC root-motion Skeleton3D must expose a finite positive MotionScale.");
        }
        ConfigureFiniteRootInterpolation();
        IsolateMutableAnimationGraph();
        ConfigureFiniteTransitionTiming();
        ConfigureIdleHandoffs();
        AnimationTree!.RootMotionLocal = true;
        AnimationTree.AnimPlayer = AnimationTree.GetPathTo(ResolveAnimationPlayer());
        // The tree owns its own automatic physics evaluation, so the whole animation pipeline — VISION-001 eye
        // blinking/eye look and hand-grab animation — keeps flowing even when navigation is idle or mid-transaction.
        // Navigation (process_physics_priority -100) publishes parameters this tick and consumes the resulting
        // root-motion delta on the next tick, after the tree's evaluation at the default priority (same contract as
        // CharacterLocomotion).
        AnimationTree.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Physics;
        // Installer rebasing can replace the tree root while this inherited AnimationTree is already active. Force one
        // deterministic mixer cache rebuild after all root/player references are final.
        AnimationTree.Active = false;
        AnimationTree.Active = true;
        InitialiseAnimationProgramme();
        ResolveExitCrossfadeDurations();
        RebuildSustainedBlendDomain();
        IsRuntimeBindingActive = true;
        SetPhysicsProcess(true);
    }

    /// <summary>
    /// Resolves the ANIM-002 runtime package once per installation attempt. A missing, malformed, stale, or
    /// capability-refusing package is a terminal failure for this attempt: it is latched, reported exactly once
    /// with the package path and failing validation, and not re-read while it remains unchanged. Wiring
    /// failures (bindings, tree root) stay fail-fast exceptions and keep their readiness retry semantics.
    /// </summary>
    private bool TryResolveRuntimeCatalogue(out StandingLocomotionCatalogue? catalogue)
    {
        catalogue = null;
        if (_runtimeBindingPackageFailureLatched)
        {
            return false;
        }

        try
        {
            using var file = Godot.FileAccess.Open(CataloguePath, Godot.FileAccess.ModeFlags.Read);
            if (file is null)
            {
                LatchRuntimeBindingPackageFailure($"the ANIM-002 runtime package catalogue '{CataloguePath}' could not be opened");
                return false;
            }

            var parsed = StandingLocomotionCatalogue.Parse(file.GetAsText(), CataloguePath);
            parsed.RequireCapabilities(
                nameof(NPCNavigation),
                Enum.GetValues<StandingLocomotionCapability>());
            catalogue = parsed;
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            LatchRuntimeBindingPackageFailure(
                $"the ANIM-002 runtime package catalogue '{CataloguePath}' failed validation: {exception.Message}");
            return false;
        }
    }

    private void LatchRuntimeBindingPackageFailure(string detail)
    {
        _runtimeBindingPackageFailureLatched = true;
        RuntimeBindingPackageFailureDiagnosticCount++;
        if (GameLoggerResolver.TryResolve(out ILogger<NPCNavigation>? logger) && logger is not null)
        {
            logger.LogError(
                "NPC navigation runtime binding degraded for this installation attempt: {Detail}. "
                + "The failing package is not re-read until a new installation attempt.",
                detail);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        base._PhysicsProcess(delta);
        if (!IsRuntimeBindingActive || !IsMovementAuthorityActive || delta <= 0d || !HasValidBinding())
        {
            Neutralise();
            return;
        }
        if (_shortBoutExecutionInvalid)
        {
            FinaliseShortBoutFailure();
            return;
        }

        float frameDelta = (float)delta;
        _physicsFrameGeneration++;
        _exactTerminalCandidateChecksThisFrame = 0;
        if (_terminalMotionBoundaryClosed)
        {
            ApplyHardTerminalMotionBoundary();
            AdvanceTerminalConfirmation();
            return;
        }
        if (CaptureCompleteMovementDiagnostics)
        {
            PublishZeroMovementDiagnosticFrame();
        }
        Transform3D before = Actor!.GlobalTransform;
        NavigationMotionIntent intent = HasDestination ? Poll(before) : default;
        if (!HasDestination || !LastPollProducedValidSample || !intent.HasValidSample || CurrentRouteSnapshot is null)
        {
            ResetTerminalCandidateDecisionDiagnostics("no coherent route sample");
            Neutralise();
            return;
        }

        NavigationRouteSnapshot snapshot = CurrentRouteSnapshot;
        if (_route is null
            || _route.DestinationRequestGeneration != snapshot.DestinationRequestGeneration
            || _route.RouteRevision != snapshot.RouteRevision)
        {
            ResetFarRouteStoppingState();
            _terminalFacingActivationDistance = ReleaseApproachActivationDistance;
            _route = NPCNavigationRoutePlan.Compile(snapshot, AuthoredExitDistance);
            _correctionBudget.BeginRoute(
                snapshot.DestinationRequestGeneration,
                snapshot.RouteRevision,
                MaximumPlanarResidualPerSegment,
                TerminalCommitmentCorrectionBudget,
                MaximumYawResidualPerSegment);
            ResetCorrectionSmoothing();
            ResetTerminalApproachState(NPCVectorConvergenceResetReason.RouteReplacement);
            ResetTerminalCandidateDecisionDiagnostics("route revision");
            if (!TryConfigureTurnInPlace(before, _route))
            {
                if (!TryConfigureSideStep(before, _route))
                {
                    TryConfigureShortBout(before, _route);
                }
            }
            if (_shortBoutExecutionInvalid)
            {
                FinaliseShortBoutFailure();
                return;
            }
        }

        RouteProjection projection = _route.Project(new Vector3(before.Origin.X, 0f, before.Origin.Z), LastRouteProgress, 0.2f);
        LastRouteTangent = projection.Direction;
        LastRouteProgress = projection.Distance;
        LastRemainingDistance = Math.Max(_route.TotalLength - projection.Distance, 0f);
        LastDestinationRequestGeneration = snapshot.DestinationRequestGeneration;
        LastRouteRevision = snapshot.RouteRevision;
        ObserveTerminalApproach(before);
        UpdateTerminalFacingActivationDistance();

        if (_programme.Stage == NPCAnimationStage.Idle && IsEffectivelyZeroRequest(before, _route))
        {
            _ = _programme.CompleteNoMovement();
            LastRemainingDistance = 0f;
            LastAuthoredRootWorldDelta = Vector3.Zero;
            LastRequestedSyntheticWorldDelta = Vector3.Zero;
            LastEvaluatedRootDelta = Vector3.Zero;
            LastEvaluatedRootYaw = 0f;
            LastEvaluatedRootRotation = Quaternion.Identity;
            LastModelledShortBoutRootDelta = Vector3.Zero;
            LastModelledShortBoutRootYaw = 0f;
            LastPublishedFrameGeneration = _physicsFrameGeneration;
            LastConsumedFrameGeneration = _physicsFrameGeneration;
            return;
        }

        AnimationNodeStateMachinePlayback playback = ResolvePlayback();
        bool finiteCompleted = ObserveFiniteCompletion(frameDelta);
        _ = _programme.ConfirmSustainedTravelEvaluated(
            LastEvaluatedState == _travelState && _sustainedCycleAvailable);
        if (_programme.Stage == NPCAnimationStage.EndpointHold && !IsTerminalTransformWithinNumericalTolerance())
        {
            InvalidateShortBout(
                NPCShortBoutInvalidationReason.ExitEndpointResidual,
                "Terminal transform left numerical tolerance during the bounded four-frame idle hold.");
        }
        if (_shortBoutExecutionInvalid)
        {
            FinaliseShortBoutFailure();
            return;
        }
        bool releaseSelected = TrySelectImminentTerminalRelease(before, _route, frameDelta);
        bool executeExit = releaseSelected && TryExecuteImminentRelease(frameDelta);
        releaseSelected = _terminalReleaseApproachActive;
        if (AdvanceRouteExhaustion(before, frameDelta))
        {
            return;
        }
        if (_programme.Stage == NPCAnimationStage.Exit && finiteCompleted)
        {
            _finiteExitCompletionObserved = true;
        }
        bool exitWindowConsumed = _vectorConvergence.Status == NPCVectorConvergenceExecutionStatus.Complete;
        NPCAnimationFrame frame = _programme.Step(
            before,
            _route,
            projection.Distance,
            _programme.Stage == NPCAnimationStage.Exit
                ? _finiteExitCompletionObserved && exitWindowConsumed
                : finiteCompleted,
            executeExit,
            terminalPhaseOneObserved: true);
        if (releaseSelected && !executeExit && frame.Stage == NPCAnimationStage.SustainedTravel)
        {
            frame = ResolveReleaseApproachFrame(frame, before, _route, frameDelta);
        }
        PublishProgramme(playback, frame, frameDelta);
        LastPublishedFrameGeneration = _physicsFrameGeneration;
        ObserveShortBoutPoseDeltas();
        if (frame.Event == NPCAnimationEvent.Complete)
        {
            ResetCorrectionSmoothing();
            ClearFiniteCorrectionEnvelopes();
            if (_shortBout is not null && LastShortBoutTerminalOutcome == NPCShortBoutTerminalOutcome.None)
            {
                LastShortBoutTerminalOutcome = NPCShortBoutTerminalOutcome.Succeeded;
                LastShortBoutRealisedShortfall = Math.Max(
                    LastShortBoutRequestedDirectLength - LastShortBoutCumulativeRealisedDisplacement,
                    0f);
            }
        }
        EnsureCorrectionScope(frame.Stage);
        CaptureEvaluatedGraphDiagnostics(playback);
        ApplyEvaluatedMotionOnce(frame, before, frameDelta, CurrentPermissions());
        LastConsumedFrameGeneration = _physicsFrameGeneration;
        RefreshTerminalCandidateDecisionRuntimeState();
        _releaseSelectedAtLastPublication = _terminalReleaseApproachActive;
        _sustainedCycleExpectedObservedAdvance = _terminalReleaseApproachActive
            || _programme.Stage == NPCAnimationStage.SustainedTravel
            ? Math.Max(_smoothedPlaybackRate, 0f) * frameDelta / Math.Max((float)ResolveSustainedSpace().CyclicLength, float.Epsilon)
            : 0f;
    }

    private void PublishZeroMovementDiagnosticFrame()
    {
        if (LastEvaluatedState.IsEmpty && _programme.Stage == NPCAnimationStage.Idle)
        {
            LastEvaluatedState = new StringName("NPCIdle");
        }
        LastPublishedFrameGeneration = _physicsFrameGeneration;
        LastConsumedFrameGeneration = _physicsFrameGeneration;
        LastMovementCompositionFrame = new(
            _physicsFrameGeneration,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            false);
    }

    /// <summary>
    /// Advances the route-exhaustion clock while ordinary sustained travel remains inside the bounded stopping
    /// region with no imminent release selected. One bound covers every imminent occurrence pair: the sustained
    /// clock presents a fresh same/opposite pair at least once per cycle, and a wait-bound pair becomes
    /// committable within the release wait window, so a full cycle plus the wait window without a commitment
    /// proves no serviceable stopping candidate exists for this route. Returns <see langword="true" /> when the
    /// route was exhausted and a durable failure was committed this frame.
    /// </summary>
    private bool AdvanceRouteExhaustion(Transform3D actor, float delta)
    {
        NavigationCurrentStatus status = CurrentStatus;
        if (_programme.Stage != NPCAnimationStage.SustainedTravel
            || _terminalReleaseApproachActive
            || status.ActiveRequest is not { } request
            || !_farRouteStoppingEstimator.IsAvailable
            || !_boundedTerminalCheckDiagnostics.IsInStoppingRegion)
        {
            _routeExhaustionSeconds = 0f;
            return false;
        }

        if (_routeExhaustionRequestGeneration != request.RequestID
            || _routeExhaustionRouteRevision != status.Revision)
        {
            _routeExhaustionRequestGeneration = request.RequestID;
            _routeExhaustionRouteRevision = status.Revision;
            _routeExhaustionSeconds = 0f;
        }

        _routeExhaustionSeconds += delta;
        float exhaustionBound = NPCTerminalReleaseScheduler.MaximumWaitSeconds
            + (float)ResolveSustainedSpace().CyclicLength;
        if (_routeExhaustionSeconds <= exhaustionBound)
        {
            return false;
        }

        CommitRouteExhaustionFailure(actor);
        return true;
    }

    /// <summary>
    /// Commits the durable route-exhaustion failure and holds the actor stationary under the hard terminal
    /// boundary. There is no post-commit motion, replan, or recovery for the request.
    /// </summary>
    private void CommitRouteExhaustionFailure(Transform3D actor)
    {
        NavigationCurrentStatus status = CurrentStatus;
        if (status.ActiveRequest is not { } request || status.RequestedTransform is not { } requested)
        {
            return;
        }

        _routeExhaustionSeconds = 0f;
        _routeExhaustionRequestGeneration = -1;
        _routeExhaustionRouteRevision = -1;
        ResetTerminalCandidateDecisionDiagnostics("route exhausted without a serviceable stopping candidate");
        DiscardImminentRelease(NPCVectorConvergenceResetReason.OccurrenceExpired);
        float finalYaw = NPCNavigationRoutePlan.YawFromDirection(
            actor.Basis.Orthonormalized() * Vector3.Forward);
        var realised = new NavigationOutcomeTransform(actor.Origin.X, actor.Origin.Y, actor.Origin.Z, finalYaw);
        var error = new NavigationPlanarYawError(
            new Vector2(actor.Origin.X - requested.X, actor.Origin.Z - requested.Z).Length(),
            Mathf.Abs(NPCNavigationRoutePlan.WrapAngle(finalYaw - requested.YawRadians)));
        NavigationSignedMargins margins = _terminalTolerances.CalculateMargins(error);
        ulong boundaryPhysicsTick = Engine.GetPhysicsFrames();
        var measurement = new NPCTerminalBoundaryMeasurement(
            request,
            status.Revision,
            status.ClockGeneration,
            default,
            boundaryPhysicsTick,
            boundaryPhysicsTick,
            requested,
            realised,
            error,
            _terminalTolerances,
            margins);
        bool committed = TryCommitFailedOutcome(
            CreateTerminalContext(measurement, NavigationTerminalReason.NoServiceableStoppingCandidate),
            boundaryPhysicsTick);
        if (committed)
        {
            _terminalBoundaryMeasurement = measurement;
            _terminalBoundaryState = NPCTerminalBoundaryState.Failed;
        }
        _terminalMotionBoundaryClosed = true;
        _terminalVectorConvergenceLedger = _vectorConvergence.Ledger;
        _ = EmitSignal(SignalName.TerminalConvergenceFailed);
    }

    private void CloseTerminalMotionBoundary()
    {
        NavigationCurrentStatus status = CurrentStatus;
        if (_terminalMotionBoundaryClosed || Actor is null || _route is null
            || status.ActiveRequest is not { } request
            || status.RequestedTransform is not { } requested)
        {
            return;
        }

        Transform3D final = Actor.GlobalTransform;
        float finalYaw = NPCNavigationRoutePlan.YawFromDirection(
            final.Basis.Orthonormalized() * Vector3.Forward);
        var realised = new NavigationOutcomeTransform(
            final.Origin.X,
            final.Origin.Y,
            final.Origin.Z,
            finalYaw);
        var error = new NavigationPlanarYawError(
            new Vector2(final.Origin.X - requested.X, final.Origin.Z - requested.Z).Length(),
            Mathf.Abs(NPCNavigationRoutePlan.WrapAngle(finalYaw - requested.YawRadians)));
        NavigationSignedMargins margins = _terminalTolerances.CalculateMargins(error);
        ulong boundaryPhysicsTick = Engine.GetPhysicsFrames();
        _terminalBoundaryMeasurement = new(
            request,
            status.Revision,
            status.ClockGeneration,
            _vectorConvergence.Occurrence,
            boundaryPhysicsTick,
            boundaryPhysicsTick,
            requested,
            realised,
            error,
            _terminalTolerances,
            margins);
        _terminalMotionBoundaryClosed = true;
        _terminalVectorConvergenceLedger = _vectorConvergence.Ledger;
        _terminalCommitmentModelledRootWorld = _lastVectorConvergenceCommitmentModelledRootWorld;
        _terminalCommitmentConsumedRootWorld = _lastVectorConvergenceCommitmentConsumedRootWorld;
        _terminalCommitmentModelledYaw = _lastVectorConvergenceCommitmentModelledYaw;
        _terminalCommitmentConsumedYaw = _lastVectorConvergenceCommitmentConsumedYaw;
        _vectorRequestedCorrection = Vector2.Zero;
        ResetCorrectionSmoothing();
        Actor.Velocity = Vector3.Zero;
        if (_terminalBoundaryMeasurement.Value.IsWithinTolerance)
        {
            _terminalBoundaryState = NPCTerminalBoundaryState.Confirmation;
            _terminalConfirmation.Begin();
            _ = EmitSignal(SignalName.TerminalConfirmationEntered);
            return;
        }

        NPCTerminalBoundaryMeasurement measurement = _terminalBoundaryMeasurement.Value;
        NavigationObstructionDetails? obstruction = LastMovementCompositionFrame.Collided
            ? new("terminal-boundary-collision", null, 0)
            : null;
        _terminalBoundaryState = NPCTerminalBoundaryState.Failed;
        bool committed = TryCommitFailedOutcome(
            CreateTerminalContext(measurement, NavigationTerminalReason.ConvergenceFailed, obstruction),
            measurement.PhysicsTick);
        if (committed)
        {
            _terminalBoundaryMeasurement = measurement;
            _terminalBoundaryState = NPCTerminalBoundaryState.Failed;
        }
        _ = EmitSignal(SignalName.TerminalConvergenceFailed);
    }

    private void AdvanceTerminalConfirmation()
    {
        if (_terminalBoundaryState != NPCTerminalBoundaryState.Confirmation
            || !_terminalConfirmation.IsActive
            || Actor is null
            || _terminalBoundaryMeasurement is not { } boundary)
        {
            return;
        }

        NavigationCurrentStatus status = CurrentStatus;
        NPCTerminalBoundaryMeasurement measurement = MeasureTerminalActor(boundary, Engine.GetPhysicsFrames());
        _terminalBoundaryMeasurement = measurement;
        if (status.ActiveRequest != boundary.Request
            || status.Revision != boundary.RouteRevision
            || status.ClockGeneration != boundary.ClockGeneration)
        {
            _terminalBoundaryState = NPCTerminalBoundaryState.Failed;
            NavigationTerminalReason reason = status.ClockGeneration != boundary.ClockGeneration
                ? NavigationTerminalReason.ClockGenerationChanged
                : NavigationTerminalReason.ContractFailure;
            bool committed = TryCommitFailedOutcome(CreateTerminalContext(measurement, reason), measurement.PhysicsTick);
            if (committed)
            {
                _terminalBoundaryMeasurement = measurement;
                _terminalBoundaryState = NPCTerminalBoundaryState.Failed;
            }
            _ = EmitSignal(SignalName.TerminalConvergenceFailed);
            return;
        }

        NPCTerminalConfirmationResult result = _terminalConfirmation.Observe(measurement.IsWithinTolerance);
        if (result == NPCTerminalConfirmationResult.Pending)
        {
            return;
        }

        if (result == NPCTerminalConfirmationResult.Failed)
        {
            _terminalBoundaryState = NPCTerminalBoundaryState.Failed;
            var disturbance = new NavigationObstructionDetails("stationary-confirmation-disturbance", null, 0);
            bool committed = TryCommitFailedOutcome(
                CreateTerminalContext(measurement, NavigationTerminalReason.ConvergenceFailed, disturbance),
                measurement.PhysicsTick);
            if (committed)
            {
                _terminalBoundaryMeasurement = measurement;
                _terminalBoundaryState = NPCTerminalBoundaryState.Failed;
            }
            _ = EmitSignal(SignalName.TerminalConvergenceFailed);
            return;
        }

        _terminalBoundaryState = NPCTerminalBoundaryState.Completed;
        bool completed = TryCommitCompletedOutcome(
            CreateTerminalContext(measurement, NavigationTerminalReason.DestinationReached),
            measurement.PhysicsTick);
        if (completed)
        {
            _terminalBoundaryMeasurement = measurement;
            _terminalBoundaryState = NPCTerminalBoundaryState.Completed;
        }
    }

    private NPCTerminalBoundaryMeasurement MeasureTerminalActor(
        in NPCTerminalBoundaryMeasurement boundary,
        ulong physicsTick)
    {
        Transform3D actor = Actor!.GlobalTransform;
        float yaw = NPCNavigationRoutePlan.YawFromDirection(actor.Basis.Orthonormalized() * Vector3.Forward);
        var final = new NavigationOutcomeTransform(actor.Origin.X, actor.Origin.Y, actor.Origin.Z, yaw);
        var error = new NavigationPlanarYawError(
            new Vector2(final.X - boundary.RequestedTransform.X, final.Z - boundary.RequestedTransform.Z).Length(),
            Mathf.Abs(NPCNavigationRoutePlan.WrapAngle(yaw - boundary.RequestedTransform.YawRadians)));
        return boundary with
        {
            PhysicsTick = physicsTick,
            FinalTransform = final,
            Error = error,
            Margins = boundary.Tolerances.CalculateMargins(error),
        };
    }

    private static NavigationTerminalContext CreateTerminalContext(
        in NPCTerminalBoundaryMeasurement measurement,
        NavigationTerminalReason reason,
        NavigationObstructionDetails? obstruction = null)
        => new(
            measurement.FinalTransform,
            measurement.Error,
            measurement.Tolerances,
            reason,
            obstruction,
            measurement.BoundaryPhysicsTick,
            measurement.Occurrence);

    private NavigationTerminalContext CreateCurrentTerminalContext(
        NavigationTerminalReason reason,
        NavigationObstructionDetails? obstruction = null)
    {
        Transform3D actor = Actor?.GlobalTransform ?? Transform3D.Identity;
        NavigationOutcomeTransform requested = CurrentStatus.RequestedTransform ?? default;
        float yaw = NPCNavigationRoutePlan.YawFromDirection(actor.Basis.Orthonormalized() * Vector3.Forward);
        var final = new NavigationOutcomeTransform(actor.Origin.X, actor.Origin.Y, actor.Origin.Z, yaw);
        var error = new NavigationPlanarYawError(
            new Vector2(final.X - requested.X, final.Z - requested.Z).Length(),
            Mathf.Abs(NPCNavigationRoutePlan.WrapAngle(yaw - requested.YawRadians)));
        return new(final, error, _terminalTolerances, reason, obstruction);
    }

    private void ApplyHardTerminalMotionBoundary()
    {
        Actor!.Velocity = Vector3.Zero;
        _vectorRequestedCorrection = Vector2.Zero;
        _smoothedResidual = Vector2.Zero;
        _residualAcceleration = Vector2.Zero;
        _smoothedYawResidual = 0f;
        _previousAppliedYawRate = 0f;
        LastPlanarResidual = Vector2.Zero;
        LastYawResidual = 0f;
        LastVectorConvergenceForecastAuthoredRootWorld = Vector3.Zero;
        LastVectorConvergenceConsumedAuthoredRootWorld = Vector3.Zero;
        LastVectorConvergenceForecastAuthoredYaw = 0f;
        LastVectorConvergenceConsumedAuthoredYaw = 0f;
        LastAuthoredRootWorldDelta = Vector3.Zero;
        LastRequestedSyntheticWorldDelta = Vector3.Zero;
        LastAuthoredRootLongitudinal = 0f;
        LastRequestedSyntheticLongitudinal = 0f;
        LastRealisedLongitudinal = 0f;
        LastRealisedRouteLongitudinal = 0f;
        _lastRealisedDisplacement = Vector3.Zero;
        _lastCollisionTravel = Vector3.Zero;
        _lastCollisionRemainder = Vector3.Zero;
        LastMovementCollided = false;
        ContactLockMotionContribution available = ContactLockCoordinator?.GetAuthorisedContribution() ?? default;
        ContactLockLiveState live = ContactLockCoordinator?.LiveState ?? default;
        LastContactLockConsumptionTrace = new(
            Engine.GetPhysicsFrames(),
            _physicsFrameGeneration,
            LastEvaluatedRole,
            LastEvaluatedNormalisedPhase,
            live.PhysicsTick,
            live.ClockGeneration,
            available.PhysicsTick,
            available.ClockGeneration,
            available.ConsumptionIdentity,
            available.IsAuthorised,
            false,
            false);
        PublishZeroMovementDiagnosticFrame();
    }

    /// <summary>
    /// Latches the terminal-facing activation distance for the active request and route revision. The required
    /// lead derives from the yaw work between the route tangent the actor walks and the requested terminal
    /// facing — a route property, measured while sustained travel supplies the ordinary walking speed. The
    /// maximum latch keeps the activation engaged as the remaining distance shrinks.
    /// </summary>
    private void UpdateTerminalFacingActivationDistance()
    {
        if (_route is null || _programme.Stage != NPCAnimationStage.SustainedTravel)
        {
            return;
        }

        float terminalRouteYaw = NPCNavigationRoutePlan.YawFromDirection(_route.TerminalDirection);
        float yawError = Mathf.Abs(NPCNavigationRoutePlan.WrapAngle(_route.TerminalYaw - terminalRouteYaw));
        const float terminalYawGuardWindow = 0.2f;
        if (yawError <= terminalYawGuardWindow)
        {
            return;
        }

        float turnSeconds = yawError / NPCTerminalReleaseScheduler.MaximumYawRate;
        float ordinarySpeed = ResolveSustainedSemanticVelocity(
            _sustainedCycleCurrentPhase,
            NPCFiniteTransitionCadence.SourceSecondsPerWallSecond).Length();
        float derived = TerminalFacingLeadSafetyFactor * turnSeconds * Mathf.Max(ordinarySpeed, 0.1f);
        _terminalFacingActivationDistance = Mathf.Max(
            _terminalFacingActivationDistance,
            Mathf.Max(ReleaseApproachActivationDistance, derived));
    }

    private void ObserveTerminalApproach(Transform3D actor)
    {
        Vector3 tangent = _route!.TerminalDirection;
        if (!tangent.IsFinite() || tangent.LengthSquared() <= 0.0000001f)
        {
            return;
        }

        tangent = tangent.Normalized();
        Vector2 terminalError = _route.TerminalError(actor.Origin);
        LastTerminalTangent = tangent;
        LastTerminalSignedRemainingDistance = terminalError.Y;
        LastTerminalLateralError = Mathf.Abs(terminalError.X);
        LastTerminalOvershot = LastTerminalSignedRemainingDistance < -NPCAnimationProgramme.NumericalConvergenceThreshold;
        Vector3 rootAccumulator = AnimationTree!.GetRootMotionPositionAccumulator();
        if (_terminalApproachObserved)
        {
            LastTerminalRootTangentDisplacement = (rootAccumulator - _previousTerminalRootAccumulator).Dot(tangent);
            LastTerminalRealisedTangentDisplacement = (actor.Origin - _previousTerminalActorPosition).Dot(tangent);
            LastTerminalResidualTangentDisplacement = LastTerminalRealisedTangentDisplacement - LastTerminalRootTangentDisplacement;
        }

        _previousTerminalRootAccumulator = rootAccumulator;
        _previousTerminalActorPosition = actor.Origin;
        _terminalApproachObserved = true;
    }

    protected override Vector3 GetNavigationStartPosition()
        => Actor?.GlobalPosition.IsFinite() == true ? Actor.GlobalPosition : base.GetNavigationStartPosition();

    protected override NavigationTerminalContext CreateLifecycleTerminalContext(
        Transform3D finalTransform,
        NavigationTerminalReason reason)
        => CurrentStatus.HasActiveRequest
            ? CreateCurrentTerminalContext(reason)
            : base.CreateLifecycleTerminalContext(finalTransform, reason);

    protected override bool CanReportNavigationComplete()
        => _programme.Stage == NPCAnimationStage.Complete;

    protected override void OnDestinationAccepted(Transform3D destination)
    {
        _terminalMotionBoundaryClosed = false;
        _terminalBoundaryMeasurement = null;
        _terminalBoundaryState = NPCTerminalBoundaryState.Inactive;
        _terminalConfirmation.Reset();
        _terminalVectorConvergenceLedger = default;
        _terminalCommitmentModelledRootWorld = Vector3.Zero;
        _terminalCommitmentConsumedRootWorld = Vector3.Zero;
        _terminalCommitmentModelledYaw = 0f;
        _terminalCommitmentConsumedYaw = 0f;
        _terminalTolerances = new(
            Mathf.Max(TerminalPositionTolerance, 0f),
            Mathf.DegToRad(Mathf.Max(TerminalYawToleranceDegrees, 0f)));
        _finiteExitCompletionObserved = false;
        ResetFarRouteStoppingState();
        _terminalFacingActivationDistance = ReleaseApproachActivationDistance;
        // NAV-001 one-entry-per-bout guard: a destination REPLACE that arrives while a manoeuvre is still
        // active (entry, sustained travel, confined manoeuvre, exit, settlement) must not reset the programme —
        // the reset re-classified the bout from Idle and re-issued a fresh i2*/side-step from mid-flight,
        // restarting the entry clip (measured foot sweep 10.9 m/s at the NPCI2FL->NPCI2FR restart). Instead the
        // route recompiles on the destination-generation mismatch next tick and the running manoeuvre commits
        // the new plan at its next natural boundary (the entry finishes into travel toward the new target; a
        // sustained bout walks its blend to the new demand). Genuine fresh requests still reset: from Idle,
        // Complete or after ClearDestination.
        bool boutActive = _programme.Stage is NPCAnimationStage.Entry
            or NPCAnimationStage.ShortBout
            or NPCAnimationStage.SustainedTravel
            or NPCAnimationStage.Exit
            or NPCAnimationStage.Settlement
            or NPCAnimationStage.Pivot
            or NPCAnimationStage.SideStep;
        _route = null;
        // Route-scoped state always resets with the new destination: the previous route's progress hint and
        // reversal ledgers belong to the replaced route and poison the recompiled route's projection if they
        // leak (measured: a mid-travel replace kept walking past the new target indefinitely).
        LastRouteProgress = 0f;
        _reversalCounter.Reset();
        LastObjectiveReversalConfirmed = false;
        if (!boutActive)
        {
            _shortBout = null;
            _shortBoutExecutionInvalid = false;
            _shortBoutFailureFinalised = false;
            _shortBoutImportedEntry = null;
            _shortBoutImportedExit = null;
            _shortBoutResidualSettlement = false;
            _pivotAbsorptionWorldTotal = Vector2.Zero;
            _pivotAbsorptionPublishedWorld = Vector2.Zero;
            _sideStepAbsorptionYawTotal = 0f;
            _sideStepAbsorptionPublishedYaw = 0f;
            LastShortBoutDirectCollisionFree = false;
            LastShortBoutCollisionDetected = false;
            LastShortBoutInvalidationReason = NPCShortBoutInvalidationReason.None;
            LastShortBoutRequestedDirectLength = 0f;
            LastShortBoutCumulativeRealisedDisplacement = 0f;
            LastShortBoutRealisedShortfall = 0f;
            LastShortBoutTerminalOutcome = NPCShortBoutTerminalOutcome.None;
            LastShortBoutTerminalFailureCount = 0;
            LastShortBoutFailureReason = string.Empty;
            LastShortBoutExitReachedEndpoint = false;
            LastShortBoutUnexpectedResetCount = 0;
            _programme.Reset();
            _correctionBudget.Invalidate();
            ResetCorrectionSmoothing();
        }

        ResetTerminalApproachState(NPCVectorConvergenceResetReason.RouteReplacement);
        ResetTerminalCandidateDecisionDiagnostics("destination accepted");
    }

    protected override void OnDestinationCleared()
    {
        _terminalBoundaryMeasurement = null;
        _terminalBoundaryState = NPCTerminalBoundaryState.Inactive;
        _terminalConfirmation.Reset();
        ResetFarRouteStoppingState();
        _terminalFacingActivationDistance = ReleaseApproachActivationDistance;
        _route = null;
        _shortBout = null;
        _shortBoutResidualSettlement = false;
        _pivotAbsorptionWorldTotal = Vector2.Zero;
        _pivotAbsorptionPublishedWorld = Vector2.Zero;
        _sideStepAbsorptionYawTotal = 0f;
        _sideStepAbsorptionPublishedYaw = 0f;
        _programme.Reset();
        _correctionBudget.Invalidate();
        _reversalCounter.Reset();
        LastObjectiveReversalConfirmed = false;
        LastRouteProgress = 0f;
        LastRemainingDistance = 0f;
        ResetCorrectionSmoothing();
        ResetTerminalApproachState(NPCVectorConvergenceResetReason.DestinationCleared);
        ResetTerminalCandidateDecisionDiagnostics("destination cleared");
        Neutralise();
    }

    public override void _ExitTree()
    {
        if (CurrentStatus.HasActiveRequest)
        {
            _ = TryCancelActiveOutcome(
                NavigationCancellationSource.OwnerRemoved,
                CreateCurrentTerminalContext(NavigationTerminalReason.Cancellation),
                Engine.GetPhysicsFrames());
        }
        ResetFarRouteStoppingState();
        ClearSustainedSamplingSnapshot();
        Neutralise();
    }

    protected virtual Vector3 GetRootMotionPositionDelta() => AnimationTree?.GetRootMotionPosition() ?? Vector3.Zero;
    protected virtual Quaternion GetRootMotionRotationDelta() => AnimationTree?.GetRootMotionRotation() ?? Quaternion.Identity;
    protected virtual Basis GetRootMotionReferenceBasis() => RootMotionReference?.GlobalBasis.Orthonormalized() ?? Basis.Identity;
    protected virtual void InitialiseAnimationProgramme()
        => ResolvePlayback().Start("NPCIdle", reset: true);
    protected virtual bool AreBindingsInTree()
        => Actor?.IsInsideTree() == true
            && AnimationTree?.IsInsideTree() == true
            && RootMotionReference?.IsInsideTree() == true;
    protected virtual bool IsAnimationTreeRootResolved()
        => AnimationTree?.GetNodeOrNull(AnimationTree.RootNode) is not null;
    protected virtual AnimationPlayer ResolveAnimationPlayer()
        => Actor?.GetNodeOrNull<AnimationPlayer>("AnimationPlayer")
            ?? throw new InvalidOperationException("NPC navigation activation requires the actor AnimationPlayer.");
    protected virtual bool MoveActorAndSlide(float delta)
    {
        PhysicalStepCount++;
        Vector3 requested = Actor!.Velocity * delta;
        KinematicCollision3D? collision = Actor.MoveAndCollide(requested);
        _lastCollisionTravel = collision?.GetTravel() ?? requested;
        _lastCollisionRemainder = collision?.GetRemainder() ?? Vector3.Zero;
        return collision is not null;
    }

    private void PublishProgramme(AnimationNodeStateMachinePlayback playback, NPCAnimationFrame frame, float delta)
    {
        LastPublishedAnimationFrame = frame;
        if (!frame.State.IsEmpty)
        {
            ValidateBoundaryMapping(frame.Event);
            if (frame.Event == NPCAnimationEvent.StartShortBout && _shortBout is NPCShortBoutSplicePlan shortBoutPlan)
            {
                ConfigureShortBoutClipWindows(frame.State, shortBoutPlan);
                AnimationTree!.ClearCaches();
                _shortBoutExitTransitionRequested = false;
                _shortBoutCorrectionInitialised = false;
                _shortBoutPreviousEntrySourcePosition = 0f;
                _shortBoutPreviousExitSourcePosition = shortBoutPlan.ExitCutTime;
                LastShortBoutEntrySourcePosition = 0f;
                LastShortBoutExitSourcePosition = shortBoutPlan.ExitCutTime;
            }
            if (frame.Event == NPCAnimationEvent.StartEntry)
            {
                AlignUnsuffixedSamplesToSelectedSeam();
                RebuildSustainedBlendDomain();
                _entryHandoffBlendPoint = ResolveSustainedPointPosition(
                    StandingLocomotionCapabilities.Travel(_programme.IsBackwardBout));
                _entryHandoffUnitRootSpeed = MeasureSustainedHandoffUnitRootSpeed(_entryHandoffBlendPoint);
                _entryHandoffBlendActive = true;
                _smoothedBlend = _entryHandoffBlendPoint;
                _blendCommandAcceleration = Vector2.Zero;
                _activeEntryCrossfadeDuration = ResolveLinearCrossfadeDuration(frame.State, _travelState);
            }
            if (frame.Event == NPCAnimationEvent.StartTravel)
            {
                playback.Travel(frame.State, resetOnTeleport: false);
            }
            else if (frame.Event is NPCAnimationEvent.StartExit
                or NPCAnimationEvent.StartEndpointHold
                or NPCAnimationEvent.Complete)
            {
                playback.Travel(frame.State, resetOnTeleport: false);
            }
            else
            {
                playback.Start(frame.State, reset: true);
            }
            _finiteStateObserved = frame.Stage is NPCAnimationStage.Entry
                or NPCAnimationStage.Exit
                or NPCAnimationStage.ShortBout
                or NPCAnimationStage.Pivot
                or NPCAnimationStage.SideStep;
            if (_finiteStateObserved)
            {
                _finiteStateElapsed = 0f;
                _finiteStateDuration = frame.Stage == NPCAnimationStage.ShortBout
                    ? ResolveShortBoutDuration()
                    : ResolveFiniteStateDuration(frame.State);
            }
            if (frame.Event == NPCAnimationEvent.StartTravel)
            {
                _entryHandoffBlendActive = true;
                _entryHandoffPublicationFrames = Math.Max(
                    Mathf.CeilToInt(_activeEntryCrossfadeDuration / Math.Max(delta, float.Epsilon)) + 2,
                    2);
                _entryHandoffPlaybackRate = ResolveEntryHandoffPlaybackRate(delta);
                _smoothedPlaybackRate = _entryHandoffPlaybackRate;
                _playbackRateAcceleration = 0f;
                BeginCorrectionScope(NPCResidualCorrectionScope.Travel);
            }
            else if (frame.Event == NPCAnimationEvent.StartExit)
            {
                // An immediate exit may be queued behind the still-evaluating entry handoff. Exit owns the
                // ordinary-rate moving timeline from this boundary, so the queued sustained contribution and the
                // subsequent travel-to-exit fade must not inherit the entry speed match or handoff pin.
                _entryHandoffBlendActive = false;
                _entryHandoffPublicationFrames = 0;
                _smoothedPlaybackRate = NPCFiniteTransitionCadence.SourceSecondsPerWallSecond;
                _playbackRateAcceleration = 0f;
                _finiteExitCompletionObserved = false;
                BeginCorrectionScope(NPCResidualCorrectionScope.Exit);
            }
            else if (frame.Event == NPCAnimationEvent.StartSettlement)
            {
                BeginCorrectionScope(NPCResidualCorrectionScope.Settlement);
            }
        }

        bool supportBlendHold = HasLiveSustainedState
            && NPCContactPhasePolicy.IsDeclaredSupportWindow(_sustainedCycleCurrentPhase);
        NPCBlendDomain domain = _sustainedBlendDomain
            ?? throw new InvalidOperationException("NPC sustained blend domain must be initialised before programme publication.");
        Vector2 targetBlend = _entryHandoffBlendActive
            ? _entryHandoffBlendPoint
            : supportBlendHold
            ? _smoothedBlend
            : domain.Project(_terminalReleaseApproachActive
                ? _releaseApproachBlendPoint
                : frame.BlendPoint);
        // The release approach converges the blend onto the pinned walk point faster than the sustained limits so a
        // lateral or arc mix stance reaches the walk-family exit pose inside the bounded lead-in —
        // otherwise the exit hop crossfades a still-mixed stance into the walk exit pose (measured sweeps
        // 4.4-5.1 m/s on diagonal and replaced routes).
        bool convergingToReleaseApproach = _terminalReleaseApproachActive && !_entryHandoffBlendActive;
        _smoothedBlend = domain.Project(NPCTransitionContinuity.StepVelocityCommand(
            domain.Project(_smoothedBlend),
            targetBlend,
            ref _blendCommandAcceleration,
            delta,
            convergingToReleaseApproach ? 4f : 0.8f,
            convergingToReleaseApproach ? 20f : 10f));
        LastBlendPoint = _smoothedBlend;
        AnimationTree!.Set(_sustainedBlendParameter, _smoothedBlend);
        float targetPlaybackRate = frame.Event == NPCAnimationEvent.StartExit
                ? NPCFiniteTransitionCadence.SourceSecondsPerWallSecond
            : _entryHandoffBlendActive
            ? _entryHandoffPlaybackRate
            : frame.PlaybackRate;
        // Rate alignment is only an armed-wait timing command. Once the forward-crossing gate requests the
        // finite exit, restore the ordinary sustained rate immediately so the outgoing side of the crossfade
        // keeps moving with the full-rate incoming clip instead of freezing at the release pose. The graph
        // consumes this publication on its next evaluation, so snapping here changes neither the selected beat
        // nor the one-shot release decision that has already executed.
        bool selectedReleaseRateAlignment = _terminalReleaseApproachActive
            && targetPlaybackRate + 0.00001f < _smoothedPlaybackRate
            && LastTimeToSelectedReleaseSeconds <= NPCCommandedReleaseProjection.PublisherRateAlignmentWindowTicks * delta;
        bool releaseCrossing = frame.Event == NPCAnimationEvent.StartExit;
        if (releaseCrossing)
        {
            _smoothedPlaybackRate = targetPlaybackRate;
            _playbackRateAcceleration = 0f;
        }
        else
        {
            _smoothedPlaybackRate = selectedReleaseRateAlignment
                ? targetPlaybackRate
                : NPCCommandedReleaseProjection.StepPlaybackRate(
                    _smoothedPlaybackRate,
                    targetPlaybackRate,
                    ref _playbackRateAcceleration,
                    delta);
        }
        AnimationTree.Set(_playbackRateParameter, _smoothedPlaybackRate);
        if (HasLiveSustainedState)
        {
            _terminalScheduler.Publish(
                new(
                    _smoothedBlend,
                    _smoothedPlaybackRate,
                    new Vector2(LastAuthoredRootWorldDelta.X, LastAuthoredRootWorldDelta.Z) / Math.Max(delta, float.Epsilon),
                    LastEvaluatedRootYaw / Math.Max(delta, float.Epsilon),
                    _sustainedCycleCurrentPhase,
                    NPCContactPhasePolicy.DeclaredSupportLane(_programme.PhaseLane)),
                domain.Contains(_smoothedBlend));
        }
        if (frame.Stage == NPCAnimationStage.SustainedTravel && _entryHandoffPublicationFrames > 0)
        {
            _entryHandoffPublicationFrames--;
            _entryHandoffBlendActive = _entryHandoffPublicationFrames > 0;
        }
        if (frame.Stage == NPCAnimationStage.ShortBout && _shortBout is NPCShortBoutSplicePlan plan)
        {
            PublishShortBoutPlayback(plan, delta);
        }
    }

    private void PublishShortBoutPlayback(NPCShortBoutSplicePlan plan, float delta)
    {
        AnimationNodeStateMachinePlayback playback = ResolvePlayback();
        string prefixState = ShortBoutPrefixState(plan);
        string exitState = ShortBoutExitState(plan);
        if (!_shortBoutExitTransitionRequested
            && playback.GetCurrentNode() == prefixState
            && playback.GetCurrentPlayPosition() + delta >= Math.Max(plan.EntryCutTime - plan.CrossfadeDuration, 0f))
        {
            playback.Travel(exitState, resetOnTeleport: false);
            _shortBoutExitTransitionRequested = true;
        }
    }

    private void ConfigureShortBoutClipWindows(StringName state, NPCShortBoutSplicePlan plan)
    {
        CacheImportedShortBoutTracks(plan);
        AnimationNodeBlendTree root = AnimationTree!.TreeRoot as AnimationNodeBlendTree
            ?? throw new InvalidOperationException("NPC AnimationTree root must be a blend tree.");
        AnimationNodeStateMachine states = root.GetNode("States") as AnimationNodeStateMachine
            ?? throw new InvalidOperationException("NPC AnimationTree must expose States.");
        AnimationNodeAnimation entry = states.GetNode(state) as AnimationNodeAnimation
            ?? throw new InvalidOperationException($"NPC short-bout state '{state}' must be an entry-prefix animation.");
        AnimationNodeAnimation exit = states.GetNode(ShortBoutExitState(plan)) as AnimationNodeAnimation
            ?? throw new InvalidOperationException($"NPC short-bout state '{ShortBoutExitState(plan)}' must be an exit-suffix animation.");
        float exitSuffixDuration = (float)_catalogue![plan.Pair.ExitRole].DurationSeconds - plan.ExitCutTime;
        entry.UseCustomTimeline = true;
        entry.StartOffset = 0f;
        entry.TimelineLength = plan.EntryCutTime;
        entry.StretchTimeScale = false;
        entry.LoopMode = Animation.LoopModeEnum.None;
        exit.UseCustomTimeline = true;
        exit.StartOffset = plan.ExitCutTime;
        exit.TimelineLength = exitSuffixDuration;
        exit.StretchTimeScale = false;
        exit.LoopMode = Animation.LoopModeEnum.None;
    }

    private void ApplyEvaluatedMotionOnce(
        NPCAnimationFrame frame,
        Transform3D before,
        float delta,
        LocomotionPermissions permissions)
    {
        NPCNavigationSemanticFrame semanticFrame = new(before.Basis, GetRootMotionReferenceBasis());
        if (frame.Event == NPCAnimationEvent.StartShortBout)
        {
            InitialiseShortBoutCorrection(before);
        }
        if (frame.Event == NPCAnimationEvent.StartSideStep)
        {
            // Every repetition but the last plays as a pure authored full clip, restarted with
            // zero crossfade; the confined idle handoffs are travel-only transitions, so the graph never visits
            // NPCIdle between repetitions. The final repetition disperses the planar remainder through the
            // side-step-only planar schedule and absorbs the yaw residual mid-phase — ending navigation on the
            // authored endpoint pose.
            bool finalSideStep = _programme.SideStepChainRemaining <= 1;
            InitialiseSideStepPlanarCorrection(before, finalSideStep);
            _sideStepAbsorptionYawTotal = finalSideStep && _programme.SideStep is { } sideStepPlan
                ? sideStepPlan.AbsorbedYawRadians
                : 0f;
            _sideStepAbsorptionPublishedYaw = 0f;
        }
        if (frame.Event == NPCAnimationEvent.StartPivot)
        {
            // Pivot chaining disperses cross-DOF planar absorption across the first pivot only; later
            // repetitions replay pure authored yaw, restarting the clip with zero crossfade and no idle seam.
            NPCTurnInPlacePlan? pivotPlan = _programme.TurnInPlace;
            if (pivotPlan is { } turn && _programme.PivotChainRemaining < turn.ChainedPivotCount)
            {
                _pivotAbsorptionWorldTotal = Vector2.Zero;
            }
        }
        if (frame.Event == NPCAnimationEvent.StartSettlement)
        {
            // The finite-state envelopes own only their clips; settlement's no-crossing convergence takes over from
            // a clean state so stale snapshots can never leak across the stage boundary.
            ClearFiniteCorrectionEnvelopes();
        }
        Vector3 rootDelta = LastEvaluatedRootDelta;
        if (frame.Stage is NPCAnimationStage.Settlement or NPCAnimationStage.EndpointHold or NPCAnimationStage.Complete)
        {
            rootDelta = Vector3.Zero;
        }
        Vector3 rootWorldVelocity = permissions.MovementAllowed && rootDelta.IsFinite()
            ? semanticFrame.EvaluatedRootDeltaToWorld(rootDelta) / delta
            : Vector3.Zero;
        Vector3 semanticProjectedRootWorldDelta = frame.Stage == NPCAnimationStage.SustainedTravel
            ? ExpectedSustainedRootWorldDelta(frame, before, delta)
            : Vector3.Zero;
        Vector3 programmeDesiredWorldDelta = semanticFrame.ActorSemanticToWorld(frame.DesiredLocalVelocity) * delta;
        if (frame.Stage == NPCAnimationStage.ShortBout)
        {
            _shortBoutObservedAuthoredWorld += rootWorldVelocity * delta;
            _shortBoutObservedAuthoredRoot += rootDelta;
            PrepareShortBoutCorrectionFromObservedEntry();
        }
        bool sideStepPlanarEngaged = _sideStepPlanarWorldTotal.LengthSquared() > 0.0000000001f;
        if (frame.Stage == NPCAnimationStage.SideStep && sideStepPlanarEngaged)
        {
            _sideStepPlanarObservedAuthoredWorld += new Vector2(rootWorldVelocity.X, rootWorldVelocity.Z) * delta;
            _sideStepPlanarWorldTotal = ResolveSideStepPlanarRemainingTotal();
        }
        Vector2 authoredVelocity = new(rootWorldVelocity.X, rootWorldVelocity.Z);
        bool rootProducesPlanarMotion = (authoredVelocity * delta).LengthSquared() > 0.0000000001f;
        bool complete = frame.Stage is NPCAnimationStage.EndpointHold or NPCAnimationStage.Complete;
        float evaluatedRootSpeedWeight = Mathf.Clamp(authoredVelocity.Length() / 0.93f, 0f, 1f);
        float speedWeight = frame.Stage is NPCAnimationStage.Entry or NPCAnimationStage.SustainedTravel
            ? evaluatedRootSpeedWeight
            : Math.Max(evaluatedRootSpeedWeight, LowSpeedCorrectionWeight);
        float phaseWeight = PhaseCorrectionWeight();
        if (frame.Stage == NPCAnimationStage.SustainedTravel
            && _terminalReleaseApproachActive
            && evaluatedRootSpeedWeight > 0.0001f)
        {
            speedWeight = 1f;
            phaseWeight = 1f;
        }
        bool shortBoutAnimating = frame.Stage == NPCAnimationStage.ShortBout
            && LastShortBoutAggregatePoseDelta > 0.000001f;
        if (shortBoutAnimating && LastShortBoutBlendWeight > 0f)
        {
            UpdateShortBoutCorrectionFromObserved();
        }
        // Confined cross-DOF residual absorption: a pivot disperses a small planar offset and a
        // confined side step disperses a small yaw residual mid-phase; both ride the existing bounded synthetic
        // correction paths and leave endpoint convergence to the settlement no-crossing guard.
        bool pivotAbsorptionActive = frame.Stage == NPCAnimationStage.Pivot
            && _pivotAbsorptionWorldTotal.LengthSquared() > 0.0000000001f;
        bool sideStepAbsorptionActive = frame.Stage == NPCAnimationStage.SideStep
            && Mathf.Abs(_sideStepAbsorptionYawTotal) > 0.0000001f;
        bool skipsPlanarMovement = (frame.Stage == NPCAnimationStage.Settlement
                && _shortBout is not null
                && !_shortBoutResidualSettlement)
            || frame.Stage is NPCAnimationStage.EndpointHold or NPCAnimationStage.Complete;
        // The authorised contact contribution must be consumed before the vector-convergence request so the
        // one composed move that carries it can pace the remaining synthetic correction with the contact share
        // already known: realised contact displacement is realised motion the schedule absorbs in the same
        // interval (NAV-001 TR-16/17), not one interval late where the closing boundary can no longer absorb it.
        ContactLockMotionContribution contactContribution = default;
        ContactLockCoordinator? contactLockCoordinator = ContactLockCoordinator;
        ContactLockMotionContribution availableContactContribution = contactLockCoordinator?.GetAuthorisedContribution() ?? default;
        ContactLockLiveState liveContactState = contactLockCoordinator?.LiveState ?? default;
        bool contactConsumptionAttempted = false;
        bool contactContributionConsumed = false;
        if (!skipsPlanarMovement && availableContactContribution.IsAuthorised)
        {
            contactConsumptionAttempted = true;
            contactContributionConsumed = contactLockCoordinator!.TryConsumeAuthorisedContribution(
                availableContactContribution.PhysicsTick,
                availableContactContribution.ClockGeneration,
                out contactContribution);
        }
        LastContactLockConsumptionTrace = new(
            Engine.GetPhysicsFrames(),
            _physicsFrameGeneration,
            LastEvaluatedRole,
            LastEvaluatedNormalisedPhase,
            liveContactState.PhysicsTick,
            liveContactState.ClockGeneration,
            availableContactContribution.PhysicsTick,
            availableContactContribution.ClockGeneration,
            availableContactContribution.ConsumptionIdentity,
            availableContactContribution.IsAuthorised,
            contactConsumptionAttempted,
            contactContributionConsumed);
        Vector2 consumedContactPlanar = new(
            contactContribution.Authorised.Translation.X,
            contactContribution.Authorised.Translation.Z);
        bool vectorCorrectionActive = TryResolveVectorCorrection(
            frame,
            delta,
            NPCNavigationRoutePlan.YawFromDirection(before.Basis.Orthonormalized() * Vector3.Forward),
            authoredVelocity * delta,
            consumedContactPlanar,
            out _vectorRequestedCorrection);
        bool sideStepPlanarActive = frame.Stage == NPCAnimationStage.SideStep && sideStepPlanarEngaged;
        bool terminalPlanarCorrection = (frame.Stage == NPCAnimationStage.Settlement
                && !_programme.TerminalCommitted
                && (_shortBout is null || _shortBoutResidualSettlement))
            || pivotAbsorptionActive
            || sideStepPlanarActive;
        Vector2 settlementEndpointError = terminalPlanarCorrection && _route is not null
            ? new Vector2(_route.Endpoint.X - before.Origin.X, _route.Endpoint.Z - before.Origin.Z)
            : Vector2.Zero;
        bool settlementWithinNumericalTolerance = terminalPlanarCorrection
            && NPCSettlementPlanarConvergence.IsWithinNumericalTolerance(settlementEndpointError);
        bool planarCorrectionAllowed = shortBoutAnimating
            || terminalPlanarCorrection
            || vectorCorrectionActive;
        Vector2 residual = permissions.MovementAllowed && !complete && planarCorrectionAllowed && !settlementWithinNumericalTolerance
            ? vectorCorrectionActive
                    ? _vectorRequestedCorrection / delta
                : CalculateExitPlanarResidual(
                frame.Stage == NPCAnimationStage.ShortBout
                    ? ResolveShortBoutCorrectionVelocity(delta)
                    : pivotAbsorptionActive
                        ? ResolvePivotAbsorptionVelocity(delta)
                        : sideStepPlanarActive
                            ? ResolveSideStepPlanarCorrectionVelocity(delta)
                            : ResolveSettlementCorrectionVelocity(settlementEndpointError),
                delta,
                frame.Stage == NPCAnimationStage.ShortBout
                    ? MaximumShortBoutCorrectionRate
                    : terminalPlanarCorrection
                        ? NPCSettlementPlanarConvergence.MaximumRate
                        : MaximumExitPlanarResidualRate,
                terminalPlanarCorrection,
                settlementEndpointError)
            : Vector2.Zero;
        if (!planarCorrectionAllowed)
        {
            _smoothedResidual = Vector2.Zero;
            _residualAcceleration = Vector2.Zero;
            LastPlanarResidual = Vector2.Zero;
        }

        Quaternion rootRotation = LastEvaluatedRootRotation;
        Quaternion actorLocalRootRotation = rootRotation.IsFinite()
            ? semanticFrame.EvaluatedRootRotationToActorLocal(rootRotation)
            : Quaternion.Identity;
        float rootYaw = permissions.RotationAllowed ? actorLocalRootRotation.GetEuler().Y : 0f;
        if (vectorCorrectionActive)
        {
            LastVectorConvergenceForecastAuthoredRootWorld = ToWorld3(
                _vectorConvergence.LastPlannedAuthoredRoot);
            LastVectorConvergenceConsumedAuthoredRootWorld = rootWorldVelocity * delta;
            LastVectorConvergenceForecastAuthoredYaw = _vectorConvergence.LastPlannedAuthoredYaw;
            LastVectorConvergenceConsumedAuthoredYaw = rootYaw;
        }
        if (frame.Event == NPCAnimationEvent.StartExit)
        {
            _lastVectorConvergenceCommitmentConsumedRootWorld = rootWorldVelocity * delta;
            _lastVectorConvergenceCommitmentConsumedYaw = rootYaw;
        }
        if (frame.Stage == NPCAnimationStage.ShortBout)
        {
            _shortBoutObservedAuthoredYaw += rootYaw;
        }
        // Confined pivots keep the authored clip yaw as the sole rotation authority; the residual yaw converges in
        // settlement through the bounded no-crossing guard instead of a per-frame synthetic correction. Side steps
        // author lateral displacement with zero yaw and absorb only their small yaw residual mid-phase.
        bool yawCorrectionAllowed = frame.Stage == NPCAnimationStage.ShortBout
            ? shortBoutAnimating
            : frame.Stage == NPCAnimationStage.SideStep
                ? sideStepAbsorptionActive
                : frame.Stage == NPCAnimationStage.Exit && _programme.TerminalCommitted
                    ? vectorCorrectionActive
                : frame.Stage != NPCAnimationStage.Complete
                    && frame.Stage != NPCAnimationStage.Pivot
                    && (frame.Stage != NPCAnimationStage.Exit || rootProducesPlanarMotion)
                    && (frame.Stage != NPCAnimationStage.Settlement || _shortBout is null || _shortBoutResidualSettlement);
        float desiredYawRate = frame.DesiredYawRate;
        float currentActorYaw = NPCNavigationRoutePlan.YawFromDirection(
            before.Basis.Orthonormalized() * Vector3.Forward);
        float terminalYawError = _route is null
            ? 0f
            : NPCNavigationRoutePlan.WrapAngle(_route.TerminalYaw - currentActorYaw);
        float movingYawError = frame.Stage is NPCAnimationStage.Entry or NPCAnimationStage.SustainedTravel
            ? ResolveMovingYawError(before)
            : 0f;
        bool stationaryMovingStage = frame.Stage is NPCAnimationStage.Entry or NPCAnimationStage.SustainedTravel
            && !rootProducesPlanarMotion;
        if (stationaryMovingStage)
        {
            desiredYawRate = 0f;
            _smoothedYawResidual = 0f;
            LastYawResidual = 0f;
        }
        float yawResidualRate = permissions.RotationAllowed && !complete && yawCorrectionAllowed && !stationaryMovingStage
            ? frame.Stage == NPCAnimationStage.Exit && _programme.TerminalCommitted
                ? _vectorConvergence.LastRequestedYawCorrection / delta
                : frame.Stage == NPCAnimationStage.ShortBout
                ? ResolveShortBoutYawCorrectionRate(delta)
                : frame.Stage == NPCAnimationStage.SideStep
                    ? ResolveSideStepAbsorptionYawRate(delta)
                    : frame.Stage == NPCAnimationStage.Settlement
                        ? ResolveSettlementYawRate(before)
                        : CalculateYawResidual(
                            desiredYawRate,
                            rootYaw / delta,
                            movingYawError,
                            terminalYawError,
                            speedWeight,
                            phaseWeight,
                            delta)
            : 0f;
        if (!yawCorrectionAllowed)
        {
            _smoothedYawResidual = 0f;
            LastYawResidual = 0f;
        }
        float yaw = rootYaw + (yawResidualRate * delta) + contactContribution.Authorised.YawRadians;
        LastYawConvergenceStepTrace = default;
        if (permissions.RotationAllowed && float.IsFinite(yaw))
        {
            // Confined pivots and side steps keep their authored/absorbed cadence as the sole rotation authority;
            // only the yaw no-crossing guard applies. The travel approach ramp must not hijack short routes, where
            // the remaining distance always sits inside its activation window.
            yaw = frame.Stage is NPCAnimationStage.Pivot or NPCAnimationStage.SideStep
                ? ClampPivotYawRotation(before, yaw)
                : ClampTerminalYawRotation(before, yaw, delta);
            _previousAppliedYawRate = yaw / delta;
            if (NPCYawConvergence.IsActionableRotation(yaw))
            {
                Actor!.RotateY(yaw);
            }
        }
        else
        {
            _previousAppliedYawRate = 0f;
        }
        if (settlementWithinNumericalTolerance)
        {
            NPCSettlementPlanarConvergence.ClearSmoothing(ref _smoothedResidual, ref _residualAcceleration);
            LastPlanarResidual = Vector2.Zero;
        }

        if (skipsPlanarMovement)
        {
            LastAuthoredRootWorldDelta = Vector3.Zero;
            LastRequestedSyntheticWorldDelta = Vector3.Zero;
            LastAuthoredRootLongitudinal = 0f;
            LastRequestedSyntheticLongitudinal = 0f;
            LastRealisedLongitudinal = 0f;
            LastRealisedRouteLongitudinal = 0f;
            _lastRealisedDisplacement = Vector3.Zero;
            return;
        }

        if (frame.Stage == NPCAnimationStage.ShortBout)
        {
            // The publication accumulator tracks the applied correction so envelope pacing acts as feedback against
            // rate- or budget-limited frames instead of running ahead of the realised synthetic motion.
            _shortBoutPublishedWorldCorrection += residual * delta;
            _shortBoutPublishedYawCorrection += yawResidualRate * delta;
        }
        if (frame.Stage == NPCAnimationStage.Pivot)
        {
            _pivotAbsorptionPublishedWorld += residual * delta;
        }
        if (frame.Stage == NPCAnimationStage.SideStep)
        {
            _sideStepAbsorptionPublishedYaw += yawResidualRate * delta;
            if (sideStepPlanarActive)
            {
                _sideStepPlanarPublishedWorld += residual * delta;
            }
        }

        Vector2 planar = authoredVelocity + residual + new Vector2(
            contactContribution.Authorised.Translation.X / delta,
            contactContribution.Authorised.Translation.Z / delta);
        Vector3 priorVelocity = Actor!.Velocity;
        Actor.Velocity = new Vector3(planar.X, priorVelocity.Y, planar.Y);
        Vector3 requestedVelocity = Actor.Velocity;
        Vector3 authoredWorldDelta = rootWorldVelocity * delta;
        Vector3 syntheticWorldDelta = new Vector3(residual.X, 0f, residual.Y) * delta;
        Vector3 contactWorldDelta = contactContribution.Authorised.Translation;
        Vector3 requestedWorldDelta = new(planar.X * delta, priorVelocity.Y * delta, planar.Y * delta);
        bool collided = MoveActorAndSlide(delta);
        LastMovementCollided = collided;
        _lastRealisedDisplacement = Actor.GlobalPosition - before.Origin;
        Vector2 realisedPlanar = new(_lastRealisedDisplacement.X, _lastRealisedDisplacement.Z);
        Vector2 authoredPlanarDelta = authoredVelocity * delta;
        Vector2 requestedResidualDelta = residual * delta;
        Vector2 realisedResidualDelta = realisedPlanar - authoredPlanarDelta;
        if (vectorCorrectionActive && _vectorConvergence.HasPendingRequest)
        {
            // The consumed contact share was already absorbed into the remaining-correction schedule when this
            // interval's request was paced, so the realisation reconciliation feeds back the synthetic share of
            // the realised composed move (realised minus authored minus contact) plus any collision clipping.
            Vector2 contactPlanar = new(contactWorldDelta.X, contactWorldDelta.Z);
            Vector2 realisedCorrection = realisedPlanar - authoredPlanarDelta - contactPlanar;
            if (!_vectorConvergence.RecordRealisation(realisedCorrection, authoredPlanarDelta, collided))
            {
                FailClosedMovingVectorConvergence();
            }
        }
        float realisedYaw = NPCNavigationRoutePlan.WrapAngle(
            NPCNavigationRoutePlan.YawFromDirection(Actor.GlobalBasis.Orthonormalized() * Vector3.Forward)
            - NPCNavigationRoutePlan.YawFromDirection(before.Basis.Orthonormalized() * Vector3.Forward));
        _ = ContactLockCoordinator?.RecordMovementRealisation(
            contactContribution,
            new Vector3(
                realisedPlanar.X - authoredPlanarDelta.X - requestedResidualDelta.X,
                0.0f,
                realisedPlanar.Y - authoredPlanarDelta.Y - requestedResidualDelta.Y),
            NPCNavigationRoutePlan.WrapAngle(realisedYaw - rootYaw - (yawResidualRate * delta)),
            collided);
        if (terminalPlanarCorrection)
        {
            _correctionBudget.AccountTerminalRealised(
                requestedResidualDelta,
                realisedResidualDelta,
                yawResidualRate * delta,
                NPCNavigationRoutePlan.WrapAngle(realisedYaw - rootYaw));
        }
        else
        {
            _correctionBudget.AccountRealised(
                requestedResidualDelta,
                realisedResidualDelta,
                yawResidualRate * delta,
                NPCNavigationRoutePlan.WrapAngle(realisedYaw - rootYaw));
        }
        Vector3 actorForward = before.Basis.Orthonormalized() * Vector3.Forward;
        LastAuthoredRootWorldDelta = authoredWorldDelta;
        LastRequestedSyntheticWorldDelta = syntheticWorldDelta;
        LastMovementCompositionFrame = new(
            _physicsFrameGeneration,
            rootDelta,
            semanticProjectedRootWorldDelta,
            authoredWorldDelta,
            programmeDesiredWorldDelta,
            Vector3.Zero,
            syntheticWorldDelta,
            contactWorldDelta,
            requestedVelocity,
            requestedWorldDelta,
            _lastCollisionTravel,
            _lastCollisionRemainder,
            _lastRealisedDisplacement,
            collided);
        if (frame.Stage == NPCAnimationStage.Exit
            && _vectorConvergence.Status == NPCVectorConvergenceExecutionStatus.Complete)
        {
            // The owned window consumed the clip's final interval (TR-18): the hard boundary starts at this
            // composed move. Publish the terminal completion first so the state machine travels off the finite
            // clip instead of freezing on it once the boundary stops programme publication.
            if (_programme.TryCompleteTerminalExit(out NPCAnimationFrame completionFrame))
            {
                PublishProgramme(ResolvePlayback(), completionFrame, delta);
            }
            CloseTerminalMotionBoundary();
        }
        LastAuthoredRootLongitudinal = LastAuthoredRootWorldDelta.Dot(actorForward);
        LastRequestedSyntheticLongitudinal = LastRequestedSyntheticWorldDelta.Dot(actorForward);
        LastRealisedLongitudinal = _lastRealisedDisplacement.Dot(actorForward);
        LastRealisedRouteLongitudinal = _lastRealisedDisplacement.Dot(LastRouteTangent);
        if (frame.Stage == NPCAnimationStage.ShortBout)
        {
            LastShortBoutCumulativeRealisedDisplacement += _lastRealisedDisplacement.Length();
            LastShortBoutRealisedShortfall = Math.Max(
                LastShortBoutRequestedDirectLength - LastShortBoutCumulativeRealisedDisplacement,
                0f);
            Vector3 requestedWorld = LastAuthoredRootWorldDelta + LastRequestedSyntheticWorldDelta;
            float divergence = (_lastRealisedDisplacement - requestedWorld).Length();
            if (collided || divergence > 0.005f)
            {
                _shortBoutExecutionInvalid = true;
                LastShortBoutCollisionDetected = collided;
                LastShortBoutInvalidationReason = collided
                    ? NPCShortBoutInvalidationReason.RealisedCollision
                    : NPCShortBoutInvalidationReason.RealisedDivergence;
                FinaliseShortBoutFailure();
            }
        }
        LastObjectiveReversalConfirmed = frame.Stage is NPCAnimationStage.Entry
            or NPCAnimationStage.SustainedTravel
            or NPCAnimationStage.Exit
            && _reversalCounter.Observe(LastRealisedRouteLongitudinal / delta);
    }

    private Vector2 CalculateExitPlanarResidual(
        Vector2 targetVelocity,
        float delta,
        float maximumRate,
        bool terminalPlanarCorrection,
        Vector2 settlementEndpointError)
    {
        if (terminalPlanarCorrection)
        {
            // Settlement resolves any residual magnitude with no remaining-budget bound. The ledger
            // still accounts realised correction for observability through AccountTerminalRealised.
            Vector2 terminalTarget = targetVelocity.LimitLength(Math.Max(MaximumPlanarResidualRate, maximumRate));
            Vector2 requested = NPCSettlementPlanarConvergence.CalculateRequestedDelta(
                settlementEndpointError,
                terminalTarget,
                ref _smoothedResidual,
                ref _residualAcceleration,
                delta);
            Vector2 requestedVelocity = requested / delta;
            LastPlanarResidual = requestedVelocity;
            return requestedVelocity;
        }

        Vector2 target = targetVelocity.LimitLength(Math.Max(MaximumPlanarResidualRate, maximumRate));
        _smoothedResidual = NPCTransitionContinuity.StepVelocityCommand(
            _smoothedResidual,
            target,
            ref _residualAcceleration,
            delta,
            2f,
            30f);
        Vector2 bounded = _correctionBudget.LimitPlanarRequest(_smoothedResidual * delta) / delta;
        LastPlanarResidual = bounded;
        return bounded;
    }

    /// <summary>
    /// Mid-phase correction dispersion across the whole splice: the envelope is zero at both outer splice endpoints
    /// and the published accumulator tracks the applied correction, so the target recomputes from live observations
    /// while the retained authored regions stay untouched.
    /// </summary>
    private Vector2 ResolveShortBoutCorrectionVelocity(float delta)
    {
        if (_route is null || _shortBout is null || !_shortBoutCorrectionInitialised)
        {
            return Vector2.Zero;
        }

        float duration = ResolveShortBoutDuration();
        if (duration <= 0f || delta <= 0f)
        {
            return Vector2.Zero;
        }

        float progress = Mathf.Clamp(_finiteStateElapsed / duration, 0f, 1f);
        Vector2 cumulative = _shortBoutTotalWorldCorrection * Mathf.SmoothStep(0f, 1f, progress);
        return (cumulative - _shortBoutPublishedWorldCorrection) / delta;
    }

    private float ResolveShortBoutYawCorrectionRate(float delta)
    {
        if (!_shortBoutCorrectionInitialised || _shortBout is null)
        {
            return 0f;
        }

        float duration = ResolveShortBoutDuration();
        if (duration <= 0f || delta <= 0f)
        {
            return 0f;
        }

        float progress = Mathf.Clamp(_finiteStateElapsed / duration, 0f, 1f);
        float cumulative = _shortBoutTotalYawCorrection * Mathf.SmoothStep(0f, 1f, progress);
        return Mathf.Clamp(
            (cumulative - _shortBoutPublishedYawCorrection) / delta,
            -MaximumShortBoutYawCorrectionRate,
            MaximumShortBoutYawCorrectionRate);
    }

    /// <summary>
    /// Disperses the pivot's absorbed planar offset across the manoeuvre's mid-phase: zero at the clip boundaries,
    /// exact in total, and left to the settlement no-crossing guard for the final landing.
    /// </summary>
    private Vector2 ResolvePivotAbsorptionVelocity(float delta)
    {
        if (_route is null || _finiteStateDuration <= 0f || delta <= 0f)
        {
            return Vector2.Zero;
        }

        float phase = Mathf.Clamp(_finiteStateElapsed / _finiteStateDuration, 0f, 1f);
        Vector2 cumulative = _pivotAbsorptionWorldTotal * NPCConfinedAbsorption.CumulativeFraction(phase);
        return (cumulative - _pivotAbsorptionPublishedWorld) / delta;
    }

    /// <summary>Disperses the side step's absorbed yaw residual across the manoeuvre's mid-phase.</summary>
    private float ResolveSideStepAbsorptionYawRate(float delta)
    {
        if (_finiteStateDuration <= 0f || delta <= 0f)
        {
            return 0f;
        }

        float phase = Mathf.Clamp(_finiteStateElapsed / _finiteStateDuration, 0f, 1f);
        float cumulative = _sideStepAbsorptionYawTotal * NPCConfinedAbsorption.CumulativeFraction(phase);
        float rate = Mathf.Clamp(
            (cumulative - _sideStepAbsorptionPublishedYaw) / delta,
            -MaximumYawResidualRate,
            MaximumYawResidualRate);
        LastYawResidual = rate = _correctionBudget.LimitYawRequest(rate);
        return rate;
    }

    /// <summary>
    /// Disperses the side step's planar remainder — the request offset minus the authored clip endpoint
    /// displacement — across the clip through the same front-loaded tapered envelope, zero at the arrival pose, so
    /// even in-band steps carry no latent post-clip glide.
    /// </summary>
    private bool TryResolveVectorCorrection(
        in NPCAnimationFrame frame,
        float delta,
        float currentActorYaw,
        Vector2 realisedAuthored,
        Vector2 concurrentContactCorrection,
        out Vector2 requestedCorrection)
    {
        requestedCorrection = Vector2.Zero;
        if (!_selectedReleaseOccurrence.IsValid)
        {
            return false;
        }

        if (frame.Stage != NPCAnimationStage.Exit
            || _vectorConvergence.Status != NPCVectorConvergenceExecutionStatus.Moving
            || frame.Event == NPCAnimationEvent.StartExit)
        {
            return false;
        }

        return _vectorConvergence.TryRequestMoving(
            _selectedReleaseOccurrence,
            delta,
            currentActorYaw,
            realisedAuthored,
            concurrentContactCorrection,
            out requestedCorrection);
    }

    private Vector2 ResolveSideStepPlanarCorrectionVelocity(float delta)
    {
        if (_route is null || _finiteStateDuration <= 0f || delta <= 0f)
        {
            return Vector2.Zero;
        }

        float phase = Mathf.Clamp(_finiteStateElapsed / _finiteStateDuration, 0f, 1f);
        return NPCSideStepPlanarCorrection.ResolveVelocity(
            _sideStepPlanarWorldTotal,
            _sideStepPlanarPublishedWorld,
            phase,
            _sideStepPlanarSnapshotMagnitude,
            delta);
    }

    /// <summary>
    /// Anchors the side-step planar envelope at a side-step start. The remaining forecast projects through the
    /// fixed anchor-time actor basis (side steps author zero yaw; mid-clip yaw absorption curvature stays bounded
    /// by the absorption capacity), and the front-loaded profile disperses the remainder with zero end-velocity;
    /// settlement only polishes sub-tolerance drift. Only the chain's final step engages the
    /// envelope — intermediate repetitions pass <c>false</c> and play as pure full clips so corrections disperse
    /// during the final chained clip. The committed snapshot refreshes from the live total at the final step's
    /// start — endpoint minus the actor's current position minus the one clip still ahead — so drift accumulated
    /// by earlier repetitions disperses inside the envelope instead of surviving the chain into settlement.
    /// </summary>
    private void InitialiseSideStepPlanarCorrection(Transform3D actor, bool disperseRemainder = true)
    {
        _sideStepPlanarAnchorWorld = actor.Origin;
        _sideStepPlanarAnchorBasis = actor.Basis.Orthonormalized();
        _sideStepPlanarPublishedWorld = Vector2.Zero;
        _sideStepPlanarObservedAuthoredWorld = Vector2.Zero;
        if (!disperseRemainder || _route is null || _catalogue is null || _programme.SideStep is not { } plan)
        {
            _sideStepPlanarWorldTotal = Vector2.Zero;
            _sideStepPlanarSnapshotMagnitude = 0f;
            return;
        }

        Vector3 endpointDisplacement = _catalogue[plan.StepCapability!.Value].EndpointRootDisplacement;
        Vector2 clipEndpointSemantic = new(endpointDisplacement.X, -endpointDisplacement.Z);
        NPCNavigationSemanticFrame semanticFrame = new(_sideStepPlanarAnchorBasis, _sideStepPlanarAnchorBasis);
        Vector3 required = _route.Endpoint
            - _sideStepPlanarAnchorWorld
            - semanticFrame.ActorSemanticToWorld(clipEndpointSemantic);
        Vector2 total = new(required.X, required.Z);
        _sideStepPlanarWorldTotal = total.IsFinite() ? total : Vector2.Zero;
        _sideStepPlanarSnapshotMagnitude = _sideStepPlanarWorldTotal.Length();
    }

    /// <summary>
    /// Live remaining side-step planar total, anchored like the exit envelope: endpoint minus anchor minus observed
    /// authored Root minus the remaining clip forecast projected through the fixed anchor basis. The total collapses
    /// onto the exact live error once the forecast is consumed, and the published correction never enters.
    /// </summary>
    private Vector2 ResolveSideStepPlanarRemainingTotal()
    {
        if (_route is null || _catalogue is null || _programme.SideStep is not { } plan)
        {
            return Vector2.Zero;
        }

        Vector2 remaining = SampleRemainingSemantic(_catalogue[plan.StepCapability!.Value], LastEvaluatedPlayPosition);
        NPCNavigationSemanticFrame forecastFrame = new(_sideStepPlanarAnchorBasis, _sideStepPlanarAnchorBasis);
        Vector3 required = _route.Endpoint
            - _sideStepPlanarAnchorWorld
            - new Vector3(
                _sideStepPlanarObservedAuthoredWorld.X,
                0f,
                _sideStepPlanarObservedAuthoredWorld.Y)
            - forecastFrame.ActorSemanticToWorld(remaining);
        Vector2 total = new(required.X, required.Z);
        return total.IsFinite() ? total : Vector2.Zero;
    }

    private static Vector2 SampleRemainingSemantic(StandingLocomotionClip clip, float playPosition)
    {
        float sourceElapsed = Mathf.Clamp(
            playPosition * FiniteTransitionPlaybackRate,
            0f,
            (float)clip.DurationSeconds);
        Vector2 endpoint = new(clip.EndpointRootDisplacement.X, -clip.EndpointRootDisplacement.Z);
        return endpoint - clip.SampleResponse(sourceElapsed).CumulativeLocalPlanar;
    }

    private void ClearFiniteCorrectionEnvelopes()
    {
        _sideStepPlanarWorldTotal = Vector2.Zero;
        _sideStepPlanarPublishedWorld = Vector2.Zero;
        _sideStepPlanarObservedAuthoredWorld = Vector2.Zero;
        _sideStepPlanarAnchorWorld = Vector3.Zero;
        _sideStepPlanarAnchorBasis = Basis.Identity;
        _sideStepPlanarSnapshotMagnitude = 0f;
    }

    private void InitialiseShortBoutCorrection(Transform3D actor)
    {
        if (_route is null || _shortBout is not NPCShortBoutSplicePlan plan || _catalogue is null)
        {
            return;
        }

        float actorYaw = NPCNavigationRoutePlan.YawFromDirection(actor.Basis.Orthonormalized() * Vector3.Forward);
        _shortBoutTotalYawCorrection = NPCNavigationRoutePlan.WrapAngle(
            _route.TerminalYaw - actorYaw - plan.ForecastYaw);
        Vector3 forecastWorld = ForecastImportedShortBoutWorld(plan);
        Vector3 requiredWorld = _route.Endpoint - actor.Origin - forecastWorld;
        _shortBoutTotalWorldCorrection = new(requiredWorld.X, requiredWorld.Z);
        _shortBoutPublishedWorldCorrection = Vector2.Zero;
        _shortBoutPublishedYawCorrection = 0f;
        _shortBoutStartWorldPosition = actor.Origin;
        _shortBoutObservedAuthoredWorld = Vector3.Zero;
        _shortBoutObservedAuthoredRoot = Vector3.Zero;
        _shortBoutObservedAuthoredYaw = 0f;
        _shortBoutForecastWorld = forecastWorld;
        _shortBoutCorrectionInitialised = true;
    }

    private void UpdateShortBoutCorrectionFromObserved()
    {
        if (_route is null || _shortBout is null)
        {
            return;
        }

        NPCShortBoutSplicePlan plan = _shortBout.Value;
        Animation entry = _shortBoutImportedEntry
            ?? throw new InvalidOperationException("NPC short-bout entry clip was not cached before correction evaluation.");
        Animation exit = _shortBoutImportedExit
            ?? throw new InvalidOperationException("NPC short-bout exit clip was not cached before correction evaluation.");
        if (LastShortBoutBlendWeight >= 1f
            && LastShortBoutEntrySourcePosition >= plan.EntryCutTime - 0.00001f)
        {
            UpdateShortBoutCorrectionFromObservedSuffix(exit);
            return;
        }

        const int remainingCrossfadeSteps = 16;
        float progress = LastShortBoutBlendWeight;
        float crossfadeStart = Math.Max(plan.EntryCutTime - plan.CrossfadeDuration, 0f);
        Vector3 entryStart = entry.PositionTrackInterpolate(_shortBoutEntryPositionTrack, 0d);
        Vector3 modelledConsumed = entry.PositionTrackInterpolate(_shortBoutEntryPositionTrack, crossfadeStart) - entryStart;
        Vector3 consumedPreviousEntry = entry.PositionTrackInterpolate(_shortBoutEntryPositionTrack, crossfadeStart);
        Vector3 consumedPreviousExit = exit.PositionTrackInterpolate(_shortBoutExitPositionTrack, plan.ExitCutTime);
        for (int step = 1; step <= remainingCrossfadeSteps; step++)
        {
            float stepProgress = progress * step / remainingCrossfadeSteps;
            float previousProgress = progress * (step - 1f) / remainingCrossfadeSteps;
            Vector3 currentEntry = entry.PositionTrackInterpolate(
                _shortBoutEntryPositionTrack,
                Mathf.Lerp(crossfadeStart, LastShortBoutEntrySourcePosition, step / (float)remainingCrossfadeSteps));
            Vector3 currentExit = exit.PositionTrackInterpolate(
                _shortBoutExitPositionTrack,
                Mathf.Lerp(plan.ExitCutTime, LastShortBoutExitSourcePosition, step / (float)remainingCrossfadeSteps));
            float midpointWeight = (previousProgress + stepProgress) * 0.5f;
            modelledConsumed += ((currentEntry - consumedPreviousEntry) * (1f - midpointWeight))
                + ((currentExit - consumedPreviousExit) * midpointWeight);
            consumedPreviousEntry = currentEntry;
            consumedPreviousExit = currentExit;
        }
        if (modelledConsumed.LengthSquared() > 0.00000001f)
        {
            float observedScale = Mathf.Clamp(
                _shortBoutObservedAuthoredRoot.Length() / modelledConsumed.Length(),
                0.5f,
                1.5f);
            LastShortBoutForecastResponseScale = Math.Max(
                observedScale - ResolveShortBoutSuffixResponseSafetyMargin(plan),
                0.5f);
        }
        Vector3 remainingRoot = Vector3.Zero;
        Vector3 previousEntry = entry.PositionTrackInterpolate(
            _shortBoutEntryPositionTrack,
            LastShortBoutEntrySourcePosition);
        Vector3 previousExit = exit.PositionTrackInterpolate(
            _shortBoutExitPositionTrack,
            LastShortBoutExitSourcePosition);
        for (int step = 1; step <= remainingCrossfadeSteps; step++)
        {
            float stepProgress = Mathf.Lerp(progress, 1f, step / (float)remainingCrossfadeSteps);
            float previousProgress = Mathf.Lerp(progress, 1f, (step - 1f) / remainingCrossfadeSteps);
            Vector3 currentEntry = entry.PositionTrackInterpolate(
                _shortBoutEntryPositionTrack,
                Mathf.Lerp(LastShortBoutEntrySourcePosition, plan.EntryCutTime, step / (float)remainingCrossfadeSteps));
            Vector3 currentExit = exit.PositionTrackInterpolate(
                _shortBoutExitPositionTrack,
                Mathf.Lerp(
                    LastShortBoutExitSourcePosition,
                    plan.ExitCutTime + plan.CrossfadeDuration,
                    step / (float)remainingCrossfadeSteps));
            float midpointWeight = (previousProgress + stepProgress) * 0.5f;
            remainingRoot += ((currentEntry - previousEntry) * (1f - midpointWeight))
                + ((currentExit - previousExit) * midpointWeight);
            previousEntry = currentEntry;
            previousExit = currentExit;
        }
        remainingRoot += exit.PositionTrackInterpolate(_shortBoutExitPositionTrack, exit.Length) - previousExit;
        remainingRoot *= LastShortBoutForecastResponseScale;
        float remainingYaw = _shortBoutTotalYawCorrection - _shortBoutPublishedYawCorrection;
        Vector3 remainingWorld = new Basis(Vector3.Up, remainingYaw) * GetRootMotionReferenceBasis() * remainingRoot;
        Vector3 required = _route.Endpoint
            - _shortBoutStartWorldPosition
            - _shortBoutObservedAuthoredWorld
            - remainingWorld;
        _shortBoutTotalWorldCorrection = new(required.X, required.Z);
    }

    /// <summary>
    /// Retained-exit-suffix response model: once the crossfade is consumed, the remaining authored forecast is the
    /// imported exit suffix from the current source position, scaled by the observed response. The dispersion
    /// envelope keeps publishing against this live total so response drift accrued during the suffix is absorbed
    /// before the authored endpoint instead of surviving it.
    /// </summary>
    private void UpdateShortBoutCorrectionFromObservedSuffix(Animation exit)
    {
        if (_route is null)
        {
            return;
        }

        NPCShortBoutRootCompositionSample current = ImportedRootSample(
            exit,
            _shortBoutExitPositionTrack,
            _shortBoutExitRotationTrack,
            LastShortBoutExitSourcePosition);
        NPCShortBoutRootCompositionSample endpoint = ImportedRootSample(
            exit,
            _shortBoutExitPositionTrack,
            _shortBoutExitRotationTrack,
            (float)exit.Length);
        Vector3 remainingRoot = (endpoint.Position - current.Position) * LastShortBoutForecastResponseScale;
        float remainingYaw = _shortBoutTotalYawCorrection - _shortBoutPublishedYawCorrection;
        Vector3 remainingWorld = new Basis(Vector3.Up, remainingYaw) * GetRootMotionReferenceBasis() * remainingRoot;
        Vector3 required = _route.Endpoint
            - _shortBoutStartWorldPosition
            - _shortBoutObservedAuthoredWorld
            - remainingWorld;
        _shortBoutTotalWorldCorrection = new(required.X, required.Z);
    }

    private void PrepareShortBoutCorrectionFromObservedEntry()
    {
        if (!_shortBoutCorrectionInitialised
            || _route is null
            || _shortBoutImportedEntry is null
            || LastShortBoutBlendWeight > 0f
            || LastShortBoutEntrySourcePosition <= 0.05f)
        {
            return;
        }

        Vector3 importedStart = _shortBoutImportedEntry.PositionTrackInterpolate(_shortBoutEntryPositionTrack, 0d);
        Vector3 importedCurrent = _shortBoutImportedEntry.PositionTrackInterpolate(
            _shortBoutEntryPositionTrack,
            LastShortBoutEntrySourcePosition);
        float importedDistance = (importedCurrent - importedStart).Length();
        if (importedDistance <= 0.000001f)
        {
            return;
        }

        float entryResponseScale = Mathf.Clamp(
            _shortBoutObservedAuthoredRoot.Length() / importedDistance,
            0.5f,
            1.5f);
        // The state-machine crossfade blends two advancing Root accumulators and converges towards unscaled suffix
        // response. Bias the entry observation towards unity rather than treating it as a replacement Root source.
        LastShortBoutForecastResponseScale = Math.Max(
            entryResponseScale - ResolveShortBoutSuffixResponseSafetyMargin(_shortBout!.Value),
            0.5f);
        Vector3 forecastWorld = GetRootMotionReferenceBasis()
            * (_shortBoutImportedForecastRoot * LastShortBoutForecastResponseScale);
        Vector3 requiredWorld = _route.Endpoint - _shortBoutStartWorldPosition - forecastWorld;
        _shortBoutTotalWorldCorrection = new(requiredWorld.X, requiredWorld.Z);
    }

    private float ResolveShortBoutSuffixResponseSafetyMargin(NPCShortBoutSplicePlan plan)
    {
        Animation exit = _shortBoutImportedExit
            ?? throw new InvalidOperationException("NPC short-bout exit clip was not cached before response forecasting.");
        Vector3 suffix = exit.PositionTrackInterpolate(_shortBoutExitPositionTrack, exit.Length)
            - exit.PositionTrackInterpolate(_shortBoutExitPositionTrack, plan.ExitCutTime);
        // Short retained suffixes provide fewer observed samples before their endpoint, so preserve a slightly larger
        // conservative response margin. This adjusts only planning correction; AnimationTree Root remains authoritative.
        return Mathf.Clamp(0.064f - (0.0553f * suffix.Length()), 0.02f, 0.06f);
    }

    private Vector3 ForecastImportedShortBoutWorld(NPCShortBoutSplicePlan plan)
    {
        CacheImportedShortBoutTracks(plan);
        Animation entry = _shortBoutImportedEntry!;
        Animation exit = _shortBoutImportedExit!;
        NPCShortBoutRootComposition composition = NPCShortBoutSplice.Compose(
            time => ImportedRootSample(entry, _shortBoutEntryPositionTrack, _shortBoutEntryRotationTrack, time),
            time => ImportedRootSample(exit, _shortBoutExitPositionTrack, _shortBoutExitRotationTrack, time),
            plan.EntryCutTime,
            plan.ExitCutTime,
            (float)exit.Length,
            plan.CrossfadeDuration);
        _shortBoutImportedForecastRoot = composition.Position;
        _shortBoutImportedForecastYaw = composition.Yaw;
        NPCShortBoutRootCompositionSample entryStart = ImportedRootSample(
            entry,
            _shortBoutEntryPositionTrack,
            _shortBoutEntryRotationTrack,
            0f);
        NPCShortBoutRootCompositionSample entryCut = ImportedRootSample(
            entry,
            _shortBoutEntryPositionTrack,
            _shortBoutEntryRotationTrack,
            plan.EntryCutTime);
        NPCShortBoutRootCompositionSample exitCut = ImportedRootSample(
            exit,
            _shortBoutExitPositionTrack,
            _shortBoutExitRotationTrack,
            plan.ExitCutTime);
        NPCShortBoutRootCompositionSample exitEnd = ImportedRootSample(
            exit,
            _shortBoutExitPositionTrack,
            _shortBoutExitRotationTrack,
            (float)exit.Length);
        _shortBoutActualGraphForecastRoot = entryCut.Position - entryStart.Position + exitEnd.Position - exitCut.Position;
        _shortBoutActualGraphForecastYaw = Mathf.Wrap(
            entryCut.Yaw - entryStart.Yaw + exitEnd.Yaw - exitCut.Yaw,
            -Mathf.Pi,
            Mathf.Pi);
        return GetRootMotionReferenceBasis() * composition.Position;
    }

    private void CacheImportedShortBoutTracks(NPCShortBoutSplicePlan plan)
    {
        if (_shortBoutImportedEntry is not null && _shortBoutImportedExit is not null)
        {
            return;
        }

        AnimationPlayer player = ResolveAnimationPlayer();
        _shortBoutImportedEntry = player.GetAnimation(LocomotionAnimationName(plan.Pair.EntryRole));
        _shortBoutImportedExit = player.GetAnimation(LocomotionAnimationName(plan.Pair.ExitRole));
        NodePath rootTrackPath = AnimationTree!.RootMotionTrack;
        _shortBoutEntryPositionTrack = _shortBoutImportedEntry.FindTrack(rootTrackPath, Animation.TrackType.Position3D);
        _shortBoutEntryRotationTrack = _shortBoutImportedEntry.FindTrack(rootTrackPath, Animation.TrackType.Rotation3D);
        _shortBoutExitPositionTrack = _shortBoutImportedExit.FindTrack(rootTrackPath, Animation.TrackType.Position3D);
        _shortBoutExitRotationTrack = _shortBoutImportedExit.FindTrack(rootTrackPath, Animation.TrackType.Rotation3D);
        if (_shortBoutEntryPositionTrack < 0 || _shortBoutExitPositionTrack < 0)
        {
            throw new InvalidOperationException("NPC short-bout clips must expose the configured Root position track.");
        }
    }

    /// <summary>
    /// Observes the short-bout aggregate pose displacement on a frame-strided contract: the tree evaluates
    /// automatically after navigation each physics tick, so consecutive calls at this point bracket exactly one
    /// automatic mixer evaluation. The aggregate compares the poses captured at the previous frame's call with the
    /// current ones, then re-captures for the next tick. The crossfade flag records the fading state left by the
    /// last completed evaluation, exactly as the mixer presents it before its next one.
    /// </summary>
    private void ObserveShortBoutPoseDeltas()
    {
        bool inShortBout = _programme.Stage == NPCAnimationStage.ShortBout;
        if (inShortBout && _shortBoutPoseObservedPreviousFrame && _shortBoutSkeleton is not null)
        {
            float aggregate = 0f;
            for (int index = 0; index < _shortBoutPoseBoneIndices.Length; index++)
            {
                Transform3D current = _shortBoutSkeleton.GetBonePose(_shortBoutPoseBoneIndices[index]);
                Transform3D previous = _shortBoutPoseBefore[index];
                aggregate += current.Origin.DistanceTo(previous.Origin);
                aggregate += current.Basis.GetRotationQuaternion().AngleTo(previous.Basis.GetRotationQuaternion());
            }
            LastShortBoutAggregatePoseDelta = aggregate;
        }
        else
        {
            LastShortBoutAggregatePoseDelta = 0f;
        }

        _shortBoutCrossfadeWasActiveBeforeEvaluation = false;
        _shortBoutPoseObservedPreviousFrame = false;
        if (!inShortBout)
        {
            return;
        }

        EnsureShortBoutPoseBones();
        _shortBoutCrossfadeWasActiveBeforeEvaluation = !ResolvePlayback().GetFadingFromNode().IsEmpty;
        for (int index = 0; index < _shortBoutPoseBoneIndices.Length; index++)
        {
            _shortBoutPoseBefore[index] = _shortBoutSkeleton!.GetBonePose(_shortBoutPoseBoneIndices[index]);
        }
        _shortBoutPoseObservedPreviousFrame = true;
    }

    private void EnsureShortBoutPoseBones()
    {
        if (_shortBoutSkeleton is not null)
        {
            return;
        }

        _shortBoutSkeleton = RootMotionReference!.FindChild("GeneralSkeleton", recursive: true, owned: false) as Skeleton3D
            ?? throw new InvalidOperationException("NPC short-bout diagnostics require the reference rig GeneralSkeleton.");
        string[] boneNames = ["Hips", "LeftUpperLeg", "LeftFoot", "RightUpperLeg", "RightFoot"];
        for (int index = 0; index < boneNames.Length; index++)
        {
            int bone = _shortBoutSkeleton.FindBone(boneNames[index]);
            if (bone < 0)
            {
                throw new InvalidOperationException($"NPC short-bout diagnostics require representative bone '{boneNames[index]}'.");
            }
            _shortBoutPoseBoneIndices[index] = bone;
        }
    }

    private static NPCShortBoutRootCompositionSample ImportedRootSample(
        Animation animation,
        int positionTrack,
        int rotationTrack,
        float time)
    {
        Vector3 position = animation.PositionTrackInterpolate(positionTrack, time);
        float yaw = rotationTrack >= 0
            ? animation.RotationTrackInterpolate(rotationTrack, time).GetEuler().Y
            : 0f;
        return new(position, yaw);
    }

    /// <summary>
    /// Resolves the planned exit-entry shaping velocity for one sustained-travel frame: the current plan's landing
    /// error spread over the remaining approach, stepped through the shared continuity limits. An inactive plan
    /// targets zero so the velocity decays smoothly instead of snapping.
    /// </summary>
    private static Vector2 ResolveSettlementCorrectionVelocity(Vector2 endpointError)
        => NPCSettlementPlanarConvergence.ResolveTaperedVelocity(
            endpointError,
            NPCSettlementPlanarConvergence.MaximumRate);

    /// <summary>
    /// Bounds one authored confined-pivot rotation command. Unlike the travel guard, the acceleration-ramp approach
    /// profile must not engage: the pivot's package cadence stays authoritative and only the yaw no-crossing guard
    /// applies, absorbing any authored overshoot of the terminal yaw.
    /// </summary>
    private float ClampPivotYawRotation(Transform3D actor, float yawDelta)
    {
        if (_route is null || Mathf.Abs(yawDelta) <= 0.000001f)
        {
            return yawDelta;
        }

        float currentYaw = NPCNavigationRoutePlan.YawFromDirection(actor.Basis.Orthonormalized() * Vector3.Forward);
        return NPCYawConvergence.ClampRotationDelta(currentYaw, _route.TerminalYaw, yawDelta);
    }

    private float ClampTerminalYawRotation(Transform3D actor, float yawDelta, float delta)
    {
        if (_route is null || delta <= 0f)
        {
            return yawDelta;
        }

        float currentYaw = NPCNavigationRoutePlan.YawFromDirection(actor.Basis.Orthonormalized() * Vector3.Forward);
        float error = NPCNavigationRoutePlan.WrapAngle(_route.TerminalYaw - currentYaw);
        const float terminalYawGuardWindow = 0.2f;
        if (LastRemainingDistance <= _terminalFacingActivationDistance
            || Mathf.Abs(error) <= terminalYawGuardWindow)
        {
            yawDelta = NPCYawConvergence.StepTerminalRotation(
                currentYaw,
                _route.TerminalYaw,
                ref _previousAppliedYawRate,
                delta,
                out NPCYawConvergenceStepTrace trace);
            LastYawConvergenceStepTrace = trace;
        }
        return NPCYawConvergence.ClampRotationDelta(currentYaw, _route.TerminalYaw, yawDelta);
    }

    /// <summary>
    /// Resolves the settlement yaw rate for one frame: brisk with no magnitude bound, tapering
    /// along a constant-deceleration braking curve into the numerical tolerance. Frame-to-frame continuity and the
    /// no-crossing guard are applied afterwards by <see cref="ClampTerminalYawRotation" />.
    /// </summary>
    private float ResolveSettlementYawRate(Transform3D actor)
    {
        if (_route is null)
        {
            return 0f;
        }

        float currentYaw = NPCNavigationRoutePlan.YawFromDirection(actor.Basis.Orthonormalized() * Vector3.Forward);
        float error = NPCNavigationRoutePlan.WrapAngle(_route.TerminalYaw - currentYaw);
        if (Mathf.Abs(error) <= NPCAnimationProgramme.NumericalConvergenceThreshold)
        {
            _smoothedYawResidual = 0f;
            LastYawResidual = 0f;
            return 0f;
        }

        float taperSpeed = Mathf.Sqrt(2f * NPCTransitionContinuity.MaximumYawAcceleration * Mathf.Abs(error));
        float rate = Mathf.Sign(error) * Mathf.Min(taperSpeed, NPCTerminalReleaseScheduler.MaximumYawRate);
        _smoothedYawResidual = rate;
        LastYawResidual = rate;
        return rate;
    }

    /// <summary>
    /// Resolves the yaw the moving actor aims its facing at: the terminal pose once the terminal-facing window is
    /// active (the terminal facing follows the route, or the remaining route distance can no longer carry the
    /// outstanding yaw turn at the ordinary gait), otherwise the ordinary look-ahead point along the route.
    /// </summary>
    private float ResolveMovingTargetYaw(Transform3D actor)
    {
        if (_route is null)
        {
            return 0f;
        }

        float terminalRouteYaw = NPCNavigationRoutePlan.YawFromDirection(_route.TerminalDirection);
        bool terminalFacingFollowsRoute = Mathf.Abs(
            NPCNavigationRoutePlan.WrapAngle(_route.TerminalYaw - terminalRouteYaw)) <= 0.1f;
        if (terminalFacingFollowsRoute || LastRemainingDistance <= _terminalFacingActivationDistance)
        {
            return _route.TerminalYaw;
        }
        Vector3 target = _route.Sample(Math.Min(LastRouteProgress + 0.35f, _route.TotalLength), out _);
        Vector3 direction = target - actor.Origin;
        direction.Y = 0f;
        return direction.LengthSquared() > 0.000001f
            ? NPCNavigationRoutePlan.YawFromDirection(direction)
            : _route.DesiredRouteYaw(Math.Min(LastRouteProgress + 0.35f, _route.TotalLength));
    }

    private float ResolveMovingYawError(Transform3D actor)
    {
        if (_route is null)
        {
            return 0f;
        }

        float actorYaw = NPCNavigationRoutePlan.YawFromDirection(actor.Basis.Orthonormalized() * Vector3.Forward);
        return NPCNavigationRoutePlan.WrapAngle(ResolveMovingTargetYaw(actor) - actorYaw);
    }

    private float CalculateYawResidual(
        float desired,
        float authored,
        float movingYawError,
        float terminalYawError,
        float speedWeight,
        float phaseWeight,
        float delta)
    {
        var state = new NPCYawCommandState(
            _smoothedYawResidual,
            _previousAuthoredYawRate,
            LastYawResidual,
            _correctionBudget.RemainingYaw);
        float totalRate = NPCYawCommandStep.Step(
            desired,
            authored,
            movingYawError,
            terminalYawError,
            speedWeight,
            phaseWeight,
            delta,
            MaximumYawResidualRate,
            ResidualSmoothingRate,
            ref state);
        _smoothedYawResidual = state.SmoothedResidual;
        _previousAuthoredYawRate = state.PreviousAuthoredRate;
        LastYawResidual = state.LastResidualRate;
        return totalRate - authored;
    }

    private bool ObserveFiniteCompletion(float delta)
    {
        if (!_finiteStateObserved)
        {
            return false;
        }
        _finiteStateElapsed += delta;
        if (_programme.Stage == NPCAnimationStage.ShortBout && _shortBout is NPCShortBoutSplicePlan plan)
        {
            AnimationNodeStateMachinePlayback playback = ResolvePlayback();
            string exitState = ShortBoutExitState(plan);
            float exitSuffixDuration = (float)_catalogue![plan.Pair.ExitRole].DurationSeconds - plan.ExitCutTime;
            bool endpointObserved = playback.GetCurrentNode() == exitState
                && playback.GetFadingFromNode().IsEmpty
                && playback.GetCurrentPlayPosition() >= exitSuffixDuration - 0.00001f;
            if (endpointObserved)
            {
                LastShortBoutExitReachedEndpoint = true;
                _finiteStateObserved = false;
                if (IsTerminalTransformWithinNumericalTolerance())
                {
                    return true;
                }

                // Cross-DOF absorption rule: a residual within the confined absorption capacity converges exactly
                // through settlement under the no-crossing guard instead of invalidating the completed splice.
                if (TryBeginShortBoutResidualSettlement())
                {
                    return false;
                }

                InvalidateShortBout(
                    NPCShortBoutInvalidationReason.ExitEndpointResidual,
                    $"Exit suffix reached its authored endpoint with residual position/yaw beyond {NPCAnimationProgramme.NumericalConvergenceThreshold:F7}.");
                return false;
            }

            const float watchdogAllowance = 0.5f;
            if (_finiteStateElapsed > ResolveShortBoutDuration() + watchdogAllowance)
            {
                InvalidateShortBout(
                    NPCShortBoutInvalidationReason.PlaybackWatchdog,
                    $"Short-bout playback did not observe exit endpoint within {ResolveShortBoutDuration() + watchdogAllowance:F3}s.");
            }
            return false;
        }
        float completionThreshold = _programme.Stage == NPCAnimationStage.Entry
            ? Math.Max(_finiteStateDuration - _activeEntryCrossfadeDuration, 0f)
            : _finiteStateDuration;
        // Entry begins the approved handoff while its outgoing Root is still moving; Exit evaluates its authored
        // endpoint before settlement.
        bool complete = completionThreshold > 0f
            && (_programme.Stage == NPCAnimationStage.Entry
                ? _finiteStateElapsed >= completionThreshold
                : _finiteStateElapsed > completionThreshold);
        if (complete)
        {
            _finiteStateObserved = false;
        }

        return complete;
    }

    private float ResolveFiniteStateDuration(StringName state)
    {
        string stateName = state.ToString();
        StandingLocomotionCapability capability = CapabilityForFiniteState(stateName);
        return (float)(_catalogue?[capability].DurationSeconds
            ?? throw new InvalidOperationException("NPC animation catalogue must be loaded before finite programme execution."))
            / FiniteTransitionPlaybackRate;
    }

    private static StandingLocomotionCapability CapabilityForFiniteState(string stateName) => stateName switch
    {
        "NPCI2FL" => StandingLocomotionCapability.EntryForwardLeft,
        "NPCI2FR" => StandingLocomotionCapability.EntryForwardRight,
        "NPCI2BL" => StandingLocomotionCapability.EntryBackwardLeft,
        "NPCI2BR" => StandingLocomotionCapability.EntryBackwardRight,
        "NPCF2IL" => StandingLocomotionCapability.ExitForwardLeft,
        "NPCF2IR" => StandingLocomotionCapability.ExitForwardRight,
        "NPCB2IL" => StandingLocomotionCapability.ExitBackwardLeft,
        "NPCB2IR" => StandingLocomotionCapability.ExitBackwardRight,
        "NPCPivotL" => StandingLocomotionCapability.ConfinedPivotLeft,
        "NPCPivotR" => StandingLocomotionCapability.ConfinedPivotRight,
        "NPCSideStepL" => StandingLocomotionCapability.ConfinedSideStepLeft,
        "NPCSideStepR" => StandingLocomotionCapability.ConfinedSideStepRight,
        _ => throw new InvalidOperationException($"Unknown finite NPC animation state '{stateName}'."),
    };

    private float ResolveShortBoutDuration()
    {
        if (_shortBout is not NPCShortBoutSplicePlan plan || _catalogue is null)
        {
            throw new InvalidOperationException("A package-approved short-bout plan is required before playback.");
        }

        return plan.EntryCutTime
            + ((float)_catalogue[plan.Pair.ExitRole].DurationSeconds - plan.ExitCutTime)
            - plan.CrossfadeDuration;
    }

    private void IsolateMutableAnimationGraph()
    {
        AnimationNodeBlendTree authored = AnimationTree!.TreeRoot as AnimationNodeBlendTree
            ?? throw new InvalidOperationException("NPC AnimationTree root must be a blend tree.");
        AnimationNodeBlendTree isolated = authored.Duplicate(true) as AnimationNodeBlendTree
            ?? throw new InvalidOperationException("NPC AnimationTree root could not be deep-duplicated for per-character playback.");
        AnimationTree.TreeRoot = isolated;
        AnimationTree.ClearCaches();
    }

    private void ConfigureFiniteTransitionTiming()
    {
        AnimationNodeBlendTree root = AnimationTree!.TreeRoot as AnimationNodeBlendTree
            ?? throw new InvalidOperationException("NPC AnimationTree root must be a blend tree.");
        AnimationNodeStateMachine states = root.GetNode("States") as AnimationNodeStateMachine
            ?? throw new InvalidOperationException("NPC AnimationTree must expose States.");
        string[] finiteStates =
        [
            "NPCI2FL", "NPCI2FR", "NPCI2BL", "NPCI2BR",
            "NPCF2IL", "NPCF2IR", "NPCB2IL", "NPCB2IR",
            "NPCPivotL", "NPCPivotR", "NPCSideStepL", "NPCSideStepR",
        ];
        foreach (string stateName in finiteStates)
        {
            AnimationNodeAnimation clip = states.GetNode(stateName) as AnimationNodeAnimation
                ?? throw new InvalidOperationException($"NPC finite state '{stateName}' must contain an animation node.");
            AnimationNodeAnimation runtimeClip = clip.Duplicate(true) as AnimationNodeAnimation
                ?? throw new InvalidOperationException($"NPC finite state '{stateName}' could not duplicate its clip node.");
            StandingLocomotionCapability capability = CapabilityForFiniteState(stateName);
            runtimeClip.Animation = LocomotionAnimationName(capability);
            if (stateName is "NPCF2IL" or "NPCF2IR" or "NPCB2IL" or "NPCB2IR")
            {
                runtimeClip.UseCustomTimeline = true;
                runtimeClip.StartOffset = 0f;
                runtimeClip.TimelineLength = (float)_catalogue![capability].DurationSeconds;
                runtimeClip.StretchTimeScale = false;
                runtimeClip.LoopMode = Animation.LoopModeEnum.None;
            }
            var wrapper = new AnimationNodeBlendTree();
            wrapper.AddNode("Clip", runtimeClip);
            wrapper.AddNode("Rate", new AnimationNodeTimeScale());
            wrapper.ConnectNode("Rate", 0, "Clip");
            wrapper.ConnectNode("output", 0, "Rate");
            states.ReplaceNode(stateName, wrapper);
        }

        for (int index = 0; index < states.GetTransitionCount(); index++)
        {
            if (states.GetTransitionTo(index) == _travelState
                && states.GetTransitionFrom(index).ToString() is "NPCI2FL" or "NPCI2FR" or "NPCI2BL" or "NPCI2BR")
            {
                states.GetTransition(index).XfadeTime = 0.3f;
            }
        }

        AnimationTree.ClearCaches();
        foreach (string stateName in finiteStates)
        {
            AnimationTree.Set($"parameters/States/{stateName}/Rate/scale", FiniteTransitionPlaybackRate);
        }
    }

    private void ConfigureFiniteRootInterpolation()
    {
        AnimationPlayer player = ResolveAnimationPlayer();
        StringName libraryName = new("locomotion");
        if (!player.HasAnimationLibrary(libraryName))
        {
            throw new InvalidOperationException(
                "NPC navigation activation requires the actor AnimationPlayer 'locomotion' library.");
        }
        AnimationLibrary authoredLibrary = player.GetAnimationLibrary(libraryName);
        AnimationLibrary runtimeLibrary = authoredLibrary.Duplicate(true) as AnimationLibrary
            ?? throw new InvalidOperationException("NPC locomotion animation library could not be isolated for runtime timing.");
        foreach (StandingLocomotionClip clip in _catalogue!.Capabilities.Values
                     .Where(candidate => candidate.BoundaryKind == StandingLocomotionBoundaryKind.Transition))
        {
            StringName animationName = new(clip.Identity);
            Animation animation = runtimeLibrary.GetAnimation(animationName);
            int rootTrack = animation.FindTrack(new NodePath("%GeneralSkeleton:Root"), Animation.TrackType.Position3D);
            if (rootTrack < 0)
            {
                throw new InvalidOperationException($"NPC finite animation '{animationName}' has no Root position track.");
            }
            animation.TrackSetInterpolationType(rootTrack, Animation.InterpolationType.Cubic);
        }
        foreach (StandingLocomotionClip clip in _catalogue!.Clips.Values)
        {
            if (clip.BoundaryKind != StandingLocomotionBoundaryKind.CyclicTravel
                || clip.ResponseSamples.Length < 2)
            {
                continue;
            }
            StringName animationName = new(clip.Identity);
            Animation animation = runtimeLibrary.GetAnimation(animationName);
            int rootTrack = animation.FindTrack(new NodePath("%GeneralSkeleton:Root"), Animation.TrackType.Position3D);
            if (rootTrack < 0)
            {
                throw new InvalidOperationException($"NPC sustained animation '{animationName}' has no Root position track.");
            }
            int sourceKeyCount = animation.TrackGetKeyCount(rootTrack);
            float[] times = new float[sourceKeyCount];
            var positions = new Vector2[sourceKeyCount];
            var velocities = new Vector2[sourceKeyCount];
            for (int index = 0; index < sourceKeyCount; index++)
            {
                times[index] = (float)animation.TrackGetKeyTime(rootTrack, index);
                var sourcePosition = (Vector3)animation.TrackGetKeyValue(rootTrack, index);
                positions[index] = new Vector2(sourcePosition.X, sourcePosition.Z);
            }
            for (int index = 1; index < velocities.Length - 1; index++)
            {
                velocities[index] = (positions[index + 1] - positions[index - 1]) / (times[index + 1] - times[index - 1]);
            }
            Vector2 seamVelocity = 0.5f * (
                ((positions[1] - positions[0]) / (times[1] - times[0]))
                + ((positions[^1] - positions[^2]) / (times[^1] - times[^2])));
            velocities[0] = seamVelocity;
            velocities[^1] = seamVelocity;
            NPCRootTrajectoryKey[] keys = NPCRootTrajectory.Resample(
                times,
                positions,
                velocities,
                (float)clip.DurationSeconds,
                60f,
                cyclic: true);
            float rootHeight = animation.PositionTrackInterpolate(rootTrack, 0d).Y;
            for (int key = animation.TrackGetKeyCount(rootTrack) - 1; key >= 0; key--)
            {
                animation.TrackRemoveKey(rootTrack, key);
            }
            animation.TrackSetInterpolationType(rootTrack, Animation.InterpolationType.Linear);
            animation.TrackSetInterpolationLoopWrap(rootTrack, false);
            foreach (NPCRootTrajectoryKey key in keys)
            {
                _ = animation.PositionTrackInsertKey(
                    rootTrack,
                    key.Time,
                    new Vector3(key.Position.X, rootHeight, key.Position.Y));
            }
        }
        player.RemoveAnimationLibrary(libraryName);
        _ = player.AddAnimationLibrary(libraryName, runtimeLibrary);
    }

    private void ConfigureIdleHandoffs()
    {
        AnimationNodeBlendTree root = AnimationTree!.TreeRoot as AnimationNodeBlendTree
            ?? throw new InvalidOperationException("NPC AnimationTree root must be a blend tree.");
        AnimationNodeStateMachine states = root.GetNode("States") as AnimationNodeStateMachine
            ?? throw new InvalidOperationException("NPC AnimationTree must expose States.");
        string[] sources =
        [
            "NPCTravel", "NPCF2IL", "NPCF2IR", "NPCB2IL", "NPCB2IR",
            "NPCShortFLExit", "NPCShortFRExit", "NPCShortBLExit", "NPCShortBRExit",
            "NPCPivotL", "NPCPivotR", "NPCSideStepL", "NPCSideStepR",
        ];
        foreach (string source in sources)
        {
            var transition = new AnimationNodeStateMachineTransition
            {
                XfadeTime = 0.2f,
                Reset = true,
            };
            states.AddTransition(source, "NPCIdle", transition);
        }
    }

    /// <summary>
    /// Classifies a heading-dominated request — planar offset within the pivot cross-DOF absorption capacity with
    /// outstanding terminal yaw — and configures the programme to turn in place through a chain of same-sign
    /// confined pivots (N = round(|yaw| / pivot); 90° is two clips, 180° four) plus a bounded settlement yaw
    /// remainder. The first chained pivot disperses the small planar offset mid-phase (confined cross-DOF absorption
    /// rule) instead of composing a second manoeuvre; later repetitions replay pure authored yaw. Returning
    /// <see langword="false" /> leaves the request to the remaining travel regimes.
    /// </summary>
    private bool TryConfigureTurnInPlace(Transform3D actor, NPCNavigationRoutePlan route)
    {
        if (_catalogue is null || _programme.Stage != NPCAnimationStage.Idle)
        {
            return false;
        }

        Vector3 planar = route.Endpoint - actor.Origin;
        planar.Y = 0f;
        NPCNavigationSemanticFrame semanticFrame = new(actor.Basis, actor.Basis);
        Vector2 semanticOffset = semanticFrame.WorldRouteToActorSemantic(planar);
        float planarDistance = semanticOffset.Length();
        float absorptionCapacity = NPCTurnInPlace.PivotPlanarAbsorptionCapacity(
            MaximumPlanarResidualPerSegment,
            TerminalCommitmentCorrectionBudget);
        if (!float.IsFinite(planarDistance) || planarDistance > absorptionCapacity)
        {
            return false;
        }

        float actorYaw = NPCNavigationRoutePlan.YawFromDirection(actor.Basis.Orthonormalized() * Vector3.Forward);
        float residualYaw = NPCNavigationRoutePlan.WrapAngle(route.TerminalYaw - actorYaw);
        if (Mathf.Abs(residualYaw) <= NPCAnimationProgramme.NumericalConvergenceThreshold)
        {
            return false;
        }

        NPCTurnInPlacePlan plan = NPCTurnInPlace.Select(
            residualYaw,
            semanticOffset,
            Mathf.Abs(_catalogue[StandingLocomotionCapability.ConfinedPivotLeft].EndpointYaw),
            Mathf.DegToRad(YawToleranceDegrees));
        _programme.ConfigureTurnInPlace(plan);
        Vector3 absorptionWorld = semanticFrame.ActorSemanticToWorld(plan.AbsorbedPlanarSemantic);
        _pivotAbsorptionWorldTotal = new(absorptionWorld.X, absorptionWorld.Z);
        _pivotAbsorptionPublishedWorld = Vector2.Zero;
        // The absorbed offset draws the reserved terminal planar capacity; the settlement stage still owns the exact
        // final landing under the no-crossing guard for anything the dispersion tail leaves behind.
        _ = _correctionBudget.ReserveTerminal(
            planarDistance,
            Mathf.Abs(plan.ConvergenceYawRadians),
            MaximumYawResidualPerSegment);
        return true;
    }

    /// <summary>
    /// Classifies a short mostly-lateral close request against the package side-step endpoint displacement and
    /// configures the confined manoeuvre sequence: a same-side side-step
    /// chain (N = round(distance / endpoint)) whose final step absorbs its curvature-safe yaw capacity and carries
    /// the dispersed planar remainder, followed — when the terminal yaw exceeds that absorption — by a same-sign
    /// chained pivot chain toward the terminal yaw, recorded as step chain first — then rotate. The pivot chain's
    /// bounded convergence remainder and the exact final landing resolve through settlement. Returning
    /// <see langword="false" /> leaves the request to the remaining travel regimes.
    /// </summary>
    private bool TryConfigureSideStep(Transform3D actor, NPCNavigationRoutePlan route)
    {
        if (_catalogue is null || _programme.Stage != NPCAnimationStage.Idle || route.Segments.Count != 1)
        {
            return false;
        }

        Vector3 direct = route.Endpoint - actor.Origin;
        direct.Y = 0f;
        if (!direct.IsFinite() || direct.Length() <= NPCShortBoutSplice.EffectiveZeroDistance)
        {
            return false;
        }

        if (!IsShortBoutDirectCollisionFree(actor, direct))
        {
            return false;
        }

        NPCNavigationSemanticFrame semanticFrame = new(actor.Basis, actor.Basis);
        Vector2 semanticOffset = semanticFrame.WorldRouteToActorSemantic(direct);
        float actorYaw = NPCNavigationRoutePlan.YawFromDirection(actor.Basis.Orthonormalized() * Vector3.Forward);
        float residualYaw = NPCNavigationRoutePlan.WrapAngle(route.TerminalYaw - actorYaw);
        Vector3 endpointDisplacement = _catalogue[StandingLocomotionCapability.ConfinedSideStepRight].EndpointRootDisplacement;
        float sideStepEndpointDistance = new Vector2(endpointDisplacement.X, -endpointDisplacement.Z).Length();
        NPCConfinedManoeuvrePlan plan = NPCConfinedManoeuvre.Select(
            semanticOffset,
            residualYaw,
            sideStepEndpointDistance,
            NPCSideStep.YawAbsorptionCapacity(sideStepEndpointDistance, MaximumPlanarResidualPerSegment),
            MaximumPlanarResidualPerSegment,
            Mathf.Abs(_catalogue[StandingLocomotionCapability.ConfinedPivotLeft].EndpointYaw),
            Mathf.DegToRad(YawToleranceDegrees));
        if (!plan.PlaysSideStepClip)
        {
            return false;
        }

        // The authored clip displacement may differ from the request inside the same confined-manoeuvre correction
        // envelope the short-bout splice already uses. The settlement reservation covers the chain's net remainder
        // plus the curvature the absorbed yaw can add to the actor-relative authored path; the composed pivot
        // chain's authored clip yaw and its bounded convergence remainder draw the same single terminal
        // reservation, so the ledger accounts every manoeuvre plus the settlement remainder under one budget.
        _correctionBudget.BeginRoute(
            route.DestinationRequestGeneration,
            route.RouteRevision,
            Math.Max(MaximumPlanarResidualPerSegment, MaximumShortBoutCorrection),
            MaximumYawResidualPerSegment);
        Vector2 clipEndpointSemantic = plan.SideStep.Kind == NPCSideStepKind.StepRight
            ? new(sideStepEndpointDistance, 0f)
            : new(-sideStepEndpointDistance, 0f);
        Vector2 chainedEndpointSemantic = clipEndpointSemantic * plan.SideStep.ChainedStepCount;
        float reservedYawRadians = plan.ComposesPivotAfterSideStep
            ? Mathf.Abs(plan.SideStep.AbsorbedYawRadians)
                + Mathf.Abs(plan.Turn.AuthoredYawRadians)
                + Mathf.Abs(plan.Turn.ConvergenceYawRadians)
            : Mathf.Abs(plan.SideStep.TotalYawRadians);
        _ = _correctionBudget.ReserveTerminal(
            (chainedEndpointSemantic - semanticOffset).Length() + MaximumPlanarResidualPerSegment,
            reservedYawRadians,
            MaximumYawResidualPerSegment);
        // Per-step yaw absorption is initialised at each StartSideStep event: intermediate chained repetitions play
        // pure full clips and only the final step disperses the absorbed yaw mid-phase.
        _sideStepAbsorptionYawTotal = 0f;
        _sideStepAbsorptionPublishedYaw = 0f;
        // A composed pivot chain carries authored yaw only — unlike an absorbed-residual pivot it absorbs no
        // planar displacement; the settlement no-crossing guard owns the final landing.
        _pivotAbsorptionWorldTotal = Vector2.Zero;
        _pivotAbsorptionPublishedWorld = Vector2.Zero;
        _programme.ConfigureSideStep(
            plan.SideStep,
            plan.ComposesPivotAfterSideStep ? plan.Turn : null);
        return true;
    }

    private void TryConfigureShortBout(Transform3D actor, NPCNavigationRoutePlan route)
    {
        if (_catalogue is null || _programme.Stage != NPCAnimationStage.Idle || route.Segments.Count != 1)
        {
            return;
        }

        Vector3 direct = route.Endpoint - actor.Origin;
        direct.Y = 0f;
        LastShortBoutRequestedDirectLength = direct.Length();
        LastShortBoutRealisedShortfall = LastShortBoutRequestedDirectLength;
        LastShortBoutDirectCollisionFree = IsShortBoutDirectCollisionFree(actor, direct);
        if (!LastShortBoutDirectCollisionFree)
        {
            _shortBoutExecutionInvalid = true;
            LastShortBoutCollisionDetected = true;
            LastShortBoutInvalidationReason = NPCShortBoutInvalidationReason.DirectPathCollision;
            return;
        }
        NPCNavigationSemanticFrame semanticFrame = new(actor.Basis, actor.Basis);
        Vector2 local = semanticFrame.WorldRouteToActorSemantic(direct);
        // Direction guard: the i2f/i2b splice forecast is ±Y semantic, so a mostly-lateral
        // request is ineligible regardless of scalar length — splicing a forward clip onto a sideways request would
        // demand an out-of-envelope world correction and invalidate the bout. Mostly-lateral requests belong to the
        // side-step or travel regimes instead.
        if (!NPCShortBoutSplice.IsDirectionEligible(local))
        {
            return;
        }

        bool backward = local.Y < -Mathf.Abs(local.X) * 0.35f;
        bool right = local.X > 0f;
        StandingLocomotionAnatomicalSide side = right
            ? StandingLocomotionAnatomicalSide.Right
            : StandingLocomotionAnatomicalSide.Left;
        string entry = _catalogue[StandingLocomotionCapabilities.Entry(backward, side)].Role;
        NPCShortBoutSpliceConfiguration configuration = new()
        {
            ProtectedEndpointDuration = ShortBoutProtectedEndpointDuration,
            CrossfadeDuration = ShortBoutCrossfadeDuration,
            MaximumCorrectionContribution = MaximumShortBoutCorrection,
        };
        if (NPCShortBoutSplice.TryCreate(_catalogue, entry, direct.Length(), configuration, out NPCShortBoutSplicePlan plan))
        {
            _shortBout = plan;
            _correctionBudget.BeginRoute(
                route.DestinationRequestGeneration,
                route.RouteRevision,
                Math.Max(MaximumPlanarResidualPerSegment, MaximumShortBoutCorrection),
                MaximumYawResidualPerSegment);
            _programme.ConfigureShortBout(plan);
        }
    }

    private static string ShortBoutPrefixState(NPCShortBoutSplicePlan plan) => plan.EntryCapability switch
    {
        StandingLocomotionCapability.EntryForwardRight => "NPCShortFR",
        StandingLocomotionCapability.EntryForwardLeft => "NPCShortFL",
        StandingLocomotionCapability.EntryBackwardRight => "NPCShortBR",
        StandingLocomotionCapability.EntryBackwardLeft => "NPCShortBL",
        _ => throw new InvalidOperationException($"Unknown short-bout entry capability '{plan.EntryCapability}'."),
    };

    private static string ShortBoutExitState(NPCShortBoutSplicePlan plan)
        => $"{ShortBoutPrefixState(plan)}Exit";

    private bool IsTerminalTransformWithinNumericalTolerance()
    {
        if (_route is null || Actor is null)
        {
            return false;
        }

        Vector3 positionError = _route.Endpoint - Actor.GlobalPosition;
        positionError.Y = 0f;
        float actorYaw = NPCNavigationRoutePlan.YawFromDirection(
            Actor.GlobalBasis.Orthonormalized() * Vector3.Forward);
        float yawError = Mathf.Abs(NPCNavigationRoutePlan.WrapAngle(_route.TerminalYaw - actorYaw));
        return positionError.Length() <= NPCAnimationProgramme.NumericalConvergenceThreshold
            && yawError <= NPCAnimationProgramme.NumericalConvergenceThreshold;
    }

    /// <summary>
    /// Converts a completed short bout whose endpoint residual sits within the confined absorption capacity into a
    /// settlement convergence: the reserved terminal budget bounds the correction and the no-crossing guard owns the
    /// endpoint convergence. Residuals beyond the capacity still fail promptly through the splice exception contract.
    /// </summary>
    private bool TryBeginShortBoutResidualSettlement()
    {
        if (_route is null || Actor is null || _shortBoutResidualSettlement)
        {
            return false;
        }

        Vector3 positionError = _route.Endpoint - Actor.GlobalPosition;
        positionError.Y = 0f;
        float planarError = positionError.IsFinite() ? positionError.Length() : float.PositiveInfinity;
        float actorYaw = NPCNavigationRoutePlan.YawFromDirection(
            Actor.GlobalBasis.Orthonormalized() * Vector3.Forward);
        float yawError = Mathf.Abs(NPCNavigationRoutePlan.WrapAngle(_route.TerminalYaw - actorYaw));
        if (planarError > MaximumPlanarResidualPerSegment || yawError > MaximumYawResidualPerSegment)
        {
            return false;
        }

        if (!_correctionBudget.ReserveTerminal(planarError, yawError, MaximumYawResidualPerSegment))
        {
            return false;
        }

        _shortBoutResidualSettlement = true;
        _programme.BeginResidualSettlement();
        return true;
    }

    private void InvalidateShortBout(NPCShortBoutInvalidationReason reason, string failureReason)
    {
        _shortBoutExecutionInvalid = true;
        LastShortBoutInvalidationReason = reason;
        LastShortBoutFailureReason = failureReason;
    }

    protected virtual bool IsShortBoutDirectCollisionFree(Transform3D actor, Vector3 direct)
        => Actor?.TestMove(
            actor,
            direct,
            collision: null,
            safeMargin: 0.001f,
            recoveryAsCollision: false,
            maxCollisions: 1) == false;

    /// <summary>
    /// Captures the mixer's evaluated graph state for this tick's consumption. The tree evaluates automatically
    /// after navigation (physics priority -100 before the tree's 0), so these playback positions, fading state and
    /// root-motion deltas reflect the previous tick's evaluation of the parameters published then — the coherent
    /// producer/consumer latency this component is built around.
    /// </summary>
    private void CaptureEvaluatedGraphDiagnostics(AnimationNodeStateMachinePlayback playback)
    {
        if (LastPublishedFrameGeneration != _physicsFrameGeneration)
        {
            throw new InvalidOperationException("NPC navigation attempted to consume an AnimationTree frame before publishing its programme parameters.");
        }
        LastEvaluatedState = playback.GetCurrentNode();
        LastEvaluatedRole = ResolveEvaluatedRole(LastEvaluatedState);
        LastEvaluatedCapability = string.IsNullOrWhiteSpace(LastEvaluatedRole)
            ? null
            : _catalogue![LastEvaluatedRole].Capability;
        LastEvaluatedClip = string.IsNullOrWhiteSpace(LastEvaluatedRole)
            ? string.Empty
            : _catalogue![LastEvaluatedRole].Identity;
        LastFadingFromState = playback.GetFadingFromNode();
        LastFadingFromPlayPosition = playback.GetFadingFromPlayPosition();
        LastFadingFromPlayLength = playback.GetFadingFromLength();
        LastFadingFromRole = ResolveEvaluatedRole(LastFadingFromState);
        LastFadingFromClip = string.IsNullOrWhiteSpace(LastFadingFromRole)
            ? string.Empty
            : _catalogue![LastFadingFromRole].Identity;
        LastFadingFromNormalisedPhase = !string.IsNullOrWhiteSpace(LastFadingFromRole)
            && AnimationPhasePublication.TryNormalise(
                LastFadingFromPlayPosition,
                LastFadingFromPlayLength,
                isCyclic: false,
                out float fadingFromNormalisedPhase)
            ? fadingFromNormalisedPhase
            : 0f;
        LastEvaluatedPlayPosition = playback.GetCurrentPlayPosition();
        float length = playback.GetCurrentLength();
        LastEvaluatedPlayLength = length;
        LastEvaluatedNormalisedPhase = TryResolveEvaluatedStateIsCyclic(out bool isCyclic)
            && AnimationPhasePublication.TryNormalise(
                LastEvaluatedPlayPosition,
                length,
                isCyclic,
                out float normalisedPhase)
            ? normalisedPhase
            : 0f;
        CaptureSustainedCycleObservation();
        LastEvaluatedRootDelta = GetRootMotionPositionDelta();
        Quaternion rootRotation = GetRootMotionRotationDelta();
        LastEvaluatedRootRotation = rootRotation.IsFinite() ? rootRotation : Quaternion.Identity;
        LastEvaluatedRootYaw = rootRotation.IsFinite() ? rootRotation.GetEuler().Y : 0f;
        if (_programme.Stage == NPCAnimationStage.ShortBout && _shortBout is { } plan)
        {
            CaptureShortBoutSourceDiagnostics(playback, plan);
            NPCShortBoutRootCompositionSample delta = EvaluateImportedShortBoutDelta();
            LastModelledShortBoutRootDelta = delta.Position;
            LastModelledShortBoutRootYaw = delta.Yaw;
        }
        else
        {
            LastModelledShortBoutRootDelta = Vector3.Zero;
            LastModelledShortBoutRootYaw = 0f;
        }
    }

    private bool TryResolveEvaluatedStateIsCyclic(out bool isCyclic)
    {
        isCyclic = false;
        AnimationNodeBlendTree root = RuntimeGraphRoot;
        if (root.GetNode(_statesNode) is not AnimationNodeStateMachine states
            || LastEvaluatedState.IsEmpty
            || !states.HasNode(LastEvaluatedState))
        {
            return false;
        }

        AnimationNode state = states.GetNode(LastEvaluatedState);
        if (state is AnimationNodeAnimation animation)
        {
            return TryResolveAnimationNodeIsCyclic(animation, out isCyclic);
        }

        if (state is not AnimationNodeBlendTree blendTree)
        {
            return false;
        }

        if (blendTree.HasNode(_clipNode) && blendTree.GetNode(_clipNode) is AnimationNodeAnimation clip)
        {
            return TryResolveAnimationNodeIsCyclic(clip, out isCyclic);
        }

        if (blendTree.HasNode(_sustainedNode)
            && blendTree.GetNode(_sustainedNode) is AnimationNodeBlendSpace2D sustained)
        {
            isCyclic = sustained.SyncMode == AnimationNodeBlendSpace2D.SyncModeEnum.CyclicConstant;
            return true;
        }

        return false;
    }

    private bool TryResolveAnimationNodeIsCyclic(AnimationNodeAnimation node, out bool isCyclic)
    {
        if (node.UseCustomTimeline)
        {
            isCyclic = node.LoopMode != Animation.LoopModeEnum.None;
            return true;
        }

        AnimationPlayer player = ResolveAnimationPlayer();
        if (!player.HasAnimation(node.Animation))
        {
            isCyclic = false;
            return false;
        }

        Animation? animation = player.GetAnimation(node.Animation);
        isCyclic = animation?.LoopMode != Animation.LoopModeEnum.None;
        return animation is not null;
    }

    private void CaptureSustainedCycleObservation()
    {
        bool sustainedStateEvaluated = LastEvaluatedState == _travelState;
        if (!sustainedStateEvaluated)
        {
            ResetSustainedCycleObservation();
            return;
        }

        float cycle = (float)ResolveSustainedSpace().CyclicLength;
        Variant positionValue = AnimationTree!.Get(_sustainedPositionParameter);
        float position = positionValue.VariantType is Variant.Type.Float or Variant.Type.Int
            ? positionValue.AsSingle()
            : float.NaN;
        if (!float.IsFinite(position) || !float.IsFinite(cycle) || cycle <= 0f)
        {
            ResetSustainedCycleObservation();
            return;
        }

        float phase = Mathf.PosMod(position, cycle) / cycle;
        _sustainedCycleClock = NPCSustainedCycleClock.ObserveEvaluatedTravel(
            _sustainedCycleClock,
            sustainedStateEvaluated,
            phase,
            _sustainedCycleExpectedObservedAdvance);
        _sustainedCyclePreviousPhase = _sustainedCycleAvailable ? _sustainedCycleCurrentPhase : phase;
        _sustainedCycleCurrentPhase = phase;
        _sustainedCycleWasArmedForObservedAdvance = _releaseSelectedAtLastPublication;
        _sustainedCycleAvailable = true;
    }

    private void ResetSustainedCycleObservation()
    {
        ResetFarRouteStoppingState();
        if (_terminalReleaseApproachActive)
        {
            _terminalReleaseApproachActive = false;
            _correctionBudget.CancelTerminalReservation();
            ResetFiniteCorrection(NPCVectorConvergenceResetReason.ClockGenerationChanged);
            ClearFiniteCorrectionEnvelopes();
        }
        _sustainedCycleClock = NPCSustainedCycleClock.ObserveEvaluatedTravel(
            _sustainedCycleClock,
            sustainedStateEvaluated: false,
            phase: 0f,
            expectedAdvance: 0f);
        _sustainedCycleAvailable = false;
        _sustainedCycleWasArmedForObservedAdvance = false;
        _sustainedCyclePreviousPhase = 0f;
        _sustainedCycleCurrentPhase = 0f;
        _sustainedCycleExpectedObservedAdvance = 0f;
        LastSustainedCycleExpectedAdvance = 0f;
    }

    private NPCSustainedCycleObservation SustainedCycleObservation(float delta)
    {
        if (!HasLiveSustainedState)
        {
            return default;
        }

        float cycle = (float)ResolveSustainedSpace().CyclicLength;
        float rate = AnimationTree!.Get(_playbackRateParameter).AsSingle();
        var observation = new NPCSustainedCycleObservation(
            _sustainedCycleAvailable,
            _sustainedCycleWasArmedForObservedAdvance,
            _sustainedCyclePreviousPhase,
            _sustainedCycleCurrentPhase,
            cycle,
            rate,
            delta,
            _sustainedCycleClock.Generation,
            _sustainedCycleClock.PreviousUnwrappedPhase,
            _sustainedCycleClock.CurrentUnwrappedPhase);
        LastSustainedCycleExpectedAdvance = NPCExitReleaseGate.ExpectedFrameAdvance(observation);
        return observation;
    }

    private string ResolveEvaluatedRole(StringName state)
    {
        if (_catalogue is null)
        {
            return string.Empty;
        }

        string stateName = state.ToString();
        if (stateName == "NPCTravel")
        {
            return _catalogue[StandingLocomotionCapabilities.Travel(_programme.IsBackwardBout)].Role;
        }

        return stateName is
            "NPCF2IL" or "NPCF2IR" or "NPCB2IL" or "NPCB2IR"
            or "NPCI2FL" or "NPCI2FR" or "NPCI2BL" or "NPCI2BR"
            or "NPCPivotL" or "NPCPivotR" or "NPCSideStepL" or "NPCSideStepR"
            ? _catalogue[CapabilityForFiniteState(stateName)].Role
            : string.Empty;
    }

    private NPCShortBoutRootCompositionSample EvaluateImportedShortBoutDelta()
    {
        Animation entry = _shortBoutImportedEntry
            ?? throw new InvalidOperationException("NPC short-bout entry clip was not cached before graph evaluation.");
        Animation exit = _shortBoutImportedExit
            ?? throw new InvalidOperationException("NPC short-bout exit clip was not cached before graph evaluation.");
        Vector3 position = Vector3.Zero;
        float yaw = 0f;

        float entryTo = LastShortBoutEntrySourcePosition;
        if (entryTo > _shortBoutPreviousEntrySourcePosition)
        {
            NPCShortBoutRootCompositionSample from = ImportedRootSample(entry, _shortBoutEntryPositionTrack, _shortBoutEntryRotationTrack, _shortBoutPreviousEntrySourcePosition);
            NPCShortBoutRootCompositionSample to = ImportedRootSample(entry, _shortBoutEntryPositionTrack, _shortBoutEntryRotationTrack, entryTo);
            float weight = LastFadingFromState.IsEmpty ? 1f : 1f - (LastShortBoutBlendWeight * 0.5f);
            position += (to.Position - from.Position) * weight;
            yaw += Mathf.Wrap(to.Yaw - from.Yaw, -Mathf.Pi, Mathf.Pi) * weight;
        }

        float exitTo = LastShortBoutExitSourcePosition;
        if (exitTo > _shortBoutPreviousExitSourcePosition)
        {
            NPCShortBoutRootCompositionSample from = ImportedRootSample(exit, _shortBoutExitPositionTrack, _shortBoutExitRotationTrack, _shortBoutPreviousExitSourcePosition);
            NPCShortBoutRootCompositionSample to = ImportedRootSample(exit, _shortBoutExitPositionTrack, _shortBoutExitRotationTrack, exitTo);
            float weight = LastFadingFromState.IsEmpty ? 1f : LastShortBoutBlendWeight * 0.5f;
            position += (to.Position - from.Position) * weight;
            yaw += Mathf.Wrap(to.Yaw - from.Yaw, -Mathf.Pi, Mathf.Pi) * weight;
        }

        _shortBoutPreviousEntrySourcePosition = entryTo;
        _shortBoutPreviousExitSourcePosition = exitTo;
        return new(position, yaw);
    }

    private void CaptureShortBoutSourceDiagnostics(
        AnimationNodeStateMachinePlayback playback,
        NPCShortBoutSplicePlan plan)
    {
        string prefixState = ShortBoutPrefixState(plan);
        string exitState = ShortBoutExitState(plan);
        float previousEntry = LastShortBoutEntrySourcePosition;
        float previousExit = LastShortBoutExitSourcePosition;
        if (playback.GetCurrentNode() == prefixState)
        {
            LastShortBoutEntrySourcePosition = Mathf.Clamp(playback.GetCurrentPlayPosition(), 0f, plan.EntryCutTime);
        }
        else if (playback.GetCurrentNode() == exitState)
        {
            LastShortBoutExitSourcePosition = Mathf.Clamp(
                plan.ExitCutTime + playback.GetCurrentPlayPosition(),
                plan.ExitCutTime,
                (float)_catalogue![plan.Pair.ExitRole].DurationSeconds);
            if (playback.GetFadingFromNode() == prefixState)
            {
                LastShortBoutEntrySourcePosition = Mathf.Clamp(
                    playback.GetFadingFromPlayPosition(),
                    0f,
                    plan.EntryCutTime);
            }
        }

        if (LastShortBoutEntrySourcePosition + 0.00001f < previousEntry
            || LastShortBoutExitSourcePosition + 0.00001f < previousExit)
        {
            LastShortBoutUnexpectedResetCount++;
        }
        LastShortBoutEntryPlaybackRate = LastShortBoutEntrySourcePosition > previousEntry ? 1f : 0f;
        LastShortBoutExitPlaybackRate = LastShortBoutExitSourcePosition > previousExit ? 1f : 0f;
        LastShortBoutBlendWeight = playback.GetCurrentNode() == exitState
            ? playback.GetFadingFromNode().IsEmpty
                ? 1f
                : Mathf.Clamp(playback.GetCurrentPlayPosition() / plan.CrossfadeDuration, 0f, 1f)
            : 0f;
    }

    private float PhaseCorrectionWeight()
    {
        AnimationNodeStateMachinePlayback playback = ResolvePlayback();
        float length = playback.GetCurrentLength();
        if (length <= 0f)
        {
            return 0.5f;
        }

        float phase = Mathf.PosMod(playback.GetCurrentPlayPosition(), length) / length;
        return 0.35f + (0.65f * Mathf.Abs(Mathf.Cos(phase * Mathf.Tau)));
    }

    private LocomotionPermissions CurrentPermissions() => new(PlanarMotionAllowed, YawMotionAllowed);

    private void UpdateTerminalCandidateDecisionDiagnostics(
        NPCNavigationRoutePlan route,
        in NPCExitArmCandidate sameLane,
        in NPCExitArmCandidate oppositeLane,
        float usablePlanarCapacityAfterUncertainty,
        bool commit,
        int selectedCandidateIndex,
        NPCTerminalCandidateRejection selectedRejection)
    {
        (bool hasPostExitRealisedError, float positionError, float yawError) = ResolvePostExitRealisedError();
        _terminalCandidateDecisionDiagnostics = new(
            HasCandidateEvaluation: true,
            route.DestinationRequestGeneration,
            route.RouteRevision,
            null,
            _programme.Stage,
            _programme.TerminalCommitted,
            _programme.ExitCount,
            _correctionBudget.RemainingReservedTerminalPlanar,
            hasPostExitRealisedError,
            positionError,
            yawError,
            commit,
            selectedCandidateIndex,
            selectedRejection,
            CreateTerminalCandidateEvaluationDiagnostics(
                0, sameLane, usablePlanarCapacityAfterUncertainty),
            CreateTerminalCandidateEvaluationDiagnostics(
                1, oppositeLane, usablePlanarCapacityAfterUncertainty),
            _terminalReleaseApproachActive);
    }

    private static NPCTerminalCandidateEvaluationDiagnostics CreateTerminalCandidateEvaluationDiagnostics(
        int candidateIndex,
        in NPCExitArmCandidate candidate,
        float usablePlanarCapacity)
    {
        bool yawCompatible = !(candidate.YawResidual > NPCTerminalReleaseScheduler.RootRotationTolerance);
        bool waitCompatible = !(candidate.ReleasePlan.TimeToReleaseSeconds
            > NPCTerminalReleaseScheduler.MaximumWaitSeconds);
        var factors = new NPCTerminalCandidateDecisionFactors(
            SupportedBlend: true,
            ContactCompatible: true,
            VelocityCompatible: candidate.VelocityCompatible,
            EndpointServiceable: candidate.VectorConvergenceFeasible,
            WouldOvershootTerminal: candidate.WouldOvershootTerminal,
            YawCompatible: yawCompatible,
            WaitCompatible: waitCompatible);
        return new(
            IsEvaluated: true,
            candidateIndex,
            candidate.ReleasePlan.Lane,
            candidate.Exit.Role,
            candidate.ReleasePlan.ReleaseEvent,
            candidate.Occurrence,
            candidate.ReleasePlan.TimeToReleaseSeconds,
            candidate.Projection.Resolved,
            candidate.Projection.BlendConverged,
            candidate.Projection.ProjectedTicks,
            candidate.VectorConvergence.RequiredCorrection,
            candidate.VectorConvergence.VectorReachableCorrection,
            candidate.VectorConvergence.PredictedEndpointResidual,
            candidate.VectorConvergence.PredictedEndpointYawResidual,
            candidate.VectorConvergence.FirstInfeasibilityReason,
            usablePlanarCapacity,
            candidate.VectorConvergenceFeasible,
            candidate.RouteRelativeOvershootMetres,
            candidate.WouldOvershootTerminal,
            candidate.YawResidual,
            yawCompatible,
            candidate.VelocityCompatible,
            true,
            waitCompatible,
            true,
            factors.RejectedUnsupportedBlend,
            factors.RejectedContactMismatch,
            factors.RejectedVelocityMismatch,
            factors.RejectedYawUnreachable,
            candidate.RejectedCorrectionBeyondCapacity,
            factors.RejectedWaitBeyondBound,
            candidate.WouldOvershootTerminal,
            factors.IsSchedulerEligible,
            factors.SchedulerRejection);
    }

    private void RefreshTerminalCandidateDecisionRuntimeState()
    {
        (bool hasPostExitRealisedError, float positionError, float yawError) = ResolvePostExitRealisedError();
        _terminalCandidateDecisionDiagnostics = _terminalCandidateDecisionDiagnostics with
        {
            Stage = _programme.Stage,
            TerminalCommitted = _programme.TerminalCommitted,
            ExitCount = _programme.ExitCount,
            RemainingReservedTerminalPlanar = _correctionBudget.RemainingReservedTerminalPlanar,
            HasPostExitRealisedError = hasPostExitRealisedError,
            PostExitRealisedPositionError = positionError,
            PostExitRealisedYawError = yawError,
            TerminalReleaseSelected = _terminalReleaseApproachActive,
        };
    }

    private void ResetTerminalCandidateDecisionDiagnostics(string reason)
    {
        (bool hasPostExitRealisedError, float positionError, float yawError) = ResolvePostExitRealisedError();
        _terminalCandidateDecisionDiagnostics = NPCTerminalCandidateDecisionDiagnostics.Empty with
        {
            Stage = _programme.Stage,
            ResetReason = reason,
            TerminalCommitted = _programme.TerminalCommitted,
            ExitCount = _programme.ExitCount,
            RemainingReservedTerminalPlanar = _correctionBudget.RemainingReservedTerminalPlanar,
            HasPostExitRealisedError = hasPostExitRealisedError,
            PostExitRealisedPositionError = positionError,
            PostExitRealisedYawError = yawError,
            TerminalReleaseSelected = _terminalReleaseApproachActive,
        };
    }

    private (bool HasPostExitRealisedError, float PositionError, float YawError) ResolvePostExitRealisedError()
    {
        if (!_programme.TerminalCommitted || _route is null || Actor is null || !IsInstanceValid(Actor))
        {
            return (false, 0f, 0f);
        }

        Vector3 positionError = _route.Endpoint - Actor.GlobalPosition;
        positionError.Y = 0f;
        float actorYaw = NPCNavigationRoutePlan.YawFromDirection(
            Actor.GlobalBasis.Orthonormalized() * Vector3.Forward);
        return (
            true,
            positionError.IsFinite() ? positionError.Length() : float.PositiveInfinity,
            Mathf.Abs(NPCNavigationRoutePlan.WrapAngle(_route.TerminalYaw - actorYaw)));
    }

    private static bool IsEffectivelyZeroRequest(Transform3D actor, NPCNavigationRoutePlan route)
    {
        Vector3 planar = route.Endpoint - actor.Origin;
        planar.Y = 0f;
        float actorYaw = NPCNavigationRoutePlan.YawFromDirection(actor.Basis.Orthonormalized() * Vector3.Forward);
        float yawError = Mathf.Abs(NPCNavigationRoutePlan.WrapAngle(route.TerminalYaw - actorYaw));
        return planar.Length() <= NPCShortBoutSplice.EffectiveZeroDistance
            && yawError <= NPCAnimationProgramme.NumericalConvergenceThreshold;
    }

    private AnimationNodeStateMachinePlayback ResolvePlayback()
        => AnimationTree?.Get(_playbackParameter).As<AnimationNodeStateMachinePlayback>()
            ?? throw new InvalidOperationException("NPC navigation requires the nested States playback parameter.");

    private bool HasValidBinding()
        => Actor is not null && IsInstanceValid(Actor)
            && AnimationTree is not null && IsInstanceValid(AnimationTree)
            && RootMotionReference is not null && IsInstanceValid(RootMotionReference)
            && _catalogue is not null;

    private bool TryEnterBoundedStoppingRegion(
        Transform3D actor,
        NPCNavigationRoutePlan route,
        float rate,
        NPCAnimationPhaseLane lane,
        out NPCFarRouteEstimateKey key)
    {
        key = default;
        try
        {
            NavigationCurrentStatus status = CurrentStatus;
            NPCSustainedBlendSnapshot snapshot = RequireSustainedBlendSnapshot();
            if (!status.HasActiveRequest
                || status.ActiveRequest is not { } request
                || _sustainedCycleClock.Generation <= 0
                || snapshot.ProjectionIdentity == 0)
            {
                ResetFarRouteStoppingState();
                PublishBoundedTerminalCheckDiagnostics(false, false, 0, default);
                return false;
            }

            key = new(
                request,
                route.RouteRevision,
                _sustainedCycleClock.Generation,
                snapshot.ProjectionIdentity > long.MaxValue
                    ? long.MaxValue
                    : (long)snapshot.ProjectionIdentity);
            bool gaitChanged = _farRouteStoppingEstimator.IsAvailable
                && (_farRouteEstimateKey != key
                    || _farRouteEstimateLane != lane
                    || _farRouteEstimateBlend.DistanceTo(LastBlendPoint) >= FarEstimateBlendMaterialChange
                    || Math.Abs(_farRouteEstimateRate - rate) >= FarEstimateRateMaterialChange);
            if (gaitChanged)
            {
                ResetFarRouteStoppingState();
            }

            if (!_farRouteStoppingEstimator.IsAvailable)
            {
                NPCFarRouteEstimateBasis basis = CreateFarRouteEstimateBasis(actor, rate, lane, snapshot);
                if (!_farRouteStoppingEstimator.TryInitialise(
                        key,
                        basis,
                        new(LastRemainingDistance, LastRouteProgress, _sustainedCycleClock.CurrentUnwrappedPhase)))
                {
                    PublishBoundedTerminalCheckDiagnostics(false, false, 0, default);
                    return false;
                }

                _farRouteEstimateKey = key;
                _farRouteEstimateBlend = LastBlendPoint;
                _farRouteEstimateRate = rate;
                _farRouteEstimateLane = lane;
                SetActiveRequestClockGeneration(_sustainedCycleClock.Generation);
            }
            else
            {
                bool gaitBoundary = Math.Floor(_sustainedCycleClock.CurrentUnwrappedPhase)
                    > Math.Floor(_sustainedCycleClock.PreviousUnwrappedPhase);
                _ = _farRouteStoppingEstimator.TryRecalibrate(
                    key,
                    new(
                        LastRouteProgress,
                        _sustainedCycleClock.CurrentUnwrappedPhase,
                        gaitBoundary,
                        FarEstimateMaterialDriftDistance));
            }

            if (!_farRouteStoppingEstimator.TryEstimate(
                    key,
                    _sustainedCycleClock.CurrentUnwrappedPhase,
                    out NPCFarRouteStoppingEstimate estimate))
            {
                PublishBoundedTerminalCheckDiagnostics(false, false, 0, default);
                return false;
            }

            PublishBoundedTerminalCheckDiagnostics(true, estimate.IsInStoppingRegion, 0, default);
            return estimate.IsInStoppingRegion;
        }
        catch
        {
            ResetFarRouteStoppingState();
            throw;
        }
    }

    private NPCFarRouteEstimateBasis CreateFarRouteEstimateBasis(
        Transform3D actor,
        float rate,
        NPCAnimationPhaseLane lane,
        NPCSustainedBlendSnapshot snapshot)
    {
        CalculateSustainedBlendWeights(LastBlendPoint, _sustainedBlendWeights);
        Vector2 blendedSemanticCycle = Vector2.Zero;
        for (int index = 0; index < snapshot.Points.Length; index++)
        {
            float weight = _sustainedBlendWeights[index];
            if (weight <= 0f)
            {
                continue;
            }

            StandingLocomotionClip clip = snapshot.Points[index].ForLane(lane).Clip;
            Vector2 start = clip.SampleResponse(0d).CumulativeLocalPlanar;
            Vector2 end = clip.SampleResponse(clip.DurationSeconds).CumulativeLocalPlanar;
            blendedSemanticCycle += (end - start) * weight;
        }

        NPCNavigationSemanticFrame frame = new(actor.Basis, GetRootMotionReferenceBasis());
        Vector3 cycleWorld = frame.ActorSemanticToWorld(blendedSemanticCycle);
        float cycleLongitudinal = Math.Max(cycleWorld.Dot(LastRouteTangent), 0.000001f);
        StandingLocomotionClip leftExit = ResolveExitClip(NPCAnimationPhaseLane.Left);
        StandingLocomotionClip rightExit = ResolveExitClip(NPCAnimationPhaseLane.Right);
        float exitTravel = Math.Max(
            new Vector2(leftExit.EndpointRootDisplacement.X, leftExit.EndpointRootDisplacement.Z).Length(),
            new Vector2(rightExit.EndpointRootDisplacement.X, rightExit.EndpointRootDisplacement.Z).Length());
        float exitSpeed = Math.Max(
            exitTravel / (float)Math.Min(leftExit.DurationSeconds, rightExit.DurationSeconds),
            0f) * FiniteTransitionPlaybackRate;
        float crossfadeDuration = Math.Max(
            ResolveExitCrossfadeDuration(leftExit),
            ResolveExitCrossfadeDuration(rightExit));
        float sustainedSpeed = cycleLongitudinal * rate / snapshot.CyclicLength;
        float crossfadeTravel = (sustainedSpeed + exitSpeed) * crossfadeDuration;
        return new(
            cycleLongitudinal,
            snapshot.CyclicLength,
            rate,
            crossfadeTravel,
            exitTravel,
            FarEstimateUncertaintyReserve,
            PositionTolerance);
    }

    private NPCReleaseOccurrence SelectNextReleaseOccurrence(
        AnimationNodeBlendSpace2D sustainedSpace,
        float phase,
        float rate,
        NPCAnimationPhaseLane sustainedLane,
        NPCAnimationPhaseLane exitLane)
    {
        float oppositeEventPhase = exitLane == sustainedLane
            ? 0f
            : ResolvePackagedOppositeReleasePhase(sustainedLane);
        NPCExitReleasePlan plan = NPCExitReleaseGate.Plan(
            ResolveExitClip(exitLane).Role,
            ResolveExitBoundaryMapping(exitLane).Source.ID,
            exitLane,
            sustainedLane,
            phase,
            (float)sustainedSpace.CyclicLength,
            rate,
            oppositeEventPhase);
        return NPCExitReleaseGate.SelectOccurrence(
            _sustainedCycleClock.Generation,
            _sustainedCycleClock.CurrentUnwrappedPhase,
            plan.TargetPhase,
            plan.Lane,
            plan.ReleaseEvent);
    }

    private void PublishBoundedTerminalCheckDiagnostics(
        bool estimateAvailable,
        bool isInStoppingRegion,
        int exactCandidateCheckCount,
        in NPCImminentCheckEvidence evidence)
    {
        _cumulativeExactTerminalCandidateChecks += exactCandidateCheckCount;
        _boundedTerminalCheckDiagnostics = new(
            _physicsFrameGeneration,
            estimateAvailable,
            isInStoppingRegion,
            exactCandidateCheckCount,
            _cumulativeExactTerminalCandidateChecks,
            evidence);
    }

    private void ResetFarRouteStoppingState()
    {
        _farRouteStoppingEstimator.Reset();
        _farRouteEstimateKey = default;
        _farRouteEstimateBlend = Vector2.Zero;
        _farRouteEstimateRate = 0f;
        _farRouteEstimateLane = default;
        _imminentCheckRetryGate.Reset();
    }

    private bool TrySelectImminentTerminalRelease(
        Transform3D actor,
        NPCNavigationRoutePlan route,
        float delta)
    {
        if (_terminalReleaseApproachActive)
        {
            // The selected occurrence owns the release window; ordinary publication must not disturb its landing.
            RefreshTerminalCandidateDecisionRuntimeState();
            return true;
        }

        if (_programme.Stage != NPCAnimationStage.SustainedTravel)
        {
            ResetTerminalCandidateDecisionDiagnostics("no sustained terminal candidate");
            return false;
        }
        if (SuppressTerminalPlanningForOrdinaryCycleDiagnostics)
        {
            ResetTerminalCandidateDecisionDiagnostics("ordinary-cycle diagnostic isolation");
            return false;
        }
        if (!HasLiveSustainedState)
        {
            ResetTerminalCandidateDecisionDiagnostics("live looping sustained child clock unavailable");
            return false;
        }
        AnimationNodeBlendSpace2D sustainedSpace = ResolveSustainedSpace();
        float phase = _sustainedCycleCurrentPhase;
        float rate = Math.Max(AnimationTree!.Get(_playbackRateParameter).AsSingle(), 0.001f);
        NPCAnimationPhaseLane sustainedLane = _programme.PhaseLane;
        if (!TryEnterBoundedStoppingRegion(actor, route, rate, sustainedLane, out NPCFarRouteEstimateKey estimateKey))
        {
            ResetTerminalCandidateDecisionDiagnostics("outside bounded stopping region");
            return false;
        }

        NPCReleaseOccurrence sameOccurrence = SelectNextReleaseOccurrence(
            sustainedSpace,
            phase,
            rate,
            sustainedLane,
            sustainedLane);
        NPCAnimationPhaseLane oppositeLane = sustainedLane == NPCAnimationPhaseLane.Left
            ? NPCAnimationPhaseLane.Right
            : NPCAnimationPhaseLane.Left;
        NPCReleaseOccurrence oppositeOccurrence = SelectNextReleaseOccurrence(
            sustainedSpace,
            phase,
            rate,
            sustainedLane,
            oppositeLane);
        var imminentEvidence = new NPCImminentCheckEvidence(estimateKey, sameOccurrence, oppositeOccurrence);
        if (!_imminentCheckRetryGate.ShouldCheck(imminentEvidence))
        {
            PublishBoundedTerminalCheckDiagnostics(true, true, 0, imminentEvidence);
            ResetTerminalCandidateDecisionDiagnostics("imminent occurrence pair already rejected");
            return false;
        }

        // Every imminent check evaluates exactly the next compatible occurrence on each authored lane. It never
        // derives a horizon from remaining route length and never scans a later recurrence.
        NPCExitArmCandidate sameLane = EvaluateBoundedImminentCandidate(
            actor,
            route,
            sustainedSpace,
            phase,
            rate,
            sustainedLane,
            sustainedLane,
            delta,
            sameOccurrence,
            _sameLaneCandidateProjection,
            _sameLaneCandidateLeadInIntervals,
            _sameLaneCandidateMovingIntervals);
        NPCExitArmCandidate opposite = EvaluateBoundedImminentCandidate(
            actor,
            route,
            sustainedSpace,
            phase,
            rate,
            sustainedLane,
            oppositeLane,
            delta,
            oppositeOccurrence,
            _oppositeLaneCandidateProjection,
            _oppositeLaneCandidateLeadInIntervals,
            _oppositeLaneCandidateMovingIntervals);
        PublishBoundedTerminalCheckDiagnostics(
            true,
            true,
            _exactTerminalCandidateChecksThisFrame,
            imminentEvidence);
        float usablePlanarCapacityAfterUncertainty = ResolveTerminalPlanarCapacity();
        NPCTerminalSchedulerDecision decision = _terminalScheduler.Evaluate(
            ToSchedulerCandidate(0, sameLane),
            ToSchedulerCandidate(1, opposite));
        UpdateTerminalCandidateDecisionDiagnostics(
            route,
            sameLane,
            opposite,
            usablePlanarCapacityAfterUncertainty,
            decision.Commit,
            decision.CandidateIndex,
            decision.Rejection);
        if (!decision.Commit)
        {
            // A candidate blocked solely by the imminent wait bound becomes committable as its lead-in shrinks
            // inside the wait window, with the occurrence pair identity unchanged. Such a pair must be re-checked
            // next frame; only a rejection on other grounds suppresses the unchanged pair.
            if (!IsSolelyWaitGatedImminentCandidate(sameLane) && !IsSolelyWaitGatedImminentCandidate(opposite))
            {
                _imminentCheckRetryGate.RecordRejected(imminentEvidence);
            }
            return false;
        }

        NPCExitArmCandidate reported = decision.CandidateIndex == 1 ? opposite : sameLane;
        Vector2 walkPoint = ResolveSustainedPointPosition(
            StandingLocomotionCapabilities.Travel(_programme.IsBackwardBout));
        // A committed imminent candidate's projected blend-pin convergence completes by the projected release
        // beat — the blend will be pinned at/definitively before release, which is the pin discipline the packaged
        // walk-continuing *2i exits require. A blocked candidate leaves ordinary locomotion under the bounded
        // retry gate; there is no gate bypass.
        bool seamEligible = NPCCommandedReleaseProjection.PassesExitSeamReleaseGate(
            reported.Projection,
            _smoothedBlend.DistanceTo(walkPoint));
        if (!seamEligible)
        {
            _imminentCheckRetryGate.RecordRejected(imminentEvidence);
            return false;
        }

        // The oracle's required vector is the sole un-authored component and is already capacity-gated before the
        // approach window opens. The finite correction executor remains the sole movement owner for supported
        // normal finite exits.
        _correctionBudget.RecordTerminal(
            reported.VectorConvergence.RequiredCorrection.Length(),
            reported.YawResidual);
        _terminalReleaseApproachActive = true;
        _selectedReleaseOccurrence = reported.Occurrence;
        LastSelectedReleaseOccurrence = reported.Occurrence;
        // NAV-001 exit-seam continuity: the packaged *2i exits are authored to continue the walk-family
        // release pose, so the approach pins the sustained blend to the bout's pure walk point (the same
        // discipline the entry handoff uses to pin its blend). Exiting from a lateral or arc mix blend
        // crossfades a mixed stance geometry into the walk exit pose — measured foot sweeps of 3.9-5.1 m/s
        // on diagonal and replaced routes. The pin holds only for the bounded lead-in (at most half a cycle);
        // the candidate evaluation keeps the walk-family release compatibility gate.
        _releaseApproachBlendPoint = walkPoint;
        LastSelectedExitRole = reported.Exit.Role;
        LastSelectedReleaseLane = reported.ReleasePlan.Lane;
        LastSelectedReleaseEvent = reported.ReleasePlan.ReleaseEvent;
        LastTargetReleasePhase = reported.ReleasePlan.TargetPhase;
        LastTimeToSelectedReleaseSeconds = reported.ReleasePlan.TimeToReleaseSeconds;
        return true;
    }

    private static NPCVectorConvergenceRequest CreateVectorConvergenceRequest(
        Transform3D actor,
        NPCNavigationRoutePlan route,
        in NPCExitArmCandidate candidate,
        IReadOnlyList<NPCVectorConvergenceInterval> leadInIntervals,
        IReadOnlyList<NPCVectorConvergenceInterval> movingIntervals)
    {
        float actorYaw = NPCNavigationRoutePlan.YawFromDirection(
            actor.Basis.Orthonormalized() * Vector3.Forward);
        return new(
            new Vector2(actor.Origin.X, actor.Origin.Z),
            new Vector2(route.Endpoint.X, route.Endpoint.Z),
            actorYaw,
            route.TerminalYaw,
            candidate.Occurrence,
            candidate.Occurrence.UnwrappedPhase,
            leadInIntervals,
            movingIntervals,
            Vector2.Zero,
            Vector2.Zero,
            // The projected lead-in never opens terminal correction. The first correction-eligible f2i interval
            // therefore starts from the same rest state installed by the executor at the release crossing.
            0f,
            CreateFiniteCorrectionConstraints());
    }

    private static NPCVectorConvergenceConstraints CreateFiniteCorrectionConstraints()
        => new(
            NPCVectorConvergenceOracle.MaximumCorrectionSpeed,
            2f,
            30f,
            NPCSettlementPlanarConvergence.TaperAcceleration,
            NPCAnimationProgramme.NumericalConvergenceThreshold);

    private float ResolveTerminalPlanarCapacity()
        => Math.Max(_correctionBudget.RemainingTerminalPlanarCapacity - TerminalForecastUncertaintyMetres, 0f);

    private float ResolvePackagedOppositeReleasePhase(NPCAnimationPhaseLane sustainedLane)
    {
        if (_catalogue is null)
        {
            throw new InvalidOperationException("NPC animation catalogue must be loaded before release planning.");
        }

        StandingLocomotionClip travel = _catalogue[StandingLocomotionCapabilities.Travel(_programme.IsBackwardBout)];
        string travelRole = travel.Role;
        StandingLocomotionPhaseEvent currentEvent = ResolveSustainedPhaseEvent(travelRole, sustainedLane);
        NPCAnimationPhaseLane oppositeLane = sustainedLane == NPCAnimationPhaseLane.Right
            ? NPCAnimationPhaseLane.Left
            : NPCAnimationPhaseLane.Right;
        StandingLocomotionPhaseEvent oppositeEvent = ResolveSustainedPhaseEvent(travelRole, oppositeLane);
        return NPCExitReleaseGate.ResolveOppositeTargetPhase(
            currentEvent.TimeSeconds,
            oppositeEvent.TimeSeconds,
            travel.DurationSeconds);
    }

    /// <summary>
    /// Evaluates one bounded imminent candidate: the next authored occurrence on one lane. The projection covers
    /// only the short lead-in to that occurrence; the exact interval materialisation and oracle feasibility check
    /// cover only the moving crossfade and complete f2i.
    /// </summary>
    private NPCExitArmCandidate EvaluateBoundedImminentCandidate(
        Transform3D actor,
        NPCNavigationRoutePlan route,
        AnimationNodeBlendSpace2D sustainedSpace,
        float phase,
        float rate,
        NPCAnimationPhaseLane sustainedLane,
        NPCAnimationPhaseLane exitLane,
        float delta,
        NPCReleaseOccurrence occurrence,
        List<NPCCommandedReleaseProjectionInterval> projectionCapture,
        List<NPCVectorConvergenceInterval> leadInIntervals,
        List<NPCVectorConvergenceInterval> movingIntervals)
    {
        _exactTerminalCandidateChecksThisFrame++;
        float oppositeEventPhase = exitLane == sustainedLane
            ? 0f
            : ResolvePackagedOppositeReleasePhase(sustainedLane);
        NPCExitReleasePlan releasePlan = NPCExitReleaseGate.Plan(
            ResolveExitClip(exitLane).Role,
            ResolveExitBoundaryMapping(exitLane).Source.ID,
            exitLane,
            sustainedLane,
            phase,
            (float)sustainedSpace.CyclicLength,
            rate,
            oppositeEventPhase);
        // The lead-in horizon is the candidate's own next-occurrence timing plus the current interval; it is never
        // derived from remaining route length and never scans a later recurrence.
        float horizonSeconds = (float)(Math.Max(
            occurrence.UnwrappedPhase - _sustainedCycleClock.CurrentUnwrappedPhase,
            0d) * sustainedSpace.CyclicLength / Math.Max(rate, 0.001f)) + delta;
        NPCExitArmCandidate candidate = EvaluateImminentCandidateOccurrence(
            actor,
            route,
            sustainedSpace,
            phase,
            rate,
            exitLane,
            LastBlendPoint,
            delta,
            occurrence,
            horizonSeconds,
            releasePlan,
            projectionCapture);
        if (!candidate.Projection.Resolved)
        {
            return candidate;
        }

        MaterialiseExactCorrectionIntervals(
            candidate,
            sustainedSpace,
            delta,
            leadInIntervals,
            movingIntervals,
            queuedEntryFade: ResolveQueuedEntryFade(delta) is { } queuedFade
                && candidate.Projection.ProjectedTicks <= queuedFade.FutureIntervalCount + 1
                    ? queuedFade
                    : null);
        NPCVectorConvergenceResult feasibility = NPCVectorConvergenceOracle.Evaluate(
            CreateVectorConvergenceRequest(actor, route, candidate, leadInIntervals, movingIntervals),
            captureSchedule: false);
        float capacity = _correctionBudget.RemainingTerminalPlanarCapacity;
        bool withinCapacity = feasibility.RequiredCorrection.IsFinite()
            && feasibility.RequiredCorrection.Length()
                <= capacity + NPCAnimationProgramme.NumericalConvergenceThreshold;
        Vector2 terminalDirection = new(route.TerminalDirection.X, route.TerminalDirection.Z);
        float routeRelativeOvershoot = -feasibility.RequiredCorrection.Dot(terminalDirection);
        return candidate with
        {
            VectorConvergenceFeasible = feasibility.IsFeasible && withinCapacity,
            RejectedCorrectionBeyondCapacity = feasibility.IsFeasible && !withinCapacity,
            VectorConvergence = feasibility,
            RouteRelativeOvershootMetres = routeRelativeOvershoot,
            WouldOvershootTerminal = routeRelativeOvershoot > TerminalOvershootToleranceMetres,
        };
    }

    private NPCExitArmCandidate EvaluateImminentCandidateOccurrence(
        Transform3D actor,
        NPCNavigationRoutePlan route,
        AnimationNodeBlendSpace2D sustainedSpace,
        float phase,
        float rate,
        NPCAnimationPhaseLane exitLane,
        Vector2 blendPoint,
        float delta,
        NPCReleaseOccurrence occurrence,
        float horizonSeconds,
        NPCExitReleasePlan releasePlan,
        List<NPCCommandedReleaseProjectionInterval> projectionCapture)
    {
        blendPoint = (_sustainedBlendDomain
            ?? throw new InvalidOperationException("NPC sustained blend domain must be initialised before exit planning."))
            .Project(blendPoint);
        StandingLocomotionClip exit = ResolveExitClip(exitLane);
        // Lead-in modelling integrates the commanded trajectory exactly what the runtime publisher commands between
        // now and release — pin convergence, ordinary cadence, phase advance and yaw rotation rather than a frozen
        // blend/rate snapshot that under-predicted sustained-until-release travel 3.25× on the defect route.
        NPCCommandedReleaseProjectionResult projection = ProjectImminentLeadIn(
            sustainedSpace,
            blendPoint,
            phase,
            rate,
            releasePlan.TargetPhase,
            actor,
            exit,
            route,
            delta,
            occurrence,
            horizonSeconds,
            projectionCapture,
            out NPCCommandedReleaseProjectionRequest projectionRequest);
        if (projection.Resolved)
        {
            releasePlan = releasePlan with
            {
                TimeToReleaseSeconds = projection.TimeToReleaseSeconds
            };
        }

        float actorYaw = NPCNavigationRoutePlan.YawFromDirection(actor.Basis.Orthonormalized() * Vector3.Forward);
        // The scheduler's yaw-feasibility decision must assess the same selected occurrence as the commanded
        // trajectory. The projection turns the actor toward the exit start across the lead-in, so checking the
        // arm-time yaw here rejects a reachable later beat on one mirrored lane. Keep unresolved projections
        // fail-closed at the current pose; only a resolved occurrence may publish its projected release pose.
        float yaw = NPCCommandedReleaseProjection.ResolveExitYawResidual(
            route.TerminalYaw,
            exit.EndpointYaw,
            actorYaw,
            projection);
        // The release seam consumes the projected blend after pin convergence, not the current blend snapshot.
        CalculateSustainedBlendWeights(
            projection.Resolved ? projection.BlendPointAtRelease : blendPoint,
            _sustainedBlendWeights);
        float releasePlaybackRate = projection.Resolved ? projection.PlaybackRateAtRelease : rate;
        Vector2 outgoingSemanticVelocity = ResolveSustainedSemanticVelocity(
            releasePlan.TargetPhase,
            releasePlaybackRate);
        // Continuity is evaluated at the selected occurrence, after the commanded projection has turned the actor to
        // the exit start. Using the current basis here can reject the same future occurrence whose projected yaw and
        // exact correction window are already feasible.
        NPCNavigationSemanticFrame releaseSustainedFrame = projection.Resolved
            ? new NPCNavigationSemanticFrame(new Basis(Vector3.Up, projection.YawAtRelease), GetRootMotionReferenceBasis())
            : new NPCNavigationSemanticFrame(actor.Basis, GetRootMotionReferenceBasis());
        NPCNavigationSemanticFrame exitStartFrame = new(
            new Basis(Vector3.Up, route.TerminalYaw - exit.EndpointYaw),
            GetRootMotionReferenceBasis());
        Vector3 outgoingWorldVelocity3 = releaseSustainedFrame.ActorSemanticToWorld(outgoingSemanticVelocity);
        const float referenceFrameDelta = 1f / 60f;
        float crossfadeDuration = Math.Min(ResolveExitCrossfadeDuration(exit), (float)exit.DurationSeconds);
        float firstCrossfadeTime = Math.Min(referenceFrameDelta, crossfadeDuration);
        float firstOutgoingPhase = Mathf.PosMod(
            releasePlan.TargetPhase + (firstCrossfadeTime * releasePlaybackRate / (float)sustainedSpace.CyclicLength),
            1f);
        Vector2 firstOutgoingSemanticVelocity = ResolveSustainedSemanticVelocity(
            firstOutgoingPhase,
            releasePlaybackRate);
        Vector3 firstOutgoingWorldVelocity3 = releaseSustainedFrame.ActorSemanticToWorld(firstOutgoingSemanticVelocity);
        StandingLocomotionResponseSample incomingResponse = exit.SampleResponse(
            firstCrossfadeTime * FiniteTransitionPlaybackRate);
        Vector3 incomingWorldVelocity3 = exitStartFrame.ActorSemanticToWorld(
            incomingResponse.PlanarVelocity * FiniteTransitionPlaybackRate);
        float firstBlendWeight = crossfadeDuration > 0f ? firstCrossfadeTime / crossfadeDuration : 1f;
        Vector2 outgoingWorldVelocity = new(outgoingWorldVelocity3.X, outgoingWorldVelocity3.Z);
        Vector2 firstOutgoingWorldVelocity = new(firstOutgoingWorldVelocity3.X, firstOutgoingWorldVelocity3.Z);
        Vector2 incomingWorldVelocity = new(incomingWorldVelocity3.X, incomingWorldVelocity3.Z);
        Vector2 firstBlendedWorldVelocity = firstOutgoingWorldVelocity.Lerp(incomingWorldVelocity, firstBlendWeight);
        float outgoingYawRate = ResolveSustainedYawRate(
            releasePlan.TargetPhase,
            releasePlaybackRate);
        float firstOutgoingYawRate = ResolveSustainedYawRate(
            firstOutgoingPhase,
            releasePlaybackRate);
        float firstBlendedYawRate = Mathf.Lerp(
            firstOutgoingYawRate,
            incomingResponse.YawRate,
            firstBlendWeight);
        bool velocityCompatible = NPCTransitionContinuity.IsVelocityCompatible(
            outgoingWorldVelocity,
            firstBlendedWorldVelocity,
            outgoingWorldVelocity,
            firstBlendedWorldVelocity,
            outgoingYawRate,
            firstBlendedYawRate);
        return new(
            exit,
            releasePlan,
            yaw,
            blendPoint,
            velocityCompatible,
            WouldOvershootTerminal: false,
            RouteRelativeOvershootMetres: 0f,
            projectionRequest,
            projection,
            occurrence,
            VectorConvergenceFeasible: false,
            RejectedCorrectionBeyondCapacity: false,
            default,
            projectionCapture);
    }

    /// <summary>
    /// Builds the exact lead-in intervals for one imminent candidate from its first-pass projection capture. The
    /// current engine evaluation is already committed before this tick's publication: it occupies the
    /// producer/consumer-latency slot, the first projected command is consumed on the following evaluation, and the
    /// projection's release-crossing interval is not consumed as lead-in movement.
    /// </summary>
    private static void BuildLeadInIntervals(
        IReadOnlyList<NPCCommandedReleaseProjectionInterval> projectionIntervals,
        List<NPCVectorConvergenceInterval> leadIn,
        NPCVectorConvergenceInterval outgoingCommitmentInterval)
    {
        leadIn.Clear();
        for (int leadInIndex = 0; leadInIndex < projectionIntervals.Count; leadInIndex++)
        {
            if (leadInIndex == 0)
            {
                leadIn.Add(outgoingCommitmentInterval with
                {
                    PreviousTick = 0,
                    CurrentTick = 1,
                });
                continue;
            }

            int projectionIndex = leadInIndex - 1;
            if (projectionIndex >= projectionIntervals.Count)
            {
                break;
            }

            NPCCommandedReleaseProjectionInterval interval = projectionIntervals[projectionIndex];
            leadIn.Add(new(
                leadInIndex,
                leadInIndex + 1,
                interval.DeltaSeconds,
                interval.AuthoredRootWorld,
                0f,
                interval.AuthoredYawRadians));
        }
    }

    /// <summary>
    /// Builds the exact owned moving window — the moving crossfade plus the complete f2i — in caller-owned reusable
    /// storage. <paramref name="tickBase" /> continues the tick layout after the lead-in at pre-commit evaluation
    /// and starts at zero when the window opens at the release crossing.
    /// </summary>
    private void BuildMovingIntervals(
        in NPCCommandedReleaseProjectionResult projection,
        AnimationNodeBlendSpace2D sustainedSpace,
        in NPCExitReleasePlan releasePlan,
        StandingLocomotionClip exit,
        float crossfadeDuration,
        float delta,
        List<NPCVectorConvergenceInterval> moving,
        int tickBase,
        float? movingStartPhase = null,
        Vector2? movingBlendPoint = null,
        Vector2? movingBlendAcceleration = null,
        NPCQueuedEntryFade? queuedEntryFade = null)
    {
        moving.Clear();
        float[] preservedBlendWeights = new float[_sustainedBlendWeights.Length];
        _sustainedBlendWeights.CopyTo(preservedBlendWeights, 0);

        // StartExit restores the ordinary sustained rate before the graph consumes the transition publication.
        // Forecasting a separate lead-in rate here made the outgoing half of the fade diverge from the executor.
        float releaseRate = NPCFiniteTransitionCadence.SourceSecondsPerWallSecond;
        float startPhase = movingStartPhase ?? releasePlan.TargetPhase;
        Vector2 blendPoint = movingBlendPoint ?? projection.BlendPointAtRelease;
        Vector2 blendAcceleration = movingBlendAcceleration ?? Vector2.Zero;
        double elapsed = 0d;
        int intervalIndex = 0;
        if (queuedEntryFade is { } queued)
        {
            AppendQueuedEntryFadeIntervals(
                moving,
                queued,
                sustainedSpace,
                releaseRate,
                delta,
                tickBase,
                ref startPhase,
                ref blendPoint,
                ref blendAcceleration,
                ref intervalIndex);
        }
        int queuedEntryFadeIntervalCount = moving.Count;
        if (queuedEntryFade is null)
        {
            blendPoint = StepFiniteExitBlend(blendPoint, ref blendAcceleration, delta);
            CalculateSustainedBlendWeights(blendPoint, _sustainedBlendWeights);
            float setupCurrentPhase = startPhase
                + (delta * releaseRate / (float)sustainedSpace.CyclicLength);
            NPCImportedRootDelta setup = ResolveSustainedImportedRootDelta(
                startPhase,
                setupCurrentPhase);
            // The tree's first crossfade evaluation already applies the first fade weight to the outgoing root
            // while the reset incoming seek emits none; an unweighted outgoing root over-predicts this interval.
            float setupBlendWeight = Mathf.Clamp(delta / Math.Max(crossfadeDuration, float.Epsilon), 0f, 1f);
            Vector2 setupRoot = setup.Root * (1f - setupBlendWeight);
            float setupYaw = setup.Yaw * (1f - setupBlendWeight);
            int setupGlobalIndex = tickBase + intervalIndex;
            moving.Add(new(
                setupGlobalIndex,
                setupGlobalIndex + 1,
                delta,
                setupRoot,
                0f,
                setupYaw,
                AuthoredRootIsActorLocal: true));
            intervalIndex++;
        }
        int exitFadeIntervalIndex = 0;
        float outgoingPhaseOffset = queuedEntryFade is null ? delta : 0f;
        while (elapsed < crossfadeDuration - double.Epsilon)
        {
            // AnimationTree always evaluates one full physics interval. The transition weight clamps at one on the
            // final evaluation; it does not shorten that motor interval to the fractional fade remainder.
            float step = delta;
            double currentFadeElapsed = elapsed + step;
            float blendWeight = Mathf.Clamp(
                (float)(currentFadeElapsed / Math.Max(crossfadeDuration, float.Epsilon)),
                0f,
                1f);
            blendPoint = StepFiniteExitBlend(blendPoint, ref blendAcceleration, delta);
            CalculateSustainedBlendWeights(blendPoint, _sustainedBlendWeights);
            NPCImportedRootDelta outgoing = ResolveSustainedImportedRootDelta(
                startPhase + ((outgoingPhaseOffset + (float)elapsed) * releaseRate / (float)sustainedSpace.CyclicLength),
                startPhase + ((outgoingPhaseOffset + (float)currentFadeElapsed) * releaseRate / (float)sustainedSpace.CyclicLength));
            // A reset immediate transition seeks the incoming node on the first fade evaluation. Seeking emits no
            // Root delta; subsequent evaluations advance it by one full physics interval.
            double incomingPrevious = Math.Max(exitFadeIntervalIndex - 1, 0) * delta * FiniteTransitionPlaybackRate;
            double incomingCurrent = exitFadeIntervalIndex == 0
                ? incomingPrevious
                : Math.Min(
                    exitFadeIntervalIndex * delta * FiniteTransitionPlaybackRate,
                    exit.DurationSeconds);
            NPCImportedRootDelta incoming = SampleImportedRootDelta(
                exit.Role,
                incomingPrevious,
                incomingCurrent,
                looping: false);
            Vector2 root = outgoing.Root.Lerp(incoming.Root, blendWeight);
            float yaw = Mathf.Lerp(outgoing.Yaw, incoming.Yaw, blendWeight);
            int globalIndex = tickBase + intervalIndex;
            moving.Add(new(
                globalIndex,
                globalIndex + 1,
                step,
                root,
                0f,
                yaw,
                AuthoredRootIsActorLocal: true));
            elapsed += step;
            intervalIndex++;
            exitFadeIntervalIndex++;
        }

        int exitCrossfadeIntervalCount = moving.Count - queuedEntryFadeIntervalCount;
        int resetOnlyExitIntervals = queuedEntryFade is null ? 2 : 1;
        double sourcePrevious = Math.Min(
            Math.Max(exitCrossfadeIntervalCount - resetOnlyExitIntervals, 0)
                * delta
                * FiniteTransitionPlaybackRate,
            exit.DurationSeconds);
        intervalIndex = 0;
        while (sourcePrevious < exit.DurationSeconds - double.Epsilon)
        {
            float step = delta;
            double sourceCurrent = Math.Min(
                sourcePrevious + (step * FiniteTransitionPlaybackRate),
                exit.DurationSeconds);
            NPCImportedRootDelta imported = SampleImportedRootDelta(
                exit.Role,
                sourcePrevious,
                sourceCurrent,
                looping: false);
            int globalIndex = tickBase + moving.Count;
            moving.Add(new(
                globalIndex,
                globalIndex + 1,
                step,
                imported.Root,
                0f,
                imported.Yaw,
                AuthoredRootIsActorLocal: true));
            sourcePrevious = sourceCurrent;
        }

        preservedBlendWeights.CopyTo(_sustainedBlendWeights, 0);
    }

    private void AppendQueuedEntryFadeIntervals(
        List<NPCVectorConvergenceInterval> crossfade,
        in NPCQueuedEntryFade queued,
        AnimationNodeBlendSpace2D sustainedSpace,
        float releaseRate,
        float delta,
        int globalIndexOffset,
        ref float phase,
        ref Vector2 blendPoint,
        ref Vector2 blendAcceleration,
        ref int intervalIndex)
    {
        double outgoingPrevious = queued.OutgoingPlayPosition;
        for (int index = 0; index < queued.FutureIntervalCount; index++)
        {
            blendPoint = StepFiniteExitBlend(blendPoint, ref blendAcceleration, delta);
            CalculateSustainedBlendWeights(blendPoint, _sustainedBlendWeights);
            float currentPhase = phase + (delta * releaseRate / (float)sustainedSpace.CyclicLength);
            NPCImportedRootDelta sustained = ResolveSustainedImportedRootDelta(
                phase,
                currentPhase);
            double outgoingCurrent = Math.Min(outgoingPrevious + delta, queued.OutgoingDurationSeconds);
            NPCImportedRootDelta entry = SampleImportedRootDelta(
                queued.OutgoingRole,
                outgoingPrevious,
                outgoingCurrent,
                looping: false);
            float fadeWeight = Mathf.Clamp(
                (queued.CurrentFadeElapsedSeconds + ((index + 2) * delta))
                    / Math.Max(queued.FadeDurationSeconds, float.Epsilon),
                0f,
                1f);
            int globalIndex = globalIndexOffset + intervalIndex;
            crossfade.Add(new(
                globalIndex,
                globalIndex + 1,
                delta,
                entry.Root.Lerp(sustained.Root, fadeWeight),
                0f,
                Mathf.Lerp(entry.Yaw, sustained.Yaw, fadeWeight),
                AuthoredRootIsActorLocal: true));
            outgoingPrevious = outgoingCurrent;
            phase = currentPhase;
            intervalIndex++;
        }
    }

    private NPCQueuedEntryFade? ResolveQueuedEntryFade(float delta)
    {
        AnimationNodeStateMachinePlayback playback = ResolvePlayback();
        StringName fadingFrom = playback.GetFadingFromNode();
        string outgoingRole = ResolveEvaluatedRole(fadingFrom);
        if (playback.GetCurrentNode() != _travelState
            || fadingFrom.IsEmpty
            || string.IsNullOrWhiteSpace(outgoingRole)
            || _catalogue![outgoingRole].Capability is not (
                StandingLocomotionCapability.EntryForwardLeft
                or StandingLocomotionCapability.EntryForwardRight
                or StandingLocomotionCapability.EntryBackwardLeft
                or StandingLocomotionCapability.EntryBackwardRight))
        {
            return null;
        }

        float fadeDuration = playback.GetFadingLength();
        float currentFadeElapsed = playback.GetCurrentPlayPosition();
        float outgoingPlayPosition = playback.GetFadingFromPlayPosition();
        if (!float.IsFinite(fadeDuration)
            || fadeDuration <= 0f
            || !float.IsFinite(currentFadeElapsed)
            || !float.IsFinite(outgoingPlayPosition))
        {
            return null;
        }

        int futureIntervals = 0;
        float projectedElapsed = currentFadeElapsed;
        while (projectedElapsed + delta < fadeDuration - float.Epsilon)
        {
            futureIntervals++;
            projectedElapsed += delta;
        }
        return new(
            outgoingRole,
            outgoingPlayPosition,
            (float)_catalogue![outgoingRole].DurationSeconds,
            currentFadeElapsed,
            fadeDuration,
            futureIntervals);
    }

    private Vector2 StepFiniteExitBlend(Vector2 blend, ref Vector2 acceleration, float delta)
    {
        NPCBlendDomain domain = _sustainedBlendDomain
            ?? throw new InvalidOperationException("NPC sustained blend domain must be initialised before forecasting an exit.");
        return domain.Project(NPCTransitionContinuity.StepVelocityCommand(
            domain.Project(blend),
            domain.Project(Vector2.Zero),
            ref acceleration,
            delta,
            0.8f,
            10f));
    }

    /// <summary>
    /// Thin seam binding the pure imminent lead-in projection to live runtime state: the current blend and its
    /// command acceleration, the release pin, the phase and the rate driving the unread tree advance, the exit
    /// start yaw, and the yaw command state. All per-tick modelling lives in
    /// <see cref="NPCCommandedReleaseProjection" />.
    /// </summary>
    private NPCCommandedReleaseProjectionResult ProjectImminentLeadIn(
        AnimationNodeBlendSpace2D sustainedSpace,
        Vector2 blendPoint,
        float phase,
        float rate,
        float targetPhase,
        Transform3D actor,
        StandingLocomotionClip exit,
        NPCNavigationRoutePlan route,
        float delta,
        NPCReleaseOccurrence occurrence,
        float horizonSeconds,
        List<NPCCommandedReleaseProjectionInterval> projectionCapture,
        out NPCCommandedReleaseProjectionRequest request)
    {
        request = CreateImminentProjectionRequest(
            sustainedSpace,
            blendPoint,
            phase,
            rate,
            targetPhase,
            actor,
            exit,
            route,
            delta,
            occurrence);
        // The tick bound is the candidate's own imminent horizon plus one convergence margin interval; it is never
        // derived from remaining route length.
        int maximumTicks = Mathf.CeilToInt(
            (horizonSeconds + NPCCommandedReleaseProjection.MaximumConvergenceSeconds) / Math.Max(delta, float.Epsilon));
        NPCSustainedBlendSnapshot samplingSnapshot = RequireSustainedBlendSnapshot();
        using NPCExactImportedRootEndpointCache<Animation>.Scope samplingScope =
            _importedRootEndpointCache.BeginScope(samplingSnapshot);
        Func<Vector2, float, Vector2, float, float, NPCCommandedReleaseAuthoredIntervalSample> intervalSampler =
            ResolveSustainedIntervalSampler(sustainedSpace);
        return NPCCommandedReleaseProjection.Project(
            request,
            intervalSampler,
            projectionCapture,
            maximumTicks);
    }

    private NPCCommandedReleaseProjectionRequest CreateImminentProjectionRequest(
        AnimationNodeBlendSpace2D sustainedSpace,
        Vector2 blendPoint,
        float phase,
        float rate,
        float targetPhase,
        Transform3D actor,
        StandingLocomotionClip exit,
        NPCNavigationRoutePlan route,
        float delta,
        NPCReleaseOccurrence occurrence)
    {
        float actorYaw = NPCNavigationRoutePlan.YawFromDirection(actor.Basis.Orthonormalized() * Vector3.Forward);
        float movingYawTarget = NPCNavigationRoutePlan.WrapAngle(actorYaw + ResolveMovingYawError(actor));
        // The projection aims at exactly the yaw the runtime publisher commands, including the derived
        // terminal-facing window, so the lead-in's modelled rotation matches the realised walk.
        float programmeTargetYaw = ResolveMovingTargetYaw(actor);
        float requestedYawRate = Mathf.Clamp(
            NPCNavigationRoutePlan.WrapAngle(programmeTargetYaw - actorYaw) * 6f,
            -NPCTerminalReleaseScheduler.MaximumYawRate,
            NPCTerminalReleaseScheduler.MaximumYawRate);
        float evaluatedSpeedWeight = Mathf.Clamp(
            LastAuthoredRootWorldDelta.Length() / Math.Max(delta * 0.93f, float.Epsilon),
            0f,
            1f);
        return new(
            blendPoint,
            _blendCommandAcceleration,
            ResolveSustainedPointPosition(StandingLocomotionCapabilities.Travel(_programme.IsBackwardBout)),
            phase,
            rate,
            (float)sustainedSpace.CyclicLength,
            targetPhase,
            actorYaw,
            route.TerminalYaw - exit.EndpointYaw,
            delta,
            RequireBlendConvergenceBeforeRelease: true,
            _playbackRateAcceleration,
            NPCFiniteTransitionCadence.SourceSecondsPerWallSecond,
            _sustainedCycleClock.CurrentUnwrappedPhase,
            occurrence,
            movingYawTarget,
            route.TerminalYaw,
            requestedYawRate,
            LastEvaluatedRootYaw / Math.Max(delta, float.Epsilon),
            _terminalReleaseApproachActive && evaluatedSpeedWeight > 0.0001f ? 1f : evaluatedSpeedWeight,
            _terminalReleaseApproachActive && evaluatedSpeedWeight > 0.0001f ? 1f : PhaseCorrectionWeight(),
            MaximumYawResidualRate,
            ResidualSmoothingRate,
            new(
                _smoothedYawResidual,
                _previousAuthoredYawRate,
                LastYawResidual,
                _correctionBudget.RemainingYaw),
            LastRemainingDistance <= _terminalFacingActivationDistance,
            _previousAppliedYawRate);
    }

    /// <summary>
    /// Materialises the exact bounded evidence for one imminent candidate: the lead-in intervals from its
    /// first-pass projection capture and the owned moving crossfade/f2i window, both in caller-owned reusable
    /// storage.
    /// </summary>
    private void MaterialiseExactCorrectionIntervals(
        in NPCExitArmCandidate candidate,
        AnimationNodeBlendSpace2D sustainedSpace,
        float delta,
        List<NPCVectorConvergenceInterval> leadInIntervals,
        List<NPCVectorConvergenceInterval> movingIntervals,
        NPCQueuedEntryFade? queuedEntryFade)
    {
        Transform3D actor = Actor?.GlobalTransform
            ?? throw new InvalidOperationException("NPC actor must exist before finite correction planning.");
        BuildLeadInIntervals(
            candidate.ProjectionIntervals,
            leadInIntervals,
            CreateOutgoingCommitmentInterval(actor, delta));
        BuildMovingIntervals(
            candidate.Projection,
            sustainedSpace,
            candidate.ReleasePlan,
            candidate.Exit,
            Math.Min(ResolveExitCrossfadeDuration(candidate.Exit), (float)candidate.Exit.DurationSeconds),
            delta,
            movingIntervals,
            tickBase: leadInIntervals.Count,
            queuedEntryFade: queuedEntryFade);
    }

    private NPCVectorConvergenceInterval CreateOutgoingCommitmentInterval(Transform3D actor, float delta)
    {
        NPCNavigationSemanticFrame semanticFrame = new(actor.Basis, GetRootMotionReferenceBasis());
        LocomotionPermissions permissions = CurrentPermissions();
        // The commitment interval must model exactly the Root interval the crossing frame's composed move
        // consumes — the mixer's most recent evaluation, read at open time. LastEvaluatedRootDelta is one
        // producer/consumer-latency slot stale whenever that evaluation already happened this physics frame
        // (the same-frame root-consumer contract the integration harness dispatches), and the crossing frame
        // composes this interval without a correction, so any staleness lands on the terminal boundary
        // unabsorbed instead of converging inside the owned window.
        Vector3 rawRoot = GetRootMotionPositionDelta();
        Vector3 evaluatedRootWorld = permissions.MovementAllowed && rawRoot.IsFinite()
            ? semanticFrame.EvaluatedRootDeltaToWorld(rawRoot)
            : Vector3.Zero;
        Quaternion rootRotation = GetRootMotionRotationDelta();
        float evaluatedRootYaw = permissions.RotationAllowed && rootRotation.IsFinite()
            ? semanticFrame.EvaluatedRootRotationToActorLocal(rootRotation).GetEuler().Y
            : 0f;
        float actorYaw = NPCNavigationRoutePlan.YawFromDirection(
            actor.Basis.Orthonormalized() * Vector3.Forward);
        return new NPCVectorConvergenceInterval(
            0,
            1,
            delta,
            new Vector2(evaluatedRootWorld.X, evaluatedRootWorld.Z),
            actorYaw,
            evaluatedRootYaw);
    }

    /// <summary>
    /// Samples the same local Root interval AnimationMixer consumes after a navigation publication. The tree phase
    /// observed by navigation precedes that consuming evaluation by one interval, so both endpoints advance once.
    /// </summary>
    private Func<Vector2, float, Vector2, float, float, NPCCommandedReleaseAuthoredIntervalSample>
        ResolveSustainedIntervalSampler(AnimationNodeBlendSpace2D space)
    {
        if (_sustainedIntervalSampler is null || _sustainedIntervalSamplerSpace != space)
        {
            _sustainedIntervalSamplerSpace = space;
            _sustainedIntervalSampler = (previousBlend, previousPhase, blendPoint, currentPhase, playbackRate) =>
            {
                _ = previousBlend;
                _ = playbackRate;
                float phaseAdvance = Mathf.PosMod(currentPhase - previousPhase, 1f);
                CalculateSustainedBlendWeights(blendPoint, _sustainedBlendWeights, _sustainedEngineTriangleIndices);
                NPCImportedRootDelta imported = ResolveSustainedImportedRootDelta(
                    Mathf.PosMod(previousPhase + phaseAdvance, 1f),
                    Mathf.PosMod(currentPhase + phaseAdvance, 1f));
                return new(imported.Root, imported.Yaw);
            };
        }

        return _sustainedIntervalSampler;
    }

    private float ResolveSustainedAuthoredYawRate(float phase, float playbackRate)
    {
        NPCSustainedBlendSnapshot snapshot = RequireSustainedBlendSnapshot();
        float yawRate = 0f;
        for (int index = 0; index < snapshot.Points.Length; index++)
        {
            float weight = _sustainedBlendWeights[index];
            if (weight <= 0f)
            {
                continue;
            }

            NPCSustainedRoleSnapshot role = snapshot.Points[index].ForLane(_programme.PhaseLane);
            double sourceTime = (role.ReleaseEventTime + (phase * role.Clip.DurationSeconds)) % role.Clip.DurationSeconds;
            float commonDurationScale = (float)(role.Clip.DurationSeconds / snapshot.CyclicLength);
            yawRate += role.Clip.SampleResponse(sourceTime).YawRate * commonDurationScale * playbackRate * weight;
        }

        return yawRate;
    }

    private NPCTerminalReleaseCandidate ToSchedulerCandidate(int index, NPCExitArmCandidate candidate)
    {
        var scheduled = new NPCTerminalReleaseCandidate(
            index,
            candidate.ReleasePlan.Lane,
            candidate.BlendPoint,
            candidate.ReleasePlan.TimeToReleaseSeconds,
            candidate.VectorConvergence.PredictedEndpointResidual.Length(),
            candidate.YawResidual,
            (_sustainedBlendDomain
                ?? throw new InvalidOperationException("NPC sustained blend domain must exist before candidate scheduling."))
                .Contains(candidate.BlendPoint),
            // Contact policy is not a NAV routing input. The composed move consumes an already-authorised
            // contribution, but release selection and the vector reservation remain contact-mode independent.
            true,
            candidate.VelocityCompatible,
            candidate.VectorConvergenceFeasible,
            // Full-horizon feasibility includes the route-relative terminal residual. Keep the scalar overshoot
            // estimate in diagnostics, but do not let it veto a vector schedule that proves the correction fits.
            WouldOvershootTerminal: false,
            candidate.RouteRelativeOvershootMetres,
            _smoothedPlaybackRate);
        if (ShouldRejectTerminalReleaseCandidate(scheduled))
        {
            return scheduled with
            {
                EndpointServiceable = false,
                // The controlled test seam must force a hard rejection: endpoint capacity is advisory under the
                // correction policy, so the seam also marks contact incompatibility to exercise the bounded
                // blocked fallback deterministically.
                ContactCompatible = false,
            };
        }

        return scheduled;
    }

    /// <summary>
    /// Gets whether one imminent candidate is blocked only by the release wait bound: its projection resolved and
    /// every scheduler predicate other than the lead-in window passes. Such an occurrence is not rejected — it is
    /// simply not imminent yet and becomes committable as its time-to-release shrinks.
    /// </summary>
    private bool IsSolelyWaitGatedImminentCandidate(in NPCExitArmCandidate candidate)
        => candidate.Projection.Resolved
            && NPCTerminalCandidateDecisionFactors.From(ToSchedulerCandidate(0, candidate))
                .SchedulerRejection == NPCTerminalCandidateRejection.WaitBeyondBound;

    /// <summary>
    /// Provides a narrow controlled rejection seam for deterministic terminal scheduling tests.
    /// </summary>
    protected virtual bool ShouldRejectTerminalReleaseCandidate(NPCTerminalReleaseCandidate candidate)
    {
        _ = candidate;
        return false;
    }

    private bool TryExecuteImminentRelease(float delta)
    {
        if (!_terminalReleaseApproachActive)
        {
            return false;
        }

        NPCSustainedCycleObservation clock = SustainedCycleObservation(delta);
        NPCReleaseOccurrenceCrossingStatus crossing = NPCExitReleaseGate.OccurrenceCrossingStatus(
            clock,
            _selectedReleaseOccurrence);
        LastTimeToSelectedReleaseSeconds = NPCExitReleaseGate.TimeToNextRelease(
            _sustainedCycleCurrentPhase,
            LastTargetReleasePhase,
            (float)ResolveSustainedSpace().CyclicLength,
            Math.Max(AnimationTree!.Get(_playbackRateParameter).AsSingle(), 0.001f));
        if (crossing is NPCReleaseOccurrenceCrossingStatus.Invalid or NPCReleaseOccurrenceCrossingStatus.Missed)
        {
            DiscardImminentRelease(NPCVectorConvergenceResetReason.OccurrenceExpired);
            return false;
        }
        if (crossing != NPCReleaseOccurrenceCrossingStatus.Crossed)
        {
            return false;
        }

        if (!TryOpenFiniteCorrectionAtRelease(delta))
        {
            DiscardImminentRelease(NPCVectorConvergenceResetReason.Cancellation);
            return false;
        }
        _programme.SelectExitLane(LastSelectedReleaseLane);
        _terminalReleaseApproachActive = false;
        // Transfer playback ownership at the release crossing itself. Waiting for StartExit publication leaves the
        // production commit snapshot and the next graph evaluation on the entry handoff's matched rate.
        _entryHandoffBlendActive = false;
        _entryHandoffPublicationFrames = 0;
        _smoothedPlaybackRate = NPCFiniteTransitionCadence.SourceSecondsPerWallSecond;
        _playbackRateAcceleration = 0f;
        AnimationTree!.Set(_playbackRateParameter, _smoothedPlaybackRate);
        LastTerminalCommitPhase = _sustainedCycleCurrentPhase;
        LastTerminalCommitPreviousPhase = _sustainedCyclePreviousPhase;
        LastTerminalCommitExpectedAdvance = LastSustainedCycleExpectedAdvance;
        LastTerminalCommitPlaybackRate = AnimationTree!.Get(_playbackRateParameter).AsSingle();
        LastTerminalCommitOrdinaryPlaybackRate = NPCFiniteTransitionCadence.SourceSecondsPerWallSecond;
        LastTerminalCommitRootAccumulator = AnimationTree.GetRootMotionPositionAccumulator();
        LastTimeToSelectedReleaseSeconds = 0f;
        return true;
    }

    /// <summary>
    /// Opens the finite f2i correction window at the release crossing from the measured actor pose and the remaining
    /// crossfade/full-clip model. The lead-in is over; the owned moving window is the sole correction authority.
    /// </summary>
    private bool TryOpenFiniteCorrectionAtRelease(float delta)
    {
        if (_route is null || Actor is null)
        {
            return false;
        }
        Transform3D actor = Actor.GlobalTransform;
        float actorYaw = NPCNavigationRoutePlan.YawFromDirection(
            actor.Basis.Orthonormalized() * Vector3.Forward);
        LocomotionPermissions permissions = CurrentPermissions();
        NPCNavigationSemanticFrame semanticFrame = new(actor.Basis, GetRootMotionReferenceBasis());
        Vector3 rawRoot = GetRootMotionPositionDelta();
        Vector3 outgoingRoot = permissions.MovementAllowed && rawRoot.IsFinite()
            ? semanticFrame.EvaluatedRootDeltaToWorld(rawRoot)
            : Vector3.Zero;
        Quaternion rootRotation = GetRootMotionRotationDelta();
        Quaternion actorLocalRootRotation = rootRotation.IsFinite()
            ? semanticFrame.EvaluatedRootRotationToActorLocal(rootRotation)
            : Quaternion.Identity;
        float outgoingYaw = permissions.RotationAllowed ? actorLocalRootRotation.GetEuler().Y : 0f;
        _lastVectorConvergenceCommitmentModelledRootWorld = outgoingRoot;
        _lastVectorConvergenceCommitmentModelledYaw = outgoingYaw;
        AnimationNodeBlendSpace2D sustainedSpace = ResolveSustainedSpace();
        StandingLocomotionClip exit = ResolveExitClip();
        NPCQueuedEntryFade? queuedEntryFade = ResolveQueuedEntryFade(delta);
        Vector2 currentSustainedBlend = AnimationTree!.Get(_sustainedBlendParameter).AsVector2();
        float firstFutureSustainedPhase = Mathf.PosMod(
            _sustainedCycleCurrentPhase
                + (delta * NPCFiniteTransitionCadence.SourceSecondsPerWallSecond / (float)sustainedSpace.CyclicLength),
            1f);
        var releasePlan = new NPCExitReleasePlan(
            exit.Role,
            LastSelectedReleaseLane,
            LastSelectedReleaseEvent,
            firstFutureSustainedPhase,
            0f);
        BuildMovingIntervals(
            default,
            sustainedSpace,
            releasePlan,
            exit,
            Math.Min(ResolveExitCrossfadeDuration(exit), (float)exit.DurationSeconds),
            delta,
            _sameLaneCandidateMovingIntervals,
            tickBase: 0,
            movingStartPhase: _sustainedCycleCurrentPhase,
            movingBlendPoint: currentSustainedBlend,
            movingBlendAcceleration: _blendCommandAcceleration,
            queuedEntryFade: queuedEntryFade);
        // The crossing frame's already-evaluated root is consumed by the composed move that opens this window, so
        // it is the first owned interval of the moving window itself — exactly as the pre-commit lead-in model
        // treats it. On the direct path that root is the sustained step; on the queued-entry-fade path it is the
        // still-running i2f→travel fade blend. Omitting it leaves one un-owned authored interval and lands the
        // final transform past the endpoint by that root.
        _sameLaneCandidateMovingIntervals.Insert(
            0,
            CreateOutgoingCommitmentInterval(actor, delta));
        NPCReleaseOccurrence occurrence = LastSelectedReleaseOccurrence;
        return _vectorConvergence.TryOpenMoving(
            new Vector2(actor.Origin.X, actor.Origin.Z),
            new Vector2(_route.Endpoint.X, _route.Endpoint.Z),
            actorYaw,
            _route.TerminalYaw,
            occurrence,
            _sameLaneCandidateMovingIntervals,
            CreateFiniteCorrectionConstraints(),
            out _,
            // The crossing frame's StartExit publication composes its authored root without a correction on both
            // open paths, so the executor's consumption begins with the interval after the commitment; the
            // commitment root stays in the plan's authored sum and is realised by that uncorrected composed move.
            firstConsumedIntervalIndex: 1);
    }

    private void FailClosedMovingVectorConvergence()
    {
        _correctionBudget.CancelTerminalReservation();
        ResetFiniteCorrection(NPCVectorConvergenceResetReason.Cancellation);
    }

    private void DiscardImminentRelease(NPCVectorConvergenceResetReason reason)
    {
        _terminalReleaseApproachActive = false;
        _selectedReleaseOccurrence = default;
        LastSelectedReleaseOccurrence = default;
        _correctionBudget.CancelTerminalReservation();
        ResetFiniteCorrection(reason);
        ClearFiniteCorrectionEnvelopes();
    }

    private NPCAnimationFrame ResolveReleaseApproachFrame(
        NPCAnimationFrame frame,
        Transform3D actor,
        NPCNavigationRoutePlan route,
        float delta)
    {
        float rate = Math.Max(frame.PlaybackRate, 0.001f);
        StandingLocomotionClip exit = ResolveExitClip(LastSelectedReleaseLane);
        float actorYaw = NPCNavigationRoutePlan.YawFromDirection(actor.Basis.Orthonormalized() * Vector3.Forward);
        float exitStartYaw = route.TerminalYaw - exit.EndpointYaw;
        float yawError = NPCNavigationRoutePlan.WrapAngle(exitStartYaw - actorYaw);
        // The approach schedules one bounded constant-rate turn so the sustained walk lands the facing on the
        // exit-start pose at the release beat. Freezing yaw here lands the release on the selection-time facing,
        // shifting the consumed window and the final landing by the un-rotated remainder.
        float yawRate = Mathf.Clamp(
            yawError / Math.Max(LastTimeToSelectedReleaseSeconds, delta),
            -NPCTerminalReleaseScheduler.MaximumYawRate,
            NPCTerminalReleaseScheduler.MaximumYawRate);
        return frame with
        {
            BlendPoint = _releaseApproachBlendPoint,
            PlaybackRate = rate,
            DesiredLocalVelocity = _releaseApproachBlendPoint,
            DesiredYawRate = yawRate,
        };
    }

    private void BeginCorrectionScope(NPCResidualCorrectionScope scope)
    {
        NPCResidualCorrectionScope previous = _correctionBudget.Scope;
        _correctionBudget.BeginScope(scope);
        if (previous != _correctionBudget.Scope)
        {
            ResetCorrectionSmoothing();
        }
    }

    private void EnsureCorrectionScope(NPCAnimationStage stage)
    {
        NPCResidualCorrectionScope expected = stage switch
        {
            NPCAnimationStage.Idle or NPCAnimationStage.Entry or NPCAnimationStage.SustainedTravel
                => NPCResidualCorrectionScope.Travel,
            NPCAnimationStage.Exit or NPCAnimationStage.ShortBout or NPCAnimationStage.Pivot or NPCAnimationStage.SideStep
                => NPCResidualCorrectionScope.Exit,
            NPCAnimationStage.Settlement or NPCAnimationStage.EndpointHold or NPCAnimationStage.Complete
                => NPCResidualCorrectionScope.Settlement,
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown NPC animation stage."),
        };
        BeginCorrectionScope(expected);
    }

    private void ResetCorrectionSmoothing()
    {
        _smoothedResidual = Vector2.Zero;
        _residualAcceleration = Vector2.Zero;
        _smoothedYawResidual = 0f;
    }

    private void ResetFiniteCorrection(NPCVectorConvergenceResetReason reason)
    {
        _vectorConvergence.Reset(reason);
        _vectorRequestedCorrection = Vector2.Zero;
        _selectedReleaseOccurrence = default;
        _lastVectorConvergenceCommitmentModelledRootWorld = Vector3.Zero;
        _lastVectorConvergenceCommitmentConsumedRootWorld = Vector3.Zero;
        _lastVectorConvergenceCommitmentModelledYaw = 0f;
        _lastVectorConvergenceCommitmentConsumedYaw = 0f;
        LastVectorConvergenceForecastAuthoredRootWorld = Vector3.Zero;
        LastVectorConvergenceConsumedAuthoredRootWorld = Vector3.Zero;
        LastVectorConvergenceForecastAuthoredYaw = 0f;
        LastVectorConvergenceConsumedAuthoredYaw = 0f;
    }

    private void ResetTerminalApproachState(
        NPCVectorConvergenceResetReason resetReason = NPCVectorConvergenceResetReason.None)
    {
        _terminalReleaseApproachActive = false;
        _selectedReleaseOccurrence = default;
        LastSelectedReleaseOccurrence = default;
        _releaseApproachBlendPoint = Vector2.Zero;
        ResetFiniteCorrection(resetReason);
        ClearFiniteCorrectionEnvelopes();
        _terminalApproachObserved = false;
        _previousTerminalRootAccumulator = Vector3.Zero;
        _previousTerminalActorPosition = Vector3.Zero;
        LastTerminalSignedRemainingDistance = 0f;
        LastTerminalLateralError = 0f;
        LastTerminalRootTangentDisplacement = 0f;
        LastTerminalRealisedTangentDisplacement = 0f;
        LastTerminalResidualTangentDisplacement = 0f;
        LastTerminalOvershot = false;
        LastTerminalTangent = Vector3.Zero;
        LastMovementCollided = false;
        _entryHandoffBlendActive = false;
        _entryHandoffBlendPoint = Vector2.Zero;
        _entryHandoffPlaybackRate = 1f;
        _entryHandoffUnitRootSpeed = 0f;
        _entryHandoffPublicationFrames = 0;
        _blendCommandAcceleration = Vector2.Zero;
        _previousAuthoredYawRate = 0f;
        _previousAppliedYawRate = 0f;
        _smoothedPlaybackRate = 1f;
        _playbackRateAcceleration = 0f;
        _terminalScheduler.Reset();
        LastSelectedExitRole = string.Empty;
        LastSelectedReleaseEvent = string.Empty;
        LastSelectedReleaseLane = NPCAnimationPhaseLane.Left;
        LastTimeToSelectedReleaseSeconds = 0f;
        LastTargetReleasePhase = 0f;
        LastTerminalCommitPhase = 0f;
        LastTerminalCommitPreviousPhase = 0f;
        LastTerminalCommitExpectedAdvance = 0f;
        LastTerminalCommitPlaybackRate = 0f;
        LastTerminalCommitOrdinaryPlaybackRate = 0f;
        LastTerminalCommitRootAccumulator = Vector3.Zero;
        LastShortBoutEntrySourcePosition = 0f;
        LastShortBoutExitSourcePosition = 0f;
        LastShortBoutBlendWeight = 0f;
        LastShortBoutEntryPlaybackRate = 0f;
        LastShortBoutExitPlaybackRate = 0f;
    }

    private Vector3 ExpectedSustainedRootWorldDelta(NPCAnimationFrame frame, Transform3D actor, float delta)
    {
        if (_catalogue is null)
        {
            return Vector3.Zero;
        }

        CalculateSustainedBlendWeights(LastBlendPoint, _sustainedBlendWeights);
        Vector2 expectedSemanticVelocity = ResolveSustainedSemanticVelocity(
            _sustainedCycleCurrentPhase,
            frame.PlaybackRate);
        NPCNavigationSemanticFrame semanticFrame = new(actor.Basis, GetRootMotionReferenceBasis());
        return semanticFrame.ActorSemanticToWorld(expectedSemanticVelocity) * delta;
    }

    private Vector2 ResolveSustainedSemanticVelocity(float phase, float playbackRate)
    {
        NPCSustainedBlendSnapshot snapshot = RequireSustainedBlendSnapshot();
        Vector2 expectedSemanticVelocity = Vector2.Zero;
        for (int index = 0; index < snapshot.Points.Length; index++)
        {
            float weight = _sustainedBlendWeights[index];
            if (weight <= 0f)
            {
                continue;
            }

            NPCSustainedRoleSnapshot role = snapshot.Points[index].ForLane(_programme.PhaseLane);
            double sourceTime = (role.ReleaseEventTime + (phase * role.Clip.DurationSeconds)) % role.Clip.DurationSeconds;
            StandingLocomotionResponseSample response = role.Clip.SampleResponse(sourceTime);
            float commonDurationScale = (float)(role.Clip.DurationSeconds / snapshot.CyclicLength);
            expectedSemanticVelocity += response.PlanarVelocity * commonDurationScale * playbackRate * weight;
        }

        // Catalogue position/velocity values are authored track units. Skeleton3D applies MotionScale to the
        // AnimationTree's evaluated positional Root, so the forecast must cross that same unit boundary exactly once.
        return expectedSemanticVelocity * _rootMotionScale;
    }

    private NPCImportedRootDelta ResolveSustainedImportedRootDelta(
        float previousPhase,
        float currentPhase,
        bool applySkeletonMotionScale = true)
    {
        NPCSustainedBlendSnapshot snapshot = RequireSustainedBlendSnapshot();
        Vector2 root = Vector2.Zero;
        float yaw = 0f;
        for (int index = 0; index < snapshot.Points.Length; index++)
        {
            float weight = _sustainedBlendWeights[index];
            if (weight <= 0f)
            {
                continue;
            }

            NPCSustainedRoleSnapshot role = snapshot.Points[index].ForLane(_programme.PhaseLane);
            double previousTime = (role.ReleaseEventTime + (Mathf.PosMod(previousPhase, 1f) * role.Clip.DurationSeconds))
                % role.Clip.DurationSeconds;
            double currentTime = (role.ReleaseEventTime + (Mathf.PosMod(currentPhase, 1f) * role.Clip.DurationSeconds))
                % role.Clip.DurationSeconds;
            bool wrapped = currentPhase - previousPhase >= 1f
                || currentTime + 0.0000001d < previousTime;
            NPCImportedRootDelta delta = SampleImportedRootDelta(
                snapshot,
                role.RootTrack,
                previousTime,
                currentTime,
                wrapped,
                applySkeletonMotionScale);
            root += delta.Root * weight;
            yaw += delta.Yaw * weight;
        }

        return new(root, yaw);
    }

    private NPCImportedRootDelta SampleImportedRootDelta(
        string role,
        double previousTime,
        double currentTime,
        bool looping,
        bool applySkeletonMotionScale = true)
    {
        NPCSustainedBlendSnapshot snapshot = RequireSustainedBlendSnapshot();
        NPCImportedRootTrack rootTrack = snapshot.FiniteRootTracks.Require(role);
        return SampleImportedRootDelta(
            snapshot,
            in rootTrack,
            previousTime,
            currentTime,
            looping,
            applySkeletonMotionScale);
    }

    private NPCImportedRootDelta SampleImportedRootDelta(
        NPCSustainedBlendSnapshot snapshot,
        in NPCImportedRootTrack rootTrack,
        double previousTime,
        double currentTime,
        bool looping,
        bool applySkeletonMotionScale = true)
    {
        using NPCExactImportedRootEndpointCache<Animation>.Scope scope = _importedRootEndpointCache.BeginScope(snapshot);
        float motionScale = applySkeletonMotionScale ? _rootMotionScale : 1f;
        if (!looping)
        {
            return SampleImportedRootSegment(
                snapshot,
                rootTrack.Animation,
                rootTrack.PositionTrack,
                rootTrack.RotationTrack,
                previousTime,
                currentTime,
                motionScale);
        }
        NPCImportedRootDelta tail = SampleImportedRootSegment(
            snapshot,
            rootTrack.Animation,
            rootTrack.PositionTrack,
            rootTrack.RotationTrack,
            previousTime,
            rootTrack.Animation.Length,
            motionScale);
        NPCImportedRootDelta head = SampleImportedRootSegment(
            snapshot,
            rootTrack.Animation,
            rootTrack.PositionTrack,
            rootTrack.RotationTrack,
            0d,
            currentTime,
            motionScale);
        return new(tail.Root + head.Root, NPCNavigationRoutePlan.WrapAngle(tail.Yaw + head.Yaw));
    }

    private NPCImportedRootDelta SampleImportedRootSegment(
        NPCSustainedBlendSnapshot snapshot,
        Animation animation,
        int positionTrack,
        int rotationTrack,
        double previousTime,
        double currentTime,
        float motionScale)
    {
        NPCImportedRootEndpoint previous = SampleImportedRootEndpoint(
            snapshot,
            animation,
            positionTrack,
            rotationTrack,
            NPCExactImportedRootEndpointSampler.ClampSourceTime(animation, previousTime));
        NPCImportedRootEndpoint current = SampleImportedRootEndpoint(
            snapshot,
            animation,
            positionTrack,
            rotationTrack,
            NPCExactImportedRootEndpointSampler.ClampSourceTime(animation, currentTime));
        return NPCExactImportedRootEndpointSampler.Compose(in previous, in current, motionScale);
    }

    private NPCImportedRootEndpoint SampleImportedRootEndpoint(
        NPCSustainedBlendSnapshot snapshot,
        Animation animation,
        int positionTrack,
        int rotationTrack,
        float clampedSourceTime)
    {
        if (_importedRootEndpointCache.TryGet(
                snapshot,
                animation,
                positionTrack,
                rotationTrack,
                clampedSourceTime,
                out NPCImportedRootEndpoint cached))
        {
            return cached;
        }

        NPCImportedRootEndpoint sampled = NPCExactImportedRootEndpointSampler.Sample(
            animation,
            positionTrack,
            rotationTrack,
            clampedSourceTime);
        _importedRootEndpointCache.Store(
            snapshot,
            animation,
            positionTrack,
            rotationTrack,
            clampedSourceTime,
            in sampled);
        return sampled;
    }

    private sealed class NPCSustainedBlendSnapshot(
        AnimationNodeBlendSpace2D space,
        float cyclicLength,
        NPCSustainedBlendPointSnapshot[] points,
        int[] triangleIndices,
        NPCFiniteRoleSamplingSnapshot<NPCImportedRootTrack> finiteRootTracks,
        ulong projectionIdentity)
    {
        public AnimationNodeBlendSpace2D Space => space;
        public float CyclicLength => cyclicLength;
        public NPCSustainedBlendPointSnapshot[] Points => points;
        public int[] TriangleIndices => triangleIndices;
        public NPCFiniteRoleSamplingSnapshot<NPCImportedRootTrack> FiniteRootTracks => finiteRootTracks;
        public ulong ProjectionIdentity => projectionIdentity;
    }

    private readonly record struct NPCSustainedBlendPointSnapshot(
        string Name,
        Vector2 Position,
        NPCSustainedRoleSnapshot Left,
        NPCSustainedRoleSnapshot Right)
    {
        public NPCSustainedRoleSnapshot ForLane(NPCAnimationPhaseLane lane)
            => lane == NPCAnimationPhaseLane.Right ? Right : Left;
    }

    private readonly record struct NPCSustainedRoleSnapshot(
        StandingLocomotionClip Clip,
        double ReleaseEventTime,
        NPCImportedRootTrack RootTrack);

    private readonly record struct NPCImportedRootTrack(
        Animation Animation,
        int PositionTrack,
        int RotationTrack);

    private readonly record struct NPCQueuedEntryFade(
        string OutgoingRole,
        double OutgoingPlayPosition,
        double OutgoingDurationSeconds,
        float CurrentFadeElapsedSeconds,
        float FadeDurationSeconds,
        int FutureIntervalCount);

    private float ResolveSustainedYawRate(float phase, float playbackRate)
    {
        NPCSustainedBlendSnapshot snapshot = RequireSustainedBlendSnapshot();
        float expectedYawRate = 0f;
        for (int index = 0; index < snapshot.Points.Length; index++)
        {
            float weight = _sustainedBlendWeights[index];
            if (weight <= 0f)
            {
                continue;
            }

            NPCSustainedRoleSnapshot role = snapshot.Points[index].ForLane(_programme.PhaseLane);
            double sourceTime = (role.ReleaseEventTime + (phase * role.Clip.DurationSeconds)) % role.Clip.DurationSeconds;
            float commonDurationScale = (float)(role.Clip.DurationSeconds / snapshot.CyclicLength);
            expectedYawRate += role.Clip.SampleResponse(sourceTime).YawRate * commonDurationScale * playbackRate * weight;
        }
        return expectedYawRate;
    }

    private AnimationNodeBlendSpace2D ResolveSustainedSpace()
    {
        AnimationNodeBlendTree root = AnimationTree!.TreeRoot as AnimationNodeBlendTree
            ?? throw new InvalidOperationException("NPC AnimationTree root must be a blend tree.");
        AnimationNodeStateMachine states = root.GetNode("States") as AnimationNodeStateMachine
            ?? throw new InvalidOperationException("NPC AnimationTree must expose States.");
        AnimationNodeBlendTree travel = states.GetNode("NPCTravel") as AnimationNodeBlendTree
            ?? throw new InvalidOperationException("NPC AnimationTree must expose NPCTravel.");
        return travel.GetNode("Sustained") as AnimationNodeBlendSpace2D
            ?? throw new InvalidOperationException("NPCTravel must expose one Sustained blend space.");
    }

    private Vector2 ResolveSustainedPointPosition(StandingLocomotionCapability capability)
    {
        NPCSustainedBlendSnapshot snapshot = RequireSustainedBlendSnapshot();
        for (int index = 0; index < snapshot.Points.Length; index++)
        {
            if (snapshot.Points[index].Left.Clip.Capability == capability
                || snapshot.Points[index].Right.Clip.Capability == capability)
            {
                return snapshot.Points[index].Position;
            }
        }

        throw new InvalidOperationException($"NPC sustained blend space is missing capability '{capability}'.");
    }

    private float ResolveEntryHandoffPlaybackRate(float delta)
    {
        float entrySpeed = new Vector2(LastAuthoredRootWorldDelta.X, LastAuthoredRootWorldDelta.Z).Length()
            / Math.Max(delta, float.Epsilon);
        float sustainedSpeed = _entryHandoffUnitRootSpeed;
        if (sustainedSpeed <= 0.0001f)
        {
            return 1f;
        }

        float matchedRate = entrySpeed / sustainedSpeed;
        float boundedDeparture = 1f - Mathf.Clamp(1f - matchedRate, 0f, 0.2f);
        return Mathf.Clamp(boundedDeparture, 0.8f, 1.2f);
    }

    /// <summary>
    /// Analytically resolves the sustained handoff sample's unit root speed (m/s at playback rate 1) from its
    /// catalogue response, never touching the live mixer: the tree owns its own automatic physics evaluation, so a
    /// measurement must leave no mixer state mutated. The handoff blend point sits exactly on one sustained sample
    /// (blend weight 1), so the probe this replaces — which reset playback and advanced one tick — reduces to that
    /// sample's clip response. The window is one full cycle instead of one tick: the packaged response is
    /// downsampled, so a one-tick difference returns the piecewise segment slope at the seam-aligned start
    /// (gait-phase dependent), while the looping clip's full-cycle displacement is exact. Because the clip loops,
    /// the displacement over any full cycle equals the whole-clip endpoint displacement regardless of the seam
    /// anchor, one cycle spans exactly one <see cref="AnimationNodeBlendSpace2D.CyclicLength" /> timeline window at
    /// rate 1, and the yaw-only reference-frame rotation the probe applied preserves the planar magnitude of the
    /// clip-local cumulative displacement.
    /// </summary>
    private float MeasureSustainedHandoffUnitRootSpeed(Vector2 blendPoint)
    {
        NPCSustainedBlendSnapshot snapshot = RequireSustainedBlendSnapshot();
        StandingLocomotionClip clip = ResolveSustainedPointRole(snapshot, blendPoint, _programme.PhaseLane).Clip;
        Vector2 cycleStart = clip.SampleResponse(0d).CumulativeLocalPlanar;
        Vector2 cycleEnd = clip.SampleResponse(clip.DurationSeconds).CumulativeLocalPlanar;
        return (cycleEnd - cycleStart).Length() / snapshot.CyclicLength;
    }

    /// <summary>
    /// Resolves the catalogue role of the sustained sample the handoff blends on: the nearest blend point to the
    /// handoff position, using the same point-name-to-role convention as the seam alignment and the sustained
    /// response forecasting.
    /// </summary>
    private static NPCSustainedRoleSnapshot ResolveSustainedPointRole(
        NPCSustainedBlendSnapshot snapshot,
        Vector2 blendPoint,
        NPCAnimationPhaseLane lane)
    {
        int nearest = 0;
        float nearestDistance = float.PositiveInfinity;
        for (int index = 0; index < snapshot.Points.Length; index++)
        {
            float distance = blendPoint.DistanceSquaredTo(snapshot.Points[index].Position);
            if (distance < nearestDistance)
            {
                nearest = index;
                nearestDistance = distance;
            }
        }

        return snapshot.Points[nearest].ForLane(lane);
    }

    private void RebuildSustainedBlendDomain()
    {
        ClearSustainedSamplingSnapshot();
        AnimationNodeBlendSpace2D space = ResolveSustainedSpace();
        var points = new Vector2[space.GetBlendPointCount()];
        for (int point = 0; point < points.Length; point++)
        {
            points[point] = space.GetBlendPointPosition(point);
        }

        int[] sourceTriangles = new int[space.GetTriangleCount() * 3];
        for (int triangle = 0; triangle < space.GetTriangleCount(); triangle++)
        {
            sourceTriangles[(triangle * 3) + 0] = space.GetTrianglePoint(triangle, 0);
            sourceTriangles[(triangle * 3) + 1] = space.GetTrianglePoint(triangle, 1);
            sourceTriangles[(triangle * 3) + 2] = space.GetTrianglePoint(triangle, 2);
        }
        int[] domainTriangles = sourceTriangles;
        if (domainTriangles.Length == 0 && space.AutoTriangles)
        {
            domainTriangles = Geometry2D.TriangulateDelaunay(points);
        }
        _sustainedBlendDomain = new(points, domainTriangles);
        _sustainedEngineTriangleIndices = domainTriangles;
        // Weight interpolation deliberately uses the authored triangle list. When a resource relies on auto
        // triangulation the historical sampler falls back to its nearest point, while the domain alone uses the
        // generated topology for projection.
        _sustainedBlendSnapshot = CreateSustainedBlendSnapshot(space, points, sourceTriangles);
    }

    private void CalculateSustainedBlendWeights(Vector2 point, float[] weights)
        => CalculateSustainedBlendWeights(point, weights, RequireSustainedBlendSnapshot().TriangleIndices);

    private void CalculateSustainedBlendWeights(Vector2 point, float[] weights, ReadOnlySpan<int> triangleIndices)
    {
        NPCSustainedBlendSnapshot snapshot = RequireSustainedBlendSnapshot();
        Array.Clear(weights);
        for (int triangle = 0; triangle < triangleIndices.Length; triangle += 3)
        {
            int a = triangleIndices[triangle];
            int b = triangleIndices[triangle + 1];
            int c = triangleIndices[triangle + 2];
            Vector3 barycentric = Barycentric(
                point,
                snapshot.Points[a].Position,
                snapshot.Points[b].Position,
                snapshot.Points[c].Position);
            if (barycentric.X < -0.0001f || barycentric.Y < -0.0001f || barycentric.Z < -0.0001f)
            {
                continue;
            }

            weights[a] = Math.Max(barycentric.X, 0f);
            weights[b] = Math.Max(barycentric.Y, 0f);
            weights[c] = Math.Max(barycentric.Z, 0f);
            return;
        }

        int nearest = 0;
        float nearestDistance = float.PositiveInfinity;
        for (int index = 0; index < snapshot.Points.Length; index++)
        {
            float distance = point.DistanceSquaredTo(snapshot.Points[index].Position);
            if (distance < nearestDistance)
            {
                nearest = index;
                nearestDistance = distance;
            }
        }

        weights[nearest] = 1f;
    }

    private NPCSustainedBlendSnapshot RequireSustainedBlendSnapshot()
        => _sustainedBlendSnapshot
            ?? throw new InvalidOperationException("NPC sustained blend snapshot must be initialised before forecasting.");

    private NPCSustainedBlendSnapshot CreateSustainedBlendSnapshot(
        AnimationNodeBlendSpace2D space,
        Vector2[] positions,
        int[] triangleIndices)
    {
        StandingLocomotionCatalogue catalogue = _catalogue
            ?? throw new InvalidOperationException("NPC animation catalogue must be loaded before sustained snapshot creation.");
        AnimationPlayer player = ResolveAnimationPlayer();
        NodePath rootTrackPath = AnimationTree!.RootMotionTrack;
        var points = new NPCSustainedBlendPointSnapshot[positions.Length];
        for (int index = 0; index < points.Length; index++)
        {
            string point = space.GetBlendPointName(index).ToString();
            AnimationNodeAnimation sample = space.GetBlendPointNode(index) as AnimationNodeAnimation
                ?? throw new InvalidOperationException($"Sustained sample '{point}' must be an AnimationNodeAnimation.");
            (StandingLocomotionCapability left, StandingLocomotionCapability right) =
                SustainedCapabilityPair(ResolveAnimationCapability(sample, catalogue));
            points[index] = new(
                point,
                positions[index],
                CreateSustainedRoleSnapshot(left, StandingLocomotionAnatomicalSide.Left, catalogue, player, rootTrackPath),
                CreateSustainedRoleSnapshot(right, StandingLocomotionAnatomicalSide.Right, catalogue, player, rootTrackPath));
        }

        return new(
            space,
            (float)space.CyclicLength,
            points,
            triangleIndices,
            CreateFiniteRootTrackSnapshot(catalogue, player, rootTrackPath),
            NextSustainedSamplingSnapshotIdentity());
    }

    private ulong NextSustainedSamplingSnapshotIdentity()
    {
        unchecked
        {
            _sustainedSamplingSnapshotGeneration++;
            if (_sustainedSamplingSnapshotGeneration == 0)
            {
                _sustainedSamplingSnapshotGeneration++;
            }
        }

        return _sustainedSamplingSnapshotGeneration;
    }

    private static NPCSustainedRoleSnapshot CreateSustainedRoleSnapshot(
        StandingLocomotionCapability capability,
        StandingLocomotionAnatomicalSide anatomicalSide,
        StandingLocomotionCatalogue catalogue,
        AnimationPlayer player,
        NodePath rootTrackPath)
    {
        StandingLocomotionClip clip = catalogue[capability];
        return new(
            clip,
            catalogue.ResolvePhaseEvent(clip.Role, anatomicalSide, StandingLocomotionBoundaryContext.Cyclic).TimeSeconds,
            CreateImportedRootTrack(clip, player, rootTrackPath));
    }

    private static (StandingLocomotionCapability Left, StandingLocomotionCapability Right) SustainedCapabilityPair(
        StandingLocomotionCapability capability) => capability switch
        {
            StandingLocomotionCapability.CyclicForwardPrimary => (capability, capability),
            StandingLocomotionCapability.CyclicBackwardPrimary => (capability, capability),
            StandingLocomotionCapability.CyclicLateralLeftLeftLane or StandingLocomotionCapability.CyclicLateralLeftRightLane =>
                (StandingLocomotionCapability.CyclicLateralLeftLeftLane, StandingLocomotionCapability.CyclicLateralLeftRightLane),
            StandingLocomotionCapability.CyclicLateralRightLeftLane or StandingLocomotionCapability.CyclicLateralRightRightLane =>
                (StandingLocomotionCapability.CyclicLateralRightLeftLane, StandingLocomotionCapability.CyclicLateralRightRightLane),
            StandingLocomotionCapability.CyclicArcLeftLeftLane or StandingLocomotionCapability.CyclicArcLeftRightLane =>
                (StandingLocomotionCapability.CyclicArcLeftLeftLane, StandingLocomotionCapability.CyclicArcLeftRightLane),
            StandingLocomotionCapability.CyclicArcRightLeftLane or StandingLocomotionCapability.CyclicArcRightRightLane =>
                (StandingLocomotionCapability.CyclicArcRightLeftLane, StandingLocomotionCapability.CyclicArcRightRightLane),
            _ => throw new InvalidOperationException($"Capability '{capability}' is not a sustained blend sample."),
        };

    private static StandingLocomotionCapability ResolveAnimationCapability(
        AnimationNodeAnimation sample,
        StandingLocomotionCatalogue catalogue)
    {
        string animation = sample.Animation.ToString();
        int separator = animation.LastIndexOf('/');
        if (separator < 0
            || !catalogue.TryGetClipByIdentity(animation[(separator + 1)..], out StandingLocomotionClip? clip))
        {
            throw new InvalidOperationException($"Sustained animation '{animation}' is not declared by the package.");
        }

        return clip!.Capability;
    }

    private static NPCFiniteRoleSamplingSnapshot<NPCImportedRootTrack> CreateFiniteRootTrackSnapshot(
        StandingLocomotionCatalogue catalogue,
        AnimationPlayer player,
        NodePath rootTrackPath)
    {
        ImmutableDictionary<string, NPCImportedRootTrack>.Builder tracks =
            ImmutableDictionary.CreateBuilder<string, NPCImportedRootTrack>(StringComparer.Ordinal);
        foreach (StandingLocomotionClip clip in catalogue.Capabilities.Values
                     .Where(candidate => candidate.BoundaryKind == StandingLocomotionBoundaryKind.Transition))
        {
            tracks.Add(clip.Role, CreateImportedRootTrack(clip, player, rootTrackPath));
        }

        return new(tracks.ToImmutable());
    }

    /// <summary>
    /// Resolves a navigation role through the loaded package declaration rather than assuming a particular
    /// published package naming convention. Production keeps its existing names because the production catalogue
    /// declares them; fixtures can provide their own declared identities.
    /// </summary>
    private StringName LocomotionAnimationName(string role) => new($"locomotion/{_catalogue![role].Identity}");

    private StringName LocomotionAnimationName(StandingLocomotionCapability capability)
        => new($"locomotion/{_catalogue![capability].Identity}");

    private StringName LocomotionAnimationLocalName(string role) => new(_catalogue![role].Identity);

    private static NPCImportedRootTrack CreateImportedRootTrack(
        StandingLocomotionClip clip,
        AnimationPlayer player,
        NodePath rootTrackPath)
    {
        Animation animation = player.GetAnimation(new StringName($"locomotion/{clip.Identity}"));
        int positionTrack = animation.FindTrack(rootTrackPath, Animation.TrackType.Position3D);
        if (positionTrack < 0)
        {
            throw new InvalidOperationException($"NPC vector forecast requires Root position track for '{clip.Role}'.");
        }

        return new(animation, positionTrack, animation.FindTrack(rootTrackPath, Animation.TrackType.Rotation3D));
    }

    private void ReleaseRuntimeSamplingSnapshot()
    {
        Neutralise();
        ClearSustainedSamplingSnapshot();
    }

    private void ClearSustainedSamplingSnapshot()
    {
        ResetFarRouteStoppingState();
        _importedRootEndpointCache.Clear();
        _sustainedBlendSnapshot = null;
        _sustainedIntervalSamplerSpace = null;
        _sustainedIntervalSampler = null;
    }

    private static Vector3 Barycentric(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
    {
        float denominator = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));
        if (Mathf.IsZeroApprox(denominator))
        {
            return new Vector3(-1f, -1f, -1f);
        }

        float wa = (((b.Y - c.Y) * (point.X - c.X)) + ((c.X - b.X) * (point.Y - c.Y))) / denominator;
        float wb = (((c.Y - a.Y) * (point.X - c.X)) + ((a.X - c.X) * (point.Y - c.Y))) / denominator;
        return new Vector3(wa, wb, 1f - wa - wb);
    }

    private void FinaliseShortBoutFailure()
    {
        if (_shortBoutFailureFinalised)
        {
            return;
        }

        _shortBoutFailureFinalised = true;
        LastShortBoutTerminalOutcome = LastShortBoutInvalidationReason == NPCShortBoutInvalidationReason.RealisedDivergence
            ? NPCShortBoutTerminalOutcome.FailedDivergence
            : LastShortBoutInvalidationReason == NPCShortBoutInvalidationReason.ExitEndpointResidual
                ? NPCShortBoutTerminalOutcome.FailedEndpoint
                : LastShortBoutInvalidationReason == NPCShortBoutInvalidationReason.PlaybackWatchdog
                    ? NPCShortBoutTerminalOutcome.FailedPlayback
                    : NPCShortBoutTerminalOutcome.FailedCollision;
        LastShortBoutTerminalFailureCount++;
        LastShortBoutRealisedShortfall = Math.Max(
            LastShortBoutRequestedDirectLength - LastShortBoutCumulativeRealisedDisplacement,
            0f);
        if (HasDestination)
        {
            bool collisionFailure = LastShortBoutTerminalOutcome == NPCShortBoutTerminalOutcome.FailedCollision;
            if (collisionFailure)
            {
                var obstruction = new NavigationObstructionDetails("short-bout-collision", null, 0);
                _ = TryCommitBlockedOutcome(
                    CreateCurrentTerminalContext(NavigationTerminalReason.Obstruction, obstruction),
                    Engine.GetPhysicsFrames());
            }
            else
            {
                NavigationTerminalReason reason = LastShortBoutTerminalOutcome == NPCShortBoutTerminalOutcome.FailedEndpoint
                    ? NavigationTerminalReason.ConvergenceFailed
                    : NavigationTerminalReason.ContractFailure;
                _ = TryCommitFailedOutcome(CreateCurrentTerminalContext(reason), Engine.GetPhysicsFrames());
            }
        }
        Neutralise();
    }

    private void Neutralise()
    {
        if (Actor is not null && IsInstanceValid(Actor))
        {
            Vector3 velocity = Actor.Velocity;
            Actor.Velocity = new Vector3(0f, velocity.Y, 0f);
        }
        LastPlanarResidual = Vector2.Zero;
        LastYawResidual = 0f;
        _lastRealisedDisplacement = Vector3.Zero;
        LastAuthoredRootWorldDelta = Vector3.Zero;
        LastRequestedSyntheticWorldDelta = Vector3.Zero;
        LastAuthoredRootLongitudinal = 0f;
        LastRequestedSyntheticLongitudinal = 0f;
        LastRealisedLongitudinal = 0f;
        LastRealisedRouteLongitudinal = 0f;
        LastObjectiveReversalConfirmed = false;
        if (!_terminalMotionBoundaryClosed)
        {
            PublishZeroMovementDiagnosticFrame();
        }
    }

    private StandingLocomotionClip ResolveExitClip() => ResolveExitClip(_programme.PhaseLane);

    private void ResolveExitCrossfadeDurations()
    {
        AnimationNodeBlendTree root = AnimationTree!.TreeRoot as AnimationNodeBlendTree
            ?? throw new InvalidOperationException("NPC AnimationTree root must be a blend tree.");
        AnimationNodeStateMachine states = root.GetNode("States") as AnimationNodeStateMachine
            ?? throw new InvalidOperationException("NPC AnimationTree must expose States.");
        _forwardLeftExitCrossfadeDuration = ResolveLinearExitCrossfadeDuration(states, "NPCF2IL");
        _forwardRightExitCrossfadeDuration = ResolveLinearExitCrossfadeDuration(states, "NPCF2IR");
        _backwardLeftExitCrossfadeDuration = ResolveLinearExitCrossfadeDuration(states, "NPCB2IL");
        _backwardRightExitCrossfadeDuration = ResolveLinearExitCrossfadeDuration(states, "NPCB2IR");
    }

    private static float ResolveLinearExitCrossfadeDuration(AnimationNodeStateMachine states, StringName target)
    {
        for (int index = 0; index < states.GetTransitionCount(); index++)
        {
            if (states.GetTransitionFrom(index) != _travelState
                || states.GetTransitionTo(index) != target)
            {
                continue;
            }

            AnimationNodeStateMachineTransition transition = states.GetTransition(index);
            return transition.XfadeCurve is null
                ? transition.XfadeTime
                : throw new InvalidOperationException(
                    $"NPCTravel to {target} must use the documented linear crossfade required by the Root forecast.");
        }

        throw new InvalidOperationException($"NPC AnimationTree is missing the NPCTravel to {target} transition.");
    }

    private float ResolveLinearCrossfadeDuration(StringName from, StringName target)
    {
        AnimationNodeBlendTree root = AnimationTree!.TreeRoot as AnimationNodeBlendTree
            ?? throw new InvalidOperationException("NPC AnimationTree root must be a blend tree.");
        AnimationNodeStateMachine states = root.GetNode("States") as AnimationNodeStateMachine
            ?? throw new InvalidOperationException("NPC AnimationTree must expose States.");
        for (int index = 0; index < states.GetTransitionCount(); index++)
        {
            if (states.GetTransitionFrom(index) != from || states.GetTransitionTo(index) != target)
            {
                continue;
            }

            AnimationNodeStateMachineTransition transition = states.GetTransition(index);
            if (transition.XfadeCurve is not null)
            {
                throw new InvalidOperationException(
                    $"{from} to {target} must use a linear crossfade for deterministic continuity scheduling.");
            }

            return transition.XfadeTime;
        }

        throw new InvalidOperationException($"NPC AnimationTree is missing the {from} to {target} transition.");
    }

    private float ResolveExitCrossfadeDuration(StandingLocomotionClip clip) => clip.Capability switch
    {
        StandingLocomotionCapability.ExitForwardLeft => _forwardLeftExitCrossfadeDuration,
        StandingLocomotionCapability.ExitForwardRight => _forwardRightExitCrossfadeDuration,
        StandingLocomotionCapability.ExitBackwardLeft => _backwardLeftExitCrossfadeDuration,
        StandingLocomotionCapability.ExitBackwardRight => _backwardRightExitCrossfadeDuration,
        _ => throw new InvalidOperationException($"Clip '{clip.Role}' does not declare an NPC exit capability."),
    };

    private StandingLocomotionClip ResolveExitClip(NPCAnimationPhaseLane lane)
    {
        if (_catalogue is null)
        {
            throw new InvalidOperationException("NPC animation catalogue must be loaded before exit forecasting.");
        }

        StandingLocomotionAnatomicalSide side = lane == NPCAnimationPhaseLane.Right
            ? StandingLocomotionAnatomicalSide.Right
            : StandingLocomotionAnatomicalSide.Left;
        return _catalogue[StandingLocomotionCapabilities.Exit(_programme.IsBackwardBout, side)];
    }

    private StandingLocomotionPhaseEvent ResolveSustainedPhaseEvent(string role, NPCAnimationPhaseLane lane)
    {
        if (_catalogue is null)
        {
            throw new InvalidOperationException("NPC animation catalogue must be loaded before phase-event resolution.");
        }

        return _catalogue.ResolvePhaseEvent(
            role,
            lane == NPCAnimationPhaseLane.Right
                ? StandingLocomotionAnatomicalSide.Right
                : StandingLocomotionAnatomicalSide.Left,
            StandingLocomotionBoundaryContext.Cyclic);
    }

    private StandingLocomotionBoundaryMapping ResolveExitBoundaryMapping(NPCAnimationPhaseLane lane)
    {
        if (_catalogue is null)
        {
            throw new InvalidOperationException("NPC animation catalogue must be loaded before exit-event resolution.");
        }

        string sustainedRole = _catalogue[StandingLocomotionCapabilities.Travel(_programme.IsBackwardBout)].Role;
        return _catalogue.ResolveBoundaryMapping(
            sustainedRole,
            ResolveExitClip(lane).Role,
            lane == NPCAnimationPhaseLane.Right
                ? StandingLocomotionAnatomicalSide.Right
                : StandingLocomotionAnatomicalSide.Left);
    }

    private void ValidateBoundaryMapping(NPCAnimationEvent programmeEvent)
    {
        if (_catalogue is null || programmeEvent is not (NPCAnimationEvent.StartEntry or NPCAnimationEvent.StartExit))
        {
            return;
        }

        StandingLocomotionAnatomicalSide anatomicalSide = _programme.PhaseLane == NPCAnimationPhaseLane.Right
            ? StandingLocomotionAnatomicalSide.Right
            : StandingLocomotionAnatomicalSide.Left;
        string sustained = _catalogue[StandingLocomotionCapabilities.Travel(_programme.IsBackwardBout)].Role;
        StandingLocomotionCapability transitionCapability = programmeEvent == NPCAnimationEvent.StartEntry
            ? StandingLocomotionCapabilities.Entry(_programme.IsBackwardBout, anatomicalSide)
            : StandingLocomotionCapabilities.Exit(_programme.IsBackwardBout, anatomicalSide);
        string transition = _catalogue[transitionCapability].Role;
        string from = programmeEvent == NPCAnimationEvent.StartEntry ? transition : sustained;
        string to = programmeEvent == NPCAnimationEvent.StartEntry ? sustained : transition;
        _ = _catalogue.ResolveBoundaryMapping(from, to, anatomicalSide);
    }

    private void AlignUnsuffixedSamplesToSelectedSeam()
    {
        if (_catalogue is null)
        {
            return;
        }

        StandingLocomotionAnatomicalSide side = _programme.PhaseLane == NPCAnimationPhaseLane.Right
            ? StandingLocomotionAnatomicalSide.Right
            : StandingLocomotionAnatomicalSide.Left;
        AnimationNodeBlendTree root = AnimationTree!.TreeRoot as AnimationNodeBlendTree
            ?? throw new InvalidOperationException("NPC AnimationTree root must be a blend tree.");
        AnimationNodeStateMachine states = root.GetNode("States") as AnimationNodeStateMachine
            ?? throw new InvalidOperationException("NPC AnimationTree must expose States.");
        AnimationNodeBlendTree travel = states.GetNode("NPCTravel") as AnimationNodeBlendTree
            ?? throw new InvalidOperationException("NPC AnimationTree must expose NPCTravel.");
        AnimationNodeBlendSpace2D sustained = travel.GetNode("Sustained") as AnimationNodeBlendSpace2D
            ?? throw new InvalidOperationException("NPCTravel must expose one Sustained blend space.");
        for (int index = 0; index < sustained.GetBlendPointCount(); index++)
        {
            string name = sustained.GetBlendPointName(index).ToString();
            AnimationNodeAnimation sample = sustained.GetBlendPointNode(index) as AnimationNodeAnimation
                ?? throw new InvalidOperationException($"Sustained sample '{name}' must be an AnimationNodeAnimation.");
            (StandingLocomotionCapability left, StandingLocomotionCapability right) =
                SustainedCapabilityPair(ResolveAnimationCapability(sample, _catalogue));
            StandingLocomotionCapability capability = _programme.PhaseLane == NPCAnimationPhaseLane.Right ? right : left;
            StandingLocomotionClip clip = _catalogue[capability];
            sample.Animation = new StringName($"locomotion/{clip.Identity}");
            // NAV-001 cadence re-anchor (UR19/TR15, user-approved Option A): the unified blend space's common
            // cycle is re-anchored from the 1.8333 s slowest-family artefact to the 1.0667 s walk-family cycle
            // the player's four blend spaces use on the same packaged clips. wf/wb now play at their authored
            // cadence (0.9273 m/s per rate unit) instead of 58% slow-motion. Accepted trade-off: the lateral
            // and arc families play fast at the blend extremes (xl/xr 1.62x, al 1.34x source rate) — they are
            // rarely sustained on real routes and the single unified space keeps every family phase-coherent
            // on one shared clock. Enforcing the per-sample timeline here keeps the runtime self-consistent
            // with the graph resource's cyclic length.
            sample.UseCustomTimeline = true;
            sample.TimelineLength = (float)sustained.CyclicLength;
            sample.StretchTimeScale = true;
            sample.LoopMode = Animation.LoopModeEnum.Linear;
            // Godot 4.7 applies start_offset before multiplying custom-timeline time by
            // source_duration / timeline_length. Scale the source event into timeline units so shared phase zero
            // evaluates exactly at the packaged event: (offset * source/common) == event.
            StandingLocomotionPhaseEvent phaseEvent = _catalogue.ResolvePhaseEvent(
                clip.Role,
                side,
                StandingLocomotionBoundaryContext.Cyclic);
            sample.StartOffset = phaseEvent.TimeSeconds * sustained.CyclicLength / clip.DurationSeconds;
            sustained.SetBlendPointPosition(index, _catalogue.GetCommonDurationResponse(clip.Role, sustained.CyclicLength));
        }
    }

    private static Vector3 ToWorld3(Vector2 value) => new(value.X, 0f, value.Y);

    private readonly record struct NPCExitArmCandidate(
        StandingLocomotionClip Exit,
        NPCExitReleasePlan ReleasePlan,
        float YawResidual,
        Vector2 BlendPoint,
        bool VelocityCompatible,
        bool WouldOvershootTerminal,
        float RouteRelativeOvershootMetres,
        NPCCommandedReleaseProjectionRequest ProjectionRequest,
        NPCCommandedReleaseProjectionResult Projection,
        NPCReleaseOccurrence Occurrence,
        bool VectorConvergenceFeasible,
        bool RejectedCorrectionBeyondCapacity,
        NPCVectorConvergenceResult VectorConvergence,
        IReadOnlyList<NPCCommandedReleaseProjectionInterval> ProjectionIntervals);
}
