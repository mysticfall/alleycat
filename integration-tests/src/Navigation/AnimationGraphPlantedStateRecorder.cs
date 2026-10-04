using System.Globalization;
using System.Text;
using AlleyCat.Control.Locomotion;
using Godot;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>
/// NAV-001 Technical Requirement 17 recorder for A2 planted-foot slip. Each physics tick it records the NPC
/// AnimationTree graph state used for that tick's evaluation (captured on <see cref="AnimationMixer.SignalName.MixerApplied"/>:
/// state-machine current and fading-from states, crossfade position and length, BlendSpace2D blend positions, and every
/// relevant clip node's playback position, plus the pre-IK ball joints of the mixer-written pose, root-relative in the
/// actor frame) and the rendered post-IK ball joints (captured on
/// <see cref="Skeleton3D.SignalName.SkeletonUpdated"/>). From that record alone it recomputes each clip's effective weight
/// (state-machine crossfade weight × recomputed BlendSpace2D triangle weight), maps each node playback position to the
/// clip's source time (including any custom timeline with <c>stretch_time_scale</c>), and applies the ANIM-002
/// <see cref="PlantedCombinationRule"/>. It never reads CharacterIK or any foot-planting state.
/// </summary>
/// <remarks>
/// The weight and time mapping replicates Godot 4.7 <c>AnimationNodeStateMachinePlayback::_process</c> (linear
/// <c>fading_pos / fading_time</c>, clamped, with <c>CMP_EPSILON</c> floors), <c>AnimationNodeBlendSpace2D::_process</c>
/// (interpolated mode: first containing triangle's barycentric weights, else closest edge) and
/// <c>AnimationNodeAnimation::_process</c> (source = (node position + start offset) × animation length / timeline length
/// when stretched, then wrap, ping-pong, or clamp; backward play mode mirrors). Graph shapes it does not model fail closed,
/// including any root-level layer whose filter would let a non-state-machine input write a leg bone.
/// <para>The state machine is read after evaluation, and the engine starts a transition only after blending the tick's
/// pose, so on a transition's first tick the recorder sees the new state at <c>CMP_EPSILON</c> and the state the engine
/// actually blended at the rest. When a fade A → B finishes on the tick that starts B → C, A was blended only at
/// <c>CMP_EPSILON</c>, so dropping it is exact under the rule. Godot 4.7 does not start a path or auto-advance transition
/// while a crossfade runs (<c>_can_transition_to_next</c>), and start and teleport requests clear the fade before
/// blending; an interrupted crossfade can arise only from an explicit <c>next</c> request. If one occurs, the engine
/// blended A and B on that tick (B at the advanced progress, A at the rest; C only probed for length), but the
/// post-evaluation playback already reports C fading from B and has discarded A. The recorder detects this from the
/// previous tick's record (a fresh fade from the previous current state while the previous fade had not finished),
/// re-reads A's still-valid blend position and playback positions, and records the exact A/B contributions. Planted
/// state on such a tick is conservative: a foot is planted only if it is planted under the exact A/B weights, under B
/// alone, and under A alone. <see cref="InterruptedCrossfadeTicks"/> counts these ticks.</para>
/// <para>Both toe (ball-joint) bones are required rig data; binding fails on a rig without either.</para>
/// <para>The oracle bone rotations for time-mapping validation are read on <c>mixer_applied</c>, before any
/// <see cref="SkeletonModifier3D"/> (CharacterIK) runs, so they validate only the mixer mapping; the ball joints are read
/// post-IK on <c>skeleton_updated</c>.</para>
/// </remarks>
internal sealed class AnimationGraphPlantedStateRecorder : IDisposable
{
    private const string StateMachineNode = "States";
    private const double EngineEpsilon = 0.00001d;
    private const double DurationToleranceSeconds = 0.0001d;


    private readonly AnimationTree _tree;
    private readonly Skeleton3D _skeleton;
    private readonly Node3D _actor;
    private readonly StringName _playbackParameter;
    private readonly Dictionary<string, StateDescriptor> _states;
    private readonly int _leftFoot;
    private readonly int _rightFoot;
    private readonly Vector3 _leftBallInFoot;
    private readonly Vector3 _rightBallInFoot;
    private readonly int _leftToes;
    private readonly int _rightToes;
    private readonly int[] _oracleBoneIndices;
    private readonly List<PlantedStateSample> _samples = new(4096);
    private GraphRecord? _latestGraph;
    private Quaternion[] _latestPreIKRotations = [];
    private Vector3 _latestPreIKLeftBall;
    private Vector3 _latestPreIKRightBall;
    private bool _attached;

    private AnimationGraphPlantedStateRecorder(
        Node3D actor,
        AnimationTree tree,
        Skeleton3D skeleton,
        Dictionary<string, StateDescriptor> states,
        Dictionary<string, ClipTrajectory> trajectories)
    {
        _actor = actor;
        _tree = tree;
        _skeleton = skeleton;
        _states = states;
        Trajectories = trajectories;
        _playbackParameter = new StringName($"parameters/{StateMachineNode}/playback");
        _leftFoot = RequireBone(skeleton, "LeftFoot");
        _rightFoot = RequireBone(skeleton, "RightFoot");
        _leftToes = RequireBone(skeleton, "LeftToes");
        _rightToes = RequireBone(skeleton, "RightToes");
        _leftBallInFoot = BallInFoot(skeleton, _leftToes, _leftFoot);
        _rightBallInFoot = BallInFoot(skeleton, _rightToes, _rightFoot);
        _oracleBoneIndices = [.. OracleBones.Select(bone => RequireBone(skeleton, bone))];
        OracleRestRotations = [.. _oracleBoneIndices.Select(bone => skeleton.GetBoneRest(bone).Basis.GetRotationQuaternion())];
    }

    /// <summary>
    /// Names of the bones whose local pose rotation is recorded for time-mapping validation: Hips and both toes.
    /// </summary>
    public static IReadOnlyList<string> OracleBones { get; } = ["Hips", "LeftToes", "RightToes"];

    /// <summary>Rest rotations of <see cref="OracleBones"/>, the mixer's blend origin for skeleton tracks.</summary>
    public Quaternion[] OracleRestRotations
    {
        get;
    }

    /// <summary>
    /// Bind-time ball-joint trajectory tables (<see cref="ClipTrajectoryTableBuilder"/>, the planner's own table
    /// derivation) of every locomotion-package clip and every idle-catalogue clip, keyed by the bound animation name
    /// (<c>library/clip</c>).
    /// </summary>
    public IReadOnlyDictionary<string, ClipTrajectory> Trajectories
    {
        get;
    }

    /// <summary>Recorded ticks in order, one per physics tick.</summary>
    public IReadOnlyList<PlantedStateSample> Samples => _samples;

    /// <summary>Ticks whose rendered pose had no graph record from the same tick.</summary>
    public int UnpairedTicks
    {
        get; private set;
    }

    /// <summary>Skeleton updates that repeated an already-recorded tick (the later one replaces the earlier).</summary>
    public int RepeatedSkeletonUpdates
    {
        get; private set;
    }

    /// <summary>Recorded ticks on which a new transition interrupted a still-running crossfade.</summary>
    public int InterruptedCrossfadeTicks => _samples.Count(sample => sample.Graph.Interruption is not null);

    /// <summary>Interrupted-crossfade ticks whose conservative planted state is stricter than the exact A/B weights.</summary>
    public int ConservativeStricterTicks => _samples.Count(sample => sample.Planted != sample.ExactPlanted);

    /// <summary>
    /// Transition-start ticks without a previous-tick record, where an interrupted crossfade cannot be ruled out.
    /// </summary>
    public int UndeterminedTransitionStarts
    {
        get; private set;
    }

    /// <summary>
    /// Root blend-tree layers checked at bind time, each confirmed to pass the state machine's leg bones unfiltered.
    /// </summary>
    public IReadOnlyList<string> VerifiedLayers
    {
        get; private init;
    } = [];

    /// <summary>Leg bones (Hips and both leg chains) the layer-filter check covered.</summary>
    public IReadOnlyList<string> LegBones
    {
        get; private init;
    } = [];

    /// <summary>
    /// Binds to the NPC's AnimationTree, AnimationPlayer libraries, and skeleton. Each library with ANIM-002 planted
    /// intervals (a locomotion package's <see cref="PerFootPlantedIntervals.FileName"/> or an idle catalogue's embedded
    /// field) is loaded and validated against its package before use.
    /// </summary>
    public static AnimationGraphPlantedStateRecorder Bind(Node3D actor, AnimationTree tree, AnimationPlayer player, Skeleton3D skeleton)
    {
        Dictionary<string, PerFootPlantedIntervals> packages = LoadPackages(player);
        Dictionary<string, ClipTrajectory> trajectories = BuildTrajectories(player, tree, skeleton);
        AnimationNodeBlendTree root = tree.TreeRoot as AnimationNodeBlendTree
            ?? throw new InvalidOperationException("SETUP: the NPC AnimationTree root is not a blend tree.");
        AnimationNodeStateMachine machine = root.GetNode(StateMachineNode) as AnimationNodeStateMachine
            ?? throw new InvalidOperationException($"SETUP: the NPC AnimationTree has no '{StateMachineNode}' state machine.");
        for (int index = 0; index < machine.GetTransitionCount(); index++)
        {
            if (machine.GetTransition(index).XfadeCurve is not null)
            {
                throw new InvalidOperationException(
                    $"SETUP: transition {machine.GetTransitionFrom(index)} → {machine.GetTransitionTo(index)} uses an xfade curve; the recorder models linear crossfades only.");
            }
        }

        var states = new Dictionary<string, StateDescriptor>(StringComparer.Ordinal);
        foreach (StringName state in machine.GetNodeList())
        {
            string name = state.ToString();
            states[name] = DescribeState(tree, name, machine.GetNode(state), packages);
        }

        string[] legBones = LegBoneNames(skeleton);
        string[] layers = VerifyLayerFilters(root, legBones);
        return new AnimationGraphPlantedStateRecorder(actor, tree, skeleton, states, trajectories) { VerifiedLayers = layers, LegBones = legBones };
    }

    /// <summary>
    /// Builds the bind-time ball-joint tables of every library that has a standing-locomotion catalogue, plus the clips
    /// of every idle catalogue (with embedded planted intervals) as uncatalogued clips, on the NPC's own skeleton and
    /// root-motion track, so the ball samples carry the rig's motion scale.
    /// </summary>
    private static Dictionary<string, ClipTrajectory> BuildTrajectories(AnimationPlayer player, AnimationTree tree, Skeleton3D skeleton)
    {
        var trajectories = new Dictionary<string, ClipTrajectory>(StringComparer.Ordinal);
        var idle = new List<(string Library, UncataloguedClip Clip)>();
        foreach (StringName library in player.GetAnimationLibraryList())
        {
            AnimationLibrary? resource = player.GetAnimationLibrary(library);
            string path = resource?.ResourcePath ?? string.Empty;
            string catalogue = path.EndsWith(".tres", StringComparison.Ordinal) ? $"{path[..^".tres".Length]}_catalogue.json" : string.Empty;
            if (resource is null
                || catalogue.Length == 0
                || !Godot.FileAccess.FileExists(catalogue)
                || !Godot.FileAccess.GetFileAsString(catalogue).Contains($"\"{PerFootPlantedIntervals.CatalogueField}\"", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (PlantedClipIntervals clip in PerFootPlantedIntervals.LoadValidatedIdleCatalogue(catalogue).Clips.Values)
            {
                idle.Add((library.ToString(), new UncataloguedClip(clip.Identity, clip.Role, clip.DurationSeconds, resource.GetAnimation(clip.Identity))));
            }
        }

        foreach (StringName library in player.GetAnimationLibraryList())
        {
            AnimationLibrary? resource = player.GetAnimationLibrary(library);
            string path = resource?.ResourcePath ?? string.Empty;
            string catalogue = path.EndsWith(".tres", StringComparison.Ordinal) ? $"{path[..^".tres".Length]}_catalogue.json" : string.Empty;
            string directory = path[..(path.LastIndexOf('/') + 1)];
            if (resource is null || catalogue.Length == 0 || !Godot.FileAccess.FileExists(directory + PerFootPlantedIntervals.FileName))
            {
                continue;
            }

            ClipTrajectoryTable table = ClipTrajectoryTableBuilder.Build(
                StandingLocomotionCatalogue.Parse(Godot.FileAccess.GetFileAsString(catalogue), catalogue),
                resource,
                skeleton,
                tree.RootMotionTrack,
                idle.Select(entry => entry.Clip));
            foreach (ClipTrajectory trajectory in table.Clips.Values)
            {
                string owner = trajectory.Capability is null
                    ? idle.First(entry => entry.Clip.Identity == trajectory.Identity).Library
                    : library.ToString();
                trajectories[$"{owner}/{trajectory.Identity}"] = trajectory;
            }
        }

        return trajectories.Count > 0
            ? trajectories
            : throw new InvalidOperationException("SETUP: the NPC AnimationPlayer binds no standing-locomotion library to build ball-joint tables from.");
    }

    /// <summary>Hips plus every bone under the two upper legs (legs, feet, toes).</summary>
    private static string[] LegBoneNames(Skeleton3D skeleton)
    {
        var bones = new List<string> { "Hips" };
        _ = RequireBone(skeleton, "Hips");
        var pending = new Stack<int>([RequireBone(skeleton, "LeftUpperLeg"), RequireBone(skeleton, "RightUpperLeg")]);
        while (pending.TryPop(out int bone))
        {
            bones.Add(skeleton.GetBoneName(bone));
            foreach (int child in skeleton.GetBoneChildren(bone))
            {
                pending.Push(child);
            }
        }

        return [.. bones];
    }

    /// <summary>
    /// Bind-time check that the root layers between the state machine and the output (the Eyes and Hand Blend2 and
    /// OneShot nodes) filter out every leg bone, so the leg pose is the state machine's alone. The main chain is followed
    /// through input 0 from <c>output</c> to <see cref="StateMachineNode"/>; each node on it must be a filtered
    /// Blend2 or OneShot. Every other filter-capable root node must also exclude the leg bones.
    /// </summary>
    private static string[] VerifyLayerFilters(AnimationNodeBlendTree root, string[] legBones)
    {
        Godot.Collections.Array connections = root.Get("node_connections").AsGodotArray();
        var inputs = new Dictionary<(string Node, int Input), string>();
        for (int index = 0; index + 2 < connections.Count; index += 3)
        {
            inputs[(connections[index].AsString(), connections[index + 1].AsInt32())] = connections[index + 2].AsString();
        }

        var chain = new List<string>();
        string node = "output";
        while (true)
        {
            if (!inputs.TryGetValue((node, 0), out string? source))
            {
                throw new InvalidOperationException($"SETUP: root blend-tree node '{node}' has no input 0 on the path to '{StateMachineNode}'.");
            }

            if (source == StateMachineNode)
            {
                break;
            }

            if (root.GetNode(source) is not (AnimationNodeBlend2 or AnimationNodeOneShot) || chain.Contains(source))
            {
                throw new InvalidOperationException(
                    $"SETUP: root node '{source}' ({root.GetNode(source).GetClass()}) between output and '{StateMachineNode}' is not a modelled Blend2/OneShot layer.");
            }

            chain.Add(source);
            node = source;
        }

        var verified = new List<string>();
        foreach (StringName child in root.GetNodeList())
        {
            string name = child.ToString();
            AnimationNode layer = root.GetNode(child);
            if (layer is not (AnimationNodeBlend2 or AnimationNodeBlend3 or AnimationNodeOneShot or AnimationNodeAdd2
                or AnimationNodeAdd3 or AnimationNodeSub2))
            {
                continue;
            }

            if (!layer.FilterEnabled)
            {
                throw new InvalidOperationException($"SETUP: root layer '{name}' has no filter, so it can blend the leg bones.");
            }

            string[] filtered = [.. layer.Get("filters").AsGodotArray()
                .Select(path => path.AsNodePath())
                .Where(path => legBones.Contains(path.GetConcatenatedSubNames().ToString(), StringComparer.Ordinal))
                .Select(path => path.ToString())];
            string[] direct = [.. legBones.Where(bone => layer.IsPathFiltered(new NodePath($"%GeneralSkeleton:{bone}")))];
            if (filtered.Length > 0 || direct.Length > 0)
            {
                throw new InvalidOperationException(
                    $"SETUP: root layer '{name}' filters leg bone track(s) {string.Join(", ", filtered.Concat(direct).Distinct())}.");
            }

            verified.Add(name);
        }

        string[] missing = [.. chain.Where(layer => !verified.Contains(layer))];
        return missing.Length == 0
            ? [.. verified]
            : throw new InvalidOperationException($"SETUP: chain layers {string.Join(", ", missing)} were not verified.");
    }

    /// <summary>Starts recording on the tree's and skeleton's signals.</summary>
    public void Attach()
    {
        if (_attached)
        {
            return;
        }

        _tree.MixerApplied += OnMixerApplied;
        _skeleton.SkeletonUpdated += OnSkeletonUpdated;
        _attached = true;
    }

    /// <summary>Stops recording.</summary>
    public void Detach()
    {
        if (!_attached)
        {
            return;
        }

        if (GodotObject.IsInstanceValid(_tree))
        {
            _tree.MixerApplied -= OnMixerApplied;
        }

        if (GodotObject.IsInstanceValid(_skeleton))
        {
            _skeleton.SkeletonUpdated -= OnSkeletonUpdated;
        }

        _attached = false;
    }

    /// <inheritdoc/>
    public void Dispose() => Detach();

    /// <summary>The animation resource a recorded contribution samples, for time-mapping validation.</summary>
    public Animation AnimationOf(ClipContribution contribution) =>
        _tree.GetAnimation(contribution.Animation)
            ?? throw new InvalidOperationException($"Animation '{contribution.Animation}' is not bound to the tree.");

    /// <summary>
    /// Recomputes the tick's contributions from a raw graph record: state-machine crossfade weights multiplied by
    /// recomputed BlendSpace2D weights, each clip's source time from its node playback position, all normalised.
    /// </summary>
    public ClipContribution[] Evaluate(GraphRecord record)
    {
        var contributions = new List<ClipContribution>(6);
        if (record.Interruption is { } interruption)
        {
            // The engine blended the previous current state (now reported as fading-from) at the advanced progress and
            // the discarded older fading-from state at the rest; the new current state was not blended.
            (double previousCurrent, double older) = FloorWeights(interruption.FadeBlend);
            AddState(contributions, record.Fading!, previousCurrent, record.FadingBlend, record.FadingPositions);
            AddState(contributions, interruption.State, older, interruption.Blend, interruption.Positions);
        }
        else
        {
            (double current, double fading) = CrossfadeWeights(record);
            AddState(contributions, record.Current, current, record.CurrentBlend, record.CurrentPositions);
            if (record.Fading is { } fadingState)
            {
                AddState(contributions, fadingState, fading, record.FadingBlend, record.FadingPositions);
            }
        }

        return Normalise(contributions, record);
    }

    private static ClipContribution[] Normalise(List<ClipContribution> contributions, GraphRecord record)
    {
        double total = contributions.Sum(contribution => contribution.RawWeight);
        return total <= 0d
            ? throw new InvalidOperationException($"Tick {record.Tick}: graph state {record.Describe()} yields no weighted clip.")
            : [.. contributions.Select(contribution => contribution with { Weight = contribution.RawWeight / total })];
    }

    private static PlantedFeetState Rule(IReadOnlyList<ClipContribution> contributions)
    {
        var rule = new PlantedClipContribution[contributions.Count];
        for (int index = 0; index < contributions.Count; index++)
        {
            ClipContribution contribution = contributions[index];
            rule[index] = new(contribution.Intervals, contribution.SourceTimeSeconds, contribution.Weight);
        }

        return PlantedCombinationRule.Evaluate(rule);
    }

    /// <summary>
    /// Planted state of a tick. On an interrupted-crossfade tick it is the conjunction of the exact A/B weights, B alone,
    /// and A alone, so no plausible interpretation of the blended pose is more permissive.
    /// </summary>
    private static (PlantedFeetState Planted, PlantedFeetState Exact) Classify(GraphRecord record, ClipContribution[] contributions)
    {
        PlantedFeetState exact = Rule(contributions);
        if (record.Interruption is not { } interruption)
        {
            return (exact, exact);
        }

        PlantedFeetState planted = exact;
        foreach (string state in new[] { record.Fading!, interruption.State })
        {
            PlantedFeetState alone = Rule(Normalise([.. contributions.Where(contribution => contribution.State == state)], record));
            planted = new(planted.Left && alone.Left, planted.Right && alone.Right);
        }

        return (planted, exact);
    }

    private void OnMixerApplied()
    {
        ulong tick = Engine.GetPhysicsFrames();
        AnimationNodeStateMachinePlayback playback = _tree.Get(_playbackParameter).As<AnimationNodeStateMachinePlayback>();
        string current = playback.GetCurrentNode().ToString();
        string fading = playback.GetFadingFromNode().ToString();
        StateDescriptor currentState = RequireState(current);
        StateDescriptor? fadingState = fading.Length > 0 ? RequireState(fading) : null;
        float fadingPosition = playback.GetFadingPosition();
        Interruption? interruption = null;
        if (fadingState is not null && fadingPosition == 0f)
        {
            // A crossfade started on this tick; the previous tick tells whether it cut off a running one.
            if (_latestGraph is { } previous && previous.Tick + 1 == tick)
            {
                double advanced = previous.FadingPosition + _tree.GetPhysicsProcessDeltaTime();
                // The engine finishes a fade at is_greater_or_equal_approx(fading_pos, fading_time); only an unfinished
                // fade left its fading-from state blended at a real weight on this tick.
                double finishTolerance = Math.Max(EngineEpsilon, EngineEpsilon * Math.Abs(previous.FadingLength));
                if (previous.Fading is { } older
                    && previous.Current == fading
                    && previous.FadingLength > 0d
                    && advanced < previous.FadingLength - finishTolerance)
                {
                    StateDescriptor olderState = RequireState(older);
                    interruption = new Interruption(
                        older,
                        Math.Min(1d, advanced / previous.FadingLength),
                        ReadBlend(olderState),
                        ReadPositions(olderState));
                }
            }
            else
            {
                UndeterminedTransitionStarts++;
            }
        }

        _latestGraph = new GraphRecord(
            tick,
            current,
            fadingState?.Name,
            fadingPosition,
            playback.GetFadingLength(),
            ReadBlend(currentState),
            fadingState is null ? null : ReadBlend(fadingState),
            ReadPositions(currentState),
            fadingState is null ? [] : ReadPositions(fadingState),
            interruption);
        // Pre-modifier (pre-IK) local pose: the mixer has written it and no SkeletonModifier3D has run yet.
        _latestPreIKRotations = [.. _oracleBoneIndices.Select(_skeleton.GetBonePoseRotation)];
        // Pre-IK ball joints, root-relative (NAV-TAC-008 frame): the skeleton-space ball expressed in the actor frame.
        // GetBoneGlobalPose recomputes the dirty global poses from the mixer-written local poses.
        Transform3D actorFromSkeleton = _actor.GlobalTransform.AffineInverse() * _skeleton.GlobalTransform;
        _latestPreIKLeftBall = actorFromSkeleton * _skeleton.GetBoneGlobalPose(_leftToes).Origin;
        _latestPreIKRightBall = actorFromSkeleton * _skeleton.GetBoneGlobalPose(_rightToes).Origin;
    }

    private void OnSkeletonUpdated()
    {
        ulong tick = Engine.GetPhysicsFrames();
        if (_latestGraph is not { } graph || graph.Tick != tick)
        {
            UnpairedTicks++;
            return;
        }

        Transform3D skeletonWorld = _skeleton.GlobalTransform;
        Transform3D leftFoot = skeletonWorld * _skeleton.GetBoneGlobalPose(_leftFoot);
        Transform3D rightFoot = skeletonWorld * _skeleton.GetBoneGlobalPose(_rightFoot);
        ClipContribution[] contributions = Evaluate(graph);
        (PlantedFeetState planted, PlantedFeetState exact) = Classify(graph, contributions);

        var sample = new PlantedStateSample(
            tick,
            _actor.GlobalTransform,
            leftFoot * _leftBallInFoot,
            rightFoot * _rightBallInFoot,
            (skeletonWorld * _skeleton.GetBoneGlobalPose(_leftToes)).Origin,
            (skeletonWorld * _skeleton.GetBoneGlobalPose(_rightToes)).Origin,
            graph,
            contributions,
            planted,
            exact,
            _latestPreIKRotations,
            [.. _oracleBoneIndices.Select(_skeleton.GetBonePoseRotation)],
            _latestPreIKLeftBall,
            _latestPreIKRightBall);
        if (_samples.Count > 0 && _samples[^1].Tick == tick)
        {
            RepeatedSkeletonUpdates++;
            _samples[^1] = sample;
            return;
        }

        _samples.Add(sample);
    }

    private StateDescriptor RequireState(string name)
    {
        StateDescriptor state = _states.TryGetValue(name, out StateDescriptor? found)
            ? found
            : throw new InvalidOperationException($"Tick {Engine.GetPhysicsFrames()}: unknown state-machine state '{name}'.");
        return state.Unsupported is { } reason
            ? throw new InvalidOperationException($"Tick {Engine.GetPhysicsFrames()}: state '{name}' is active but not modelled: {reason}.")
            : state;
    }

    private Vector2? ReadBlend(StateDescriptor state) =>
        state.Space is { } space ? _tree.Get(space.BlendPositionParameter).AsVector2() : null;

    private double[] ReadPositions(StateDescriptor state)
    {
        double[] positions = new double[state.Leaves.Length];
        for (int index = 0; index < positions.Length; index++)
        {
            Variant value = _tree.Get(state.Leaves[index].PositionParameter);
            positions[index] = value.VariantType == Variant.Type.Float
                ? value.AsDouble()
                : throw new InvalidOperationException($"Parameter '{state.Leaves[index].PositionParameter}' is not a playback position.");
        }

        return positions;
    }

    private void AddState(List<ClipContribution> contributions, string stateName, double stateWeight, Vector2? blend, double[] positions)
    {
        if (stateWeight <= 0d)
        {
            return;
        }

        StateDescriptor state = _states[stateName];
        float[] leafWeights = state.Space is { } space
            ? space.Weights(blend ?? throw new InvalidOperationException($"State '{stateName}' has no recorded blend position."))
            : [1f];
        for (int index = 0; index < state.Leaves.Length; index++)
        {
            double weight = stateWeight * leafWeights[index];
            if (weight <= 0d)
            {
                continue;
            }

            ClipLeaf leaf = state.Leaves[index];
            contributions.Add(new ClipContribution(
                stateName,
                leaf.PointName,
                leaf.Animation,
                leaf.Intervals,
                positions[index],
                leaf.SourceTime(positions[index]),
                weight,
                weight));
        }
    }

    private static (double Current, double Fading) CrossfadeWeights(GraphRecord record)
    {
        if (record.Fading is null)
        {
            return (1d, 0d);
        }

        double blend = record.FadingLength > 0d ? Math.Min(1d, record.FadingPosition / record.FadingLength) : 1d;
        return FloorWeights(blend);
    }

    /// <summary>Godot's crossfade weights for a fade blend: (blend, 1 − blend), each floored at <c>CMP_EPSILON</c>.</summary>
    private static (double Current, double Fading) FloorWeights(double blend)
    {
        blend = Math.Abs(blend) < EngineEpsilon ? EngineEpsilon : blend;
        double inverse = 1d - blend;
        inverse = Math.Abs(inverse) < EngineEpsilon ? EngineEpsilon : inverse;
        return (blend, inverse);
    }

    private static StateDescriptor DescribeState(
        AnimationTree tree,
        string name,
        AnimationNode node,
        Dictionary<string, PerFootPlantedIntervals> packages)
    {
        string prefix = $"parameters/{StateMachineNode}/{name}";
        try
        {
            switch (node)
            {
                case AnimationNodeAnimation animation:
                    return new StateDescriptor(name, [DescribeLeaf(tree, animation, $"{prefix}/current_position", null, packages)], null, null);
                case AnimationNodeBlendTree blendTree:
                    {
                        // Only pass-through time scales and time seeks around one content node are modelled (no blend
                        // weights to recompute; a seek moves the playback position, which is recorded after evaluation).
                        string[] content = [.. blendTree.GetNodeList()
                        .Select(child => child.ToString())
                        .Where(child => child != "output" && blendTree.GetNode(child) is not (AnimationNodeTimeScale or AnimationNodeTimeSeek))];
                        if (content.Length != 1)
                        {
                            return Unsupported(name, $"blend tree has {content.Length} non-time-scale nodes");
                        }

                        string child = content[0];
                        return blendTree.GetNode(child) switch
                        {
                            AnimationNodeAnimation animation => new StateDescriptor(
                                name,
                                [DescribeLeaf(tree, animation, $"{prefix}/{child}/current_position", null, packages)],
                                null,
                                null),
                            AnimationNodeBlendSpace2D space => DescribeSpace(tree, name, $"{prefix}/{child}", space, packages),
                            _ => Unsupported(name, $"blend tree content '{child}' is {blendTree.GetNode(child).GetClass()}"),
                        };
                    }
                case AnimationNodeBlendSpace2D space:
                    return DescribeSpace(tree, name, prefix, space, packages);
                default:
                    return Unsupported(name, $"state node is {node.GetClass()}");
            }
        }
        catch (UnmodelledClipException exception)
        {
            return Unsupported(name, exception.Message);
        }
    }

    private static StateDescriptor DescribeSpace(
        AnimationTree tree,
        string name,
        string prefix,
        AnimationNodeBlendSpace2D space,
        Dictionary<string, PerFootPlantedIntervals> packages)
    {
        if (space.BlendMode != AnimationNodeBlendSpace2D.BlendModeEnum.Interpolated)
        {
            return Unsupported(name, $"blend space mode {space.BlendMode}");
        }

        int count = space.GetBlendPointCount();
        var leaves = new ClipLeaf[count];
        var points = new Vector2[count];
        for (int index = 0; index < count; index++)
        {
            string point = space.GetBlendPointName(index).ToString();
            points[index] = space.GetBlendPointPosition(index);
            leaves[index] = space.GetBlendPointNode(index) is AnimationNodeAnimation animation
                ? DescribeLeaf(tree, animation, $"{prefix}/{point}/current_position", point, packages)
                : throw new UnmodelledClipException($"blend point '{point}' is not an animation node");
        }

        return new StateDescriptor(name, leaves, new BlendSpaceModel(new StringName($"{prefix}/blend_position"), space, points), null);
    }

    private static ClipLeaf DescribeLeaf(
        AnimationTree tree,
        AnimationNodeAnimation node,
        string positionParameter,
        string? point,
        Dictionary<string, PerFootPlantedIntervals> packages)
    {
        string animation = node.Animation.ToString();
        int separator = animation.IndexOf('/', StringComparison.Ordinal);
        string library = separator > 0 ? animation[..separator] : string.Empty;
        string clip = separator > 0 ? animation[(separator + 1)..] : animation;
        if (!packages.TryGetValue(library, out PerFootPlantedIntervals? package) || !package.TryGetClip(clip, out PlantedClipIntervals intervals))
        {
            throw new UnmodelledClipException($"animation '{animation}' has no ANIM-002 planted intervals");
        }

        Animation resource = tree.GetAnimation(animation)
            ?? throw new InvalidOperationException($"SETUP: animation '{animation}' is not bound to the tree.");
        return Math.Abs(resource.Length - intervals.DurationSeconds) > DurationToleranceSeconds
            ? throw new InvalidOperationException(
                $"SETUP: animation '{animation}' length {resource.Length} differs from its planted-interval duration {intervals.DurationSeconds}.")
            : new ClipLeaf(
            new StringName(positionParameter),
            point,
            animation,
            intervals,
            resource.Length,
            node.UseCustomTimeline ? node.LoopMode : resource.LoopMode,
            node.UseCustomTimeline,
            node.TimelineLength,
            node.StretchTimeScale,
            node.StartOffset,
            node.PlayMode == AnimationNodeAnimation.PlayModeEnum.Backward);
    }

    private static StateDescriptor Unsupported(string name, string reason) => new(name, [], null, reason);

    private static Dictionary<string, PerFootPlantedIntervals> LoadPackages(AnimationPlayer player)
    {
        var packages = new Dictionary<string, PerFootPlantedIntervals>(StringComparer.Ordinal);
        foreach (StringName library in player.GetAnimationLibraryList())
        {
            string path = player.GetAnimationLibrary(library)?.ResourcePath ?? string.Empty;
            if (!path.EndsWith(".tres", StringComparison.Ordinal))
            {
                continue;
            }

            string directory = path[..(path.LastIndexOf('/') + 1)];
            string catalogue = $"{path[..^".tres".Length]}_catalogue.json";
            string intervalsPath = directory + PerFootPlantedIntervals.FileName;
            if (Godot.FileAccess.FileExists(intervalsPath))
            {
                var intervals = PerFootPlantedIntervals.Load(intervalsPath);
                intervals.ValidateAgainst(StandingLocomotionCatalogue.Parse(Godot.FileAccess.GetFileAsString(catalogue), catalogue));
                packages[library.ToString()] = intervals;
            }
            else if (Godot.FileAccess.FileExists(catalogue)
                && Godot.FileAccess.GetFileAsString(catalogue).Contains($"\"{PerFootPlantedIntervals.CatalogueField}\"", StringComparison.Ordinal))
            {
                packages[library.ToString()] = PerFootPlantedIntervals.LoadValidatedIdleCatalogue(catalogue);
            }
        }

        return packages.Count > 0
            ? packages
            : throw new InvalidOperationException("SETUP: the NPC AnimationPlayer binds no library with ANIM-002 planted intervals.");
    }

    private static int RequireBone(Skeleton3D skeleton, string name)
    {
        int index = skeleton.FindBone(name);
        return index >= 0 ? index : throw new InvalidOperationException($"SETUP: skeleton has no '{name}' bone.");
    }

    /// <summary>IK-003 TR7 ball joint: the toe bone's rest-pose origin in its parent foot's space.</summary>
    private static Vector3 BallInFoot(Skeleton3D skeleton, int toes, int foot) =>
        skeleton.GetBoneParent(toes) == foot
            ? skeleton.GetBoneRest(toes).Origin
            : throw new InvalidOperationException($"SETUP: '{skeleton.GetBoneName(toes)}' is not a child of '{skeleton.GetBoneName(foot)}'.");

    private sealed class UnmodelledClipException(string message) : Exception(message);

    private sealed record StateDescriptor(string Name, ClipLeaf[] Leaves, BlendSpaceModel? Space, string? Unsupported);

    /// <summary>One clip node: where its playback position is read and how it maps to source time.</summary>
    private sealed record ClipLeaf(
        StringName PositionParameter,
        string? PointName,
        string Animation,
        PlantedClipIntervals Intervals,
        double AnimationLength,
        Animation.LoopModeEnum LoopMode,
        bool UseCustomTimeline,
        double TimelineLength,
        bool StretchTimeScale,
        double StartOffset,
        bool Backward)
    {
        /// <summary>Godot 4.7 <c>AnimationNodeAnimation::_process</c> step 3: node position → animation time.</summary>
        public double SourceTime(double nodePosition)
        {
            double time = nodePosition;
            if (UseCustomTimeline)
            {
                time += StartOffset;
                if (StretchTimeScale)
                {
                    time *= AnimationLength / TimelineLength;
                }
            }

            time = LoopMode switch
            {
                Godot.Animation.LoopModeEnum.Linear => FPosMod(time, AnimationLength),
                Godot.Animation.LoopModeEnum.Pingpong => PingPong(time, AnimationLength),
                Godot.Animation.LoopModeEnum.None => Math.Clamp(time, 0d, AnimationLength),
                _ => throw new InvalidOperationException($"Unknown loop mode {LoopMode} of '{Animation}'."),
            };
            return Backward ? AnimationLength - time : time;
        }

        private static double FPosMod(double value, double length)
        {
            double wrapped = value % length;
            return wrapped < 0d ? wrapped + length : wrapped;
        }

        private static double PingPong(double value, double length)
        {
            double wrapped = FPosMod(value, length * 2d);
            return wrapped > length ? (length * 2d) - wrapped : wrapped;
        }
    }

    /// <summary>Recomputes Godot 4.7 interpolated BlendSpace2D weights from a blend position.</summary>
    private sealed class BlendSpaceModel
    {
        private readonly Vector2[] _points;
        private readonly int[][] _triangles;

        public BlendSpaceModel(StringName blendPositionParameter, AnimationNodeBlendSpace2D space, Vector2[] points)
        {
            BlendPositionParameter = blendPositionParameter;
            _points = points;
            // Reading a triangle point refreshes the auto-triangulation the engine uses; the count alone does not.
            _ = space.GetTrianglePoint(0, 0);
            int count = space.GetTriangleCount();
            if (count == 0)
            {
                throw new InvalidOperationException("SETUP: the travel blend space has no triangles.");
            }

            _triangles = [.. Enumerable.Range(0, count).Select(triangle =>
                new[] { space.GetTrianglePoint(triangle, 0), space.GetTrianglePoint(triangle, 1), space.GetTrianglePoint(triangle, 2) })];
        }

        public StringName BlendPositionParameter
        {
            get;
        }

        public float[] Weights(Vector2 position)
        {
            float[] weights = new float[_points.Length];
            float[] blend = new float[3];
            var corners = new Vector2[3];
            int chosen = -1;
            bool first = true;
            Vector2 best = default;
            for (int triangle = 0; triangle < _triangles.Length; triangle++)
            {
                for (int corner = 0; corner < 3; corner++)
                {
                    corners[corner] = _points[_triangles[triangle][corner]];
                }

                if (IsPointInTriangle(position, corners[0], corners[1], corners[2]))
                {
                    chosen = triangle;
                    Barycentric(position, corners, blend);
                    break;
                }

                for (int corner = 0; corner < 3; corner++)
                {
                    Vector2 a = corners[corner];
                    Vector2 b = corners[(corner + 1) % 3];
                    Vector2 closest = ClosestPointToSegment(position, a, b);
                    if (first || closest.DistanceTo(position) < best.DistanceTo(position))
                    {
                        best = closest;
                        chosen = triangle;
                        first = false;
                        float length = a.DistanceTo(b);
                        float c = length == 0f ? 0f : a.DistanceTo(closest) / length;
                        blend[corner] = 1f - c;
                        blend[(corner + 1) % 3] = c;
                        blend[(corner + 2) % 3] = 0f;
                    }
                }
            }

            for (int corner = 0; corner < 3; corner++)
            {
                weights[_triangles[chosen][corner]] = blend[corner];
            }

            return weights;
        }

        private static bool IsPointInTriangle(Vector2 s, Vector2 a, Vector2 b, Vector2 c)
        {
            Vector2 an = a - s;
            Vector2 bn = b - s;
            Vector2 cn = c - s;
            bool orientation = an.Cross(bn) > 0f;
            return (bn.Cross(cn) > 0f) == orientation && (cn.Cross(an) > 0f) == orientation;
        }

        private static Vector2 ClosestPointToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 p = point - a;
            Vector2 n = b - a;
            float l2 = n.LengthSquared();
            if (l2 < 1e-20f)
            {
                return a;
            }

            float d = n.Dot(p) / l2;
            return d <= 0f ? a : d >= 1f ? b : a + (n * d);
        }

        private static void Barycentric(Vector2 position, Vector2[] points, float[] weights)
        {
            for (int corner = 0; corner < 3; corner++)
            {
                if (position.IsEqualApprox(points[corner]))
                {
                    weights[0] = weights[1] = weights[2] = 0f;
                    weights[corner] = 1f;
                    return;
                }
            }

            Vector2 v0 = points[1] - points[0];
            Vector2 v1 = points[2] - points[0];
            Vector2 v2 = position - points[0];
            float d00 = v0.Dot(v0);
            float d01 = v0.Dot(v1);
            float d11 = v1.Dot(v1);
            float d20 = v2.Dot(v0);
            float d21 = v2.Dot(v1);
            float denominator = (d00 * d11) - (d01 * d01);
            if (denominator == 0f)
            {
                weights[0] = 1f;
                weights[1] = weights[2] = 0f;
                return;
            }

            float v = ((d11 * d20) - (d01 * d21)) / denominator;
            float w = ((d00 * d21) - (d01 * d20)) / denominator;
            weights[0] = 1f - v - w;
            weights[1] = v;
            weights[2] = w;
        }
    }
}

/// <summary>
/// The older fading-from state of an interrupted crossfade, blended on the interrupting tick but discarded by the engine
/// before the playback is read: its state, the advanced fade blend of the previous current state, and its blend position
/// and playback positions as used on that tick.
/// </summary>
internal sealed record Interruption(string State, double FadeBlend, Vector2? Blend, double[] Positions);

/// <summary>Raw AnimationTree graph state of one tick, as used by that tick's evaluation.</summary>
internal readonly record struct GraphRecord(
    ulong Tick,
    string Current,
    string? Fading,
    double FadingPosition,
    double FadingLength,
    Vector2? CurrentBlend,
    Vector2? FadingBlend,
    double[] CurrentPositions,
    double[] FadingPositions,
    Interruption? Interruption)
{
    public string Describe()
    {
        var text = new StringBuilder(Current);
        _ = text.Append(CultureInfo.InvariantCulture, $"@[{string.Join('|', CurrentPositions.Select(Format))}]");
        if (CurrentBlend is { } blend)
        {
            _ = text.Append(CultureInfo.InvariantCulture, $" blend=({blend.X:0.#####};{blend.Y:0.#####})");
        }

        if (Fading is { } fading)
        {
            _ = text.Append(CultureInfo.InvariantCulture, $" fading_from={fading}@[{string.Join('|', FadingPositions.Select(Format))}] fade={FadingPosition:0.#####}/{FadingLength:0.#####}");
            if (FadingBlend is { } fadingBlend)
            {
                _ = text.Append(CultureInfo.InvariantCulture, $" fading_blend=({fadingBlend.X:0.#####};{fadingBlend.Y:0.#####})");
            }
        }

        if (Interruption is { } interruption)
        {
            _ = text.Append(CultureInfo.InvariantCulture, $" interrupted={interruption.State}@[{string.Join('|', interruption.Positions.Select(Format))}] blended_fade={interruption.FadeBlend:0.#####}");
        }

        return text.ToString();
    }

    private static string Format(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
}

/// <summary>One clip's recomputed contribution on a tick.</summary>
internal readonly record struct ClipContribution(
    string State,
    string? BlendPoint,
    string Animation,
    PlantedClipIntervals Intervals,
    double NodePositionSeconds,
    double SourceTimeSeconds,
    double RawWeight,
    double Weight)
{
    public override string ToString() => FormattableString.Invariant(
        $"{State}{(BlendPoint is null ? string.Empty : "/" + BlendPoint)}:{Intervals.Identity}@{SourceTimeSeconds:0.#####}s w={Weight:0.#####}");
}

/// <summary>
/// One recorded tick: rendered post-IK ball joints, graph state, recomputed contributions, planted state (conservative on
/// interrupted-crossfade ticks) and the exact-weight planted state, the oracle bones' pre-IK (mixer output) and
/// post-IK local rotations, and the pre-IK (mixer output) ball joints expressed root-relative in the actor frame.
/// </summary>
internal sealed record PlantedStateSample(
    ulong Tick,
    Transform3D Actor,
    Vector3 LeftBall,
    Vector3 RightBall,
    Vector3 LeftToesOrigin,
    Vector3 RightToesOrigin,
    GraphRecord Graph,
    ClipContribution[] Contributions,
    PlantedFeetState Planted,
    PlantedFeetState ExactPlanted,
    Quaternion[] OracleBoneRotations,
    Quaternion[] PostIKOracleBoneRotations,
    Vector3 PreIKLeftBallInActor,
    Vector3 PreIKRightBallInActor)
{
    public Vector3 Ball(StandingLocomotionAnatomicalSide foot) =>
        foot == StandingLocomotionAnatomicalSide.Left ? LeftBall : RightBall;

    /// <summary>Pre-IK ball joint of one foot, root-relative in this tick's actor frame (actor-local axes).</summary>
    public Vector3 PreIKBallInActor(StandingLocomotionAnatomicalSide foot) =>
        foot == StandingLocomotionAnatomicalSide.Left ? PreIKLeftBallInActor : PreIKRightBallInActor;
}
