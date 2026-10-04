using System.Globalization;
using System.Text;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>
/// NAV-001 NAV-TAC-006 (Technical Requirement 15): the NPC AnimationTrees are statically authored graphs of discrete,
/// native-speed clip states. There is no blend-space sync, no custom timeline, no time scale, and no travel blend space
/// in the NPC states; the graph is not modified at runtime; and every clip's recorded playback advances at exactly the
/// tick's time, so the sampled source time is the native clip time.
/// </summary>
public sealed class NAV001NativeSpeedGraphIntegrationTests
{
    internal const string FemaleTreePath = "res://assets/characters/templates/animation/animation_tree_root_npc.tres";
    internal const string MaleTreePath = "res://assets/characters/templates/animation/animation_tree_root_reference_male_npc.tres";
    private const string EvidenceDirectory = "res://temp/nav001-p4-u2/native-speed";
    private const double PositionToleranceSeconds = 1e-5d;
    private const int MinimumCycleTicks = 60;
    private const int RequestTimeoutTicks = 1200;

    /// <summary>
    /// Inspection of both NPC tree resources: every NPC state is <c>Clip → Seek</c> (animation without a custom
    /// timeline, time seek without explicit elapse); no NPC state holds a blend space or time scale; every NPC
    /// transition is a linear crossfade; the male graph authors the same states and crossfades as the female graph.
    /// </summary>
    [Headless]
    [Fact]
    public void NPCTrees_AuthorDiscreteNativeSpeedClipStatesWithoutCadenceStretching()
    {
        string female = DescribeNPCGraph(FemaleTreePath, out List<string> femaleFailures);
        string male = DescribeNPCGraph(MaleTreePath, out List<string> maleFailures);
        var failures = new List<string>();
        failures.AddRange(femaleFailures.Select(failure => $"female: {failure}"));
        failures.AddRange(maleFailures.Select(failure => $"male: {failure}"));
        if (female != male)
        {
            failures.Add($"the male NPC graph differs from the female one:\n--- female\n{female}\n--- male\n{male}");
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    /// <summary>NAV-TAC-006 runtime: native playback rate and no runtime graph change on the female rig.</summary>
    [Fact]
    public Task Female_ClipsPlayAtNativeSpeedAndGraphIsUnchanged() => NativeSpeedAsync(NPCNavigationRig.Female);

    /// <summary>NAV-TAC-006 runtime: native playback rate and no runtime graph change on the male rig.</summary>
    [Fact]
    public Task Male_ClipsPlayAtNativeSpeedAndGraphIsUnchanged() => NativeSpeedAsync(NPCNavigationRig.Male);

    /// <summary>
    /// A 4 m walk (entry, forward cycle, exit, Idle). On every recorded tick after the first, each clip that played
    /// on the previous tick in the same role (current or fading-from) advanced by exactly the tick's time, except on a
    /// tick that entered it (its seek) or where a finite clip has reached its end; the recorded source time equals
    /// that native position. The running graph equals the authored resource before and after the walk.
    /// </summary>
    private static async Task NativeSpeedAsync(NPCNavigationRig rig)
    {
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(rig);
        using AnimationGraphPlantedStateRecorder recorder = fixture.BindRecorder();
        string treePath = rig == NPCNavigationRig.Male ? MaleTreePath : FemaleTreePath;
        string authored = DescribeNPCGraph(treePath, out _);
        string before = DescribeNPCGraph(fixture.Tree.TreeRoot, out _);
        Assert.True(before == authored, $"SETUP: {rig}: the running NPC graph is not the authored '{treePath}':\n{before}");
        recorder.Attach();
        await fixture.StepAsync(NAV001ArrivalOutcomeIntegrationTests.WarmUpPhysicsTicks);
        ulong terminal = await fixture.RunRequestAsync(new Vector3(0f, 0f, -4f), 0f, $"native-speed-{rig}", RequestTimeoutTicks);
        await fixture.StepAsync(30);
        await fixture.SettleAsync();
        recorder.Detach();
        Assert.True(terminal != 0, $"{rig}: SETUP: the walk reported no outcome.");

        string after = DescribeNPCGraph(fixture.Tree.TreeRoot, out _);
        HashSet<ulong> entryTicks = [.. fixture.Navigation.IssuedClipTransitions.Select(transition => transition.PhysicsTick)];
        double tickSeconds = Engine.TimeScale / Engine.PhysicsTicksPerSecond;
        var perState = new Dictionary<string, int>(StringComparer.Ordinal);
        var failures = new List<string>();
        var csv = new StringBuilder("tick,state,role,previous_position,position,advance,source_time\n");
        IReadOnlyList<PlantedStateSample> samples = recorder.Samples;
        for (int index = 1; index < samples.Count; index++)
        {
            PlantedStateSample previous = samples[index - 1];
            PlantedStateSample sample = samples[index];
            if (sample.Tick != previous.Tick + 1)
            {
                continue;
            }

            foreach (ClipContribution contribution in sample.Contributions)
            {
                double expectedSource = contribution.Intervals.NormaliseSourceTime(contribution.NodePositionSeconds);
                if (Math.Abs(contribution.SourceTimeSeconds - expectedSource) > PositionToleranceSeconds)
                {
                    failures.Add(FormattableString.Invariant(
                        $"tick {sample.Tick}: {contribution} samples source {contribution.SourceTimeSeconds} s at node position {contribution.NodePositionSeconds} s."));
                }

                bool entered = entryTicks.Contains(sample.Tick) && contribution.State == sample.Graph.Current;
                ClipContribution? prior = previous.Contributions
                    .Where(candidate => candidate.State == contribution.State && candidate.Animation == contribution.Animation)
                    .Select(candidate => (ClipContribution?)candidate)
                    .FirstOrDefault();
                if (entered || prior is not { } before1)
                {
                    continue;
                }

                double length = contribution.Intervals.DurationSeconds;
                bool looping = contribution.Intervals.IsLooping;
                double advance = contribution.NodePositionSeconds - before1.NodePositionSeconds;
                if (looping && advance < 0d)
                {
                    advance += length;
                }

                if (!looping && before1.NodePositionSeconds + tickSeconds > length)
                {
                    // A finite clip holds its end.
                    continue;
                }

                string role = contribution.State == sample.Graph.Current ? "current" : "fading";
                _ = csv.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{sample.Tick},{contribution.State},{role},{before1.NodePositionSeconds:R},{contribution.NodePositionSeconds:R},{advance:R},{contribution.SourceTimeSeconds:R}"));
                perState[contribution.State] = perState.GetValueOrDefault(contribution.State) + 1;
                if (Math.Abs(advance - tickSeconds) > PositionToleranceSeconds)
                {
                    failures.Add(FormattableString.Invariant(
                        $"tick {sample.Tick}: {contribution.State} ({role}) advanced {advance} s in a {tickSeconds} s tick [{sample.Graph.Describe()}]."));
                }
            }
        }

        string summary = FormattableString.Invariant(
            $"{rig}: tick={tickSeconds} s; native-rate ticks per state: {string.Join(", ", perState.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"))}; entries={entryTicks.Count}; failures={failures.Count}");
        string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(EvidenceDirectory));
        File.WriteAllText(Path.Combine(directory, $"{rig}.txt"), summary + "\n" + string.Join('\n', failures.Take(100)));
        File.WriteAllText(Path.Combine(directory, $"{rig}.csv"), csv.ToString());

        Assert.True(before == after, $"{rig}: the NPC graph changed at runtime:\n--- before\n{before}\n--- after\n{after}");
        Assert.True(
            perState.GetValueOrDefault(NPCAnimationProgramme.WalkForwardState.ToString()) >= MinimumCycleTicks,
            $"SETUP: {summary}: too few forward-cycle ticks to measure the playback rate.");
        Assert.True(failures.Count == 0, $"{summary}\n{string.Join('\n', failures.Take(40))}");
    }

    private static string DescribeNPCGraph(string path, out List<string> failures)
        => DescribeNPCGraph(ResourceLoader.Load<AnimationNodeBlendTree>(path, cacheMode: ResourceLoader.CacheMode.Ignore)
            ?? throw new InvalidOperationException($"SETUP: '{path}' is not a blend tree."), out failures);

    /// <summary>
    /// A canonical description of the NPC states and transitions of a tree root, collecting every NAV-TAC-006
    /// violation found on the way.
    /// </summary>
    internal static string DescribeNPCGraph(AnimationRootNode root, out List<string> failures)
    {
        failures = [];
        AnimationNodeStateMachine machine = (root as AnimationNodeBlendTree)?.GetNode("States") as AnimationNodeStateMachine
            ?? throw new InvalidOperationException("SETUP: the tree root has no 'States' state machine.");
        var text = new StringBuilder();
        foreach (StringName required in NPCAnimationProgramme.RequiredStates)
        {
            if (!machine.HasNode(required))
            {
                failures.Add($"required state '{required}' is missing.");
            }
        }

        foreach (string state in machine.GetNodeList().Select(name => name.ToString()).Order(StringComparer.Ordinal))
        {
            AnimationNode node = machine.GetNode(state);
            foreach (string violation in StretchingNodes(node, state))
            {
                failures.Add(violation);
            }

            if (!state.StartsWith(NPCAnimationTreeDriver.StatePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (node is not AnimationNodeBlendTree blendTree)
            {
                failures.Add($"state '{state}' is a {node.GetClass()}, not a Clip → Seek blend tree.");
                continue;
            }

            string[] children = [.. blendTree.GetNodeList().Select(child => child.ToString()).Order(StringComparer.Ordinal)];
            Godot.Collections.Array connections = blendTree.Get("node_connections").AsGodotArray();
            string wiring = string.Join(',', connections.Select(value => value.ToString()));
            if (!children.SequenceEqual(["Clip", "Seek", "output"])
                || blendTree.GetNode(NPCAnimationTreeDriver.ClipNodeName) is not AnimationNodeAnimation clip
                || blendTree.GetNode(NPCAnimationTreeDriver.SeekNodeName) is not AnimationNodeTimeSeek seek
                || wiring != "output,0,Seek,Seek,0,Clip")
            {
                failures.Add($"state '{state}' is not exactly output ← Seek ← Clip (nodes {string.Join(", ", children)}; wiring {wiring}).");
                continue;
            }

            if (clip.UseCustomTimeline)
            {
                failures.Add($"state '{state}' clip uses a custom timeline.");
            }

            if (seek.ExplicitElapse)
            {
                failures.Add($"state '{state}' seeks with explicit elapse.");
            }

            // A backward play mode reverses the clip, so it no longer plays at its authored native speed and direction.
            if (clip.PlayMode != AnimationNodeAnimation.PlayModeEnum.Forward)
            {
                failures.Add($"state '{state}' clip plays in {clip.PlayMode} mode, not Forward.");
            }

            _ = text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"state {state} plays {clip.Animation} play_mode={clip.PlayMode}"));
        }

        var transitions = new List<string>();
        for (int index = 0; index < machine.GetTransitionCount(); index++)
        {
            string from = machine.GetTransitionFrom(index).ToString();
            string to = machine.GetTransitionTo(index).ToString();
            if (!from.StartsWith(NPCAnimationTreeDriver.StatePrefix, StringComparison.Ordinal)
                && !to.StartsWith(NPCAnimationTreeDriver.StatePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            AnimationNodeStateMachineTransition transition = machine.GetTransition(index);
            if (transition.XfadeCurve is not null)
            {
                failures.Add($"transition {from} → {to} uses a crossfade curve.");
            }

            if (transition.AdvanceMode == AnimationNodeStateMachineTransition.AdvanceModeEnum.Auto)
            {
                failures.Add($"transition {from} → {to} auto-advances.");
            }

            transitions.Add(string.Create(CultureInfo.InvariantCulture, $"transition {from} -> {to} xfade={transition.XfadeTime} switch={transition.SwitchMode} advance={transition.AdvanceMode}"));
        }

        foreach (string transition in transitions.Order(StringComparer.Ordinal))
        {
            _ = text.AppendLine(transition);
        }

        return text.ToString();
    }

    /// <summary>
    /// Shared-cadence stretching inside a state: blend spaces (sync or not, they blend clips of different cycle
    /// lengths), time scales, and stretched custom timelines, anywhere in the state's node tree.
    /// </summary>
    private static IEnumerable<string> StretchingNodes(AnimationNode node, string path)
    {
        switch (node)
        {
            case AnimationNodeBlendSpace1D or AnimationNodeBlendSpace2D:
                yield return $"'{path}' is a blend space ({node.GetClass()}).";
                break;
            case AnimationNodeTimeScale when path.StartsWith(NPCAnimationTreeDriver.StatePrefix, StringComparison.Ordinal):
                yield return $"'{path}' is a time scale.";
                break;
            case AnimationNodeAnimation { UseCustomTimeline: true, StretchTimeScale: true }:
                yield return $"'{path}' stretches a custom timeline.";
                break;
            case AnimationNodeBlendTree blendTree:
                foreach (StringName child in blendTree.GetNodeList())
                {
                    if (child != "output")
                    {
                        foreach (string violation in StretchingNodes(blendTree.GetNode(child), $"{path}/{child}"))
                        {
                            yield return violation;
                        }
                    }
                }

                break;
            case AnimationNodeStateMachine nested:
                foreach (StringName child in nested.GetNodeList())
                {
                    foreach (string violation in StretchingNodes(nested.GetNode(child), $"{path}/{child}"))
                    {
                        yield return violation;
                    }
                }

                break;
            default:
                break;
        }
    }
}
