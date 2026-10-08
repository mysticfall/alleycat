using System.Globalization;
using System.Text;
using AlleyCat.Common;
using AlleyCat.Control.Locomotion;
using AlleyCat.IK;
using AlleyCat.Navigation;
using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Navigation.NAV001ArrivalOutcomeIntegrationTests;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>
/// IK-003 foot planting on the real NPC in the navigation test scene (NAV-001 scenarios).
/// </summary>
/// <remarks>
/// <para>IK-TAC-006 / Technical Requirement 16: the runtime planted state the foot-planting provider consumes must agree
/// on every tick with the planted state that <see cref="AnimationGraphPlantedStateRecorder"/> recomputes independently
/// from the recorded graph state; neither reads the other.</para>
/// <para>Technical Requirement 24: every tick of the request window carries, for each foot, the metadata planted
/// state, pin, animated target, and a post-IK ball point captured inside <see cref="Skeleton3D.SignalName.SkeletonUpdated"/>.
/// The report attributes each A2 run above tolerance to planting gaps (ticks the provider was not pinned), reach
/// releases, or actor motion (for example the temporary crude-stop snap) while the foot was pinned.</para>
/// <para>IK-UAC-004: the actor trajectory with foot planting is identical, tick by tick, to the trajectory with the
/// foot providers removed.</para>
/// </remarks>
public sealed class IK003NavigationFootPlantingIntegrationTests
{
    private const string EvidenceDirectory = "res://temp/ik003-foot-planting/navigation";
    private const float SlipToleranceMetres = 0.005f;
    private const float MaximumReachUsage = 1.0001f;
    private const int RequestTimeoutTicks = 1200;

    private static readonly ArrivalRequest _oblique = new(new Vector3(-0.49705806f, 0f, -0.61596584f), 0.6789687f);
    private static readonly ArrivalRequest _terminalSlide = new(new Vector3(0.48621222f, 0f, -1.419342f), -0.33003297f);

    private static readonly (string Name, ArrivalRequest[] Requests)[] _scenarios =
    [
        ("oblique", [_oblique]),
        ("terminal-slide", [_terminalSlide]),
        ("terminal-slide-mirror", [new ArrivalRequest(_terminalSlide.Destination with { X = -_terminalSlide.Destination.X }, -_terminalSlide.YawRadians)]),
        ("repeated-nearby", [_oblique, new ArrivalRequest(_oblique.Destination + (new Basis(Vector3.Up, _oblique.YawRadians) * Vector3.Forward * 0.3f), _oblique.YawRadians)]),
        ("lateral-0.5m", [new ArrivalRequest(new Vector3(0.5f, 0f, 0f), 0f)]),
        ("distant-lateral-3m", [new ArrivalRequest(new Vector3(3f, 0f, 0f), 0f)]),
        ("behind-1.5m", [new ArrivalRequest(new Vector3(0f, 0f, 1.5f), 0f)]),
        ("turn-only-90deg", [new ArrivalRequest(Vector3.Zero, Mathf.Pi / 2f)]),
        ("long-straight-4m", [new ArrivalRequest(new Vector3(0f, 0f, -4f), 0f)]),
    ];

    /// <summary>
    /// All NAV-001 scenarios: runtime and independent planted state agree on every tick, the diagnostics are complete,
    /// no applied target exceeds the leg's rest length, and the per-run attribution is written as evidence.
    /// </summary>
    [Fact]
    public async Task Scenarios_RuntimePlantedStateMatchesIndependentRecorder_AndDiagnosticsAreComplete()
    {
        var summary = new StringBuilder("scenario,ticks,disagreements,missing_diagnostics,max_reach_usage,reach_blend_outs_l,reach_blend_outs_r,worst_run\n");
        var failures = new List<string>();
        foreach ((string name, ArrivalRequest[] requests) in _scenarios)
        {
            ScenarioTrace trace = await RunAsync(name, requests, planting: true);
            ScenarioAnalysis analysis = Analyse(trace);
            _ = summary.AppendLine(analysis.SummaryLine(name));
            WriteEvidence($"{name}.txt", analysis.Report);
            if (analysis.Disagreements > 0)
            {
                failures.Add($"{name}: {analysis.Disagreements} ticks where runtime planted state differs from the recorder.");
            }

            if (analysis.MissingDiagnostics > 0)
            {
                failures.Add($"{name}: {analysis.MissingDiagnostics} foot-ticks without a complete post-IK diagnostic.");
            }

            if (analysis.MaxReachUsage > MaximumReachUsage)
            {
                failures.Add(FormattableString.Invariant($"{name}: reach usage {analysis.MaxReachUsage:0.####} exceeds the leg rest length."));
            }
        }

        WriteEvidence("summary.csv", summary.ToString());
        Assert.True(failures.Count == 0, string.Join('\n', failures) + "\n" + summary);
    }

    /// <summary>IK-UAC-004: foot planting never moves, steers, or delays the actor.</summary>
    [Fact]
    public async Task LongStraight_FootPlantingCausesNoActorMovement()
    {
        ArrivalRequest[] requests = [new ArrivalRequest(new Vector3(0f, 0f, -4f), 0f)];
        ScenarioTrace withPlanting = await RunAsync("uac004-with", requests, planting: true, preparePairedIdlePhase: true);
        ScenarioTrace without = await RunAsync("uac004-without", requests, planting: false, preparePairedIdlePhase: true);

        Assert.True(withPlanting.PinnedFootTicks > 0, "SETUP: the planted run never pinned a foot.");
        Assert.Equal(without.ActorTrace.Count, withPlanting.ActorTrace.Count);
        for (int index = 0; index < without.ActorTrace.Count; index++)
        {
            Assert.True(
                ExactlyEqual(without.ActorTrace[index], withPlanting.ActorTrace[index]),
                $"Relative tick {index}: actor {withPlanting.ActorTrace[index]} with planting differs from {without.ActorTrace[index]} without.");
        }
    }

    private static async Task<ScenarioTrace> RunAsync(
        string scenario, ArrivalRequest[] requests, bool planting, bool preparePairedIdlePhase = false)
    {
        SceneTree sceneTree = GetSceneTree();
        int previousStepLimit = Engine.MaxPhysicsStepsPerFrame;
        Engine.MaxPhysicsStepsPerFrame = 1;
        Node3D root = Assert.IsType<Node3D>(LoadPackedScene(NavigationTestScenePath).Instantiate(), exactMatch: false);
        AnimationGraphPlantedStateRecorder? recorder = null;
        FootPlantingTargetProvider? left = null;
        FootPlantingTargetProvider? right = null;
        var trace = new ScenarioTrace();
        void OnLeft(FootPlantingTickDiagnostic diagnostic)
        {
            trace.Left[diagnostic.Tick] = diagnostic;
        }

        void OnRight(FootPlantingTickDiagnostic diagnostic)
        {
            trace.Right[diagnostic.Tick] = diagnostic;
        }

        try
        {
            Node3D actor = root.GetNode<Node3D>(ActorNodeName);
            PrepareActorBeforeTreeEntry(actor);
            CharacterIK characterIK = actor.GetNode<CharacterIK>("CharacterIK");
            left = Assert.IsType<FootPlantingTargetProvider>(characterIK.LeftFootTargetIntentProvider);
            right = Assert.IsType<FootPlantingTargetProvider>(characterIK.RightFootTargetIntentProvider);
            Assert.Equal(LimbSide.Left, left.Side);
            Assert.Equal(LimbSide.Right, right.Side);
            if (!planting)
            {
                characterIK.LeftFootTargetIntentProvider = null;
                characterIK.RightFootTargetIntentProvider = null;
            }

            sceneTree.Root.AddChild(root);
            INavigation navigation = await WaitForReadyNavigationAsync(sceneTree, root, actor);
            if (preparePairedIdlePhase)
            {
                await PreparePairedIdlePhaseAsync(sceneTree, actor, navigation, left, right);
            }
            recorder = BindRecorder(actor);
            recorder.Attach();
            left.TickRecorded += OnLeft;
            right.TickRecorded += OnRight;
            await WaitForPhysicsFramesAsync(sceneTree, WarmUpPhysicsTicks);
            if (preparePairedIdlePhase)
            {
                AssertIdlePhase(actor, (float)(WarmUpPhysicsTicks / (double)Engine.PhysicsTicksPerSecond));
            }

            trace.WindowStart = Engine.GetPhysicsFrames() + 1;
            for (int index = 0; index < requests.Length; index++)
            {
                NavigationRequestIdentity identity = IssueRequest(navigation, requests[index], scenario, index);
                NavigationTerminalOutcomeRecord? outcome = null;
                for (int tick = 0; tick < RequestTimeoutTicks && outcome is null; tick++)
                {
                    await WaitForNextPhysicsFrameAsync(sceneTree);
                    trace.ActorTrace.Add(actor.GlobalTransform);
                    outcome = FindOutcome(navigation, identity);
                }

                Assert.True(outcome is not null, $"SETUP: [{scenario}] request {index + 1} reported no outcome.");
                trace.TerminalTicks.Add(Engine.GetPhysicsFrames());
            }

            for (int tick = 0; tick < Engine.PhysicsTicksPerSecond; tick++)
            {
                await WaitForNextPhysicsFrameAsync(sceneTree);
                trace.ActorTrace.Add(actor.GlobalTransform);
            }

            await WaitForFramesAsync(sceneTree, 1);
            trace.WindowEnd = Engine.GetPhysicsFrames() - 1;
            recorder.Detach();
            foreach (PlantedStateSample sample in recorder.Samples)
            {
                trace.Recorded[sample.Tick] = sample;
            }

            trace.LeftReachBlendOuts = left.ReachBlendOutCount;
            trace.RightReachBlendOuts = right.ReachBlendOutCount;
            trace.PinnedFootTicks = trace.Left.Values.Count(value => value.Phase == FootPlantingPhase.Pinned)
                + trace.Right.Values.Count(value => value.Phase == FootPlantingPhase.Pinned);
            return trace;
        }
        finally
        {
            recorder?.Detach();
            if (left is not null)
            {
                left.TickRecorded -= OnLeft;
            }

            if (right is not null)
            {
                right.TickRecorded -= OnRight;
            }

            Engine.MaxPhysicsStepsPerFrame = previousStepLimit;
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    private static async Task PreparePairedIdlePhaseAsync(
        SceneTree sceneTree, Node3D actor, INavigation navigation,
        FootPlantingTargetProvider left, FootPlantingTargetProvider right)
    {
        Assert.Equal(60, Engine.PhysicsTicksPerSecond);
        NPCNavigation npc = Assert.IsType<NPCNavigation>(navigation, exactMatch: false);
        Assert.True(npc.IsRuntimeBindingActive && npc.IsMovementAuthorityActive,
            "SETUP: paired Idle preparation requires the active native NPC owner.");
        Assert.False(npc.HasDestination);
        NPCAnimationTreeDriver driver = npc.AnimationDriver
            ?? throw new InvalidOperationException("SETUP: paired Idle preparation has no native animation driver.");
        long evaluations = driver.EvaluationCount;
        Assert.True(driver.TryRequestTransition(NPCAnimationProgramme.IdleState, 0d, 0d),
            "SETUP: native driver refused the paired authored Idle start.");
        await WaitForNextPhysicsFrameAsync(sceneTree);
        Assert.False(driver.HasPendingTransition);
        Assert.Equal(evaluations + 1, driver.EvaluationCount);
        AssertIdlePhase(actor, 0f);
        left.ResetPin();
        right.ResetPin();
    }

    private static void AssertIdlePhase(Node3D actor, float expectedPosition)
    {
        AnimationTree tree = actor.RequireNode<AnimationTree>("AnimationTree");
        AnimationNodeStateMachinePlayback playback = tree.Get("parameters/States/playback")
            .As<AnimationNodeStateMachinePlayback>();
        using StringName current = playback.GetCurrentNode();
        using StringName fading = playback.GetFadingFromNode();
        Assert.Equal(NPCAnimationProgramme.IdleState, current);
        Assert.True(fading.IsEmpty, "SETUP: paired Idle admission must not be fading.");
        Assert.Equal(expectedPosition, playback.GetCurrentPlayPosition());
    }

    private static ScenarioAnalysis Analyse(ScenarioTrace trace)
    {
        var report = new StringBuilder();
        int disagreements = 0;
        int missing = 0;
        float maxReach = 0f;
        var csv = new StringBuilder("tick,actor_x,actor_z,rec_l,rec_r,rt_l,rt_r,phase_l,phase_r,reach_l,reach_r,post_l_x,post_l_z,post_r_x,post_r_z,anim_l_x,anim_l_z,anim_r_x,anim_r_z\n");
        for (ulong tick = trace.WindowStart; tick <= trace.WindowEnd; tick++)
        {
            bool hasSample = trace.Recorded.TryGetValue(tick, out PlantedStateSample? sample);
            bool hasLeft = trace.Left.TryGetValue(tick, out FootPlantingTickDiagnostic l) && l.PostIKCaptured && l.PlantedStateAvailable;
            bool hasRight = trace.Right.TryGetValue(tick, out FootPlantingTickDiagnostic r) && r.PostIKCaptured && r.PlantedStateAvailable;
            missing += (hasLeft ? 0 : 1) + (hasRight ? 0 : 1);
            if (hasSample && hasLeft && hasRight
                && (sample!.Planted.Left != l.MetadataPlanted || sample.Planted.Right != r.MetadataPlanted))
            {
                disagreements++;
                _ = report.AppendLine(FormattableString.Invariant(
                    $"disagreement tick {tick}: recorder L={sample.Planted.Left} R={sample.Planted.Right} runtime L={l.MetadataPlanted} R={r.MetadataPlanted} [{string.Join("; ", sample.Contributions)}]"));
            }

            maxReach = Math.Max(maxReach, Math.Max(hasLeft ? l.ReachUsage : 0f, hasRight ? r.ReachUsage : 0f));
            Vector3 actor = hasSample ? sample!.Actor.Origin : Vector3.Zero;
            _ = csv.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{tick},{actor.X:R},{actor.Z:R},{(hasSample && sample!.Planted.Left ? 1 : 0)},{(hasSample && sample!.Planted.Right ? 1 : 0)},{(l.MetadataPlanted ? 1 : 0)},{(r.MetadataPlanted ? 1 : 0)},{l.Phase},{r.Phase},{l.ReachUsage:R},{r.ReachUsage:R},{l.PostIKBall.X:R},{l.PostIKBall.Z:R},{r.PostIKBall.X:R},{r.PostIKBall.Z:R},{l.AnimatedBall.X:R},{l.AnimatedBall.Z:R},{r.AnimatedBall.X:R},{r.AnimatedBall.Z:R}"));
        }

        string worst = AttributeRuns(trace, report);
        _ = report.Insert(0, FormattableString.Invariant(
            $"window={trace.WindowStart}..{trace.WindowEnd} terminal={string.Join('|', trace.TerminalTicks)} disagreements={disagreements} missing_diagnostics={missing} max_reach_usage={maxReach:0.####} reach_blend_outs L={trace.LeftReachBlendOuts} R={trace.RightReachBlendOuts}\n"));
        _ = report.AppendLine().Append(csv);
        return new ScenarioAnalysis(
            (int)(trace.WindowEnd - trace.WindowStart + 1),
            disagreements,
            missing,
            maxReach,
            trace.LeftReachBlendOuts,
            trace.RightReachBlendOuts,
            worst,
            report.ToString());
    }

    /// <summary>
    /// For every recorder planted run whose post-IK ball excursion from its first tick exceeds the A2 tolerance, reports
    /// how many ticks the provider was not pinned, reach releases, and the actor's planar motion while pinned.
    /// </summary>
    private static string AttributeRuns(ScenarioTrace trace, StringBuilder report)
    {
        string worst = "none";
        float worstExcursion = 0f;
        foreach (LimbSide side in new[] { LimbSide.Left, LimbSide.Right })
        {
            Dictionary<ulong, FootPlantingTickDiagnostic> diagnostics = side == LimbSide.Left ? trace.Left : trace.Right;
            ulong start = 0;
            for (ulong tick = trace.WindowStart; tick <= trace.WindowEnd + 1; tick++)
            {
                bool planted = tick <= trace.WindowEnd
                    && trace.Recorded.TryGetValue(tick, out PlantedStateSample? sample)
                    && (side == LimbSide.Left ? sample.Planted.Left : sample.Planted.Right);
                if (planted && start == 0)
                {
                    start = tick;
                }
                else if (!planted && start != 0)
                {
                    ulong end = tick - 1;
                    Vector3 anchor = Planar(trace.Recorded[start].Ball(side == LimbSide.Left ? StandingLocomotionAnatomicalSide.Left : StandingLocomotionAnatomicalSide.Right));
                    float excursion = 0f;
                    ulong excursionTick = start;
                    int notPinned = 0;
                    int reachReleases = 0;
                    float actorWhilePinned = 0f;
                    float maxActorStepWhilePinned = 0f;
                    for (ulong runTick = start; runTick <= end; runTick++)
                    {
                        PlantedStateSample runSample = trace.Recorded[runTick];
                        float distance = Planar(runSample.Ball(side == LimbSide.Left ? StandingLocomotionAnatomicalSide.Left : StandingLocomotionAnatomicalSide.Right)).DistanceTo(anchor);
                        if (distance > excursion)
                        {
                            excursion = distance;
                            excursionTick = runTick;
                        }

                        bool pinned = diagnostics.TryGetValue(runTick, out FootPlantingTickDiagnostic diagnostic)
                            && diagnostic.Phase == FootPlantingPhase.Pinned;
                        notPinned += pinned ? 0 : 1;
                        reachReleases += diagnostic.ReachBlendOutStarted ? 1 : 0;
                        if (pinned && runTick > start && trace.Recorded.TryGetValue(runTick - 1, out PlantedStateSample? previous))
                        {
                            float step = Planar(runSample.Actor.Origin).DistanceTo(Planar(previous.Actor.Origin));
                            actorWhilePinned += step;
                            maxActorStepWhilePinned = Math.Max(maxActorStepWhilePinned, step);
                        }
                    }

                    string line = FormattableString.Invariant(
                        $"{side} run {start}..{end} ({end - start + 1} ticks) excursion={excursion * 1000:0.###} mm at {excursionTick}; not_pinned_ticks={notPinned} reach_releases={reachReleases} actor_travel_while_pinned={actorWhilePinned * 1000:0.###} mm max_actor_step_while_pinned={maxActorStepWhilePinned * 1000:0.###} mm terminal_in_run={trace.TerminalTicks.Any(terminal => terminal >= start && terminal <= end)} starting in [{string.Join("; ", trace.Recorded[start].Contributions)}]");
                    if (excursion > SlipToleranceMetres)
                    {
                        _ = report.AppendLine(line);
                    }

                    if (excursion > worstExcursion)
                    {
                        worstExcursion = excursion;
                        worst = line;
                    }

                    start = 0;
                }
            }
        }

        _ = report.AppendLine("worst: " + worst);
        return worst;
    }

    private static void WriteEvidence(string file, string text)
    {
        string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(EvidenceDirectory));
        File.WriteAllText(Path.Combine(directory, file), text);
    }

    private sealed class ScenarioTrace
    {
        public Dictionary<ulong, FootPlantingTickDiagnostic> Left { get; } = [];

        public Dictionary<ulong, FootPlantingTickDiagnostic> Right { get; } = [];

        public Dictionary<ulong, PlantedStateSample> Recorded { get; } = [];

        public List<Transform3D> ActorTrace { get; } = [];

        public List<ulong> TerminalTicks { get; } = [];

        public ulong WindowStart
        {
            get; set;
        }

        public ulong WindowEnd
        {
            get; set;
        }

        public int LeftReachBlendOuts
        {
            get; set;
        }

        public int RightReachBlendOuts
        {
            get; set;
        }

        public int PinnedFootTicks
        {
            get; set;
        }
    }

    private readonly record struct ScenarioAnalysis(
        int Ticks,
        int Disagreements,
        int MissingDiagnostics,
        float MaxReachUsage,
        int LeftReachBlendOuts,
        int RightReachBlendOuts,
        string Worst,
        string Report)
    {
        public string SummaryLine(string scenario) => FormattableString.Invariant(
            $"{scenario},{Ticks},{Disagreements},{MissingDiagnostics},{MaxReachUsage:0.####},{LeftReachBlendOuts},{RightReachBlendOuts},\"{Worst}\"");
    }
}
