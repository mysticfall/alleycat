using System.Collections.Immutable;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core.Logging;
using AlleyCat.Navigation.Arrival;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Navigation;

/// <summary>Owns NPC requests, aligned arrival playback, and the single collision-realised movement per tick.</summary>
[GlobalClass]
public partial class NPCNavigation : NavigationBase
{
    private const string SeamTolerancePath = "res://assets/characters/templates/navigation/seam_tolerance_sad.tres";
    private static readonly StringName _playbackParameter = new("parameters/States/playback");
    private static readonly StringName _statesNode = new("States");
    private readonly Dictionary<string, StringName> _stateOf = new(StringComparer.Ordinal);
    private readonly Dictionary<StringName, string> _roleOf = [];

    /// <summary>The pivot-continuation alias states this rig's tree wires, validated at bind time to be exactly
    /// <see cref="NPCAnimationProgramme.PivotContinuationStates" />: they are the authored-graph realisation of the
    /// planner set's crossfaded same-role pivot re-entry (see ActivateRuntimeBinding).</summary>
    private StringName[] _pivotContinuationAliases = [];
    private readonly List<NPCNavigationMovementSample> _movementSamples = new(4096);
    private ArrivalPlanner? _planner;
    private ArrivalPlan? _plan;
    private SequencePrediction? _prediction;
    private SwingCorrectionAllocator? _allocator;
    private Skeleton3D? _skeleton;
    private AnimationGraphPlantedStateSource? _plantedSource;
    private NPCNavigationRoutePlan? _route;
    private readonly NPCNavigationRouteConfiguration _routeConfiguration = new();
    private float _routeDistance;
    private int _sectionIndex;
    private int _holdingSections;
    private int _degenerateSections;

    /// <summary>The consecutive zero-tick section adoptions tolerated before the degenerate winner is treated
    /// as a failed leg (see TryBeginSection): one full loop of the current travel clip (76 physics ticks for
    /// the walk/arc cycles) plus margin — past that, every aligned seam has recurred and identical retries
    /// cannot change the outcome.</summary>
    private const int MaxDegenerateSectionAdoptions = 90;
    private bool _redirectPending;
    private string? _pendingRouteFailure;
    private int _leftToes;
    private int _rightToes;
    private int _tick;
    private int _nextSegment;
    private int _windowIndex;
    private bool _awaitingPlan;
    private bool _bindingFailureLatched;
    private bool _committingOutcome;

    /// <summary>Whether any live support phase has been observed since the runtime binding was activated.</summary>
    private bool _phaseObserved;

    /// <summary>The most recently observed live support phase (identity of the ongoing physical phase).</summary>
    private SupportPhase _livePhase;

    /// <summary>
    /// The committed allowance of the ongoing physical support phase (CORNER-03): an immutable value charged at the
    /// movement boundary and never reset by plan adoption, section handoffs, replans or request supersession — only
    /// a genuine live phase change replaces it (with a fresh grant once a covering window establishes the basis) and
    /// only a runtime-binding reset clears the history.
    /// </summary>
    private SwingCorrectionPhaseBudget? _phaseBudget;

    /// <summary>Most recent movement decompositions kept for diagnostics; oldest entries are dropped beyond it.</summary>
    private const int MaximumMovementSamples = 8192;

    /// <inheritdoc/>
    public NPCNavigation()
    {
        PathDesiredDistance = 0.05f;
        DestinationReachedDistance = 0.05f;
        ProcessPhysicsPriority = -100;
    }

    /// <inheritdoc/>
    [ExportGroup("Required Binding")]
    [Export]
    public CharacterBody3D? Actor
    {
        get; set;
    }

    /// <inheritdoc/>
    [Export]
    public AnimationTree? AnimationTree
    {
        get; set;
    }

    /// <inheritdoc/>
    [Export]
    public Node3D? RootMotionReference
    {
        get; set;
    }

    /// <inheritdoc/>
    [Export(PropertyHint.File, "*.json")]
    public string CataloguePath { get; set; } = string.Empty;

    /// <inheritdoc/>
    [Export]
    public bool PlanarMotionAllowed { get; set; } = true;

    /// <inheritdoc/>
    [Export]
    public bool YawMotionAllowed { get; set; } = true;

    /// <inheritdoc/>
    [Export]
    public bool AwaitInstallerActivation
    {
        get; set;
    }

    /// <inheritdoc/>
    [ExportGroup("Arrival")]
    [Export(PropertyHint.Range, "0,0.1,0.0001")]
    public float PositionTolerance { get; set; } = 0.01f;

    /// <inheritdoc/>
    [Export(PropertyHint.Range, "0,5,0.01")]
    public float YawToleranceDegrees { get; set; } = 1f;

    /// <summary>Whether the runtime binding is active.</summary>
    public bool IsRuntimeBindingActive
    {
        get; private set;
    }

    /// <summary>Runtime-scale trajectories of the bound rig.</summary>
    internal ClipTrajectoryTable? Trajectories
    {
        get; private set;
    }

    /// <summary>Issued clip transitions, oldest first, in the driver's bounded history.</summary>
    public IReadOnlyList<NPCClipTransitionRecord> IssuedClipTransitions => AnimationDriver?.IssuedTransitions ?? [];

    /// <summary>The sole driver of the manually evaluated animation tree.</summary>
    internal NPCAnimationTreeDriver? AnimationDriver
    {
        get; private set;
    }

    /// <summary>Number of evaluations consumed by the movement operation.</summary>
    internal long RootMotionReadCount
    {
        get; private set;
    }

    /// <summary>
    /// Recent per-tick movement decompositions and realised results, exactly as the movement operation already
    /// computed and applied them (NAV-001 Technical Requirement 17 value-only diagnostics: no additional root read,
    /// planted-state commit, tree evaluation or movement call is made to produce these). Oldest entries are dropped
    /// beyond a fixed bound.
    /// </summary>
    public IReadOnlyList<NPCNavigationMovementSample> MovementSamples => _movementSamples;

    /// <summary>Physics tick of the latest movement root-motion read.</summary>
    internal ulong LastRootMotionReadTick
    {
        get; private set;
    }

    private NavigationTerminalTolerances Tolerances => new(Mathf.Max(PositionTolerance, 0f), Mathf.DegToRad(Mathf.Max(YawToleranceDegrees, 0f)));

    /// <inheritdoc/>
    public override string[] _GetConfigurationWarnings()
    {
        List<string> warnings = [];
        if (Actor is null)
        {
            warnings.Add("NPC navigation requires an Actor.");
        }

        if (AnimationTree is null)
        {
            warnings.Add("NPC navigation requires an AnimationTree.");
        }

        if (RootMotionReference is null)
        {
            warnings.Add("NPC navigation requires a RootMotionReference.");
        }

        if (string.IsNullOrWhiteSpace(CataloguePath))
        {
            warnings.Add("NPC navigation requires a locomotion catalogue.");
        }

        return [.. warnings];
    }

    /// <inheritdoc/>
    public override void _Ready()
    {
        if (!AwaitInstallerActivation)
        {
            ActivateRuntimeBinding();
        }
    }

    /// <summary>Builds the rig-specific trajectories, seams and planner once after installer rebasing.</summary>
    public void ActivateRuntimeBinding()
    {
        if (IsRuntimeBindingActive || _bindingFailureLatched)
        {
            return;
        }

        string[] warnings = _GetConfigurationWarnings();
        if (warnings.Length != 0)
        {
            throw new InvalidOperationException(string.Join(" ", warnings));
        }

        if (!Actor!.IsInsideTree() || !AnimationTree!.IsInsideTree() || !RootMotionReference!.IsInsideTree())
        {
            throw new InvalidOperationException("NPC navigation activation requires all bound nodes in the scene tree.");
        }

        try
        {
            AnimationPlayer player = Actor.GetNodeOrNull<AnimationPlayer>("AnimationPlayer")
                ?? throw new InvalidOperationException("NPC navigation requires the actor AnimationPlayer.");
            AnimationTree.AnimPlayer = AnimationTree.GetPathTo(player);
            var catalogue = StandingLocomotionCatalogue.Parse(Godot.FileAccess.GetFileAsString(CataloguePath), CataloguePath);
            catalogue.RequireCapabilities(nameof(NPCNavigation), Enum.GetValues<StandingLocomotionCapability>());
            var driver = new NPCAnimationTreeDriver(AnimationTree, _statesNode);
            NodePath track = AnimationTree.RootMotionTrack;
            _skeleton = AnimationTree.GetNodeOrNull(AnimationTree.RootNode)?.GetNodeOrNull<Skeleton3D>(new NodePath(track.GetConcatenatedNames()))
                ?? throw new InvalidOperationException($"Root-motion track '{track}' does not resolve to a Skeleton3D.");
            _leftToes = _skeleton.FindBone("LeftToes");
            _rightToes = _skeleton.FindBone("RightToes");
            if (_leftToes < 0 || _rightToes < 0)
            {
                throw new InvalidOperationException("NPC arrival binding requires both LeftToes and RightToes bones.");
            }

            _plantedSource = Actor.FindChildren("*", "", true, false).OfType<AnimationGraphPlantedStateSource>()
                .Single(source => source.AnimationTree == AnimationTree);
            foreach ((StringName state, StandingLocomotionCapability capability) in NPCAnimationProgramme.StateCapabilities)
            {
                string animation = driver.ClipAnimation(state).ToString();
                string identity = catalogue[capability].Identity;
                if (!animation.EndsWith("/" + identity, StringComparison.Ordinal) && animation != identity)
                {
                    throw new InvalidOperationException($"NPC state '{state}' does not play the {capability} catalogue clip.");
                }

                _stateOf[catalogue[capability].Role] = state;
            }

            // Pivot-continuation alias states are a required rig capability (NPCAnimationProgramme.RequiredStates):
            // they are the only authored-graph realisation of the planner set's crossfaded same-role pivot
            // re-entry (Godot 4.7 state machines reject from == to transitions and travel to the current node
            // never re-enters it), so a rig without the aliases cannot materialise pivot continuations and
            // binding fails rather than degrading to a same-role request the animation driver must refuse.
            List<StringName> continuationAliases = [];
            foreach ((StringName state, StandingLocomotionCapability capability) in NPCAnimationProgramme.PivotContinuationStates)
            {
                if (!driver.HasState(state))
                {
                    throw new InvalidOperationException(
                        $"NPC animation tree '{AnimationTree.Name}' is missing the required pivot-continuation state '{state}'.");
                }

                StringName canonical = _stateOf[catalogue[capability].Role];
                if (driver.ClipAnimation(state) != driver.ClipAnimation(canonical)
                    || !driver.IsLooping(state) || !driver.IsLooping(canonical))
                {
                    throw new InvalidOperationException($"NPC continuation '{state}' must loop the same animation as '{canonical}'.");
                }

                continuationAliases.Add(state);
            }

            _pivotContinuationAliases = [.. continuationAliases];

            string idleAnimation = driver.ClipAnimation(NPCAnimationProgramme.IdleState).ToString();
            int separator = idleAnimation.IndexOf('/', StringComparison.Ordinal);
            string idleLibrary = player.GetAnimationLibrary(separator > 0 ? idleAnimation[..separator] : string.Empty).ResourcePath;
            if (!idleLibrary.EndsWith(".tres", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Idle animation '{idleAnimation}' has no idle catalogue.");
            }

            var idleIntervals = PerFootPlantedIntervals.LoadValidatedIdleCatalogue($"{idleLibrary[..^5]}_catalogue.json");
            PlantedClipIntervals idleClip = idleIntervals[idleAnimation[(separator + 1)..]];
            AnimationLibrary library = ResourceLoader.Load<AnimationLibrary>(catalogue.AnimationLibraryPath)
                ?? throw new InvalidDataException($"Package '{catalogue.AnimationLibraryPath}' could not be loaded.");
            Trajectories = ClipTrajectoryTableBuilder.Build(catalogue, library, _skeleton, track,
                [new UncataloguedClip(idleClip.Identity, idleClip.Role, idleClip.DurationSeconds, AnimationTree.GetAnimation(idleAnimation))]);
            string path = catalogue.AnimationLibraryPath;
            var intervals = PerFootPlantedIntervals.Load($"{path[..(path.LastIndexOf('/') + 1)]}{PerFootPlantedIntervals.FileName}");
            intervals.ValidateAgainst(catalogue);
            List<SeamClip> clips = [];
            foreach (StandingLocomotionClip clip in catalogue.Clips.Values.OrderBy(clip => clip.Role, StringComparer.Ordinal))
            {
                clips.Add(SeamClip.FromTrajectory(clip, Trajectories[clip.Role], intervals[clip.Identity]));
            }

            ClipTrajectory idle = Trajectories.Clips.Values.Single(trajectory => trajectory.Capability is null);
            clips.Add(SeamClip.FromTrajectory(idle, SeamClipRole.Idle, idleIntervals[idle.Identity]));
            _stateOf[idle.Role] = NPCAnimationProgramme.IdleState;
            foreach ((string role, StringName state) in _stateOf)
            {
                _roleOf[state] = role;
            }
            foreach ((StringName state, StandingLocomotionCapability capability) in NPCAnimationProgramme.PivotContinuationStates)
            {
                if (_pivotContinuationAliases.Contains(state))
                {
                    _roleOf[state] = catalogue[capability].Role;
                }
            }

            var seams = SeamTable.Build(clips);
            HashSet<string> looping = [.. _stateOf.Where(pair => driver.IsLooping(pair.Value)).Select(pair => pair.Key)];
            SeamToleranceConfig config = ResourceLoader.Load<SeamToleranceConfig>(SeamTolerancePath)
                ?? throw new InvalidDataException($"Seam tolerance config '{SeamTolerancePath}' could not be loaded.");
            var predictor = new SequencePredictor(seams, Trajectories.Clips, looping, config.ToSeamTolerance(catalogue.PackageVariant).ToleranceMetres);
            _planner = new ArrivalPlanner(seams, predictor, new ArrivalPlannerConfig
            {
                ArrivedDistanceMetres = Mathf.Max(PositionTolerance, 0f),
                ArrivedYawRadians = Mathf.DegToRad(Mathf.Max(YawToleranceDegrees, 0f))
            });
            AnimationDriver = driver;
            AnimationTree.RootMotionLocal = true;
            AnimationTree.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
            AnimationTree.Active = false;
            AnimationTree.Active = true;
            ResolvePlayback().Start(NPCAnimationProgramme.IdleState, reset: true);
            // The runtime-binding reset is the only boundary that clears physical-phase allowance history
            // (CORNER-03): everything else — plan adoption, sections, replans, supersession — must continue it.
            _phaseObserved = false;
            _phaseBudget = null;
            IsRuntimeBindingActive = true;
            SetPhysicsProcess(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or ArgumentException or FileNotFoundException)
        {
            _bindingFailureLatched = true;
            GameLoggerResolver.ResolveRequired<NPCNavigation>().LogError(exception, "NPC navigation runtime binding is unavailable: {Detail}", exception.Message);
        }
    }

    /// <inheritdoc/>
    public override void _PhysicsProcess(double delta)
    {
        base._PhysicsProcess(delta);
        if (!IsRuntimeBindingActive || delta <= 0d)
        {
            return;
        }

        if (IsMovementAuthorityActive && _awaitingPlan && HasDestination)
        {
            PlanPendingLeg();
        }

        if (IsMovementAuthorityActive && _plan is not null && _prediction is not null)
        {
            if (_nextSegment < _plan.Segments.Length && _prediction.EntryTicks[_nextSegment] == _tick)
            {
                ArrivalPlanSegment segment = _plan.Segments[_nextSegment];
                using StringName current = ResolvePlayback().GetCurrentNode();
                StringName destination = _stateOf[segment.Role];
                if (_roleOf.TryGetValue(current, out string? currentRole) && currentRole == segment.Role
                    && current == destination)
                {
                    foreach (StringName alias in _pivotContinuationAliases)
                    {
                        if (_roleOf[alias] == segment.Role)
                        {
                            destination = alias;
                            break;
                        }
                    }
                }

                if (!AnimationDriver!.TryRequestTransition(destination, segment.EntrySeconds, segment.CrossfadeSeconds))
                {
                    Fail($"NAV-001: animation driver refused planned seam into {segment.Role} at tick {_tick}.");
                }
                else
                {
                    _nextSegment++;
                }
            }
        }

        if (AnimationTree!.Active)
        {
            AnimationDriver!.Advance(delta);
        }

        if (!IsMovementAuthorityActive)
        {
            return;
        }

        if (_plan is null || _prediction is null)
        {
            // No plan drives motion this tick, but the physical support phase keeps evolving: observe it so a
            // genuine phase change during the gap ends the old phase's allowance instead of silently extending it
            // into the next plan (no correction and no charging happen without a plan).
            if (_plantedSource!.TryGetPlantedState(out PlantedFeetState gapPlanted))
            {
                TrackLivePhase(SupportPhases.Of(gapPlanted), null);
            }

            return;
        }

        if (_tick < _prediction.Ticks.Length)
        {
            ApplyMovementOnce();
            _tick++;
        }
        if (_prediction is not null && _tick >= _prediction.Ticks.Length)
        {
            if (_plan.Kind == ArrivalMovementKind.TravelSection)
            {
                // The section ended mid-travel: the walk keeps playing while the next leg is planned from the
                // realised state in this same tick, so there is no movement gap.
                if (HasDestination)
                {
                    PlanPendingLeg();
                }
            }
            else if (!_awaitingPlan)
            {
                Finish();
            }
        }
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        if (CurrentStatus.HasActiveRequest)
        {
            _ = TryCancelActiveOutcome(NavigationCancellationSource.OwnerRemoved,
                CreateCurrentTerminalContext(NavigationTerminalReason.Cancellation), Engine.GetPhysicsFrames());
        }
    }

    /// <inheritdoc/>
    protected override Vector3 GetNavigationStartPosition() => Actor?.GlobalPosition.IsFinite() == true ? Actor.GlobalPosition : base.GetNavigationStartPosition();

    /// <inheritdoc/>
    protected override NavigationTerminalContext CreateLifecycleTerminalContext(Transform3D finalTransform, NavigationTerminalReason reason)
        => CurrentStatus.HasActiveRequest ? CreateCurrentTerminalContext(reason) : base.CreateLifecycleTerminalContext(finalTransform, reason);

    /// <inheritdoc/>
    protected override bool CanReportNavigationComplete() => false;

    /// <inheritdoc/>
    protected override void OnDestinationAccepted(Transform3D destination)
    {
        // A replacement is a redirect: the predecessor plan keeps driving motion until the redirecting leg is
        // planned (this tick or the next), and the redirect's first transition obeys TR14 prompt semantics. The
        // allocator is stateless (its caps are immutable), so recreating it changes nothing here; the allowance
        // state that matters lives in the physical-phase budget, which deliberately SURVIVES this supersession: an
        // ongoing support phase keeps its consumed state into the new request, and a new request invents no ceiling.
        _redirectPending = _plan is not null;
        _awaitingPlan = true;
        _holdingSections = 0;
        _degenerateSections = 0;
        _allocator = new SwingCorrectionAllocator(new SwingCorrectionCaps());
    }

    /// <inheritdoc/>
    protected override void OnDestinationCleared()
    {
        bool stopping = !_committingOutcome && _plan is not null && IsRuntimeBindingActive;
        ClearPlan();
        if (stopping)
        {
            BeginPlan(Actor!.GlobalTransform);
        }
    }

    /// <summary>
    /// Plans the next leg of the active request from the collision-realised state, within the calling tick: a
    /// straight remainder is one terminal plan; a route with corners is followed by nonterminal sections until the
    /// terminal approach. When no coherent route sample exists, a bounded holding section continues the current
    /// travel clip so motion and correction continue without a seam or a frozen tick; exhausting the bound ends the
    /// request through a planner-owned natural stop.
    /// </summary>
    private void PlanPendingLeg()
    {
        if (!HasDestination || !IsRuntimeBindingActive)
        {
            _awaitingPlan = false;
            _redirectPending = false;
            return;
        }

        bool prompt = _redirectPending;
        _awaitingPlan = false;
        _redirectPending = false;
        _ = Poll(Actor!.GlobalTransform);
        if (LastPollProducedValidSample && CurrentRouteSnapshot is { } snapshot && BeginNextLeg(snapshot, prompt))
        {
            // A successful leg genuinely advanced the route (TryBeginSection rejects zero-tick plans, so a
            // success cannot be the degenerate adoption loop), and holding credit is restored: long routes
            // that legitimately hold between legs (the mid-walk corner scenario) keep their recovery budget.
            _holdingSections = 0;
            return;
        }

        string reason = LastPollProducedValidSample ? "the next route leg did not plan" : "no coherent route sample";
        if (_holdingSections < _routeConfiguration.MaxHoldingSections && BeginContinuationFallback())
        {
            _holdingSections++;
            return;
        }

        FailRouteWithNaturalStop($"NAV-001: route following could not continue ({reason}); holding bound exhausted.");
    }

    private bool BeginNextLeg(NavigationRouteSnapshot snapshot, bool prompt)
    {
        var route = NPCNavigationRoutePlan.Compile(snapshot, authoredExitDistance: 0f, _routeConfiguration);
        float previous = _route is not null && route.DestinationRequestGeneration == _route.DestinationRequestGeneration
            ? _routeDistance
            : 0f;
        RouteProjection projection = route.Project(Actor!.GlobalPosition, previous, searchBacktrack: 0.25f);
        _route = route;
        _routeDistance = projection.Distance;
        using StringName current = ResolvePlayback().GetCurrentNode();
        bool travellingStart = current != NPCAnimationProgramme.IdleState;
        // TR18: an unvisited via is a binding route point — the station may not leapfrog it, so the corridor
        // passes through the via (within its configured pass radius) before advancing beyond it.
        IReadOnlyList<float> unvisitedVias = UnvisitedViaRouteDistances;
        NPCNavigationLegDecision decision = NPCNavigationRouteSections.Decide(route, projection.Distance, _routeConfiguration, travellingStart);
        float stationDistance = decision.Terminal
            ? decision.StationDistance
            : NavigationViaChain.ClampStationToBindingVia(decision.StationDistance, projection.Distance, unvisitedVias);
        if (decision.Terminal)
        {
            // A terminal plan at a section boundary must travel on to the destination; a replacement issued at
            // accept time keeps the full prompt search (its redirect may legitimately stop when it cannot travel on).
            BeginPlan(Destination, prompt, travellingOnly: !prompt, admitComposedPath: true);
            return _plan is not null;
        }

        Vector3 station = route.Sample(stationDistance, out Vector3 direction)
            + new Vector3(decision.StationOutwardOffset.X, 0f, decision.StationOutwardOffset.Y);
        var target = new ArrivalTarget(new Vector2(station.X, station.Z), NPCNavigationRoutePlan.YawFromDirection(direction));
        if (TryBeginSection(target, prompt))
        {
            return true;
        }

        // One bounded retry with half the lookahead: a shorter section may fit the seam grid the full one missed.
        Vector3 nearer = route.Sample(
            NavigationViaChain.ClampStationToBindingVia(
                MathF.Max(projection.Distance + _routeConfiguration.MinSectionAdvanceMetres,
                    projection.Distance + (0.5f * (decision.StationDistance - projection.Distance))),
                projection.Distance,
                unvisitedVias),
            out direction);
        var retryTarget = new ArrivalTarget(new Vector2(nearer.X, nearer.Z), NPCNavigationRoutePlan.YawFromDirection(direction));
        if (TryBeginSection(retryTarget, prompt))
        {
            return true;
        }

        // CORNER-07: a significant corner may bind this leg while neither station admits a composed section that
        // stays on the navigable surface — from a close approach, every arc family cuts inside the corner vertex.
        // One final bounded attempt anchors the station at the corner itself, offset to the turn's outside like
        // the before-corner stations: the section then closes on the corner on the outside of the turn, and the
        // next leg — planned from the corner latitude, with the corner no longer ahead — owns the turn.
        if (decision.BindingCorner is { } corner)
        {
            Vector3 anchored = route.Sample(
                NavigationViaChain.ClampStationToBindingVia(corner.Distance, projection.Distance, unvisitedVias),
                out Vector3 cornerDirection);
            Vector2 outward = NPCNavigationRouteSections.OutsideOffset(cornerDirection, corner, _routeConfiguration);
            anchored += new Vector3(outward.X, 0f, outward.Y);
            var anchoredTarget = new ArrivalTarget(
                new Vector2(anchored.X, anchored.Z), NPCNavigationRoutePlan.YawFromDirection(cornerDirection));
            return TryBeginSection(anchoredTarget, prompt);
        }

        return false;
    }

    private bool TryBeginSection(ArrivalTarget station, bool prompt)
    {
        ArrivalStart start = CurrentArrivalStart(prompt);
        ArrivalPlan plan = _planner!.PlanSection(
            start,
            station,
            promptRedirect: prompt && start.Role != _planner.IdleRole,
            ComposedSectionAdmission(station));
        if (plan.Status != ArrivalPlanStatus.Planned)
        {
            // Value-only diagnostics (CORNER-idle): the station geometry against the actor's current heading,
            // so a future "leg did not plan" report localises the unreachable bearing without an engine probe.
            Vector2 origin = new(Actor!.GlobalPosition.X, Actor.GlobalPosition.Z);
            float actorYaw = NPCNavigationRoutePlan.YawOf(Actor.GlobalBasis);
            double bearing = Math.Round(
                Mathf.RadToDeg(Mathf.AngleDifference(
                    NPCNavigationRoutePlan.YawFromDirection(new Vector3(
                        station.Position.X - origin.X, 0f, station.Position.Y - origin.Y)),
                    actorYaw)),
                1);
            GameLoggerResolver.ResolveRequired<NPCNavigation>().LogWarning(
                "NAV-001: route section to ({X:0.###}, {Z:0.###}, yaw {Yaw:0.###}) failed ({Failure}): {Detail} | station bearing {Bearing:0.#} deg, distance {Distance:0.###} m, actor yaw {ActorYaw:0.###} rad, role {Role}",
                station.Position.X, station.Position.Y, station.Yaw, plan.Failure, plan.FailureDetail,
                bearing, origin.DistanceTo(station.Position), actorYaw, start.Role);
            return false;
        }

        // A zero-tick section cannot move the actor, but a SHORT run of them is the machinery's legitimate
        // seam-alignment escape: while the travel clip's playback advances, degenerate corner-anchored
        // adoptions hold the projection until an aligned seam composition lands in the window (the from-idle
        // corner route recovers exactly this way after ~26 adoptions). An UNBOUNDED run is the CORNER-stuck
        // infinite adoption loop (the west-quadrant route retried the same degenerate winner forever, walking
        // in place with warning spam and no terminal outcome), so the adoptions are budgeted: past the budget
        // — one full loop of the current travel clip plus margin, after which every aligned seam has recurred
        // and further identical retries cannot change the outcome — the degenerate plan is a failed leg, and
        // the holding/natural-stop lifecycle below takes over.
        if (plan.TickCount <= 0)
        {
            if (_degenerateSections >= MaxDegenerateSectionAdoptions)
            {
                GameLoggerResolver.ResolveRequired<NPCNavigation>().LogWarning(
                    "NAV-001: route section to ({X:0.###}, {Z:0.###}) degenerated to zero ticks {Count} times in a row; rejected as unexecutable.",
                    station.Position.X, station.Position.Y, _degenerateSections);
                return false;
            }

            _degenerateSections++;
        }
        else
        {
            _degenerateSections = 0;
        }

        SequencePrediction prediction = _planner.Predict(plan);
        if (!prediction.IsValid)
        {
            GameLoggerResolver.ResolveRequired<NPCNavigation>().LogWarning(
                "NAV-001: route section prediction rejected: {Rejection}", prediction.Rejection);
            return false;
        }

        AdoptPlan(plan, prediction);
        _sectionIndex++;
        return true;
    }

    /// <summary>
    /// The composed-trajectory admission for one section station (CORNER-07): replays the runtime's own capped
    /// correction over each finalist's exact per-tick prediction — the same allocator, the live phase's immutable
    /// remaining-allowance snapshot, and the station the runtime will correct toward — and requires every
    /// realised pose and consecutive midpoint to lie on the navigation map's surface. A finalist whose composed
    /// path leaves the authored surface is rejected, so a lower-ranked surface-safe composition can win. The
    /// check is planning-time only; it adds no per-tick animation evaluation, root read or movement. Null when
    /// the navigation map is not queryable, which keeps the ungated section behaviour.
    /// </summary>
    private ArrivalPlanner.SectionComposedPathAdmission? ComposedSectionAdmission(ArrivalTarget station, string context = "section")
    {
        if (!IsNavigationMapReady)
        {
            return null;
        }

        Rid map = GetNavigationMap();
        SwingCorrectionAllocator allocator = _allocator ??= new SwingCorrectionAllocator(new SwingCorrectionCaps());
        SwingCorrectionPhaseBudget? seed = _phaseBudget;
        return Admit;

        bool Admit(SequencePrediction prediction, ImmutableArray<ArrivalPlanSegment> segments)
        {
            List<Vector2> composed = [];
            _ = TerminalCorrectionSchedule.Simulate(prediction, station, allocator, seed, composed);
            ComposedPathContainmentReport report = ComposedPathContainment.Check(composed, OffSurfaceDepth);
            if (report.IsContained)
            {
                return true;
            }

            GameLoggerResolver.ResolveRequired<NPCNavigation>().LogWarning(
                "NAV-001: composed {Context} finalist to ({X:0.###}, {Z:0.###}) leaves the navigable surface by {Depth:0.#} mm at composed tick {Tick}; rejected.",
                context, station.Position.X, station.Position.Y, report.WorstDepthMetres * 1000f, Math.Max(report.FirstOffSurfaceTick, 0));
            return false;
        }

        float OffSurfaceDepth(Vector2 worldXz)
        {
            Vector3 closest = NavigationServer3D.MapGetClosestPoint(map, new Vector3(worldXz.X, 0f, worldXz.Y));
            return new Vector2(closest.X, closest.Z).DistanceTo(worldXz);
        }
    }

    /// <summary>
    /// CORNER-12 stoppability: whether a contained natural stop can be planned from a composed section's end
    /// tick. The end tick of a section is a settled single-clip tick (no crossfade in flight), so it is an exact
    /// <see cref="ArrivalStart"/>; the stop search runs under the same composed-path containment admission as
    /// every adopted plan, with the SAME prompt semantics the route-failure natural stop will use at the section
    /// boundary (the prompt prefix families are the ones that search actually enumerates), and its result is
    /// discarded — only its admissibility matters.
    /// </summary>
    private bool CanStopContainedOnSurface(ArrivalStart start)
    {
        ArrivalTarget stopTarget = new(start.Pose.Position, start.Pose.Yaw);
        ArrivalPlan stop = _planner!.Plan(
            start,
            stopTarget,
            travellingOnly: false,
            _phaseBudget,
            ComposedSectionAdmission(stopTarget, "natural stop"));
        return stop.Status == ArrivalPlanStatus.Planned;
    }

    /// <summary>A holding section: continue the current travel clip toward a point one horizon ahead of the realised
    /// pose, so motion and correction continue while the next coherent route leg is planned at the next boundary.
    /// The horizon point is clamped onto the navigable surface (CORNER-12) and the holding composition passes the
    /// SAME composed-path containment admission as every route section: a recovery leg that would walk or settle
    /// off the authored surface is not a hold, it is a failed leg, and the request-wide bound routes it to the
    /// finite natural-stop lifecycle.</summary>
    private bool BeginContinuationFallback()
    {
        ArrivalStart start = CurrentArrivalStart(prompt: false);
        if (_planner?.IdleRole is not { } idle || start.Role == idle)
        {
            return false;
        }

        Transform3D actor = Actor!.GlobalTransform;
        var forward = new Vector3(-actor.Basis.Z.X, 0f, -actor.Basis.Z.Z);
        if (forward.LengthSquared() < 0.000001f)
        {
            return false;
        }

        Vector3 station = actor.Origin + (forward.Normalized() * _routeConfiguration.SectionHorizonMetres);
        // The horizon point can lie off the authored surface (a west-route holding leg faced the west wall and
        // targeted a point beyond it): clamp it to the closest navigable point — the same on-surface guarantee
        // the route query's own endpoints enjoy — so a holding leg targets the surface, never the void past its
        // edge.
        if (IsNavigationMapReady)
        {
            station = NavigationServer3D.MapGetClosestPoint(GetNavigationMap(), station);
        }

        ArrivalTarget holdingStation = new(new Vector2(station.X, station.Z), NPCNavigationRoutePlan.YawOf(actor.Basis));
        ArrivalPlan plan = _planner.PlanSection(
            start,
            holdingStation,
            promptRedirect: false,
            ComposedSectionAdmission(holdingStation));
        if (plan.Status != ArrivalPlanStatus.Planned)
        {
            return false;
        }

        SequencePrediction prediction = _planner.Predict(plan);
        if (!prediction.IsValid)
        {
            return false;
        }

        // CORNER-12 stoppability: a holding leg is adoptable only when the actor can still legally STOP — not
        // merely from the section's composed end, but from its TARGET STATION: the realised walk diverges from
        // the composed prediction by the corridor's tracking-error scale (measured 157 mm on this very leg —
        // the composed end was stoppable at 0.49 m from the west wall while the realised end landed 0.34 m from
        // it, from which every stop's walk-exit overshot the surface and the gated natural stop could not plan,
        // freezing the graph mid-walk). The station is the walk's intent; a target from which the actor cannot
        // legally stop on the surface is not a lawful hold. Both checks plan the natural stop under the same
        // composed-path containment admission with the same prompt semantics the route-failure stop will use;
        // the results are discarded — only admissibility matters. No margin constant is introduced: the
        // stopping distance is whatever the exit clips actually compose to.
        if (prediction.Ticks.Length > 0 && IsNavigationMapReady)
        {
            PredictedTick tail = prediction.Ticks[^1];
            ArrivalStart tailStart = new(tail.Pose, tail.Current.Role, tail.Current.PositionSeconds)
            {
                Prompt = true,
            };
            ArrivalStart stationStart = new(new SequencePose(holdingStation.Position, holdingStation.Yaw), tail.Current.Role, tail.Current.PositionSeconds)
            {
                Prompt = true,
            };
            if (!CanStopContainedOnSurface(tailStart) || !CanStopContainedOnSurface(stationStart))
            {
                return false;
            }
        }

        AdoptPlan(plan, prediction);
        _sectionIndex++;
        GameLoggerResolver.ResolveRequired<NPCNavigation>().LogWarning(
            "NAV-001: holding section {Section} continues travel while the route is re-queried.", _sectionIndex);
        return true;
    }

    /// <summary>
    /// Ends an unexecutable route through a planner-owned natural stop from the travelling state: the stop plan plays
    /// out to its predicted stopping endpoint (no snap to Idle, no plan back to the failure position enforced as an
    /// arrival), and the request then commits <c>Failed</c> honestly.
    /// </summary>
    private void FailRouteWithNaturalStop(string detail)
    {
        GameLoggerResolver.ResolveRequired<NPCNavigation>().LogError("{Detail}", detail);
        _pendingRouteFailure = detail;
        // CORNER-12: the natural stop is planned with the same composed-path containment admission as every
        // other adopted plan — a recovery stop whose exit overshoots the authored surface is not a stop.
        BeginPlan(Actor!.GlobalTransform, admitComposedPath: true);
        if (_plan is null)
        {
            _pendingRouteFailure = null;
            Fail($"{detail}; the natural stop could not be planned either.");
        }
    }

    private void AdoptPlan(ArrivalPlan plan, SequencePrediction prediction)
    {
        _plan = plan;
        _prediction = prediction;
        _allocator ??= new SwingCorrectionAllocator(new SwingCorrectionCaps());
        _tick = 0;
        _nextSegment = 0;
        _windowIndex = 0;
    }

    private ArrivalStart CurrentArrivalStart(bool prompt)
    {
        AnimationNodeStateMachinePlayback playback = ResolvePlayback();
        using StringName current = playback.GetCurrentNode();
        using StringName fading = playback.GetFadingFromNode();
        SequenceCrossfadeProgress? fade = fading.IsEmpty ? null : new SequenceCrossfadeProgress(
            _roleOf[fading], playback.GetFadingFromPlayPosition(), playback.GetFadingPosition(), playback.GetFadingLength());
        Transform3D actor = Actor!.GlobalTransform;
        return new ArrivalStart(new SequencePose(new Vector2(actor.Origin.X, actor.Origin.Z), NPCNavigationRoutePlan.YawOf(actor.Basis)),
            _roleOf[current], playback.GetCurrentPlayPosition(), fade)
        {
            Prompt = prompt
        };
    }

    private void BeginPlan(Transform3D target, bool prompt = true, bool travellingOnly = false, bool admitComposedPath = false)
    {
        ArrivalStart start = CurrentArrivalStart(prompt);
        // The terminal admission assessment plans against the live physical phase's committed remaining allowance
        // (CORNER-03): an immutable value, so the search cannot spend the runtime's budget, and a mid-motion call
        // never defaults to fresh history.
        ArrivalTarget arrivalTarget = new(new Vector2(target.Origin.X, target.Origin.Z), NPCNavigationRoutePlan.YawOf(target.Basis));
        ArrivalPlan plan = _planner!.Plan(
            start,
            arrivalTarget,
            travellingOnly,
            _phaseBudget,
            // CORNER-12: the route's terminal approach and the natural stop are planned from a travelling state
            // near mesh edges during recovery; their composed trajectories pass the SAME containment admission as
            // every route section (a stop plan whose walk-exit overshoots the surface is rejected so a contained
            // finalist wins). Construction is untouched — the gate only selects among the search's own finalists.
            admitComposedPath ? ComposedSectionAdmission(arrivalTarget, "terminal plan") : null);
        if (plan.Status == ArrivalPlanStatus.Failed)
        {
            Fail($"NAV-001: planning failed ({plan.Failure}): {plan.FailureDetail}");
            return;
        }
        AdoptPlan(plan, _planner.Predict(plan));
    }

    private void ApplyMovementOnce()
    {
        CharacterBody3D actor = Actor!;
        SequencePrediction prediction = _prediction!;
        Vector3 rootPosition = AnimationTree!.GetRootMotionPosition();
        Quaternion rootRotation = AnimationTree.GetRootMotionRotation();
        RootMotionReadCount++;
        LastRootMotionReadTick = Engine.GetPhysicsFrames();
        var frame = new NPCNavigationSemanticFrame(actor.GlobalBasis, RootMotionReference!.GlobalBasis);
        float rootYaw = YawMotionAllowed ? frame.EvaluatedRootRotationToActorLocal(rootRotation).GetEuler().Y : 0f;
        rootYaw = Mathf.IsZeroApprox(rootYaw) ? 0f : rootYaw;
        Basis afterRoot = actor.GlobalBasis * new Basis(Vector3.Up, rootYaw);
        Basis afterReference = afterRoot * actor.GlobalBasis.Inverse() * RootMotionReference.GlobalBasis;
        Vector3 rootDelta = PlanarMotionAllowed
            ? new NPCNavigationSemanticFrame(afterRoot, afterReference).EvaluatedRootDeltaToWorld(rootPosition) : Vector3.Zero;
        rootDelta.Y = 0f;
        var actualAfterRoot = new Transform3D(afterRoot, actor.GlobalPosition + rootDelta);
        while (_tick > prediction.Windows[_windowIndex].LastTick)
        {
            _windowIndex++;
        }

        if (!_plantedSource!.TryGetPlantedState(out PlantedFeetState planted))
        {
            Fail("NAV-001: live planted state unavailable during arrival.");
            return;
        }
        SupportPhase phase = SupportPhases.Of(planted);
        TrackLivePhase(phase, prediction.Windows[_windowIndex]);
        SwingCorrectionResidual residual = Residual(actualAfterRoot, prediction.Ticks[_tick].Pose, prediction.EndPose, _plan!.Target, out Vector2 endpointOffset);
        Transform3D actorFromSkeleton = actor.GlobalTransform.AffineInverse() * _skeleton!.GlobalTransform;
        Vector3 ball = phase == SupportPhase.LeftSwing ? actorFromSkeleton * _skeleton.GetBoneGlobalPose(_rightToes).Origin
            : phase == SupportPhase.RightSwing ? actorFromSkeleton * _skeleton.GetBoneGlobalPose(_leftToes).Origin : Vector3.Zero;
        SwingCorrection correction = _phaseBudget is { Phase: var budgetPhase } budget
            && budgetPhase == phase && phase == prediction.Windows[_windowIndex].Phase
            ? _allocator!.Step(residual, prediction.Windows[_windowIndex], _tick,
                prediction.Windows.AsSpan()[(_windowIndex + 1)..], new Vector2(ball.X, -ball.Z), endpointOffset, phase, budget)
            : _allocator!.Step(residual, prediction.Windows[_windowIndex], _tick,
                prediction.Windows.AsSpan()[(_windowIndex + 1)..], new Vector2(ball.X, -ball.Z), endpointOffset, phase);
        Vector2 synthetic = PlanarMotionAllowed ? correction.OriginShift + correction.Planar : Vector2.Zero;
        float correctionYaw = YawMotionAllowed ? correction.Yaw : 0f;
        // Charge the enabled correction proposal to the ongoing physical phase exactly once, before collision
        // resolution: a collision-blocked part of the translation is spent, never refunded into later capacity.
        if (_phaseBudget is { Phase: var chargedPhase } spent && chargedPhase == phase)
        {
            _phaseBudget = spent.Charge(correction with
            {
                Planar = PlanarMotionAllowed ? correction.Planar : Vector2.Zero,
                OriginShift = PlanarMotionAllowed ? correction.OriginShift : Vector2.Zero,
                Yaw = correctionYaw,
            });
        }

        Basis afterCorrection = afterRoot * new Basis(Vector3.Up, correctionYaw);
        Vector3 delta = rootDelta + (afterCorrection * new Vector3(synthetic.X, 0f, -synthetic.Y));
        float yaw = rootYaw + correctionYaw;
        if (yaw != 0f)
        {
            actor.RotateY(yaw);
        }

        if (!delta.IsZeroApprox())
        {
            _ = actor.MoveAndCollide(delta);
        }

        RecordMovementSample(rootDelta, rootYaw, synthetic, correctionYaw, phase, _phaseBudget);
    }

    /// <summary>
    /// Tracks the identity and allowance basis of the ongoing physical support phase from LIVE planted-state
    /// observations (CORNER-03): a genuine live phase change ends the previous phase's allowance (it is discarded,
    /// never carried to the new phase), and a phase with no established budget receives one from the first
    /// prediction window that covers it. Plan adoption, same-phase clip seams, request replacement and observation
    /// gaps never refill an ongoing phase: the window only establishes a basis while the budget is still empty.
    /// </summary>
    private void TrackLivePhase(SupportPhase phase, SupportWindow? coveringWindow)
    {
        if (!_phaseObserved || _livePhase != phase)
        {
            _livePhase = phase;
            _phaseObserved = true;
            _phaseBudget = null;
        }

        if (_phaseBudget is null && coveringWindow is { } window && window.Phase == phase && _allocator is not null)
        {
            _phaseBudget = SwingCorrectionPhaseBudget.ForWindow(window, _allocator.Caps);
        }
    }

    /// <summary>Records the tick's already-computed movement decomposition and realised result (value-only diagnostics).</summary>
    private void RecordMovementSample(
        Vector3 rootDelta,
        float rootYaw,
        Vector2 synthetic,
        float syntheticYaw,
        SupportPhase phase,
        SwingCorrectionPhaseBudget? phaseBudget)
    {
        if (_movementSamples.Count >= MaximumMovementSamples)
        {
            _movementSamples.RemoveRange(0, MaximumMovementSamples / 8);
        }

        NavigationCurrentStatus status = CurrentStatus;
        ArrivalTarget target = _plan is { } plan ? plan.Target : default;
        Transform3D actor = Actor!.GlobalTransform;
        _movementSamples.Add(new NPCNavigationMovementSample(
            LastRootMotionReadTick,
            (int)(status.ActiveRequest?.RequestID ?? 0),
            status.ActiveRequest?.Generation ?? 0,
            status.Revision,
            _plan?.Kind == ArrivalMovementKind.TravelSection,
            _sectionIndex,
            rootDelta,
            rootYaw,
            synthetic,
            syntheticYaw,
            phase,
            phaseBudget,
            target.Position,
            target.Yaw,
            new Vector2(actor.Origin.X, actor.Origin.Z),
            NPCNavigationRoutePlan.YawOf(actor.Basis),
            ViaPointsPassedCount));
    }

    private static SwingCorrectionResidual Residual(Transform3D actual, SequencePose predicted, SequencePose end, ArrivalTarget target, out Vector2 endpointOffset)
    {
        static Transform3D Transform(SequencePose pose)
        {
            return new(new Basis(Vector3.Up, pose.Yaw), new Vector3(pose.Position.X, 0f, pose.Position.Y));
        }

        Transform3D projected = actual * Transform(predicted).AffineInverse() * Transform(end);
        Vector3 offset = actual.Basis.Inverse() * (projected.Origin - actual.Origin);
        endpointOffset = new Vector2(offset.X, -offset.Z);
        Vector3 error = actual.Basis.Inverse() * (new Vector3(target.Position.X, 0f, target.Position.Y) - projected.Origin);
        return new SwingCorrectionResidual(new Vector2(error.X, -error.Z), Mathf.AngleDifference(NPCNavigationRoutePlan.YawOf(projected.Basis), target.Yaw));
    }

    private void Finish()
    {
        if (_pendingRouteFailure is { } failure)
        {
            // The route could not execute; the planner-owned natural stop has ended at its predicted stopping
            // endpoint, and the request now ends Failed honestly (never Completed at a wrong place).
            _pendingRouteFailure = null;
            Fail(failure);
            return;
        }

        AnimationNodeStateMachinePlayback playback = ResolvePlayback();
        using StringName current = playback.GetCurrentNode();
        using StringName fading = playback.GetFadingFromNode();
        if (current != NPCAnimationProgramme.IdleState || !fading.IsEmpty)
        {
            Fail("NAV-001: plan ended before returning fully to Idle.");
            return;
        }
        ArrivalTarget target = _plan!.Target;
        Transform3D actor = Actor!.GlobalTransform;
        float distance = new Vector2(actor.Origin.X, actor.Origin.Z).DistanceTo(target.Position);
        float yaw = Mathf.Abs(Mathf.AngleDifference(NPCNavigationRoutePlan.YawOf(actor.Basis), target.Yaw));
        if (distance > PositionTolerance || yaw > Mathf.DegToRad(YawToleranceDegrees))
        {
            Fail($"NAV-001: arrival residual exceeds tolerance: {distance:R} m, {Mathf.RadToDeg(yaw):R} degrees; corrective steps deferred.");
        }
        else
        {
            CommitOutcome(true);
        }
    }

    private void Fail(string detail)
    {
        GameLoggerResolver.ResolveRequired<NPCNavigation>().LogError("{Detail}", detail);
        CommitOutcome(false);
    }

    private void CommitOutcome(bool completed)
    {
        _committingOutcome = true;
        try
        {
            NavigationTerminalReason reason = completed ? NavigationTerminalReason.DestinationReached : NavigationTerminalReason.ConvergenceFailed;
            NavigationTerminalContext context = CreateCurrentTerminalContext(reason);
            _ = completed
                ? TryCommitCompletedOutcome(context, Engine.GetPhysicsFrames())
                : TryCommitFailedOutcome(context, Engine.GetPhysicsFrames());

            ClearPlan();
        }
        finally { _committingOutcome = false; }
    }

    private void ClearPlan()
    {
        AnimationDriver?.CancelPendingTransition();
        _plan = null;
        _prediction = null;
        _allocator = null;
        _awaitingPlan = false;
        _redirectPending = false;
        _pendingRouteFailure = null;
        _holdingSections = 0;
        _degenerateSections = 0;
        _route = null;
        _routeDistance = 0f;
        _sectionIndex = 0;
    }

    private NavigationTerminalContext CreateCurrentTerminalContext(NavigationTerminalReason reason)
    {
        Transform3D actor = Actor?.GlobalTransform ?? Transform3D.Identity;
        NavigationOutcomeTransform requested = CurrentStatus.RequestedTransform ?? default;
        float yaw = NPCNavigationRoutePlan.YawOf(actor.Basis);
        var final = new NavigationOutcomeTransform(actor.Origin.X, actor.Origin.Y, actor.Origin.Z, yaw);
        var error = new NavigationPlanarYawError(new Vector2(final.X - requested.X, final.Z - requested.Z).Length(), Mathf.Abs(NPCNavigationRoutePlan.WrapAngle(yaw - requested.YawRadians)));
        return new(final, error, Tolerances, reason, boundaryPhysicsTick: Engine.GetPhysicsFrames());
    }

    private AnimationNodeStateMachinePlayback ResolvePlayback() => AnimationTree!.Get(_playbackParameter).As<AnimationNodeStateMachinePlayback>();
}

/// <summary>
/// One tick's movement decomposition and realised result, exactly as <see cref="NPCNavigation"/>'s movement operation
/// computed and applied it: the consumed root motion, the applied synthetic (swing-correction) translation and yaw,
/// the tick's support phase, the plan target in force and the collision-realised pose after the tick. These are
/// value-only NAV-001 Technical Requirement 17 diagnostics; producing them adds no root read, planted-state commit,
/// tree evaluation or movement call.
/// </summary>
/// <param name="PhysicsTick">Physics tick the movement operation ran on.</param>
/// <param name="RequestId">Active request id, or 0 when none.</param>
/// <param name="RequestGeneration">Active request generation, or 0 when none.</param>
/// <param name="RouteRevision">Coherent route revision in force.</param>
/// <param name="Section">Whether the plan in force is a nonterminal route section.</param>
/// <param name="SectionIndex">Zero-based section index since the request was accepted.</param>
/// <param name="RootDelta">Consumed planar root motion in world metres.</param>
/// <param name="RootYawRadians">Consumed root yaw in radians.</param>
/// <param name="SyntheticSemantic">Applied synthetic translation, actor-semantic [right, forward] metres.</param>
/// <param name="SyntheticYawRadians">Applied synthetic yaw in radians.</param>
/// <param name="Phase">Support phase the correction was gated by.</param>
/// <param name="PhaseBudget">Committed allowance of that physical phase after this tick's charge (CORNER-03; null while no basis is established).</param>
/// <param name="TargetPosition">Plan target position (station for a section, destination for the final approach).</param>
/// <param name="TargetYawRadians">Plan target yaw.</param>
/// <param name="RealisedPosition">Collision-realised planar position after the tick's movement.</param>
/// <param name="RealisedYawRadians">Collision-realised yaw after the tick's movement.</param>
/// <param name="ViaPointsPassed">Cumulative count of this request's via-points passed within the configured
/// radius as of this tick (TR17-style value-only diagnostics; vias are never stopping points).</param>
public readonly record struct NPCNavigationMovementSample(
    ulong PhysicsTick,
    int RequestId,
    long RequestGeneration,
    long RouteRevision,
    bool Section,
    int SectionIndex,
    Vector3 RootDelta,
    float RootYawRadians,
    Vector2 SyntheticSemantic,
    float SyntheticYawRadians,
    SupportPhase Phase,
    SwingCorrectionPhaseBudget? PhaseBudget,
    Vector2 TargetPosition,
    float TargetYawRadians,
    Vector2 RealisedPosition,
    float RealisedYawRadians,
    int ViaPointsPassed);
