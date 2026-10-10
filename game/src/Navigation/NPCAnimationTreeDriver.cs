using AlleyCat.Control.Locomotion;
using Godot;

namespace AlleyCat.Navigation;

/// <summary>A clip transition as issued to the NPC AnimationTree (NAV-001 Technical Requirement 17 evidence).</summary>
/// <param name="PhysicsTick">Physics tick whose final evaluation entered the clip.</param>
/// <param name="From">State current before the transition.</param>
/// <param name="To">Entered state.</param>
/// <param name="EntryPositionSeconds">Clip position the entered state was sought to (the slice start).</param>
/// <param name="CrossfadeSeconds">Authored crossfade duration, or zero for a teleport.</param>
public readonly record struct NPCClipTransitionRecord(
    ulong PhysicsTick,
    string From,
    string To,
    double EntryPositionSeconds,
    double CrossfadeSeconds);

/// <summary>
/// Sole driver of a manually processed NPC AnimationTree (NAV-001 Technical Requirement 16). Each tick it issues the
/// pending clip transition, if any, and then advances the tree once by the tick's delta. A transition enters its
/// clip at a chosen position through the authored graph only: <c>Travel</c> along an authored direct crossfade (or
/// <c>Start</c> for a teleport), the sanctioned zero-delta pre-evaluation
/// (<see cref="AnimationGraphPlantedStateSource.PreEvaluate" />), then the entered state's <c>TimeSeek</c> request,
/// which the tick's final evaluation honours. On that entry tick the entered clip is at the requested position, it
/// contributes no root motion, and the crossfade has advanced by the tick's delta, so the outgoing clip's weighted
/// root motion continues normally.
/// </summary>
/// <remarks>
/// <para>Every NPC state must be a blend tree of one <c>Clip</c> animation node and one <c>Seek</c> time seek with
/// <c>explicit_elapse</c> disabled, so a seek never integrates root motion across the jump. Crossfade durations are
/// the authored transitions' own; the driver never edits the graph.</para>
/// <para>Only one crossfade runs at a time: a request is refused while a crossfade is still running, because the
/// engine would defer it to the end of that crossfade.</para>
/// <para>Seam timing: a transition requested after the tick that leaves the current clip at <c>a</c> enters on the
/// next tick with the entered clip at its entry position <c>b</c> and the outgoing clip at <c>a + Δ</c>, one physics
/// delta ahead. This is the entry lag the arrival seam table evaluates each seam <c>(a, b)</c> with
/// (<see cref="Arrival.SeamTable.EntryLagSeconds" />), and each authored crossfade must equal that pair's seam-table
/// duration (<see cref="Arrival.SeamPair.CrossfadeSeconds" />).</para>
/// </remarks>
public sealed class NPCAnimationTreeDriver
{
    /// <summary>Prefix of the states this driver may enter.</summary>
    public const string StatePrefix = "NPC";

    /// <summary>Name of the animation node inside every NPC state.</summary>
    public const string ClipNodeName = "Clip";

    /// <summary>Name of the time-seek node inside every NPC state.</summary>
    public const string SeekNodeName = "Seek";

    private const int MaximumRecordedTransitions = 512;
    private const double CrossfadeTolerance = 1e-4d;

    private readonly AnimationTree _tree;
    private readonly Action<ulong, double>? _beforeAdvance;
    private readonly StringName _playbackParameter;
    private readonly Dictionary<StringName, StateBinding> _states = [];
    private readonly Dictionary<(StringName From, StringName To), double> _crossfades = [];
    private readonly List<NPCClipTransitionRecord> _transitions = new(MaximumRecordedTransitions);
    private Pending? _pending;

    /// <summary>Binds the driver to the tree's authored NPC states and transitions.</summary>
    /// <param name="tree">The NPC AnimationTree, already set to manual process callbacks.</param>
    /// <param name="stateMachineNode">Name of the root blend tree's state machine node.</param>
    /// <param name="beforeAdvance">Optional tick-time admission before transitions and final evaluation.</param>
    /// <exception cref="InvalidOperationException">An NPC state does not have the required shape.</exception>
    public NPCAnimationTreeDriver(AnimationTree tree, StringName stateMachineNode, Action<ulong, double>? beforeAdvance = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(stateMachineNode);
        _tree = tree;
        _beforeAdvance = beforeAdvance;
        AnimationNodeStateMachine machine = (tree.TreeRoot as AnimationNodeBlendTree)?.GetNode(stateMachineNode) as AnimationNodeStateMachine
            ?? throw new InvalidOperationException($"The AnimationTree root has no '{stateMachineNode}' state machine.");
        string prefix = $"parameters/{stateMachineNode}";
        _playbackParameter = new StringName($"{prefix}/playback");
        foreach (StringName state in machine.GetNodeList())
        {
            if (state.ToString().StartsWith(StatePrefix, StringComparison.Ordinal))
            {
                _states[state] = BindState(tree, prefix, state, machine.GetNode(state));
            }
        }

        for (int index = 0; index < machine.GetTransitionCount(); index++)
        {
            AnimationNodeStateMachineTransition transition = machine.GetTransition(index);
            if (transition.AdvanceMode != AnimationNodeStateMachineTransition.AdvanceModeEnum.Disabled)
            {
                _crossfades[(machine.GetTransitionFrom(index), machine.GetTransitionTo(index))] = transition.XfadeTime;
            }
        }
    }

    /// <summary>Whether a transition waits to be issued at the start of the next tick.</summary>
    public bool HasPendingTransition => _pending is not null;

    /// <summary>State of the pending transition, or <see langword="null" /> when none is pending.</summary>
    public StringName? PendingTransitionState => _pending?.State;

    /// <summary>Number of zero-delta pre-evaluations made (one per issued transition).</summary>
    public long PreEvaluationCount
    {
        get; private set;
    }

    /// <summary>Number of final evaluations made (one per advanced tick).</summary>
    public long EvaluationCount
    {
        get; private set;
    }

    /// <summary>The most recently issued transitions, oldest first (bounded).</summary>
    public IReadOnlyList<NPCClipTransitionRecord> IssuedTransitions => _transitions;

    /// <summary>Whether <paramref name="state" /> is an NPC clip state this driver can enter.</summary>
    public bool HasState(StringName state) => _states.ContainsKey(state);

    /// <summary>Length of an NPC state's clip in seconds.</summary>
    public double ClipLength(StringName state) => _states[state].Length;

    /// <summary>Whether an NPC state's clip loops natively.</summary>
    public bool IsLooping(StringName state) => _states[state].Looping;

    /// <summary>The bound animation name (<c>library/clip</c>) an NPC state plays.</summary>
    public StringName ClipAnimation(StringName state) => _states[state].Animation;

    /// <summary>Authored crossfade duration of the direct transition, or <see langword="null" /> when none exists.</summary>
    public double? AuthoredCrossfade(StringName from, StringName to)
        => _crossfades.TryGetValue((from, to), out double crossfade) ? crossfade : null;

    /// <summary>
    /// Requests that the next tick's final evaluation enters <paramref name="state" /> at
    /// <paramref name="entryPositionSeconds" /> with a crossfade of <paramref name="crossfadeSeconds" />.
    /// A positive crossfade must equal the authored direct transition's duration from the current state; zero
    /// teleports without a crossfade.
    /// </summary>
    /// <returns>
    /// <see langword="false" /> when a crossfade is still running or a transition is already pending; the request is
    /// then not recorded.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The state is not an NPC clip state, the position lies outside the clip, or the crossfade does not match an
    /// authored direct transition.
    /// </exception>
    public bool TryRequestTransition(StringName state, double entryPositionSeconds, double crossfadeSeconds)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!_states.TryGetValue(state, out StateBinding? binding))
        {
            throw new ArgumentException($"'{state}' is not an NPC clip state of the AnimationTree.", nameof(state));
        }

        if (!double.IsFinite(entryPositionSeconds) || entryPositionSeconds < 0d || entryPositionSeconds > binding.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(entryPositionSeconds),
                FormattableString.Invariant($"Entry position {entryPositionSeconds} s lies outside '{state}' (0..{binding.Length} s)."));
        }

        AnimationNodeStateMachinePlayback playback = Playback();
        using StringName fading = playback.GetFadingFromNode();
        if (_pending is not null || !fading.IsEmpty)
        {
            return false;
        }

        if (crossfadeSeconds > 0d)
        {
            using StringName current = playback.GetCurrentNode();
            double? authored = AuthoredCrossfade(current, state);
            if (authored is not { } duration || Math.Abs(duration - crossfadeSeconds) > CrossfadeTolerance)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"No authored {crossfadeSeconds} s transition leads from '{current}' to '{state}' (authored: {(authored is { } value ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) + " s" : "none")})."),
                    nameof(crossfadeSeconds));
            }
        }
        else if (crossfadeSeconds < 0d || !double.IsFinite(crossfadeSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(crossfadeSeconds), "A crossfade must be zero or positive.");
        }

        _pending = new Pending(state, binding, entryPositionSeconds, crossfadeSeconds);
        return true;
    }

    /// <summary>
    /// Issues the pending transition, if any, then makes the tick's single final evaluation by
    /// <paramref name="delta" />.
    /// </summary>
    public void Advance(double delta)
    {
        _beforeAdvance?.Invoke(Engine.GetPhysicsFrames(), delta);
        if (_pending is { } pending)
        {
            _pending = null;
            Issue(pending);
        }

        AnimationGraphPlantedStateSource.Advance(_tree, delta);
        EvaluationCount++;
    }

    /// <summary>Drops a pending transition that has not been issued yet.</summary>
    public void CancelPendingTransition() => _pending = null;

    private void Issue(Pending pending)
    {
        AnimationNodeStateMachinePlayback playback = Playback();
        using StringName from = playback.GetCurrentNode();
        if (pending.Crossfade > 0d)
        {
            playback.Travel(pending.State, resetOnTeleport: true);
        }
        else
        {
            playback.Start(pending.State, reset: true);
        }

        // The pre-evaluation starts the transition; the seek then takes effect on the final evaluation, where the
        // entered clip is evaluated at the slice start with no root motion of its own.
        AnimationGraphPlantedStateSource.PreEvaluate(_tree);
        PreEvaluationCount++;
        _tree.Set(pending.Binding.SeekRequestParameter, pending.Position);
        if (_transitions.Count == MaximumRecordedTransitions)
        {
            _transitions.RemoveAt(0);
        }

        _transitions.Add(new NPCClipTransitionRecord(
            Engine.GetPhysicsFrames(),
            from.ToString(),
            pending.State.ToString(),
            pending.Position,
            pending.Crossfade));
    }

    private AnimationNodeStateMachinePlayback Playback()
        => _tree.Get(_playbackParameter).As<AnimationNodeStateMachinePlayback>()
            ?? throw new InvalidOperationException("The NPC AnimationTree has no state-machine playback.");

    private static StateBinding BindState(AnimationTree tree, string prefix, StringName state, AnimationNode node)
    {
        if (node is not AnimationNodeBlendTree blendTree
            || blendTree.GetNode(ClipNodeName) is not AnimationNodeAnimation clip
            || blendTree.GetNode(SeekNodeName) is not AnimationNodeTimeSeek seek)
        {
            throw new InvalidOperationException(
                $"NPC state '{state}' must be a blend tree of a '{ClipNodeName}' animation node and a '{SeekNodeName}' time seek.");
        }

        if (seek.ExplicitElapse)
        {
            throw new InvalidOperationException($"NPC state '{state}' seeks with explicit elapse, which integrates root motion across the jump.");
        }

        if (clip.UseCustomTimeline)
        {
            throw new InvalidOperationException($"NPC state '{state}' uses a custom timeline; NPC clips play at native speed.");
        }

        Animation animation = tree.HasAnimation(clip.Animation)
            ? tree.GetAnimation(clip.Animation)
            : throw new InvalidOperationException($"NPC state '{state}' plays '{clip.Animation}', which the AnimationTree does not bind.");
        return new StateBinding(
            new StringName($"{prefix}/{state}/{SeekNodeName}/seek_request"),
            clip.Animation,
            animation.Length,
            animation.LoopMode != Animation.LoopModeEnum.None);
    }

    private sealed record StateBinding(StringName SeekRequestParameter, StringName Animation, double Length, bool Looping);

    private sealed record Pending(StringName State, StateBinding Binding, double Position, double Crossfade);
}
