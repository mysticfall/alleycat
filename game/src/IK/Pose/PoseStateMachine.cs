using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Scene-authorable node that orchestrates pose-state lifecycle and transitions.
/// </summary>
/// <remarks>
/// <para>
/// The state machine owns its set of <see cref="PoseState"/> and <see cref="PoseTransition"/>
/// resources and evaluates them via <see cref="Tick"/> each frame. The caller (in Increment 2,
/// the <c>PlayerVRIK</c> driver) is responsible for building a <see cref="PoseStateContext"/>
/// snapshot for each call.
/// </para>
/// <para>
/// The active state's reconciliation output is returned from <see cref="Tick"/> and cached
/// internally for consumers such as <see cref="HipReconciliationModifier"/> that run later in
/// the same frame. The state machine itself does not mutate bone poses directly.
/// </para>
/// </remarks>
[GlobalClass]
public partial class PoseStateMachine : Node
{
    private readonly List<IPoseTransition> _transitionView = [];
    private PoseStateMachineTickResult _lastTickResult;
    private bool _initialStateResolved;

    /// <summary>
    /// Pose states available to the state machine.
    /// </summary>
    [Export]
    public PoseState[] States
    {
        get;
        set;
    } = [];

    /// <summary>
    /// Transition edges evaluated in array order.
    /// </summary>
    [Export]
    public PoseTransition[] Transitions
    {
        get;
        set;
    } = [];

    /// <summary>
    /// Classifier resources available for future disambiguation logic. See
    /// <see cref="PoseClassifier"/> for the deferred wiring note.
    /// </summary>
    [Export]
    public PoseClassifier[] Classifiers
    {
        get;
        set;
    } = [];

    /// <summary>
    /// Identifier of the state activated on <see cref="_Ready"/>.
    /// </summary>
    [Export]
    public StringName InitialStateId
    {
        get;
        set;
    } = new();

    /// <summary>
    /// Optional AnimationTree observed and driven by the active state and transition resources.
    /// </summary>
    [Export]
    public AnimationTree? AnimationTree
    {
        get;
        set;
    }

    /// <summary>
    /// When true, enables state machine processing. When false, skips state machine ticks.
    /// </summary>
    [Export]
    public bool Active
    {
        get;
        set;
    } = true;

    /// <summary>
    /// Currently active pose state.
    /// </summary>
    public PoseState? CurrentState
    {
        get;
        private set;
    }

    /// <summary>
    /// Raised after the state machine switches from one active state to another.
    /// </summary>
    public event Action<PoseState, PoseState>? StateChanged;

    /// <inheritdoc />
    public override void _Ready() => EnsureInitialStateResolved();

    /// <summary>
    /// Advances the state machine by one tick.
    /// </summary>
    /// <param name="context">Current pose-state context snapshot.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="context"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// When the state machine has no states, no initial state, or the initial state cannot be resolved.
    /// </exception>
    public PoseStateMachineTickResult Tick(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Active)
        {
            return _lastTickResult;
        }

        EnsureInitialStateResolved();

        PoseState currentState = CurrentState
            ?? throw new InvalidOperationException(
                $"{nameof(PoseStateMachine)} has no active pose state.");

        RebuildTransitionView();

        PoseTickExecutor.Result tickResult = PoseTickExecutor.Execute(
            currentState,
            _transitionView,
            context,
            ResolveStateByIdForExecutor,
            OnExecutorStateChanged);

        var activePoseState = (PoseState)tickResult.ActiveState;
        CurrentState = activePoseState;

        PoseStateContext enrichedContext = tickResult.Context;
        HipReconciliationTickResult? hipTickResult = activePoseState.ResolveHipReconciliation(enrichedContext);
        _lastTickResult = hipTickResult is null
            ? new PoseStateMachineTickResult(activePoseState, null, null, Vector3.Zero, enrichedContext)
            : new PoseStateMachineTickResult(
                activePoseState,
                hipTickResult.AppliedHipLocalPosition,
                hipTickResult.LimitedHeadTargetTransform,
                hipTickResult.ResidualFinalHipOffset,
                enrichedContext);

        return _lastTickResult;
    }

    internal bool TryGetLatestHipLocalPosition(out Vector3 hipLocalPosition)
    {
        if (_lastTickResult.HipLocalPosition is Vector3 target)
        {
            hipLocalPosition = target;
            return true;
        }

        hipLocalPosition = Vector3.Zero;
        return false;
    }

    private IPoseState? ResolveStateByIdForExecutor(string id) => ResolveStateByName(id);

    private void OnExecutorStateChanged(IPoseState oldState, IPoseState newState) =>
        StateChanged?.Invoke((PoseState)oldState, (PoseState)newState);

    /// <summary>
    /// Resolves the current state from <see cref="InitialStateId"/> when invoked for the first time.
    /// </summary>
    /// <remarks>
    /// Exposed as a separate helper (rather than only in <see cref="_Ready"/>) so driver code can
    /// force resolution before the first <see cref="Tick"/> when the machine is created
    /// dynamically outside the scene tree.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// When the initial state cannot be resolved against <see cref="States"/>.
    /// </exception>
    public void EnsureInitialStateResolved()
    {
        if (_initialStateResolved)
        {
            return;
        }

        if (States.Length == 0)
        {
            throw new InvalidOperationException(
                $"{nameof(PoseStateMachine)} has no states configured.");
        }

        if (InitialStateId.IsEmpty)
        {
            throw new InvalidOperationException(
                $"{nameof(PoseStateMachine)}.{nameof(InitialStateId)} is not set.");
        }

        PoseState? initial = ResolveStateById(InitialStateId)
            ?? throw new InvalidOperationException(
                $"{nameof(PoseStateMachine)} cannot resolve initial state '{InitialStateId}'.");

        CurrentState = initial;

        if (AnimationTree is not null)
        {
            initial.Start(AnimationTree);
        }

        _initialStateResolved = true;
    }

    private PoseState? ResolveStateById(StringName id)
    {
        for (int i = 0; i < States.Length; i++)
        {
            PoseState candidate = States[i];
            if (candidate.Id == id)
            {
                return candidate;
            }
        }

        return null;
    }

    private PoseState? ResolveStateByName(string id)
    {
        for (int i = 0; i < States.Length; i++)
        {
            PoseState candidate = States[i];
            if (string.Equals(candidate.Id.ToString(), id, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    private void RebuildTransitionView()
    {
        _transitionView.Clear();
        for (int i = 0; i < Transitions.Length; i++)
        {
            _transitionView.Add(Transitions[i]);
        }
    }
}

/// <summary>
/// Per-tick pose-state-machine output returned to immediate callers and cached for downstream consumers.
/// </summary>
public readonly record struct PoseStateMachineTickResult(
    PoseState? ActiveState,
    Vector3? HipLocalPosition,
    Transform3D? LimitedHeadTargetTransform,
    Vector3 ResidualHipOffset,
    PoseStateContext Context);
