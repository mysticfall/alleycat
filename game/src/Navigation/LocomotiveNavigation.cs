using System.Diagnostics.CodeAnalysis;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core;
using Godot;
using CharacterActor = AlleyCat.Character.Character;

namespace AlleyCat.Navigation;

/// <summary>
/// Production navigation consumer that publishes polled intent through an explicitly bound locomotive actor.
/// </summary>
[GlobalClass]
public partial class LocomotiveNavigation : NavigationBase
{
    private const int NavigationPhysicsPriority = -100;
    private readonly LocomotionTrajectoryPlanner _planner = new();
    private ILocomotive? _commandTarget;
    private LocomotionRoutePlan? _routePlan;
    private bool _hasPublishedCommands;
    private bool _isStopping;

    /// <summary>Gets the latest planner movement output for diagnostics.</summary>
    public Vector2 LastPlannedMovement
    {
        get;
        private set;
    }

    /// <summary>Gets the latest planner turn output for diagnostics.</summary>
    public float LastPlannedTurn
    {
        get;
        private set;
    }

    /// <summary>Gets projected arc-length progress from the latest planner tick.</summary>
    public float LastRouteProgress
    {
        get; private set;
    }

    /// <summary>Gets signed cross-track error from the latest planner tick.</summary>
    public float LastCrossTrackError
    {
        get; private set;
    }

    /// <summary>Gets remaining compiled route distance from the latest planner tick.</summary>
    public float LastRemainingDistance
    {
        get; private set;
    }

    /// <summary>Gets position completion from the latest coherent navigation poll.</summary>
    public bool LastPositionComplete
    {
        get; private set;
    }

    /// <summary>Gets facing completion from the latest coherent navigation poll.</summary>
    public bool LastFacingComplete
    {
        get; private set;
    }

    /// <summary>Gets remaining path distance from the latest coherent navigation poll.</summary>
    public float LastNavigationRemainingDistance
    {
        get; private set;
    }

    /// <summary>Gets the accepted destination generation from the latest coherent runtime sample.</summary>
    public long LastDestinationRequestGeneration
    {
        get; private set;
    }

    /// <summary>Gets the route revision from the latest coherent runtime sample.</summary>
    public long LastRouteRevision
    {
        get; private set;
    }

    /// <summary>
    /// Initialises navigation to publish before default-priority locomotion physics processing.
    /// </summary>
    public LocomotiveNavigation()
    {
        ProcessPhysicsPriority = NavigationPhysicsPriority;
        PathDesiredDistance = 0.05f;
        DestinationReachedDistance = 0.05f;
        TreeExiting += InvalidateCommands;
    }

    /// <summary>
    /// Gets or sets the authoritative actor. It must also implement <see cref="ILocomotive" />.
    /// </summary>
    [Export]
    public Node3D? Actor
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
            {
                return;
            }

            InvalidateCommands();
            field = value;
            _commandTarget = value as ILocomotive;
            UpdateConfigurationWarnings();
        }
    }

    /// <summary>
    /// Gets or sets the character-specific response profile used for trajectory prediction.
    /// </summary>
    [Export]
    public StandingLocomotionCharacter ResponseProfileCharacter
    {
        get;
        set;
    }

    /// <summary>Gets or sets the bound locomotion component's radial input deadzone.</summary>
    [Export(PropertyHint.Range, "0,0.99,0.01")]
    public float TargetInputDeadzone { get; set; } = 0.15f;

    /// <summary>Gets or sets the single locomotion-side gain applied to signed turn input.</summary>
    [Export(PropertyHint.Range, "0.01,20,0.01,or_greater")]
    public float TargetTurnGain { get; set; } = 2.5f;

    /// <inheritdoc/>
    public override string[] _GetConfigurationWarnings()
    {
        var warnings = new List<string>(2);
        if (Actor is null)
        {
            warnings.Add("Actor must reference the authoritative Node3D that implements ILocomotive.");
        }
        else if (Actor is not ILocomotive)
        {
            warnings.Add("Actor must implement ILocomotive in addition to being a Node3D.");
        }

        return [.. warnings];
    }

    /// <inheritdoc/>
    public override void _PhysicsProcess(double delta)
    {
        base._PhysicsProcess(delta);

        if (!TryGetActor(out Node3D? actor, out ILocomotive? locomotive))
        {
            InvalidateCommands();
            return;
        }

        Transform3D actorTransform = GetAuthoritativeActorTransform(actor);
        if (!actorTransform.IsFinite())
        {
            InvalidateCommands();
            return;
        }

        if (!HasDestination)
        {
            PublishStoppingCommands(delta, locomotive);
            return;
        }

        NavigationMotionIntent intent = Poll(actorTransform);
        LastPositionComplete = intent.PositionReached;
        LastFacingComplete = intent.FacingReached;
        LastNavigationRemainingDistance = intent.RemainingPathDistance;
        NavigationRouteSnapshot? snapshot = CurrentRouteSnapshot;
        if (!LastPollProducedValidSample || !intent.HasValidSample || snapshot is null)
        {
            InvalidateCommands();
            return;
        }

        LastDestinationRequestGeneration = snapshot.DestinationRequestGeneration;
        LastRouteRevision = snapshot.RouteRevision;

        // Completion remains NavigationBase policy. Continue polling while stopped so authoritative
        // post-arrival state can release only through the planner's terminal-position hysteresis or
        // NavigationBase's terminal-facing condition; do not let the planner's narrower facing
        // tolerance restart a stable completed request.
        if (_isStopping)
        {
            if (intent.IsComplete)
            {
                PublishStoppingCommands(delta, locomotive);
                return;
            }

            LocomotionResponseProfile recoveryProfile = StandingLocomotionResponseProfiles.Get(ResponseProfileCharacter);
            LocomotionRoutePlan recoveryPlan = GetOrCompileRoutePlan(snapshot, recoveryProfile);
            bool requiresRecovery = !intent.FacingReached
                || _planner.RequiresTerminalPositionRecovery(actorTransform, recoveryPlan);
            if (!requiresRecovery)
            {
                PublishStoppingCommands(delta, locomotive);
                return;
            }

            _isStopping = false;
        }

        if (intent.IsComplete)
        {
            _isStopping = true;
            PublishStoppingCommands(delta, locomotive);
            return;
        }

        LocomotionResponseProfile profile = StandingLocomotionResponseProfiles.Get(ResponseProfileCharacter);
        LocomotionRoutePlan routePlan = GetOrCompileRoutePlan(snapshot, profile);
        LocomotionPlannerOutput output = _planner.Tick(actorTransform, delta, profile, routePlan);
        LastRouteProgress = output.ProjectedProgress;
        LastCrossTrackError = output.CrossTrackError;
        LastRemainingDistance = output.RemainingDistance;
        PublishCommands(locomotive, output.Movement, output.Turn);
    }

    /// <summary>
    /// Disables physics processing and immediately releases any command owned by this consumer.
    /// </summary>
    public new void SetPhysicsProcess(bool enable)
    {
        if (!enable)
        {
            InvalidateCommands();
        }

        base.SetPhysicsProcess(enable);
    }

    /// <inheritdoc/>
    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what is (int)NotificationDisabled or (int)NotificationExitTree)
        {
            InvalidateCommands();
        }
    }

    /// <inheritdoc/>
    public override void _ExitTree() => InvalidateCommands();

    /// <inheritdoc/>
    protected override Vector3 GetNavigationStartPosition()
        => TryGetActor(out Node3D? actor, out _) && actor.GlobalTransform.Origin.IsFinite()
            ? actor.GlobalTransform.Origin
             : base.GetNavigationStartPosition();

    /// <summary>Gets the current authoritative actor transform for a navigation poll.</summary>
    protected virtual Transform3D GetAuthoritativeActorTransform(Node3D actor) => actor.GlobalTransform;

    /// <inheritdoc/>
    protected override void OnDestinationAccepted(Transform3D destination)
    {
        _isStopping = false;
        _routePlan = null;
    }

    /// <inheritdoc/>
    protected override void OnDestinationCleared()
    {
        _routePlan = null;
        _isStopping = true;
    }

    private bool TryGetActor(
        [NotNullWhen(true)] out Node3D? actor,
        [NotNullWhen(true)] out ILocomotive? locomotive)
    {
        actor = Actor;
        locomotive = _commandTarget;
        if (actor is null || !IsInstanceValid(actor) || locomotive is null)
        {
            actor = null;
            locomotive = null;
            return false;
        }

        if (actor is CharacterActor character
            && !character.TryGetComponent<ILocomotion>(out _))
        {
            actor = null;
            locomotive = null;
            return false;
        }

        return true;
    }

    private void NeutraliseCommands()
    {
        ILocomotive? target = _commandTarget;
        Node3D? actor = Actor;
        if (!_hasPublishedCommands || actor is null || target is null || !IsInstanceValid(actor))
        {
            return;
        }

        target.Move(Vector2.Zero);
        target.Rotate(Vector2.Zero);
        _hasPublishedCommands = false;
    }

    private void PublishStoppingCommands(double delta, ILocomotive locomotive)
    {
        LocomotionPlannerState state = _planner.Stop(delta);
        PublishCommands(locomotive, state.Movement, state.Turn);
        if (state.Movement.IsZeroApprox() && Mathf.IsZeroApprox(state.Turn))
        {
            _planner.Reset();
            _hasPublishedCommands = false;
        }
    }

    private void PublishCommands(ILocomotive locomotive, Vector2 movement, float turn)
    {
        LastPlannedMovement = movement;
        LastPlannedTurn = turn;
        locomotive.Move(EncodeRadialDeadzone(movement, TargetInputDeadzone));
        float turnGain = float.IsFinite(TargetTurnGain) ? Math.Max(TargetTurnGain, 0.01f) : 1.0f;
        float semanticTurn = LocomotiveNavigationInput.ToSemanticTurnInput(turn / turnGain);
        locomotive.Rotate(EncodeRadialDeadzone(new Vector2(semanticTurn, 0.0f), TargetInputDeadzone));
        _commandTarget = locomotive;
        _hasPublishedCommands = true;
    }

    private void InvalidateCommands()
    {
        NeutraliseCommands();
        _planner.Reset();
        _routePlan = null;
        _isStopping = false;
        LastPlannedMovement = Vector2.Zero;
        LastPlannedTurn = 0.0f;
        LastRouteProgress = 0.0f;
        LastCrossTrackError = 0.0f;
        LastRemainingDistance = 0.0f;
        LastPositionComplete = false;
        LastFacingComplete = false;
        LastNavigationRemainingDistance = 0.0f;
        LastDestinationRequestGeneration = 0;
        LastRouteRevision = 0;
    }

    private LocomotionRoutePlan GetOrCompileRoutePlan(
        NavigationRouteSnapshot snapshot,
        LocomotionResponseProfile profile)
    {
        if (_routePlan is null
            || _routePlan.DestinationRequestGeneration != snapshot.DestinationRequestGeneration
            || _routePlan.RouteRevision != snapshot.RouteRevision)
        {
            _routePlan = LocomotionRoutePlan.Compile(snapshot, profile);
        }

        return _routePlan;
    }

    private static Vector2 EncodeRadialDeadzone(Vector2 value, float deadzone)
    {
        if (!value.IsFinite() || value.IsZeroApprox())
        {
            return Vector2.Zero;
        }

        float threshold = float.IsFinite(deadzone) ? Mathf.Clamp(deadzone, 0.0f, 0.99f) : 0.0f;
        float length = Mathf.Clamp(value.Length(), 0.0f, 1.0f);
        return value.Normalized() * (threshold + ((1.0f - threshold) * length));
    }
}
