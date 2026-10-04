using System.Globalization;
using System.Text;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core.Installer;
using AlleyCat.IK;
using AlleyCat.Navigation;
using AlleyCat.Rigging.Installation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>
/// Outcome-only NAV-001 arrival acceptance tests. They drive the authored navigation test scene exclusively through the
/// public <see cref="INavigation"/> request/outcome API and observe only the actor transform, the rendered skeleton, and
/// the AnimationTree graph state, so they remain valid across any rebuild of the NPC arrival/stop machinery.
/// </summary>
/// <remarks>
/// <para>Observation window: from the physics tick the (first) request is issued until one second after the (last)
/// request reports its terminal outcome.</para>
/// <para>A1: the actor's world transform at completion is within 0.01 m planar and 1° yaw of the requested destination,
/// and the request reports <see cref="NavigationTerminalOutcome.Completed"/>.</para>
/// <para>A2 (planted-foot slip): <see cref="AnimationGraphPlantedStateRecorder"/> records, per tick, the rendered
/// post-IK ball joint of each foot (IK-003 TR7: the toe bone's rest-pose origin in foot space) and the graph state
/// evaluated on that tick. Planted state comes from the ANIM-002 per-foot planted intervals combined under the Planted
/// Combination Rule, with clip weights and source times recomputed from the recorded graph state, never from the
/// foot-planting implementation. A planted run is a continuous run of ticks on which one foot is planted; each run is
/// anchored at the ball joint's position on its own first tick (a run split by even a one-tick unplanted flicker
/// restarts its anchor; a run already planted when the window opens is anchored at the window's first tick). A run's
/// slip is the maximum horizontal (world XZ) distance of the ball joint from that anchor over the run; vertical motion
/// and rotation about the ball are not slip. The worst run over both feet must be ≤ 5 mm. Each run's summed XZ path and
/// its first-to-last net displacement are reported as diagnostics only.</para>
/// <para>A3: every actor global transform from completion to one second after is exactly (component-wise, without
/// tolerance) equal to the completion transform.</para>
/// <para>A4 proxy (report only, not NAV-001 A4 evidence): actor planar per-tick motion while both feet are planted
/// under the same rule. It mixes animation root motion with any synthetic correction, so it cannot show that no
/// correction was applied; A4 evidence needs navigation's own recorded corrections.</para>
/// <para>NAV-TAC-008 (seams): at every tick of every crossfade in the window, (1) phase agreement — every contributing
/// state marks each foot with the same planted/swing state under the Planted Combination Rule over its own clips'
/// ANIM-002 intervals at their recorded playback positions (asserted); (2) pose agreement — the separation of the states'
/// bind-time table balls (report only, as the seam tolerance is not yet agreed); (3) swing travel — on ticks a foot
/// swings under the rule, the summed recorded pre-IK ball travel, root-relative in each tick's actor frame, must not
/// exceed the summed travel of the clips' own table-ball motion blended by effective weight by more than the
/// provisional bound (asserted). The pre-IK ball is read from the mixer-written pose before any skeleton modifier runs,
/// and a frame-integrity gate checks it against the tables on single-clip ticks.</para>
/// <para>A request that reports no terminal outcome within 20 s fails as a timeout; the remaining criteria are still
/// measured from the timeout tick and reported.</para>
/// </remarks>
public sealed class NAV001ArrivalOutcomeIntegrationTests
{
    internal const string NavigationTestScenePath = "res://assets/testing/navigation/navigation_test.tscn";
    internal const string ActorNodeName = "NavigationTestNpc";
    internal const string SkeletonPath = "Female/GeneralSkeleton";
    // Output-only redirect: runs may point evidence at a fresh tree via ALLEYCAT_NAV001_OUTCOME_EVIDENCE without
    // touching the recorded parent-outcome folder; the default remains the original location. Resolution is
    // non-destructive (CORNER-06): an occupied destination redirects to a fresh sibling with a note instead of
    // overwriting prior evidence.
    private static readonly string _evidenceDirectory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(
        OS.GetEnvironment("ALLEYCAT_NAV001_OUTCOME_EVIDENCE") is { Length: > 0 } path
            ? $"res://temp/{path.TrimStart('/')}"
            : "res://temp/nav001-robotic-pivot-ranking-order-fix/run-01/parent-outcome"));

    private const float PlanarPositionToleranceMetres = 0.01f;
    private const float YawToleranceDegrees = 1f;
    private const float PlantedSlipToleranceMetres = 0.005f;

    /// <summary>
    /// Provisional NAV-TAC-008 swing-travel bound (the spec leaves it tunable and not yet user-agreed): 50 mm, the
    /// excess-forward-travel threshold of the shuffle diagnostic's PUSH detector, which separated the reported pushes
    /// (excess 89–441 mm) from ordinary crossfades.
    /// </summary>
    private const float SwingTravelExcessBoundMetres = 0.05f;

    /// <summary>
    /// Measurement-integrity gate for the pre-IK ball frame: on single-clip ticks the recorded pre-IK ball must be
    /// within twice the tables' declared 5 mm ball interpolation bound of that clip's table ball.
    /// </summary>
    private const float SeamFrameIntegrityToleranceMetres = 0.01f;

    private const int ReadinessTimeoutPhysicsTicks = 600;
    internal const int WarmUpPhysicsTicks = 30;
    private const double RequestTimeoutSeconds = 20.0;
    private const double PostCompletionObservationSeconds = 1.0;
    private const int ReportedRuns = 5;

    // Values copied from the NAV-001 Stage 4 diagnostic presets (destination and yaw in world space; the actor spawns at
    // the identity transform).
    private static readonly ArrivalRequest _terminalSlide = new(new Vector3(0.48621222f, 0f, -1.419342f), -0.33003297f);

    /// <summary>Terminal Slide mirrored about the actor's forward axis.</summary>
    [Fact]
    public Task TerminalSlideMirror_ArrivesExactlyWithoutFootSlipAndStaysStill()
        => RunScenarioAsync(
            "terminal-slide-mirror",
            [new ArrivalRequest(_terminalSlide.Destination with { X = -_terminalSlide.Destination.X }, -_terminalSlide.YawRadians)]);

    /// <summary>0.5 m directly to the actor's right, keeping the original facing.</summary>
    [Fact]
    public Task LateralTarget_ArrivesExactlyWithoutFootSlipAndStaysStill()
        => RunScenarioAsync("lateral-0.5m", [new ArrivalRequest(new Vector3(0.5f, 0f, 0f), 0f)]);

    /// <summary>Turn in place 90° to the left with no translation.</summary>
    [Fact]
    public Task TurnOnly_ArrivesExactlyWithoutFootSlipAndStaysStill()
        => RunScenarioAsync("turn-only-90deg", [new ArrivalRequest(Vector3.Zero, Mathf.Pi / 2f)]);

    private static async Task RunScenarioAsync(string scenario, IReadOnlyList<ArrivalRequest> requests)
    {
        SceneTree sceneTree = GetSceneTree();
        int previousStepLimit = Engine.MaxPhysicsStepsPerFrame;
        Engine.MaxPhysicsStepsPerFrame = 1;
        Node3D root = Assert.IsType<Node3D>(LoadPackedScene(NavigationTestScenePath).Instantiate(), exactMatch: false);
        AnimationGraphPlantedStateRecorder? recorder = null;

        try
        {
            Node3D actor = root.GetNode<Node3D>(ActorNodeName);
            PrepareActorBeforeTreeEntry(actor);
            sceneTree.Root.AddChild(root);

            INavigation navigation = await WaitForReadyNavigationAsync(sceneTree, root, actor);
            recorder = BindRecorder(actor);
            recorder.Attach();
            await WaitForPhysicsFramesAsync(sceneTree, WarmUpPhysicsTicks);
            AssertIdleWarmUp(recorder, actor);

            int ticksPerSecond = Engine.PhysicsTicksPerSecond;
            int timeoutTicks = (int)Math.Ceiling(RequestTimeoutSeconds * ticksPerSecond);
            int settleTicks = (int)Math.Ceiling(PostCompletionObservationSeconds * ticksPerSecond);

            var results = new List<RequestResult>(requests.Count);
            string? timeout = null;
            ulong windowStart = 0;
            for (int index = 0; index < requests.Count; index++)
            {
                ArrivalRequest request = requests[index];
                NavigationRequestIdentity identity = IssueRequest(navigation, request, scenario, index);
                ulong issueTick = Engine.GetPhysicsFrames();
                if (index == 0)
                {
                    windowStart = issueTick;
                }

                NavigationTerminalOutcomeRecord? outcome = null;
                for (int tick = 0; tick < timeoutTicks && outcome is null; tick++)
                {
                    await WaitForNextPhysicsFrameAsync(sceneTree);
                    outcome = FindOutcome(navigation, identity);
                }

                if (outcome is null)
                {
                    // Timeout is a criterion failure (completion never reported); keep measuring the remaining
                    // criteria from the timeout tick so the report stays complete, and issue no further requests.
                    timeout = FormattableString.Invariant(
                        $"TIMEOUT: request {index + 1}/{requests.Count} to {request.Destination} yaw {Mathf.RadToDeg(request.YawRadians):0.###}° reported no terminal outcome within {RequestTimeoutSeconds:0} s (status {navigation.CurrentStatus.Status}); A1 below is measured at the timeout tick and A3 after it.");
                }

                results.Add(new RequestResult(request, issueTick, Engine.GetPhysicsFrames(), outcome, actor.GlobalTransform));
                if (outcome is null)
                {
                    break;
                }
            }

            // A3: completion (or timeout) to one second after, measured on the authoritative actor transform. Movement
            // must be exactly zero, so every tick's transform must equal the completion transform component-wise.
            Transform3D completionTransform = actor.GlobalTransform;
            ulong completionTick = Engine.GetPhysicsFrames();
            float maxPostDisplacement = 0f;
            float maxPostYawDegrees = 0f;
            int changedTicks = 0;
            ulong? firstChangedTick = null;
            for (int tick = 0; tick < settleTicks; tick++)
            {
                await WaitForNextPhysicsFrameAsync(sceneTree);
                Transform3D current = actor.GlobalTransform;
                if (!ExactlyEqual(current, completionTransform))
                {
                    changedTicks++;
                    firstChangedTick ??= Engine.GetPhysicsFrames();
                }

                maxPostDisplacement = Math.Max(maxPostDisplacement, current.Origin.DistanceTo(completionTransform.Origin));
                maxPostYawDegrees = Math.Max(
                    maxPostYawDegrees,
                    Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(YawOf(completionTransform), YawOf(current)))));
            }

            // Let the last tick's skeleton update land before closing the record.
            await WaitForFramesAsync(sceneTree, 1);
            ulong windowEnd = results[^1].TerminalTick + (ulong)settleTicks;
            recorder.Detach();
            Transform3D finalTransform = actor.GlobalTransform;

            PlantedStateSample[] window = RequireCompleteWindow(recorder, windowStart, windowEnd);
            var metrics = SlipMetrics.Evaluate(window, ticksPerSecond);

            var failures = new List<string>();
            if (timeout is not null)
            {
                failures.Add(timeout);
            }
            var report = new StringBuilder();
            _ = report.AppendLine(CultureInfo.InvariantCulture, $"scenario={scenario} window_ticks={windowStart}..{windowEnd} samples={window.Length} ticks_per_second={ticksPerSecond}");
            for (int index = 0; index < results.Count; index++)
            {
                RequestResult result = results[index];
                float planarError = Planar(result.CompletionTransform.Origin).DistanceTo(Planar(result.Request.Destination));
                float yawErrorDegrees = Mathf.Abs(Mathf.RadToDeg(
                    Mathf.AngleDifference(result.Request.YawRadians, YawOf(result.CompletionTransform))));
                _ = report.AppendLine(FormattableString.Invariant(
                    $"A1 request {index + 1}: issued={result.IssueTick} terminal={result.TerminalTick} ({(result.TerminalTick - result.IssueTick) / (double)ticksPerSecond:0.00} s) outcome={DescribeOutcome(result.Outcome)} planar_error={planarError * 1000:0.###} mm yaw_error={yawErrorDegrees:0.####}°"));
                if (result.Outcome is { } terminal && terminal.Outcome != NavigationTerminalOutcome.Completed)
                {
                    failures.Add($"A1 request {index + 1}: outcome {DescribeOutcome(terminal)}, expected Completed.");
                }
                if (planarError > PlanarPositionToleranceMetres)
                {
                    failures.Add(FormattableString.Invariant($"A1 request {index + 1}: planar error {planarError * 1000:0.###} mm > {PlanarPositionToleranceMetres * 1000:0} mm."));
                }
                if (yawErrorDegrees > YawToleranceDegrees)
                {
                    failures.Add(FormattableString.Invariant($"A1 request {index + 1}: yaw error {yawErrorDegrees:0.####}° > {YawToleranceDegrees:0.##}°."));
                }
            }

            ArrivalRequest last = results[^1].Request;
            _ = report.AppendLine(FormattableString.Invariant(
                $"A1 end-of-window: planar_error={Planar(finalTransform.Origin).DistanceTo(Planar(last.Destination)) * 1000:0.###} mm yaw_error={Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(last.YawRadians, YawOf(finalTransform)))):0.####}°"));
            _ = report.AppendLine(metrics.Describe());
            _ = report.AppendLine(FormattableString.Invariant(
                $"recorder: interrupted crossfade ticks={window.Count(sample => sample.Graph.Interruption is not null)} (conservative planted stricter than exact on {window.Count(sample => sample.Planted != sample.ExactPlanted)}); undetermined transition starts (whole recording)={recorder.UndeterminedTransitionStarts}; layer filters verified: {string.Join(", ", recorder.VerifiedLayers)}"));
            if (metrics.Worst is { } worst && worst.MaxExcursionMetres > PlantedSlipToleranceMetres)
            {
                failures.Add(FormattableString.Invariant(
                    $"A2: worst planted-run ball excursion {worst.MaxExcursionMetres * 1000:0.###} mm ({worst.Describe()}) > {PlantedSlipToleranceMetres * 1000:0} mm."));
            }

            _ = report.AppendLine(FormattableString.Invariant(
                $"A3: completion tick {completionTick}; {changedTicks} of {settleTicks} post-completion ticks differ from the completion transform (exact component comparison){(firstChangedTick is { } first ? FormattableString.Invariant($", first at tick {first}") : string.Empty)}; diagnostics max displacement={maxPostDisplacement * 1000:0.######} mm max yaw={maxPostYawDegrees:0.#######}°"));
            if (changedTicks > 0)
            {
                failures.Add(FormattableString.Invariant(
                    $"A3: actor transform changed on {changedTicks} of {settleTicks} post-completion ticks (first at tick {firstChangedTick}); max displacement {maxPostDisplacement * 1000:0.######} mm, max yaw {maxPostYawDegrees:0.#######}°; required exactly zero."));
            }

            var seams = CrossfadeSeamMetrics.Evaluate(window, recorder.Trajectories, ticksPerSecond);
            _ = report.AppendLine(seams.Describe());
            failures.AddRange(seams.Failures());

            string reportText = report.ToString();
            WriteEvidence(scenario, reportText, window, metrics, seams);
            Assert.True(
                failures.Count == 0,
                $"[{scenario}] {failures.Count} arrival criterion failure(s):\n  {string.Join("\n  ", failures)}\n{reportText}");
        }
        finally
        {
            recorder?.Detach();
            Engine.MaxPhysicsStepsPerFrame = previousStepLimit;
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    /// <summary>Binds the independent A2 recorder to the NPC's AnimationTree, AnimationPlayer, and skeleton.</summary>
    internal static AnimationGraphPlantedStateRecorder BindRecorder(Node3D actor) =>
        AnimationGraphPlantedStateRecorder.Bind(
            actor,
            actor.GetNode<AnimationTree>("AnimationTree"),
            actor.GetNode<AnimationPlayer>("AnimationPlayer"),
            actor.GetNode<Skeleton3D>(SkeletonPath));

    /// <summary>
    /// Setup gate: the NPC starts at the identity spawn transform, idling, and the recorder sees Idle's intervals.
    /// </summary>
    internal static void AssertIdleWarmUp(AnimationGraphPlantedStateRecorder recorder, Node3D actor)
    {
        Assert.True(recorder.Samples.Count > 0, "SETUP: no recorder samples were observed during warm-up.");
        PlantedStateSample idle = recorder.Samples[^1];
        Assert.True(
            idle.Graph.Fading is null && idle.Contributions.Length == 1 && idle.Planted == new PlantedFeetState(true, true),
            $"SETUP: warm-up is not a settled, fully planted single-clip Idle ({idle.Graph.Describe()}: {string.Join("; ", idle.Contributions)}).");
        Assert.True(
            actor.GlobalPosition.Length() < 0.001f
                && Mathf.Abs(Mathf.RadToDeg(YawOf(actor.GlobalTransform))) < 0.05f,
            $"SETUP: actor did not start at the identity spawn transform ({actor.GlobalTransform}).");
    }

    /// <summary>
    /// Measurement-integrity gate: every tick of the window is recorded once, with graph state from the same tick.
    /// </summary>
    internal static PlantedStateSample[] RequireCompleteWindow(
        AnimationGraphPlantedStateRecorder recorder,
        ulong windowStart,
        ulong windowEnd)
    {
        PlantedStateSample[] window = [.. recorder.Samples.Where(sample => sample.Tick >= windowStart && sample.Tick <= windowEnd)];
        Assert.True(
            window.Length == (int)(windowEnd - windowStart + 1) && window[0].Tick == windowStart,
            FormattableString.Invariant(
                $"SETUP: recorder covered {window.Length} of {windowEnd - windowStart + 1} window ticks ({windowStart}..{windowEnd}); unpaired={recorder.UnpairedTicks}, repeated={recorder.RepeatedSkeletonUpdates}."));
        return window;
    }

    /// <summary>
    /// Installs the NPC role before tree entry and removes the authored AI mind so that no agent other than the test
    /// issues navigation or head-tracking intent.
    /// </summary>
    internal static void PrepareActorBeforeTreeEntry(Node3D actor)
    {
        RigRoleTemplateSceneInstaller installer = actor.GetNode<RigRoleTemplateSceneInstaller>("NPCCharacterInstaller");
        installer.AutoInstallOnReady = false;
        SceneInstallationResult installation = installer.Install(new SceneInstallationContext(actor));
        Assert.True(installation.Succeeded, "SETUP: NPC installation failed:\n" + string.Join('\n', installation.Errors));

        if (actor.GetNodeOrNull("CharacterIK") is CharacterIK characterIK)
        {
            characterIK.HeadTargetIntentProvider = null;
            characterIK.HeadFallbackIntentProvider = null;
        }
        if (actor.GetNodeOrNull("Mind") is { } mind)
        {
            actor.RemoveChild(mind);
            mind.Free();
        }
    }

    internal static async Task<INavigation> WaitForReadyNavigationAsync(SceneTree sceneTree, Node3D root, Node3D actor)
    {
        NavigationRegion3D region = root.GetNode<NavigationRegion3D>("NavigationRegion3D");
        for (int tick = 0; tick < ReadinessTimeoutPhysicsTicks; tick++)
        {
            await WaitForNextPhysicsFrameAsync(sceneTree);
            Rid map = region.GetNavigationMap();
            if (actor.GetNodeOrNull("Navigation") is INavigation navigation
                && map.IsValid
                && NavigationServer3D.MapGetIterationId(map) > 0
                && actor.GetNodeOrNull<Skeleton3D>(SkeletonPath) is not null)
            {
                return navigation;
            }
        }

        throw new InvalidOperationException("SETUP: navigation component, navigation map, or skeleton was not ready in time.");
    }

    internal static NavigationRequestIdentity IssueRequest(INavigation navigation, ArrivalRequest request, string scenario, int index)
    {
        var destination = new Transform3D(new Basis(Vector3.Up, request.YawRadians), request.Destination);
        NavigationDestinationResult result = navigation.SetDestination(destination);
        Assert.True(
            result == NavigationDestinationResult.Accepted,
            $"[{scenario}] request {index + 1} to {destination} was refused: {result}.");
        NavigationRequestIdentity? identity = navigation.CurrentStatus.ActiveRequest;
        Assert.True(identity.HasValue, $"[{scenario}] accepted request {index + 1} has no active request identity.");
        return identity!.Value;
    }

    internal static NavigationTerminalOutcomeRecord? FindOutcome(INavigation navigation, NavigationRequestIdentity identity)
    {
        IReadOnlyList<NavigationTerminalOutcomeRecord> outcomes = navigation.TerminalOutcomes;
        for (int index = outcomes.Count - 1; index >= 0; index--)
        {
            if (outcomes[index].Request == identity)
            {
                return outcomes[index];
            }
        }

        return null;
    }

    private static string DescribeOutcome(NavigationTerminalOutcomeRecord? outcome)
        => outcome is { } terminal ? $"{terminal.Outcome}/{terminal.Reason}" : "none (timeout)";

    private static void WriteEvidence(
        string scenario,
        string report,
        IReadOnlyList<PlantedStateSample> samples,
        SlipMetrics metrics,
        CrossfadeSeamMetrics seams)
    {
        string directory = _evidenceDirectory;
        File.WriteAllText(Path.Combine(directory, $"{scenario}.txt"), report);
        File.WriteAllText(Path.Combine(directory, $"{scenario}.csv"), FormatSamplesCsv(samples));
        File.WriteAllText(Path.Combine(directory, $"{scenario}-runs.csv"), metrics.FormatRunsCsv());
        File.WriteAllText(Path.Combine(directory, $"{scenario}-crossfades.csv"), seams.FormatTicksCsv());
    }

    /// <summary>Exact component-wise equality of two transforms (basis columns and origin), with no tolerance.</summary>
    internal static bool ExactlyEqual(Transform3D a, Transform3D b) =>
        a.Basis.X.X == b.Basis.X.X && a.Basis.X.Y == b.Basis.X.Y && a.Basis.X.Z == b.Basis.X.Z
        && a.Basis.Y.X == b.Basis.Y.X && a.Basis.Y.Y == b.Basis.Y.Y && a.Basis.Y.Z == b.Basis.Y.Z
        && a.Basis.Z.X == b.Basis.Z.X && a.Basis.Z.Y == b.Basis.Z.Y && a.Basis.Z.Z == b.Basis.Z.Z
        && a.Origin.X == b.Origin.X && a.Origin.Y == b.Origin.Y && a.Origin.Z == b.Origin.Z;

    /// <summary>
    /// Per-tick evidence: actor, ball joints, raw graph state, recomputed contributions, planted state (conservative and
    /// exact-weight), and whether the tick interrupted a running crossfade.
    /// </summary>
    internal static string FormatSamplesCsv(IReadOnlyList<PlantedStateSample> samples)
    {
        var csv = new StringBuilder(
            "tick,actor_x,actor_y,actor_z,actor_yaw_deg,lball_x,lball_y,lball_z,rball_x,rball_y,rball_z,planted_l,planted_r,exact_planted_l,exact_planted_r,interrupted_from,current,current_positions,blend_x,blend_y,fading_from,fading_positions,fade_pos,fade_len,contributions,pre_ik_lball_actor_x,pre_ik_lball_actor_y,pre_ik_lball_actor_z,pre_ik_rball_actor_x,pre_ik_rball_actor_y,pre_ik_rball_actor_z\n");
        foreach (PlantedStateSample sample in samples)
        {
            Vector3 a = sample.Actor.Origin;
            GraphRecord graph = sample.Graph;
            Vector2 blend = graph.CurrentBlend ?? graph.FadingBlend ?? new Vector2(float.NaN, float.NaN);
            Vector3 preLeft = sample.PreIKLeftBallInActor;
            Vector3 preRight = sample.PreIKRightBallInActor;
            _ = csv.AppendLine(FormattableString.Invariant(
                $"{sample.Tick},{a.X:R},{a.Y:R},{a.Z:R},{Mathf.RadToDeg(YawOf(sample.Actor)):R},{sample.LeftBall.X:R},{sample.LeftBall.Y:R},{sample.LeftBall.Z:R},{sample.RightBall.X:R},{sample.RightBall.Y:R},{sample.RightBall.Z:R},{(sample.Planted.Left ? 1 : 0)},{(sample.Planted.Right ? 1 : 0)},{(sample.ExactPlanted.Left ? 1 : 0)},{(sample.ExactPlanted.Right ? 1 : 0)},{graph.Interruption?.State},{graph.Current},{string.Join('|', graph.CurrentPositions.Select(value => value.ToString("R", CultureInfo.InvariantCulture)))},{blend.X:R},{blend.Y:R},{graph.Fading},{string.Join('|', graph.FadingPositions.Select(value => value.ToString("R", CultureInfo.InvariantCulture)))},{graph.FadingPosition:R},{graph.FadingLength:R},{string.Join(';', sample.Contributions)},{preLeft.X:R},{preLeft.Y:R},{preLeft.Z:R},{preRight.X:R},{preRight.Y:R},{preRight.Z:R}"));
        }

        return csv.ToString();
    }

    /// <summary>Yaw of the actor's forward (-Z) about +Y, matching <c>new Basis(Vector3.Up, yaw)</c>.</summary>
    internal static float YawOf(Transform3D transform)
    {
        Vector3 forward = transform.Basis * Vector3.Forward;
        return Mathf.Atan2(-forward.X, -forward.Z);
    }

    internal static Vector3 Planar(Vector3 value) => new(value.X, 0f, value.Z);

    internal readonly record struct ArrivalRequest(Vector3 Destination, float YawRadians);

    private readonly record struct RequestResult(
        ArrivalRequest Request,
        ulong IssueTick,
        ulong TerminalTick,
        NavigationTerminalOutcomeRecord? Outcome,
        Transform3D CompletionTransform);

    /// <summary>
    /// One continuous planted run of one foot. <see cref="MaxExcursionMetres"/> (the A2 measure) is the largest planar
    /// distance of the ball joint from its position on the run's first tick; path and net are diagnostics only.
    /// </summary>
    internal readonly record struct SlipRun(
        StandingLocomotionAnatomicalSide Foot,
        float MaxExcursionMetres,
        ulong MaxExcursionTick,
        float PathMetres,
        float NetMetres,
        ulong StartTick,
        ulong EndTick,
        string StartClips)
    {
        public string Describe() => FormattableString.Invariant(
            $"{Foot} ticks {StartTick}..{EndTick} ({EndTick - StartTick + 1}) max_excursion={MaxExcursionMetres * 1000:0.###} mm at tick {MaxExcursionTick}; diagnostics path={PathMetres * 1000:0.###} mm net={NetMetres * 1000:0.###} mm; starting in [{StartClips}]");
    }

    /// <summary>A2 slip over the Planted Combination Rule intervals and the A4 proxy (both-planted actor motion).</summary>
    internal sealed class SlipMetrics
    {
        private readonly List<SlipRun> _runs = [];
        private int _leftPlanted;
        private int _rightPlanted;
        private int _samples;
        private float _maxDoublePlantedActorStep;
        private ulong _maxDoublePlantedActorTick;
        private float _totalDoublePlantedActorTravel;
        private float _maxDoublePlantedActorYawDegrees;
        private float _totalDoublePlantedActorYawDegrees;
        private int _doublePlantedSteps;
        private int _ticksPerSecond;

        /// <summary>The run with the largest ball excursion from its own anchor, over both feet.</summary>
        public SlipRun? Worst => _runs.Count == 0 ? null : _runs.MaxBy(run => run.MaxExcursionMetres);

        public static SlipMetrics Evaluate(
            IReadOnlyList<PlantedStateSample> samples,
            int ticksPerSecond)
        {
            var metrics = new SlipMetrics
            {
                _samples = samples.Count,
                _ticksPerSecond = ticksPerSecond,
            };
            foreach (StandingLocomotionAnatomicalSide foot in Enum.GetValues<StandingLocomotionAnatomicalSide>())
            {
                metrics.CollectRuns(samples, foot);
            }

            for (int index = 0; index < samples.Count; index++)
            {
                PlantedStateSample sample = samples[index];
                metrics._leftPlanted += sample.Planted.Left ? 1 : 0;
                metrics._rightPlanted += sample.Planted.Right ? 1 : 0;
                if (index > 0 && IsDoublePlanted(sample) && IsDoublePlanted(samples[index - 1]))
                {
                    float step = Planar(sample.Actor.Origin).DistanceTo(Planar(samples[index - 1].Actor.Origin));
                    float yaw = Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(YawOf(samples[index - 1].Actor), YawOf(sample.Actor))));
                    metrics._totalDoublePlantedActorTravel += step;
                    metrics._totalDoublePlantedActorYawDegrees += yaw;
                    metrics._maxDoublePlantedActorYawDegrees = Math.Max(metrics._maxDoublePlantedActorYawDegrees, yaw);
                    metrics._doublePlantedSteps++;
                    if (step > metrics._maxDoublePlantedActorStep)
                    {
                        metrics._maxDoublePlantedActorStep = step;
                        metrics._maxDoublePlantedActorTick = sample.Tick;
                    }
                }
            }

            return metrics;
        }

        public string Describe()
        {
            var text = new StringBuilder();
            _ = text.AppendLine(FormattableString.Invariant(
                $"A2: worst planted-run ball excursion from its run anchor={(Worst?.MaxExcursionMetres ?? 0f) * 1000:0.###} mm ({Worst?.Describe() ?? "no planted run"}); planted ticks L={_leftPlanted} R={_rightPlanted} of {_samples}; runs={_runs.Count}"));
            _ = text.AppendLine($"A2 top {ReportedRuns} runs by max excursion (path and net are diagnostics, not the A2 measure):");
            foreach (SlipRun run in _runs.OrderByDescending(run => run.MaxExcursionMetres).Take(ReportedRuns))
            {
                _ = text.AppendLine("  run " + run.Describe());
            }

            _ = text.Append(FormattableString.Invariant(
                $"A4 proxy (report only; NOT NAV-001 A4 evidence: actor planar motion while both feet are planted mixes animation root motion with any synthetic correction; A4 evidence needs navigation-recorded corrections): max step={_maxDoublePlantedActorStep * 1000:0.###} mm/tick ({_maxDoublePlantedActorStep * _ticksPerSecond:0.####} m/s) at tick {_maxDoublePlantedActorTick}; total={_totalDoublePlantedActorTravel * 1000:0.###} mm; yaw max step={_maxDoublePlantedActorYawDegrees:0.###}°/tick total={_totalDoublePlantedActorYawDegrees:0.###}° over {_doublePlantedSteps} steps"));
            return text.ToString();
        }

        /// <summary>Every planted run, in foot then tick order, with the A2 measure and the diagnostic columns.</summary>
        public string FormatRunsCsv()
        {
            var csv = new StringBuilder("foot,start_tick,end_tick,ticks,max_excursion_mm,max_excursion_tick,diagnostic_path_mm,diagnostic_net_mm,start_clips\n");
            foreach (SlipRun run in _runs)
            {
                _ = csv.AppendLine(FormattableString.Invariant(
                    $"{run.Foot},{run.StartTick},{run.EndTick},{run.EndTick - run.StartTick + 1},{run.MaxExcursionMetres * 1000:R},{run.MaxExcursionTick},{run.PathMetres * 1000:R},{run.NetMetres * 1000:R},\"{run.StartClips}\""));
            }

            return csv.ToString();
        }

        private static bool IsDoublePlanted(PlantedStateSample sample) => sample.Planted.Left && sample.Planted.Right;

        private void CollectRuns(IReadOnlyList<PlantedStateSample> samples, StandingLocomotionAnatomicalSide foot)
        {
            int start = -1;
            Vector3 anchor = default;
            float path = 0f;
            float excursion = 0f;
            ulong excursionTick = 0;
            for (int index = 0; index <= samples.Count; index++)
            {
                bool planted = index < samples.Count && samples[index].Planted.IsPlanted(foot);
                if (planted && start < 0)
                {
                    // Every run, including one split off by a single unplanted tick, has its own anchor.
                    start = index;
                    anchor = Planar(samples[index].Ball(foot));
                    path = 0f;
                    excursion = 0f;
                    excursionTick = samples[index].Tick;
                }
                else if (planted)
                {
                    Vector3 ball = Planar(samples[index].Ball(foot));
                    path += ball.DistanceTo(Planar(samples[index - 1].Ball(foot)));
                    float distance = ball.DistanceTo(anchor);
                    if (distance > excursion)
                    {
                        excursion = distance;
                        excursionTick = samples[index].Tick;
                    }
                }
                else if (start >= 0)
                {
                    PlantedStateSample first = samples[start];
                    PlantedStateSample last = samples[index - 1];
                    _runs.Add(new SlipRun(
                        foot,
                        excursion,
                        excursionTick,
                        path,
                        Planar(last.Ball(foot)).DistanceTo(anchor),
                        first.Tick,
                        last.Tick,
                        string.Join("; ", first.Contributions)));
                    start = -1;
                }
            }
        }
    }

    /// <summary>
    /// NAV-TAC-008 seam checks over every crossfade in the window. A crossfade is a maximal run of recorded ticks with the
    /// same fading-from and current state (a restarted or interrupting fade starts a new one); every tick of it is
    /// checked. Each contributing state's per-foot planted state is the Planted Combination Rule over that state's own
    /// recomputed clip contributions, and its ball is those clips' bind-time table balls blended by their weights within
    /// the state. Every input is the recorder's graph state, playback positions, and mixer-output (pre-IK) ball points;
    /// nothing is read from the foot-planting code.
    /// </summary>
    internal sealed class CrossfadeSeamMetrics
    {
        private static readonly StandingLocomotionAnatomicalSide[] _feet =
            [StandingLocomotionAnatomicalSide.Left, StandingLocomotionAnatomicalSide.Right];

        private readonly List<Crossfade> _crossfades = [];
        private readonly StringBuilder _ticks = new(
            "crossfade,tick,foot,from,to,state_feet,phase_agrees,combined_planted,recorded_step_mm,predicted_step_mm,pose_separation_mm\n");

        private float _frameErrorMax;
        private ulong _frameErrorTick;
        private string _frameErrorClip = string.Empty;
        private int _frameTicks;
        private float _preToPostMax;
        private ulong _preToPostTick;
        private int _ticksPerSecond;

        public static CrossfadeSeamMetrics Evaluate(
            IReadOnlyList<PlantedStateSample> samples,
            IReadOnlyDictionary<string, ClipTrajectory> trajectories,
            int ticksPerSecond)
        {
            var metrics = new CrossfadeSeamMetrics { _ticksPerSecond = ticksPerSecond };
            for (int index = 0; index < samples.Count; index++)
            {
                PlantedStateSample sample = samples[index];
                PlantedStateSample? previous = index > 0 ? samples[index - 1] : null;
                metrics.CheckFrame(sample, trajectories);
                if (sample.Graph.Fading is null)
                {
                    continue;
                }

                if (previous is null
                    || previous.Graph.Fading != sample.Graph.Fading
                    || previous.Graph.Current != sample.Graph.Current
                    || sample.Graph.FadingPosition < previous.Graph.FadingPosition
                    || sample.Graph.Interruption is not null)
                {
                    metrics._crossfades.Add(new Crossfade(metrics._crossfades.Count + 1, sample));
                }

                metrics._crossfades[^1].Add(sample, previous, trajectories, metrics._ticks);
            }

            return metrics;
        }

        public IEnumerable<string> Failures()
        {
            if (_frameTicks == 0)
            {
                yield return "MEASUREMENT: no single-clip tick with a ball-joint table was recorded, so the pre-IK ball frame could not be checked against the tables.";
            }
            else if (_frameErrorMax > SeamFrameIntegrityToleranceMetres)
            {
                yield return FormattableString.Invariant(
                    $"MEASUREMENT: recorded pre-IK ball differs from the single clip's table ball by {_frameErrorMax * 1000:0.###} mm at tick {_frameErrorTick} ({_frameErrorClip}) > {SeamFrameIntegrityToleranceMetres * 1000:0} mm; the root-relative frame does not match the tables.");
            }

            foreach (Crossfade crossfade in _crossfades)
            {
                foreach (FootSeam foot in crossfade.Feet)
                {
                    if (foot.PhaseMismatchTicks > 0)
                    {
                        yield return FormattableString.Invariant(
                            $"NAV-TAC-008 phase: {crossfade.Describe()} {foot.Foot}: the contributing clips disagree on planted/swing on {foot.PhaseMismatchTicks} of {crossfade.TickCount} ticks; first at {foot.FirstMismatch}.");
                    }

                    if (foot.ExcessMetres > SwingTravelExcessBoundMetres)
                    {
                        yield return FormattableString.Invariant(
                            $"NAV-TAC-008 swing travel: {crossfade.Describe()} {foot.Foot}: recorded pre-IK swing travel {foot.RecordedMetres * 1000:0.###} mm exceeds the weight-blended clip travel {foot.PredictedMetres * 1000:0.###} mm by {foot.ExcessMetres * 1000:0.###} mm > {SwingTravelExcessBoundMetres * 1000:0} mm over {foot.SwingTicks} swing ticks (peak step {foot.PeakRecordedStep * _ticksPerSecond:0.###} m/s vs predicted {foot.PeakPredictedStep * _ticksPerSecond:0.###} m/s at tick {foot.PeakTick}).");
                    }
                }
            }
        }

        /// <summary>Additional configured pose and trajectory-coverage gates for isolated planner replays.</summary>
        public IEnumerable<string> ConfiguredPoseFailures(float toleranceMetres)
        {
            foreach (Crossfade crossfade in _crossfades)
            {
                foreach (FootSeam foot in crossfade.Feet)
                {
                    if (foot.MaxSeparation is not { } separation || separation > toleranceMetres)
                    {
                        yield return $"NAV-TAC-008 pose: {crossfade.Describe()} {foot.Foot}: missing ball table or separation {foot.MaxSeparation} m exceeds {toleranceMetres} m.";
                    }

                    if (foot.UncoveredSwingTicks > 0)
                    {
                        yield return $"MEASUREMENT: {crossfade.Describe()} {foot.Foot}: {foot.UncoveredSwingTicks} swing ticks lack a trajectory baseline.";
                    }
                }
            }
        }

        public string Describe()
        {
            var text = new StringBuilder();
            _ = text.AppendLine(FormattableString.Invariant(
                $"NAV-TAC-008: {_crossfades.Count} crossfade(s); asserted: phase agreement, swing-travel excess ≤ {SwingTravelExcessBoundMetres * 1000:0} mm (provisional); pose agreement is report-only (seam tolerance not yet agreed)."));
            _ = text.AppendLine(FormattableString.Invariant(
                $"NAV-TAC-008 frame check: pre-IK ball vs single-clip table ball max {_frameErrorMax * 1000:0.###} mm at tick {_frameErrorTick} ({_frameErrorClip}) over {_frameTicks} ticks (gate {SeamFrameIntegrityToleranceMetres * 1000:0} mm); diagnostic pre-IK vs post-IK ball (actor frame) max {_preToPostMax * 1000:0.###} mm at tick {_preToPostTick}"));
            foreach (Crossfade crossfade in _crossfades)
            {
                _ = text.AppendLine("  " + crossfade.Describe());
                foreach (FootSeam foot in crossfade.Feet)
                {
                    _ = text.AppendLine("    " + foot.Describe(crossfade.TickCount, _ticksPerSecond));
                }
            }

            Crossfade? worst = _crossfades.MaxBy(crossfade => crossfade.Feet.Max(foot => foot.ExcessMetres));
            _ = text.Append(worst is null
                ? "NAV-TAC-008 worst swing-travel excess: no crossfade"
                : FormattableString.Invariant(
                    $"NAV-TAC-008 worst swing-travel excess: {worst.Feet.Max(foot => foot.ExcessMetres) * 1000:0.###} mm in {worst.Describe()}; max pose separation over all crossfades {_crossfades.SelectMany(crossfade => crossfade.Feet).Max(foot => foot.MaxSeparation ?? 0f) * 1000:0.###} mm"));
            return text.ToString();
        }

        public string FormatTicksCsv() => _ticks.ToString();

        /// <summary>
        /// Frame-integrity gate: on single-clip ticks the recorded pre-IK ball must match that clip's table ball, which
        /// shows both are the same root-relative point in the same axes.
        /// </summary>
        private void CheckFrame(PlantedStateSample sample, IReadOnlyDictionary<string, ClipTrajectory> trajectories)
        {
            Transform3D actorInverse = sample.Actor.AffineInverse();
            foreach (StandingLocomotionAnatomicalSide foot in _feet)
            {
                float preToPost = (actorInverse * sample.Ball(foot)).DistanceTo(sample.PreIKBallInActor(foot));
                if (preToPost > _preToPostMax)
                {
                    _preToPostMax = preToPost;
                    _preToPostTick = sample.Tick;
                }
            }

            if (sample.Contributions.Length != 1
                || !trajectories.TryGetValue(sample.Contributions[0].Animation, out ClipTrajectory? trajectory))
            {
                return;
            }

            _frameTicks++;
            foreach (StandingLocomotionAnatomicalSide foot in _feet)
            {
                float error = trajectory.Ball(foot, sample.Contributions[0].SourceTimeSeconds).DistanceTo(sample.PreIKBallInActor(foot));
                if (error > _frameErrorMax)
                {
                    _frameErrorMax = error;
                    _frameErrorTick = sample.Tick;
                    _frameErrorClip = sample.Contributions[0].ToString();
                }
            }
        }

        private sealed class Crossfade(int index, PlantedStateSample first)
        {
            public FootSeam[] Feet { get; } = [.. _feet.Select(foot => new FootSeam(foot))];

            public int TickCount
            {
                get; private set;
            }

            private ulong EndTick
            {
                get; set;
            } = first.Tick;

            public string Describe() => FormattableString.Invariant(
                $"XF{index} {first.Graph.Fading}→{first.Graph.Current}{(first.Graph.Interruption is { } interruption ? $" (interrupting {interruption.State})" : string.Empty)} ticks {first.Tick}..{EndTick} ({TickCount}) starting [{string.Join("; ", first.Contributions)}]");

            public void Add(
                PlantedStateSample sample,
                PlantedStateSample? previous,
                IReadOnlyDictionary<string, ClipTrajectory> trajectories,
                StringBuilder csv)
            {
                EndTick = sample.Tick;
                TickCount++;
                ClipContribution[][] states = [.. sample.Contributions.GroupBy(contribution => contribution.State).Select(group => group.ToArray())];
                foreach (FootSeam foot in Feet)
                {
                    bool[] planted = [.. states.Select(state => StatePlanted(state, foot.Foot))];
                    Vector3?[] balls = [.. states.Select(state => StateBall(state, foot.Foot, trajectories))];
                    bool agrees = planted.All(value => value == planted[0]);
                    string stateFeet = string.Join(
                        " | ",
                        states.Select((state, position) => $"{state[0].State}={(planted[position] ? "planted" : "swing")} [{string.Join(", ", state.Select(contribution => contribution.ToString()))}]"));
                    float? separation = balls.Length > 1 && balls.All(ball => ball.HasValue)
                        ? balls.SelectMany(a => balls, (a, b) => a!.Value.DistanceTo(b!.Value)).Max()
                        : null;
                    foot.AddPose(sample.Tick, agrees, stateFeet, separation);

                    bool swing = !sample.Planted.IsPlanted(foot.Foot);
                    float? recorded = null;
                    float? predicted = null;
                    if (swing && previous is not null)
                    {
                        recorded = sample.PreIKBallInActor(foot.Foot).DistanceTo(previous.PreIKBallInActor(foot.Foot));
                        predicted = PredictedStep(sample, previous, foot.Foot, trajectories);
                        foot.AddSwing(sample.Tick, recorded.Value, predicted);
                    }

                    _ = csv.AppendLine(FormattableString.Invariant(
                        $"{index},{sample.Tick},{foot.Foot},{first.Graph.Fading},{first.Graph.Current},\"{stateFeet}\",{(agrees ? 1 : 0)},{(swing ? 0 : 1)},{recorded * 1000:R},{predicted * 1000:R},{separation * 1000:R}"));
                }
            }

            private static bool StatePlanted(ClipContribution[] state, StandingLocomotionAnatomicalSide foot)
            {
                var rule = new PlantedClipContribution[state.Length];
                for (int position = 0; position < state.Length; position++)
                {
                    rule[position] = new(state[position].Intervals, state[position].SourceTimeSeconds, state[position].Weight);
                }

                return PlantedCombinationRule.IsPlanted(rule, foot);
            }

            private static Vector3? StateBall(
                ClipContribution[] state,
                StandingLocomotionAnatomicalSide foot,
                IReadOnlyDictionary<string, ClipTrajectory> trajectories)
            {
                Vector3 sum = Vector3.Zero;
                double total = 0d;
                foreach (ClipContribution contribution in state)
                {
                    if (!trajectories.TryGetValue(contribution.Animation, out ClipTrajectory? trajectory))
                    {
                        return null;
                    }

                    sum += trajectory.Ball(foot, contribution.SourceTimeSeconds) * (float)contribution.Weight;
                    total += contribution.Weight;
                }

                return sum / (float)total;
            }

            /// <summary>
            /// Travel predicted by the clips' own ball trajectories blended by effective weight: the length of
            /// Σ wᵢ(t)·(Ballᵢ(sᵢ(t)) − Ballᵢ(sᵢ(t−1))). A clip absent on the previous tick contributes no own motion.
            /// Returns <c>null</c> when a deciding clip has no table.
            /// </summary>
            private static float? PredictedStep(
                PlantedStateSample sample,
                PlantedStateSample previous,
                StandingLocomotionAnatomicalSide foot,
                IReadOnlyDictionary<string, ClipTrajectory> trajectories)
            {
                Vector3 step = Vector3.Zero;
                foreach (ClipContribution contribution in sample.Contributions)
                {
                    if (!trajectories.TryGetValue(contribution.Animation, out ClipTrajectory? trajectory))
                    {
                        if (PlantedCombinationRule.Decides(contribution.Weight))
                        {
                            return null;
                        }

                        continue;
                    }

                    foreach (ClipContribution prior in previous.Contributions)
                    {
                        if (prior.State == contribution.State && prior.Animation == contribution.Animation)
                        {
                            step += (trajectory.Ball(foot, contribution.SourceTimeSeconds) - trajectory.Ball(foot, prior.SourceTimeSeconds))
                                * (float)contribution.Weight;
                            break;
                        }
                    }
                }

                return step.Length();
            }
        }

        private sealed class FootSeam(StandingLocomotionAnatomicalSide foot)
        {
            public StandingLocomotionAnatomicalSide Foot => foot;

            public int PhaseMismatchTicks
            {
                get; private set;
            }

            public string? FirstMismatch
            {
                get; private set;
            }

            public int SwingTicks
            {
                get; private set;
            }

            public int UncoveredSwingTicks
            {
                get; private set;
            }

            public float RecordedMetres
            {
                get; private set;
            }

            public float PredictedMetres
            {
                get; private set;
            }

            public float ExcessMetres => RecordedMetres - PredictedMetres;

            public float PeakRecordedStep
            {
                get; private set;
            }

            public float PeakPredictedStep
            {
                get; private set;
            }

            public ulong PeakTick
            {
                get; private set;
            }

            public float? StartSeparation
            {
                get; private set;
            }

            public float? MaxSeparation
            {
                get; private set;
            }

            private ulong MaxSeparationTick
            {
                get; set;
            }

            private bool _started;

            public void AddPose(ulong tick, bool agrees, string stateFeet, float? separation)
            {
                if (!agrees)
                {
                    PhaseMismatchTicks++;
                    FirstMismatch ??= FormattableString.Invariant($"tick {tick}: {stateFeet}");
                }

                if (!_started)
                {
                    StartSeparation = separation;
                    _started = true;
                }

                if (separation is { } value && (MaxSeparation is null || value > MaxSeparation))
                {
                    MaxSeparation = value;
                    MaxSeparationTick = tick;
                }
            }

            public void AddSwing(ulong tick, float recorded, float? predicted)
            {
                if (predicted is not { } expected)
                {
                    UncoveredSwingTicks++;
                    return;
                }

                SwingTicks++;
                RecordedMetres += recorded;
                PredictedMetres += expected;
                if (recorded > PeakRecordedStep)
                {
                    PeakRecordedStep = recorded;
                    PeakPredictedStep = expected;
                    PeakTick = tick;
                }
            }

            public string Describe(int ticks, int ticksPerSecond) => FormattableString.Invariant(
                $"{Foot}: phase {(PhaseMismatchTicks == 0 ? "agrees" : $"DISAGREES on {PhaseMismatchTicks}/{ticks} ticks, first at {FirstMismatch}")}; swing travel recorded={RecordedMetres * 1000:0.###} mm predicted={PredictedMetres * 1000:0.###} mm excess={ExcessMetres * 1000:0.###} mm over {SwingTicks} swing ticks (uncovered {UncoveredSwingTicks}; peak {PeakRecordedStep * ticksPerSecond:0.###} m/s vs {PeakPredictedStep * ticksPerSecond:0.###} m/s at tick {PeakTick}); pose separation (report only) start={FormatMillimetres(StartSeparation)} max={FormatMillimetres(MaxSeparation)} at tick {MaxSeparationTick}");

            private static string FormatMillimetres(float? metres) =>
                metres is { } value ? FormattableString.Invariant($"{value * 1000:0.###} mm") : "n/a (no table)";
        }
    }
}
