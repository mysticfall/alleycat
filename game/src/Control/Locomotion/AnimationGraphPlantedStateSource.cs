using AlleyCat.Core.Logging;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Control.Locomotion;

/// <summary>
/// Runtime ANIM-002 planted-state evaluator for one AnimationTree. On every evaluation it reads the state machine's
/// current and fading-from states and crossfade progress, each state's BlendSpace2D blend position, and every clip
/// node's playback position, derives each contributing clip's effective weight and source time, and applies the
/// <see cref="PlantedCombinationRule" />. The graph state is captured on
/// <see cref="AnimationMixer.SignalName.MixerApplied" />, so it is exactly the state that evaluation used even when
/// parameters are changed later in the same tick.
/// </summary>
/// <remarks>
/// <para>Planted intervals are loaded once from every bound library that carries them (a locomotion package's
/// <see cref="PerFootPlantedIntervals.FileName" /> or an idle catalogue's embedded field) and are bound to their package
/// by identity, clip inventory, and each clip's animation length; the text-hash library binding is not used because it
/// is unavailable in exported builds.</para>
/// <para>A clip without planted intervals, or a state the model does not describe, never marks a foot planted: when it
/// decides under the rule (normalised weight above <see cref="PlantedCombinationRule.WeightThreshold" />) both feet are
/// reported unplanted.</para>
/// <para>Interrupted crossfades are not modelled. The state is read after the evaluation, so when a new transition
/// starts while a crossfade is still running (for example through <c>AnimationNodeStateMachinePlayback.Next</c>), the
/// evaluation that tick still blended the old fading-from state, which the playback no longer reports. That tick is
/// reported with both feet unplanted and counted in <see cref="InterruptedCrossfadeTicks" />; the next tick is modelled
/// exactly again. Whether the previous crossfade could have completed is judged against the time the evaluation
/// actually advanced: the delta passed to <see cref="Advance" /> for a manually driven tree, otherwise one time-scaled
/// physics step.</para>
/// <para>A zero-delta pre-evaluation made through <see cref="PreEvaluate" /> is ignored: only the tick's final
/// evaluation is a planted-state step.</para>
/// <para>Each evaluation reads two state names from the playback object as short-lived <see cref="StringName" />
/// wrappers, which it disposes immediately; everything else reuses preallocated buffers.</para>
/// </remarks>
[GlobalClass]
public partial class AnimationGraphPlantedStateSource : PlantedStateSource
{
    private const double DurationToleranceSeconds = 0.0001d;

    private static readonly PlantedClipIntervals _unmodelledClip = new(
        "<unmodelled>",
        string.Empty,
        string.Empty,
        isLooping: true,
        durationSeconds: 1d,
        [new PlantedIntervalSegment(0d, 1d, true)],
        [new PlantedIntervalSegment(0d, 1d, true)]);

    [ThreadStatic]
    private static AnimationTree? _advancingTree;

    [ThreadStatic]
    private static double _advancingDelta;

    [ThreadStatic]
    private static AnimationTree? _preEvaluatingTree;

    private readonly Dictionary<StringName, StateModel> _states = [];
    private AnimationNodeStateMachinePlayback? _playback;
    private StringName? _playbackParameter;
    private PlantedClipContribution[] _contributions = [];
    private bool[] _modelled = [];
    private float[] _leafWeights = [];
    private int _contributionCount;
    private AnimationTree? _connectedTree;
    private bool _bindingAttempted;
    private ILogger<AnimationGraphPlantedStateSource>? _logger;
    private StateModel? _previousCurrent;
    private bool _previousFading;
    private double _previousFadingPosition;
    private double _previousFadingLength;

    /// <summary>The AnimationTree whose evaluations drive the pose.</summary>
    [Export]
    public AnimationTree? AnimationTree
    {
        get; set;
    }

    /// <summary>Name of the root blend tree's state-machine node that carries the locomotion and idle states.</summary>
    [Export]
    public StringName StateMachineNode { get; set; } = new("States");

    /// <summary>Whether the graph model and planted intervals are bound.</summary>
    public bool IsBound
    {
        get; private set;
    }

    /// <summary>Why binding failed closed, or <see langword="null" />.</summary>
    public string? BindingFailure
    {
        get; private set;
    }

    /// <summary>Physics tick of the latest evaluation, or zero before the first one.</summary>
    public ulong EvaluationTick
    {
        get; private set;
    }

    /// <summary>Planted state of the latest evaluation.</summary>
    public PlantedFeetState LastState
    {
        get; private set;
    }

    /// <summary>Whether every deciding contribution of the latest evaluation was modelled with planted intervals.</summary>
    public bool LastEvaluationModelled
    {
        get; private set;
    }

    /// <summary>Number of evaluations reported unplanted because a running crossfade was interrupted.</summary>
    public int InterruptedCrossfadeTicks
    {
        get; private set;
    }

    /// <summary>Time the latest evaluation advanced the tree by, as used by the interrupted-crossfade guard.</summary>
    public double LastAdvanceDeltaSeconds
    {
        get; private set;
    }

    /// <summary>
    /// Number of planted-state steps taken: one per final AnimationTree evaluation. A zero-delta pre-evaluation made
    /// through <see cref="PreEvaluate" /> is not a step.
    /// </summary>
    public long EvaluationCount
    {
        get; private set;
    }

    /// <summary>Contributions of the latest evaluation, with unnormalised weights.</summary>
    public ReadOnlySpan<PlantedClipContribution> LastContributions => _contributions.AsSpan(0, _contributionCount);

    /// <summary>
    /// Advances a manually driven <paramref name="tree" /> by <paramref name="delta" />, letting every source bound to
    /// it judge that evaluation with the actual advanced time.
    /// </summary>
    /// <param name="tree">The AnimationTree to advance.</param>
    /// <param name="delta">Time to advance by, as passed to <see cref="AnimationMixer.Advance" />.</param>
    public static void Advance(AnimationTree tree, double delta)
    {
        ArgumentNullException.ThrowIfNull(tree);
        (AnimationTree? previousTree, double previousDelta) = (_advancingTree, _advancingDelta);
        (_advancingTree, _advancingDelta) = (tree, delta);
        try
        {
            tree.Advance(delta);
        }
        finally
        {
            (_advancingTree, _advancingDelta) = (previousTree, previousDelta);
        }
    }

    /// <summary>
    /// Makes the sanctioned NAV-001 Technical Requirement 16 zero-delta pre-evaluation of a manually driven
    /// <paramref name="tree" />: <c>Advance(0)</c>, so that a transition requested before it starts and a seek set
    /// after it takes effect on the tick's final evaluation. Sources bound to the tree ignore this evaluation; it is
    /// not a planted-state step.
    /// </summary>
    /// <param name="tree">The AnimationTree to pre-evaluate.</param>
    public static void PreEvaluate(AnimationTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        AnimationTree? previous = _preEvaluatingTree;
        _preEvaluatingTree = tree;
        try
        {
            tree.Advance(0d);
        }
        finally
        {
            _preEvaluatingTree = previous;
        }
    }

    /// <summary>Whether <paramref name="tree" /> is inside a <see cref="PreEvaluate" /> call on this thread.</summary>
    /// <param name="tree">The AnimationTree to check.</param>
    public static bool IsPreEvaluating(AnimationTree tree) => ReferenceEquals(_preEvaluatingTree, tree);

    /// <inheritdoc />
    public override bool TryGetPlantedState(out PlantedFeetState state)
    {
        if (IsBound && EvaluationTick != 0 && EvaluationTick == Engine.GetPhysicsFrames())
        {
            state = LastState;
            return true;
        }

        state = default;
        return false;
    }

    /// <inheritdoc />
    public override void _EnterTree()
    {
        base._EnterTree();
        Connect();
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        base._Ready();
        Connect();
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        Disconnect();
        base._ExitTree();
    }

    private void Connect()
    {
        AnimationTree? tree = AnimationTree;
        if (tree is null || ReferenceEquals(tree, _connectedTree) || !IsInstanceValid(tree))
        {
            return;
        }

        Disconnect();
        tree.MixerApplied += OnMixerApplied;
        _connectedTree = tree;
    }

    private void Disconnect()
    {
        if (_connectedTree is not null && IsInstanceValid(_connectedTree))
        {
            _connectedTree.MixerApplied -= OnMixerApplied;
        }

        _connectedTree = null;
    }

    private void OnMixerApplied()
    {
        // The pre-evaluation's intermediate state (an entered clip still at position 0) is never observed.
        if (ReferenceEquals(_preEvaluatingTree, _connectedTree) || (!IsBound && !TryBind()))
        {
            return;
        }

        Evaluate();
        EvaluationCount++;
        EvaluationTick = Engine.GetPhysicsFrames();
    }

    private void Evaluate()
    {
        AnimationTree tree = _connectedTree!;
        if (_playback is null || !IsInstanceValid(_playback))
        {
            _playback = tree.Get(_playbackParameter!).As<AnimationNodeStateMachinePlayback>();
        }

        _contributionCount = 0;
        if (_playback is null)
        {
            LastState = default;
            LastEvaluationModelled = false;
            return;
        }

        // Both names are fresh native-backed wrappers; dispose them now rather than leave them to the finaliser.
        using StringName current = _playback.GetCurrentNode();
        using StringName fadingFrom = _playback.GetFadingFromNode();
        bool fading = !fadingFrom.IsEmpty;
        double fadingPosition = _playback.GetFadingPosition();
        double fadingLength = _playback.GetFadingLength();
        StateModel? currentState = _states.GetValueOrDefault(current);
        StateModel? fadingState = fading ? _states.GetValueOrDefault(fadingFrom) : null;
        // A tree driven through Advance reports its actual delta; an engine-driven tree advances one time-scaled step.
        LastAdvanceDeltaSeconds = ReferenceEquals(_advancingTree, tree)
            ? _advancingDelta
            : tree.GetPhysicsProcessDeltaTime();
        bool interrupted = AnimationGraphWeightModel.InterruptsCrossfade(
            _previousFading,
            _previousFadingPosition,
            _previousFadingLength,
            LastAdvanceDeltaSeconds,
            currentChanged: _previousCurrent is not null && !ReferenceEquals(currentState, _previousCurrent),
            fading,
            fadingFromIsPreviousCurrent: fadingState is not null && ReferenceEquals(fadingState, _previousCurrent));
        _previousCurrent = currentState;
        _previousFading = fading;
        _previousFadingPosition = fadingPosition;
        _previousFadingLength = fadingLength;
        if (interrupted)
        {
            // The pose of this tick blended a fading-from state the playback no longer reports; see the remarks.
            InterruptedCrossfadeTicks++;
            ResolveLogger()?.LogWarning(
                "Animation graph planted-state source '{Source}': crossfade into '{State}' was interrupted on tick {Tick}; both feet are reported unplanted for that tick.",
                Name.ToString(),
                current.ToString(),
                Engine.GetPhysicsFrames());
            LastState = default;
            LastEvaluationModelled = false;
            return;
        }

        (double currentWeight, double fadingWeight) = AnimationGraphWeightModel.CrossfadeWeights(
            fading,
            fadingPosition,
            fadingLength);

        AddState(tree, current, currentWeight);
        if (fading)
        {
            AddState(tree, fadingFrom, fadingWeight);
        }

        double total = 0d;
        for (int index = 0; index < _contributionCount; index++)
        {
            total += _contributions[index].Weight;
        }

        bool modelled = total > 0d;
        for (int index = 0; modelled && index < _contributionCount; index++)
        {
            modelled = _modelled[index] || !PlantedCombinationRule.Decides(_contributions[index].Weight / total);
        }

        LastEvaluationModelled = modelled;
        LastState = modelled ? PlantedCombinationRule.Evaluate(LastContributions) : default;
    }

    private void AddState(AnimationTree tree, StringName name, double stateWeight)
    {
        if (stateWeight <= 0d)
        {
            return;
        }

        if (!_states.TryGetValue(name, out StateModel? state) || state.Leaves.Length == 0)
        {
            Append(_unmodelledClip, 0d, stateWeight, modelled: false);
            return;
        }

        Span<float> leafWeights = _leafWeights.AsSpan(0, state.Leaves.Length);
        if (state.Space is { } space)
        {
            space.Evaluate(tree.Get(state.BlendPositionParameter!).AsVector2(), leafWeights);
        }
        else
        {
            leafWeights[0] = 1f;
        }

        for (int index = 0; index < state.Leaves.Length; index++)
        {
            double weight = stateWeight * leafWeights[index];
            if (weight <= 0d)
            {
                continue;
            }

            LeafModel leaf = state.Leaves[index];
            if (leaf.Intervals is null)
            {
                Append(_unmodelledClip, 0d, weight, modelled: false);
                continue;
            }

            double position = tree.Get(leaf.PositionParameter).AsDouble();
            Append(leaf.Intervals, leaf.Timeline.SourceTime(position), weight, modelled: true);
        }
    }

    private void Append(PlantedClipIntervals clip, double sourceTime, double weight, bool modelled)
    {
        _contributions[_contributionCount] = new PlantedClipContribution(clip, sourceTime, weight);
        _modelled[_contributionCount] = modelled;
        _contributionCount++;
    }

    private bool TryBind()
    {
        if (_bindingAttempted)
        {
            return false;
        }

        _bindingAttempted = true;
        try
        {
            Bind(_connectedTree!);
            IsBound = true;
            ResolveLogger()?.LogDebug(
                "Animation graph planted-state source '{Source}' bound {States} states.",
                GetPath().ToString(),
                _states.Count);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException or FileNotFoundException)
        {
            BindingFailure = exception.Message;
            _states.Clear();
            ResolveLogger()?.LogError(
                "Animation graph planted-state source '{Source}' failed closed: {Reason}",
                GetPath().ToString(),
                exception.Message);
            return false;
        }
    }

    private void Bind(AnimationTree tree)
    {
        AnimationMixer mixer = tree.GetNodeOrNull<AnimationPlayer>(tree.AnimPlayer) as AnimationMixer ?? tree;
        Dictionary<string, PerFootPlantedIntervals> packages = LoadPackages(mixer);
        AnimationNodeBlendTree root = tree.TreeRoot as AnimationNodeBlendTree
            ?? throw new InvalidOperationException("The AnimationTree root is not a blend tree.");
        AnimationNodeStateMachine machine = root.GetNode(StateMachineNode) as AnimationNodeStateMachine
            ?? throw new InvalidOperationException($"The AnimationTree root has no '{StateMachineNode}' state machine.");
        for (int index = 0; index < machine.GetTransitionCount(); index++)
        {
            if (machine.GetTransition(index).XfadeCurve is not null)
            {
                throw new InvalidOperationException(
                    $"Transition {machine.GetTransitionFrom(index)} → {machine.GetTransitionTo(index)} uses a crossfade curve; only linear crossfades are modelled.");
            }
        }

        string prefix = $"parameters/{StateMachineNode}";
        int maximumLeaves = 1;
        foreach (StringName name in machine.GetNodeList())
        {
            StateModel state = DescribeState(tree, $"{prefix}/{name}", machine.GetNode(name), packages);
            _states[name] = state;
            maximumLeaves = Math.Max(maximumLeaves, state.Leaves.Length);
        }

        _playbackParameter = new StringName($"{prefix}/playback");
        _contributions = new PlantedClipContribution[maximumLeaves * 2];
        _modelled = new bool[maximumLeaves * 2];
        _leafWeights = new float[maximumLeaves];
    }

    private static StateModel DescribeState(
        AnimationTree tree,
        string prefix,
        AnimationNode node,
        Dictionary<string, PerFootPlantedIntervals> packages)
    {
        switch (node)
        {
            case AnimationNodeAnimation animation:
                return new StateModel([DescribeLeaf(tree, animation, $"{prefix}/current_position", packages)], null, null);
            case AnimationNodeBlendSpace2D space:
                return DescribeSpace(tree, prefix, space, packages);
            case AnimationNodeBlendTree blendTree:
                {
                    // A blend tree state is modelled when it wraps one content node in pass-through time scales and time
                    // seeks only: a seek moves the clip's playback position, which is read after the evaluation.
                    StringName? content = null;
                    int contentCount = 0;
                    foreach (StringName child in blendTree.GetNodeList())
                    {
                        if (child == "output" || blendTree.GetNode(child) is AnimationNodeTimeScale or AnimationNodeTimeSeek)
                        {
                            continue;
                        }

                        content = child;
                        contentCount++;
                    }

                    return contentCount != 1
                        ? StateModel.Unmodelled
                        : blendTree.GetNode(content!) switch
                        {
                            AnimationNodeAnimation animation => new StateModel(
                                [DescribeLeaf(tree, animation, $"{prefix}/{content}/current_position", packages)],
                                null,
                                null),
                            AnimationNodeBlendSpace2D space => DescribeSpace(tree, $"{prefix}/{content}", space, packages),
                            _ => StateModel.Unmodelled,
                        };
                }

            default:
                return StateModel.Unmodelled;
        }
    }

    private static StateModel DescribeSpace(
        AnimationTree tree,
        string prefix,
        AnimationNodeBlendSpace2D space,
        Dictionary<string, PerFootPlantedIntervals> packages)
    {
        if (space.BlendMode != AnimationNodeBlendSpace2D.BlendModeEnum.Interpolated)
        {
            return StateModel.Unmodelled;
        }

        int count = space.GetBlendPointCount();
        var leaves = new LeafModel[count];
        var points = new Vector2[count];
        for (int index = 0; index < count; index++)
        {
            points[index] = space.GetBlendPointPosition(index);
            if (space.GetBlendPointNode(index) is not AnimationNodeAnimation animation)
            {
                return StateModel.Unmodelled;
            }

            leaves[index] = DescribeLeaf(tree, animation, $"{prefix}/{space.GetBlendPointName(index)}/current_position", packages);
        }

        // Reading a triangle point first refreshes the space's deferred auto-triangulation.
        _ = space.GetTrianglePoint(0, 0);
        int[] triangles = new int[space.GetTriangleCount() * 3];
        for (int index = 0; index < triangles.Length; index++)
        {
            triangles[index] = space.GetTrianglePoint(index / 3, index % 3);
        }

        return new StateModel(leaves, new BlendSpace2DWeightModel(points, triangles), new StringName($"{prefix}/blend_position"));
    }

    private static LeafModel DescribeLeaf(
        AnimationTree tree,
        AnimationNodeAnimation node,
        string positionParameter,
        Dictionary<string, PerFootPlantedIntervals> packages)
    {
        string animationName = node.Animation.ToString();
        Animation? resource = tree.HasAnimation(node.Animation) ? tree.GetAnimation(node.Animation) : null;
        if (resource is null)
        {
            // A state whose clip the rig does not bind never contributes planted state.
            return new LeafModel(new StringName(positionParameter), null, default);
        }

        int separator = animationName.IndexOf('/', StringComparison.Ordinal);
        string library = separator > 0 ? animationName[..separator] : string.Empty;
        string clip = separator > 0 ? animationName[(separator + 1)..] : animationName;
        PlantedClipIntervals? intervals = null;
        if (packages.TryGetValue(library, out PerFootPlantedIntervals? package) && package.TryGetClip(clip, out PlantedClipIntervals found))
        {
            intervals = Math.Abs(resource.Length - found.DurationSeconds) <= DurationToleranceSeconds
                ? found
                : throw new InvalidDataException(
                    $"Animation '{animationName}' length {resource.Length} differs from its planted-interval duration {found.DurationSeconds}.");
        }

        return new LeafModel(
            new StringName(positionParameter),
            intervals,
            new ClipTimelineMapping(
                resource.Length,
                node.UseCustomTimeline ? node.LoopMode : resource.LoopMode,
                node.UseCustomTimeline,
                node.TimelineLength,
                node.StretchTimeScale,
                node.StartOffset,
                node.PlayMode == AnimationNodeAnimation.PlayModeEnum.Backward));
    }

    private static Dictionary<string, PerFootPlantedIntervals> LoadPackages(AnimationMixer mixer)
    {
        var packages = new Dictionary<string, PerFootPlantedIntervals>(StringComparer.Ordinal);
        foreach (StringName libraryName in mixer.GetAnimationLibraryList())
        {
            string path = mixer.GetAnimationLibrary(libraryName)?.ResourcePath ?? string.Empty;
            if (!path.EndsWith(".tres", StringComparison.Ordinal))
            {
                continue;
            }

            string directory = path[..(path.LastIndexOf('/') + 1)];
            string cataloguePath = $"{path[..^".tres".Length]}_catalogue.json";
            string intervalsPath = directory + PerFootPlantedIntervals.FileName;
            if (Godot.FileAccess.FileExists(intervalsPath))
            {
                var catalogue = StandingLocomotionCatalogue.Parse(Godot.FileAccess.GetFileAsString(cataloguePath), cataloguePath);
                var intervals = PerFootPlantedIntervals.Load(intervalsPath);
                intervals.ValidateAgainst(
                    PerFootPlantedIntervals.CharacterOf(catalogue.AnimationLibraryPath),
                    catalogue.Package,
                    catalogue.PackageVariant,
                    [.. catalogue.Clips.Values.Select(clip => new PlantedClipExpectation(
                        clip.Identity,
                        clip.Role,
                        StandingLocomotionCatalogue.CapabilityIdentifier(clip.Capability),
                        clip.IsLooping,
                        clip.DurationSeconds))]);
                packages[libraryName.ToString()] = intervals;
            }
            else if (Godot.FileAccess.FileExists(cataloguePath))
            {
                string json = Godot.FileAccess.GetFileAsString(cataloguePath);
                if (!json.Contains($"\"{PerFootPlantedIntervals.CatalogueField}\"", StringComparison.Ordinal))
                {
                    continue;
                }

                var intervals = PerFootPlantedIntervals.ParseValidatedIdleCatalogue(
                    json,
                    PerFootPlantedIntervals.CharacterOf(cataloguePath),
                    out string libraryFile);
                if (!path.EndsWith("/" + libraryFile, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Idle catalogue '{cataloguePath}' binds library '{libraryFile}', not '{path}'.");
                }

                packages[libraryName.ToString()] = intervals;
            }
        }

        return packages;
    }

    private ILogger<AnimationGraphPlantedStateSource>? ResolveLogger()
    {
        if (_logger is null && GameLoggerResolver.TryResolve(out ILogger<AnimationGraphPlantedStateSource>? logger))
        {
            _logger = logger;
        }

        return _logger;
    }

    private sealed record LeafModel(StringName PositionParameter, PlantedClipIntervals? Intervals, ClipTimelineMapping Timeline);

    private sealed record StateModel(LeafModel[] Leaves, BlendSpace2DWeightModel? Space, StringName? BlendPositionParameter)
    {
        public static StateModel Unmodelled { get; } = new([], null, null);
    }
}
