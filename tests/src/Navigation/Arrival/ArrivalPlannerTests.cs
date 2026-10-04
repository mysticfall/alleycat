using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using AlleyCat.Control.Locomotion;
using AlleyCat.Navigation.Arrival;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Navigation.Arrival;

/// <summary>
/// <see cref="ArrivalPlanner"/> on a synthetic rig with analytic root motion and one shared gait phase: a static Idle,
/// forward entries, a forward cycle, turning arcs, forward exits, and looping pivots and side-steps. Every clip carries
/// the same root-relative balls, so every phase-agreeing seam is aligned unless a test offsets a clip's balls. Expected
/// values come from the NAV-001 A5 thresholds, the seam table's own queries and the clips' analytic motion, not from
/// the planner's search.
/// </summary>
public sealed class ArrivalPlannerTests
{
    private const double Rate = 30d;
    private const float Tolerance = 0.19f;

    private static readonly Vector3 _leftBall = new(0.1f, 0f, 0f);
    private static readonly Vector3 _rightBall = new(-0.1f, 0f, 0f);
    private static readonly SequencePose _origin = new(Vector2.Zero, 0f);

    /// <summary>Each A5 family is chosen on the inside of its threshold and not on the outside (design Q5 defaults).</summary>
    [Theory]
    [InlineData(0.004d, 0.004d, 0.005d, ArrivalMovementKind.None)]
    [InlineData(0.004d, 0.004d, 0.1d, ArrivalMovementKind.TurnOnly)]
    [InlineData(0.03d, 0.03d, 0d, ArrivalMovementKind.TurnOnly)]
    // Side-step: lateral-dominant (|x| > 2|y|), within 0.71 m, heading change within 45°.
    [InlineData(0.5d, 0.2d, 0d, ArrivalMovementKind.SideStep)]
    [InlineData(-0.7d, 0.1d, 0.7d, ArrivalMovementKind.SideStep)]
    [InlineData(0.5d, 0.26d, 0d, ArrivalMovementKind.TurnFirst)]
    [InlineData(0.72d, 0d, 0d, ArrivalMovementKind.TurnFirst)]
    [InlineData(0.5d, 0d, 0.8d, ArrivalMovementKind.TurnFirst)]
    // Turn first: bearing beyond 100°, or beyond 45° nearer than 1.5 m.
    [InlineData(0d, -1.5d, 0d, ArrivalMovementKind.TurnFirst)]
    [InlineData(3d, -0.6d, 0d, ArrivalMovementKind.TurnFirst)]
    [InlineData(1d, 0.9d, 0d, ArrivalMovementKind.TurnFirst)]
    // Arc: bearing in (45°, 100°] at 1.5 m or more.
    [InlineData(3d, 0d, 0d, ArrivalMovementKind.Arc)]
    [InlineData(-3d, -0.5d, 0d, ArrivalMovementKind.Arc)]
    [InlineData(1.1d, 1.05d, 0d, ArrivalMovementKind.Arc)]
    // Straight: bearing within 45°.
    [InlineData(0d, 4d, 0d, ArrivalMovementKind.Straight)]
    [InlineData(0.5d, 0.62d, 0.7d, ArrivalMovementKind.Straight)]
    [InlineData(-1d, 1.01d, 0d, ArrivalMovementKind.Straight)]
    public void MovementChoice_FollowsTheA5Thresholds(double right, double forward, double yaw, ArrivalMovementKind expected)
        => Assert.Equal(expected, ArrivalMovementChoice.Choose(right, forward, yaw, ArrivalPlannerConfig.Default));

    /// <summary>The A5 thresholds are tunable: moving one moves the boundary.</summary>
    [Fact]
    public void MovementChoice_ThresholdsAreTunable()
    {
        ArrivalPlannerConfig wider = ArrivalPlannerConfig.Default with
        {
            SideStepMaxDistanceMetres = 1f,
            ArcMinDistanceMetres = 4f
        };

        Assert.Equal(ArrivalMovementKind.TurnFirst, ArrivalMovementChoice.Choose(0.9d, 0d, 0d, ArrivalPlannerConfig.Default));
        Assert.Equal(ArrivalMovementKind.SideStep, ArrivalMovementChoice.Choose(0.9d, 0d, 0d, wider));
        Assert.Equal(ArrivalMovementKind.Arc, ArrivalMovementChoice.Choose(3d, 0d, 0d, ArrivalPlannerConfig.Default));
        Assert.Equal(ArrivalMovementKind.TurnFirst, ArrivalMovementChoice.Choose(3d, 0d, 0d, wider));
    }


    /// <summary>
    /// The planner plays the A5 family it chose: a turn only pivots, a nearby lateral target side-steps, a target
    /// behind pivots before it walks, a distant lateral target arcs into walking, and a target ahead walks.
    /// </summary>
    [Theory]
    [InlineData(0f, 0f, 1.5708f, ArrivalMovementKind.TurnOnly)]
    [InlineData(0.3f, 0f, 0f, ArrivalMovementKind.SideStep)]
    [InlineData(0f, 1.5f, 0f, ArrivalMovementKind.TurnFirst)]
    [InlineData(3f, 0f, 0f, ArrivalMovementKind.Arc)]
    [InlineData(0f, -3f, 0f, ArrivalMovementKind.Straight)]
    public void Plan_PlaysTheChosenFamily(float x, float z, float yaw, ArrivalMovementKind expected)
    {
        var rig = Rig.Build();

        ArrivalPlan plan = rig.Planner.Plan(IdleStart(rig), new ArrivalTarget(new Vector2(x, z), yaw));

        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        Assert.Equal(expected, plan.Kind);
        Assert.Equal(SeamClipRole.Idle, plan.Segments[^1].Kind);
        SeamClipRole[] kinds = [.. plan.Segments.Select(segment => segment.Kind)];
        string[] roles = [.. plan.Segments.Select(segment => segment.Role)];
        switch (expected)
        {
            case ArrivalMovementKind.TurnOnly:
                Assert.All(roles, role => Assert.Contains(role, (string[])["pl45", "pr45", "idle"]));
                Assert.Contains("pl45", roles);
                break;
            case ArrivalMovementKind.SideStep:
                Assert.Contains("sr", roles);
                Assert.DoesNotContain(SeamClipRole.Entry, kinds);
                break;
            case ArrivalMovementKind.TurnFirst:
                // A pivot aligns the NPC before the entry, turning it at least 90° towards the target behind.
                int entry = Array.IndexOf(kinds, SeamClipRole.Entry);
                Assert.True(entry > 0, string.Join(' ', roles));
                Assert.Contains(plan.Segments.Take(entry), segment => segment.Stage == ArrivalStage.Align && segment.Kind == SeamClipRole.Confined);
                Assert.Equal(ArrivalStage.Travel, plan.Segments[entry].Stage);
                break;
            case ArrivalMovementKind.Arc:
                Assert.Contains(plan.Segments, segment => segment.Role is "ar_l" && segment.Stage == ArrivalStage.Travel);
                break;
            case ArrivalMovementKind.Straight:
                Assert.Contains(SeamClipRole.Entry, kinds);
                Assert.Contains(SeamClipRole.Exit, kinds);
                break;
            case ArrivalMovementKind.None:
                break;
            case ArrivalMovementKind.ContinueTravel:
                break;
            case ArrivalMovementKind.TravelSection:
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Every join of every plan is a usable seam of a planner-set pair, aligned within the tolerance, with the pair's
    /// crossfade, requested at a native-grid outgoing time; no segment teleports (only a sequence's initial Start
    /// could, and a plan starts from the playing clip).
    /// </summary>
    [Theory]
    [InlineData(0f, 0f, 1.5708f)]
    [InlineData(0.3f, 0f, 0.3f)]
    [InlineData(0f, 1.5f, 0f)]
    [InlineData(3f, 0f, 0f)]
    [InlineData(-0.6f, -2.5f, 0.4f)]
    [InlineData(0f, -0.9f, 0f)]
    public void Plan_JoinsOnlyAtAlignedSeamsAndNeverTeleports(float x, float z, float yaw)
    {
        var rig = Rig.Build();

        ArrivalPlan plan = rig.Planner.Plan(IdleStart(rig), new ArrivalTarget(new Vector2(x, z), yaw));

        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        AssertAlignedJoins(rig, plan);
        Assert.True(rig.Planner.Predict(plan).IsValid);
    }

    /// <summary>The plan's summary is the exact prediction, and its residual is measured against the target.</summary>
    [Fact]
    public void Plan_ReportsTheExactPredictionAndResidual()
    {
        var rig = Rig.Build();
        var target = new ArrivalTarget(new Vector2(-0.6f, -2.5f), 0.4f);

        ArrivalPlan plan = rig.Planner.Plan(IdleStart(rig), target);
        SequencePrediction prediction = rig.Planner.Predict(plan);

        Assert.Equal(prediction.EndPose, plan.PredictedEnd);
        Assert.Equal(prediction.Ticks.Length, plan.TickCount);
        Assert.True(prediction.Windows.SequenceEqual(plan.Windows), "The summary's support windows differ from the per-tick prediction's.");
        Assert.Equal(target.Position - prediction.EndPose.Position, plan.PlanarResidual);
        Assert.Contains(plan.Windows, window => window.Phase.IsSingleSwing());
        // The synthetic clips reach the target closely; the residual is what swing correction would close.
        Assert.True(plan.PlanarResidual.Length() < 0.05f, $"{plan.PlanarResidual.Length()} m");
        Assert.True(Math.Abs(plan.YawResidual) < Mathf.DegToRad(3f), $"{Mathf.RadToDeg(plan.YawResidual)}°");
    }

    /// <summary>
    /// The work budget bounds the search: a plan evaluates at most the configured compositions and exactly predicts
    /// at most the configured finalists, and reports when the budget ran out.
    /// </summary>
    [Fact]
    public void Plan_RespectsTheWorkBudget()
    {
        var unbounded = Rig.Build();
        var bounded = Rig.Build(ArrivalPlannerConfig.Default with
        {
            MaxCompositions = 40,
            MaxFinalists = 3
        });
        var target = new ArrivalTarget(new Vector2(3f, 0f), 0f);

        ArrivalPlan full = unbounded.Planner.Plan(IdleStart(unbounded), target);
        ArrivalPlan plan = bounded.Planner.Plan(IdleStart(bounded), target);

        Assert.True(full.Statistics.Compositions > 40, $"SETUP: the unbounded search used {full.Statistics.Compositions} compositions.");
        Assert.True(plan.Statistics.Compositions <= 40, $"{plan.Statistics.Compositions}");
        Assert.True(plan.Statistics.BudgetExhausted);
        Assert.InRange(plan.Statistics.SummaryPredictions, 0, 3);
        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        AssertAlignedJoins(bounded, plan);
    }

    /// <summary>
    /// With no aligned seam into any exit (exit balls far from the cycle's, a tight tolerance), no walk can end in Idle:
    /// a target ahead fails with no aligned plan rather than joining unaligned, while a turn only still plans.
    /// </summary>
    [Fact]
    public void Plan_FailsWhenNoAlignedPlanExists()
    {
        var rig = Rig.Build(exitBallOffset: new Vector3(0f, 0f, 0.2f), tolerance: 0.05f);

        ArrivalPlan walk = rig.Planner.Plan(IdleStart(rig), new ArrivalTarget(new Vector2(0f, -3f), 0f));
        ArrivalPlan turn = rig.Planner.Plan(IdleStart(rig), new ArrivalTarget(Vector2.Zero, 1f));

        Assert.Equal(ArrivalPlanStatus.Failed, walk.Status);
        Assert.Equal(ArrivalPlanFailure.NoAlignedPlan, walk.Failure);
        Assert.Empty(walk.Segments);
        Assert.Equal(ArrivalPlanStatus.Planned, turn.Status);
        AssertAlignedJoins(rig, turn);
    }

    /// <summary>A start clip outside the planner set is not plannable.</summary>
    [Fact]
    public void Plan_RejectsAnUnplannableStart()
    {
        var rig = Rig.Build();

        ArrivalPlan plan = rig.Planner.Plan(new ArrivalStart(_origin, "missing", 0d), new ArrivalTarget(Vector2.Zero, 1f));

        Assert.Equal(ArrivalPlanFailure.InvalidStart, plan.Failure);
    }

    /// <summary>
    /// A replan from the forward cycle mid-swing leaves it only at aligned seams at or after its playback position and
    /// ends in Idle near the new target; a prompt replan leaves at the cycle's first aligned seam into an exit.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plan_ReplansFromAMidClipStart(bool prompt)
    {
        var rig = Rig.Build();
        var start = new ArrivalStart(_origin, "wf", 0.4d)
        {
            Prompt = prompt
        };
        var target = new ArrivalTarget(new Vector2(0.8f, -3f), -0.3f);

        ArrivalPlan plan = rig.Planner.Plan(start, target);

        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        Assert.Equal("wf", plan.SequenceStart!.CurrentRole);
        AssertAlignedJoins(rig, plan);
        Assert.True(plan.PlanarResidual.Length() < 0.1f, $"{plan.PlanarResidual.Length()} m");
        if (prompt)
        {
            Assert.True(rig.Seams.TryFindNextAligned("wf", ["f2i_l", "f2i_r"], 0.4d, Tolerance, out SeamChoice first));
            Assert.Equal(first.Candidate.OutgoingTime, plan.SequenceStart.ExitSeconds, 9);
            Assert.Equal(0, plan.SequenceStart.ExitCycles);
            Assert.Equal(first.Pair.Incoming.Role, plan.Segments[0].Role);
        }
    }

    /// <summary>A replan from a running crossfade waits for it to end before the first transition.</summary>
    [Fact]
    public void Plan_ReplansFromARunningCrossfadeOnlyAfterItEnds()
    {
        var rig = Rig.Build();
        // Idle entered from the right exit two ticks ago: seven of its nine crossfade ticks remain.
        var start = new ArrivalStart(_origin, "idle", 0.5d, new SequenceCrossfadeProgress("f2i_r", 1.2d, 2f / 60f, 0.15f));

        ArrivalPlan plan = rig.Planner.Plan(start, new ArrivalTarget(Vector2.Zero, 1f));

        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        Assert.True(plan.SequenceStart!.ExitSeconds >= 0.5d + (7d / 60d) - 1e-9d, $"{plan.SequenceStart.ExitSeconds}");
        Assert.True(rig.Planner.Predict(plan).IsValid);
    }

    /// <summary>
    /// A prompt replan from a looping pivot mid-play leaves it at its first aligned seam into Idle and continues from
    /// Idle through the first aligned authored successor (Idle or its native continuation). A continuation is not
    /// an endpoint: its bounded prefix must still reach Idle at an aligned seam.
    /// </summary>
    [Fact]
    public void Plan_ReplansFromAMidPivotThroughItsFirstAlignedSeamIntoIdle()
    {
        var rig = Rig.Build();
        var start = new ArrivalStart(_origin, "pl45", 0.5d)
        {
            Prompt = true
        };

        ArrivalPlan plan = rig.Planner.Plan(start, new ArrivalTarget(Vector2.Zero, 0f));

        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        Assert.Equal("pl45", plan.SequenceStart!.CurrentRole);
        Assert.Contains(plan.Segments[0].Role, (string[])["idle", "pl45"]);
        Assert.Equal(0, plan.SequenceStart.ExitCycles);
        Assert.True(rig.Seams.TryFindNextAligned("pl45", (string[])["idle", "pl45"], 0.5d, Tolerance, out SeamChoice expected));
        Assert.Equal(expected.Candidate.OutgoingTime, plan.SequenceStart.ExitSeconds, 9);
        Assert.Equal(expected.Candidate.IncomingTime, plan.Segments[0].EntrySeconds, 9);
        Assert.Equal(SeamClipRole.Idle, plan.Segments[^1].Kind);
        AssertAlignedJoins(rig, plan);
        Assert.True(rig.Planner.Predict(plan).IsValid);
    }

    /// <summary>
    /// A prompt replan from an exit mid-play leaves it at its first aligned seam into Idle and continues from Idle.
    /// The exit's gait swings its right foot over [0.8, 1.0), so from 0.8 s both feet plant again only over
    /// [1.0, 1.2), which the 0.15 s blend window fits: the first aligned seam leaves at 1.0 s (hand-worked from the
    /// fixture's intervals; the seam-table query cross-checks it).
    /// </summary>
    [Fact]
    public void Plan_ReplansFromAMidExitIntoIdleAtItsFirstAlignedSeam()
    {
        var rig = Rig.Build();
        var start = new ArrivalStart(_origin, "f2i_l", 0.8d)
        {
            Prompt = true
        };
        // The exit walks 0.5 m/s until its final double support at 1 s: from 0.8 s it advances 0.1 m ahead.
        var target = new ArrivalTarget(new Vector2(0f, -0.1f), 0f);

        ArrivalPlan plan = rig.Planner.Plan(start, target);

        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        Assert.Equal("f2i_l", plan.SequenceStart!.CurrentRole);
        Assert.Equal("idle", plan.Segments[0].Role);
        Assert.Equal(SeamClipRole.Idle, plan.Segments[0].Kind);
        Assert.Equal(1.0d, plan.SequenceStart.ExitSeconds, 9);
        Assert.Equal(0, plan.SequenceStart.ExitCycles);
        Assert.True(rig.Seams.TryFindNextAligned("f2i_l", "idle", 0.8d, Tolerance, out SeamChoice expected));
        Assert.Equal(1.0d, expected.Candidate.OutgoingTime, 9);
        Assert.Equal(expected.Candidate.IncomingTime, plan.Segments[0].EntrySeconds, 9);
        Assert.Equal(SeamClipRole.Idle, plan.Segments[^1].Kind);
        AssertAlignedJoins(rig, plan);
        Assert.True(rig.Planner.Predict(plan).IsValid);
    }

    /// <summary>
    /// A non-prompt replan from a mid-pivot or mid-exit start also leaves through Idle: every join of the chosen plan
    /// is an aligned seam, and the start clip is never left before its playback position.
    /// </summary>
    [Theory]
    [InlineData("pl45", 0.5d)]
    [InlineData("f2i_l", 0.8d)]
    public void Plan_ReplansFromAMidManoeuvreOrExitThroughIdle(string role, double position)
    {
        var rig = Rig.Build();
        var start = new ArrivalStart(_origin, role, position);

        ArrivalPlan plan = rig.Planner.Plan(start, new ArrivalTarget(Vector2.Zero, 0f));

        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        Assert.Equal("idle", plan.Segments[0].Role);
        Assert.True(plan.SequenceStart!.ExitSeconds >= position - 1e-9d, $"{plan.SequenceStart.ExitSeconds} s");
        AssertAlignedJoins(rig, plan);
        Assert.True(rig.Planner.Predict(plan).IsValid);
    }

    /// <summary>The planner declares which planner-set pairs it uses, and never issues a pair outside them.</summary>
    [Fact]
    public void UsablePairs_CoverEveryIssuedJoin()
    {
        var rig = Rig.Build();

        Assert.NotEmpty(rig.Planner.UsablePairs);
        Assert.All(rig.Planner.UsablePairs, pair => Assert.True(rig.Seams.IsInPlannerSet(pair.Outgoing.Role, pair.Incoming.Role)));
        foreach ((float x, float z, float yaw) in (ReadOnlySpan<(float, float, float)>)[(0f, 0f, 1.5708f), (0.3f, 0f, 0f), (0f, 1.5f, 0f), (3f, 0f, 0f), (0f, -3f, 0f)])
        {
            ArrivalPlan plan = rig.Planner.Plan(IdleStart(rig), new ArrivalTarget(new Vector2(x, z), yaw));
            string previous = plan.Start.Role;
            foreach (ArrivalPlanSegment segment in plan.Segments)
            {
                Assert.True(rig.Seams.TryGetPair(previous, segment.Role, out SeamPair? pair));
                Assert.Contains(pair!, rig.Planner.UsablePairs);
                previous = segment.Role;
            }
        }
    }

    /// <summary>Stationary partial composition remains bounded and uses real, same-direction Idle joins.</summary>
    [Theory]
    [InlineData(10, 1)]
    [InlineData(-10, 1)]
    [InlineData(20, 1)]
    [InlineData(-20, 1)]
    [InlineData(25, 1)]
    [InlineData(-25, 1)]
    [InlineData(90, 2)]
    [InlineData(-90, 2)]
    [InlineData(135, 1)]
    public void Plan_StationaryPartialsComposeThroughIdle(int degrees, int blocks)
    {
        // At ±90 the synthetic fixture deliberately permits no extra loops: one block cannot reach the
        // heading, while two same-direction partials can. Other rows retain ordinary repeated-unit options.
        ArrivalPlannerConfig config = ArrivalPlannerConfig.Default with
        {
            MaxPivotLoops = Math.Abs(degrees) == 90 ? 0 : ArrivalPlannerConfig.Default.MaxPivotLoops
        };
        var rig = Rig.Build(config);
        ArrivalPlan plan = rig.Planner.Plan(IdleStart(rig), new ArrivalTarget(Vector2.Zero, Mathf.DegToRad(degrees)));
        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        Assert.True(Math.Abs(Mathf.RadToDeg(plan.YawResidual)) <= 1f, $"{degrees}: {Mathf.RadToDeg(plan.YawResidual)}; {string.Join(';', plan.Segments)}");
        Assert.All(plan.Segments, segment => Assert.Contains(segment.Role, (string[])["idle", degrees > 0 ? "pl45" : "pr45"]));
        AssertAlignedJoins(rig, plan);
        Assert.Equal(blocks, plan.Segments.Count(segment => segment.Kind == SeamClipRole.Confined));
        Assert.InRange(plan.Statistics.Compositions, 1, rig.Planner.Config.MaxCompositions);
        Assert.Equal(rig.Planner.Predict(plan).EndPose, plan.PredictedEnd);
        if (Math.Abs(degrees) == 90)
        {
            var singleOnly = new ArrivalPlanner(rig.Seams, rig.Predictor, config with
            {
                MaxCompositions = 2
            });
            ArrivalPlan single = singleOnly.Plan(IdleStart(rig), new ArrivalTarget(Vector2.Zero, Mathf.DegToRad(degrees)));
            _ = Assert.Single(single.Segments.Where(segment => segment.Kind == SeamClipRole.Confined));
            Assert.True(Math.Abs(Mathf.RadToDeg(single.YawResidual)) > 1f);
            Assert.Equal(2, single.Statistics.Compositions);
            Assert.True(single.Statistics.BudgetExhausted);
        }

        if (degrees == 135)
        {
            Assert.Contains(plan.Segments, segment => segment.Kind == SeamClipRole.Confined && segment.ExitCycles > 0);
        }
    }

    /// <summary>A native repeated pivot need not return to Idle between equally accurate stationary turns.</summary>
    [Fact]
    public void Plan_StationaryTurnPrefersContinuousPivotOverIdleBridge()
    {
        // Vary the ball position by pivot phase so the alternative seams have different separations.
        var rig = Rig.Build(pivotBallShift: time => new Vector3(0f, 0f, 0.02f * Mathf.Sin(Mathf.Tau * (float)time)));
        ArrivalPlan plan = rig.Planner.Plan(IdleStart(rig), new ArrivalTarget(Vector2.Zero, Mathf.DegToRad(90.3f)));

        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        Assert.InRange(Math.Abs(Mathf.RadToDeg(plan.YawResidual)), 0f, 1f);
        _ = Assert.Single(plan.Segments.Where(segment => segment.Kind == SeamClipRole.Confined));
        Assert.True(plan.Statistics.SummaryPredictions >= 2, "Equal-bucket finalists must reach exact prediction.");
        Assert.DoesNotContain(plan.Segments.SkipLast(1), segment => segment.Kind == SeamClipRole.Idle);
        Assert.Contains(plan.Segments, segment => segment.Kind == SeamClipRole.Confined && segment.ExitCycles > 0);
        AssertAlignedJoins(rig, plan);
    }

    /// <summary>
    /// A search can contain stationary and continuing-travel candidates. Its bounded offer and sorted-finalist
    /// paths must agree on one winner regardless of the order the three candidates arrive in.
    /// </summary>
    [Fact]
    public void Search_MixedCandidatesUseOneTransitiveOrderingForOffersAndFinalists()
    {
        foreach (int finalists in (int[])[2, 3])
        {
            foreach (string order in (string[])["ABC", "ACB", "BAC", "BCA", "CAB", "CBA"])
            {
                object search = CreateOrderingSearch(stationaryOnly: false, finalists);
                foreach (char candidate in order)
                {
                    OfferOrderingCandidate(search, candidate);
                }

                Assert.Equal('B', BestOrderingCandidate(search)); // legacy mixed ordering: 10 mm seam
            }
        }
    }

    /// <summary>Within a standalone stationary search the uninterrupted pivot beats the smaller-seam pair.</summary>
    [Fact]
    public void Search_StandaloneStationaryCandidatesPreferNoIdleBridge()
    {
        foreach (string order in (string[])["AB", "BA"])
        {
            object search = CreateOrderingSearch(stationaryOnly: true, finalists: 2);
            foreach (char candidate in order)
            {
                OfferOrderingCandidate(search, candidate);
            }

            Assert.Equal('A', BestOrderingCandidate(search));
        }
    }

    /// <summary>Real S4 rival fields: a productive fifth piece must survive the eight-slot offer path.</summary>
    [Fact]
    public void Search_StandaloneStationaryDurationBeatsPieceCountAndSeparationInEitherOfferOrder()
    {
        foreach (bool reverse in (bool[])[false, true])
        {
            object search = CreateOrderingSearch(stationaryOnly: true, finalists: 8);
            Type planner = typeof(ArrivalPlanner);
            Type recipeType = planner.GetNestedType("Recipe", BindingFlags.NonPublic)!;
            Type kindType = planner.GetNestedType("MainKind", BindingFlags.NonPublic)!;
            Type candidateType = planner.GetNestedType("Candidate", BindingFlags.NonPublic)!;
            object recipe = Activator.CreateInstance(recipeType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                [Enum.Parse(kindType, "None"), -1, -1, 0], null)!;
            recipeType.GetProperty("Kind")!.SetValue(recipe, ArrivalMovementKind.TurnOnly);
            recipeType.GetProperty("Face")!.SetValue(recipe, 0);
            List<object> offers = [];
            for (int index = 0; index < 8; index++)
            {
                offers.Add(Activator.CreateInstance(candidateType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    [0.000504447352760881d, 4, 0.008933576f + (index * 0.001f), 6.6d, recipe], null)!);
            }

            offers.Add(Activator.CreateInstance(candidateType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                [0.0006576557139152607d, 5, 0.108977735f, 5.933333333333334d, recipe], null)!);
            foreach (object offer in reverse ? offers.AsEnumerable().Reverse() : offers)
            {
                _ = search.GetType().GetMethod("Offer")!.Invoke(search, [offer]);
            }

            var best = (IList)search.GetType().GetProperty("Best")!.GetValue(search)!;
            Assert.Equal(8, best.Count);
            Assert.Contains(best.Cast<object>(), candidate => (int)candidateType.GetProperty("Steps")!.GetValue(candidate)! == 5);
            Assert.Equal(5, candidateType.GetProperty("Steps")!.GetValue(best[0]));
            Assert.Equal(5.933333333333334d, candidateType.GetProperty("Duration")!.GetValue(best[0]));
        }
    }

    private static object CreateOrderingSearch(bool stationaryOnly, int finalists)
    {
        Type type = typeof(ArrivalPlanner).GetNestedType("Search", BindingFlags.NonPublic)!;
        ConstructorInfo constructor = Assert.Single(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        ArrivalPlannerConfig config = ArrivalPlannerConfig.Default with
        {
            MaxFinalists = finalists
        };
        return constructor.Invoke([config, PlanarMotion.Identity, stationaryOnly]);
    }

    private static void OfferOrderingCandidate(object search, char name)
    {
        Type planner = typeof(ArrivalPlanner);
        Type recipeType = planner.GetNestedType("Recipe", BindingFlags.NonPublic)!;
        Type kindType = planner.GetNestedType("MainKind", BindingFlags.NonPublic)!;
        Type candidateType = planner.GetNestedType("Candidate", BindingFlags.NonPublic)!;
        object recipe = Activator.CreateInstance(recipeType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [Enum.Parse(kindType, "None"), -1, -1, 0], null)!;
        recipeType.GetProperty("Kind")!.SetValue(recipe,
            name == 'C' ? ArrivalMovementKind.ContinueTravel : ArrivalMovementKind.TurnOnly);
        recipeType.GetProperty("Align")!.SetValue(recipe, name == 'B' ? 0 : -1);
        recipeType.GetProperty("Face")!.SetValue(recipe, name == 'B' ? 1 : 0);
        object candidate = Activator.CreateInstance(candidateType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [0.001d, 4, name == 'A' ? 0.03f : name == 'B' ? 0.01f : 0.02f, 4d, recipe], null)!;
        _ = search.GetType().GetMethod("Offer")!.Invoke(search, [candidate]);
    }

    private static char BestOrderingCandidate(object search)
    {
        var best = (IList)search.GetType().GetProperty("Best")!.GetValue(search)!;
        object candidate = best[0]!;
        float separation = (float)candidate.GetType().GetProperty("Separation")!.GetValue(candidate)!;
        Assert.Contains(separation, (float[])[0.01f, 0.02f, 0.03f]);
        return separation == 0.03f ? 'A' : separation == 0.01f ? 'B' : 'C';
    }

    /// <summary>A lower-separation incoming cut does not dominate a shorter legal exit tail.</summary>
    [Fact]
    public void Plan_DirectSplicePreservesEndpointDiversity()
    {
        var rig = Rig.BuildSpliceDiversity();
        // Independent arithmetic, metres: v = 1/2, dt = 1/60, fade = 9 ticks.
        // Idle->entry: incoming advances on ticks 2..9, weights 2/9..9/9 => 44/1080.
        // Entry solo .133333->.3: 1/12. Middle fade: outgoing first weight 8/9,
        // then eight full-speed ticks => 80/1080. Exit solo .933333->1: 1/30.
        // Exit->Idle contributes zero (exit motion stops at 1). Total = 25/108 m.
        const float expectedDistance = 25f / 108f;
        Assert.True(rig.Seams.TryEvaluateSeam("i2f_r", "f2i_l", 0.3d, 0.3d, out SeamCandidate retained));
        Assert.True(rig.Seams.TryEvaluateSeam("i2f_r", "f2i_l", 0.3d, 0.8d, out SeamCandidate shorter));
        Assert.True(retained.IsAlignedWithin(Tolerance) && shorter.IsAlignedWithin(Tolerance));
        Assert.True(retained.MaxSeparation < shorter.MaxSeparation);
        SequenceSegment[] alternative =
        [
            new("i2f_r", 0d, 0.15d, 0.3d),
            new("f2i_l", 0.8d, 0.15d, 1d),
            new("idle", 0d, 0.15d, 1d + (8d / 60d))
        ];
        SequencePrediction legal = rig.Predictor.Predict(SequenceStart.Playing(_origin, "idle", 0d, 0d), alternative);
        Assert.True(legal.IsValid, legal.Rejection.ToString());
        Assert.InRange(Math.Abs(legal.EndPose.Position.Y + expectedDistance), 0f, 0.000001f);
        Assert.Equal(0f, legal.EndPose.Position.X);
        Assert.Equal(0f, legal.EndPose.Yaw);
        ArrivalPlan plan = rig.Planner.Plan(IdleStart(rig), new ArrivalTarget(new Vector2(0f, -expectedDistance), 0f));
        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        Assert.False(plan.Statistics.BudgetExhausted);
        Assert.InRange(plan.Statistics.Compositions, 1, 60000);
        AssertAlignedJoins(rig, plan);
        SequenceSegment[] observed = [.. plan.SequenceSegments];
        observed[^1] = observed[^1] with
        {
            ExitSeconds = observed[^1].ExitSeconds + 1d
        };
        SequencePrediction fullWindow = rig.Predictor.Predict(plan.SequenceStart!, observed);
        Assert.True(fullWindow.IsValid);
        Assert.Equal(plan.TickCount + 60, fullWindow.Ticks.Length);
        Assert.All(fullWindow.Ticks.TakeLast(60), tick => Assert.Equal(plan.PredictedEnd, tick.Pose));
        Assert.All(legal.Ticks.TakeLast(60), tick => Assert.Equal(legal.EndPose, tick.Pose));
        Assert.True(plan.PlanarResidual.Length() <= 0.01f,
            $"Analytic legal endpoint {expectedDistance:R} m; planner residual {plan.PlanarResidual.Length():R} m; {string.Join(';', plan.Segments)}");
    }

    private static ArrivalStart IdleStart(Rig rig) => new(_origin, rig.Planner.IdleRole, 0d);

    // ---------------------------------------------------------------------------------------------------------------
    // Nonterminal route sections (NAV-001 User Requirement 8 route follow-through).
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>A straight section from a travelling walk continues the walk with no seam, no exit and no Idle.</summary>
    [Fact]
    public void PlanSection_FromWalk_StraightStation_ContinuesWalkWithNoTerminalPieces()
    {
        var rig = Rig.Build();
        var start = new ArrivalStart(_origin, "wf", 0.4d);
        var station = new ArrivalTarget(new Vector2(0f, -1.6f), 0f);

        ArrivalPlan plan = rig.Planner.PlanSection(start, station, promptRedirect: false);

        Assert.True(plan.Status == ArrivalPlanStatus.Planned, $"{plan.Failure}: {plan.FailureDetail}");
        Assert.Equal(ArrivalMovementKind.TravelSection, plan.Kind);
        Assert.Empty(plan.Segments);
        Assert.Equal("wf", plan.SequenceStart!.CurrentRole);
        Assert.InRange(plan.TickCount, 60, 240);
        SequencePrediction prediction = rig.Predictor.Predict(plan.SequenceStart, plan.SequenceSegments);
        Assert.True(prediction.IsValid, prediction.Rejection.ToString());
        Assert.True(plan.TickCount > 0);
        Assert.NotEmpty(plan.Windows);
        Assert.True(plan.PlanarResidual.Length() <= 0.3f, $"{plan.PlanarResidual.Length()} m");
        Assert.InRange(plan.Statistics.Compositions, 1, 60000);
    }

    /// <summary>A station off to the side redirects through the turn-side arc and back into the walk, still with no
    /// exit, Idle tail or pivot: no terminal semantics at a bend.</summary>
    [Fact]
    public void PlanSection_FromWalk_BentStation_RedirectsThroughArcWithoutTerminalPieces()
    {
        var rig = Rig.Build();
        var start = new ArrivalStart(_origin, "wf", 0.4d);
        var station = new ArrivalTarget(new Vector2(1.1f, -1.9f), -0.5f);

        ArrivalPlan plan = rig.Planner.PlanSection(start, station, promptRedirect: false);

        Assert.True(plan.Status == ArrivalPlanStatus.Planned, $"{plan.Failure}: {plan.FailureDetail}");
        Assert.Equal(ArrivalMovementKind.TravelSection, plan.Kind);
        AssertAlignedJoins(rig, plan);
        Assert.Contains(plan.Segments, segment => segment.Role == "ar_l");
        Assert.All(plan.Segments, segment => Assert.Equal(SeamClipRole.Cycle, segment.Kind));
        Assert.DoesNotContain(plan.Segments, segment => segment.Role is "idle" or "f2i_l" or "f2i_r" or "pl45" or "pr45" or "sl" or "sr");
        SequencePrediction prediction = rig.Predictor.Predict(plan.SequenceStart!, plan.SequenceSegments);
        Assert.True(prediction.IsValid, prediction.Rejection.ToString());
        Assert.True(plan.PlanarResidual.Length() <= 0.3f, $"{plan.PlanarResidual.Length()} m");
    }

    /// <summary>A prompt redirect (a supersession while walking) begins its first transition at the first aligned
    /// seam into a travelling successor, never later.</summary>
    [Fact]
    public void PlanSection_PromptRedirect_BeginsAtTheFirstAlignedTravellingSeam()
    {
        var rig = Rig.Build();
        var start = new ArrivalStart(_origin, "wf", 0.4d);
        var station = new ArrivalTarget(new Vector2(1.1f, -1.9f), -0.6f);

        ArrivalPlan plan = rig.Planner.PlanSection(start, station, promptRedirect: true);

        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        Assert.NotEmpty(plan.Segments);
        Assert.True(rig.Seams.TryFindNextAligned("wf", ["ar_l"], 0.4d, Tolerance, out SeamChoice first),
            "SETUP: the fixture has no aligned wf → ar_l seam after 0.4 s.");
        Assert.Equal(first.Candidate.OutgoingTime, plan.SequenceStart!.ExitSeconds, 9);
        Assert.Equal(first.Pair.Incoming.Role, plan.Segments[0].Role);
    }

    /// <summary>A section from Idle enters through an entry clip and ends mid-walk; the Idle never returns inside
    /// the section.</summary>
    [Fact]
    public void PlanSection_FromIdle_EntersThroughEntryAndEndsMidWalk()
    {
        var rig = Rig.Build();
        var station = new ArrivalTarget(new Vector2(0f, -1.6f), 0f);

        ArrivalPlan plan = rig.Planner.PlanSection(IdleStart(rig), station, promptRedirect: true);

        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        Assert.Equal(ArrivalMovementKind.TravelSection, plan.Kind);
        AssertAlignedJoins(rig, plan);
        Assert.Equal(SeamClipRole.Entry, plan.Segments[0].Kind);
        Assert.Equal("wf", plan.Segments[^1].Role);
        Assert.Equal(SeamClipRole.Cycle, plan.Segments[^1].Kind);
        Assert.All(plan.Segments, segment => Assert.NotEqual(SeamClipRole.Idle, segment.Kind));
        Assert.All(plan.Segments, segment => Assert.NotEqual(SeamClipRole.Exit, segment.Kind));
    }

    /// <summary>
    /// CORNER-idle regression (user blocker): a route station can bear in ANY direction from a standing actor
    /// (the first leg of an obstacle-avoiding route is purely route-geometric). From Idle, a BEHIND station
    /// (world +Y is backward in this rig: forward maps to −Y) must plan a pivot-led section — one confined pivot
    /// block, then the planner-set pivot → Idle → entry hand-off, then the walk run — ending mid-walk with no
    /// exit or Idle tail. Before the pivot-led family existed this failed NoAlignedPlan ("No travelling
    /// composition ends within 0.3 m of the station"), which surfaced as the user's "route following could not
    /// continue; holding bound exhausted". Independent expectations: the endpoint model must land the exact
    /// predicted end within the declared 0.3 m window, and every join must be an aligned planner-set seam.
    /// </summary>
    [Fact]
    public void PlanSection_FromIdle_BehindStation_PivotsThenWalks()
    {
        // The synthetic rig's idle→entry slots all sit at ONE idle time, so the pivot hand-off always wraps a
        // whole 2 s idle loop (dwell 1.867 s); the production default dwell bound (1.5 s) presumes the real rig's
        // dense slot grid. The bound is widened here for the rig's degeneracy only.
        var rig = Rig.Build(ArrivalPlannerConfig.Default with
        {
            MaxIdleDwellSeconds = 2.5d
        });
        ArrivalTarget station = new(new Vector2(0f, 1.6f), Mathf.Pi);

        ArrivalPlan plan = rig.Planner.PlanSection(IdleStart(rig), station, promptRedirect: true);

        Assert.True(plan.Status == ArrivalPlanStatus.Planned, $"{plan.Failure}: {plan.FailureDetail}");
        Assert.Equal(ArrivalMovementKind.TravelSection, plan.Kind);
        AssertAlignedJoins(rig, plan);
        Assert.Equal(SeamClipRole.Confined, plan.Segments[0].Kind);
        Assert.Contains(plan.Segments, static segment => segment.Role is "pl45" or "pr45");
        // The pivot hands off through Idle into the entry (an interior hand-off, never a trailing Idle tail).
        int lastTravel = -1;
        for (int index = 0; index < plan.Segments.Length; index++)
        {
            if (plan.Segments[index].Kind is SeamClipRole.Cycle or SeamClipRole.Entry)
            {
                lastTravel = index;
            }
        }

        Assert.True(lastTravel > 0, "the section must reach a looping travel clip");
        Assert.Equal(plan.Segments.Length - 1, lastTravel);
        Assert.Equal("wf", plan.Segments[^1].Role);
        Assert.All(plan.Segments, segment => Assert.NotEqual(SeamClipRole.Exit, segment.Kind));
        SequencePrediction prediction = rig.Predictor.Predict(plan.SequenceStart!, plan.SequenceSegments);
        Assert.True(prediction.IsValid, prediction.Rejection.ToString());
        Assert.True(
            plan.PredictedEnd.Position.DistanceTo(station.Position) <= 0.3f,
            $"exact predicted end {plan.PredictedEnd.Position.X:0.###},{plan.PredictedEnd.Position.Y:0.###} misses the behind station by {plan.PredictedEnd.Position.DistanceTo(station.Position) * 1000:0.#} mm");
    }

    /// <summary>
    /// A WIDE-BEARING station (this rig's 135° diagonal: forward is −Y, so (0.8, 0.8) bears equally left and
    /// behind) takes the same pivot-led family: one confined pivot block turns first, then the travelling head
    /// runs along the new heading, ending mid-travel within the declared window.
    /// </summary>
    [Fact]
    public void PlanSection_FromIdle_WideBearingStation_PivotsThenWalks()
    {
        var rig = Rig.Build(ArrivalPlannerConfig.Default with
        {
            MaxIdleDwellSeconds = 2.5d
        });
        ArrivalTarget station = new(new Vector2(0.8f, 0.8f), 3f * Mathf.Pi / 4f);

        ArrivalPlan plan = rig.Planner.PlanSection(IdleStart(rig), station, promptRedirect: false);

        Assert.True(plan.Status == ArrivalPlanStatus.Planned, $"{plan.Failure}: {plan.FailureDetail}");
        AssertAlignedJoins(rig, plan);
        Assert.Equal(SeamClipRole.Confined, plan.Segments[0].Kind);
        Assert.Equal(SeamClipRole.Cycle, plan.Segments[^1].Kind);
        Assert.True(
            plan.PredictedEnd.Position.DistanceTo(station.Position) <= 0.3f,
            $"exact predicted end misses the wide-bearing station by {plan.PredictedEnd.Position.DistanceTo(station.Position) * 1000:0.#} mm");
    }

    /// <summary>
    /// The straight-from-Idle composition is unchanged where it already reaches: an ahead station still enters
    /// through the entry clip directly, with NO pivot in the section (the pivot-led family carries more steps
    /// and must not displace the natural straight winner).
    /// </summary>
    [Fact]
    public void PlanSection_FromIdle_StraightStation_StillEntersDirectlyWithoutPivot()
    {
        var rig = Rig.Build();
        var station = new ArrivalTarget(new Vector2(0f, -1.6f), 0f);

        ArrivalPlan plan = rig.Planner.PlanSection(IdleStart(rig), station, promptRedirect: true);

        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        Assert.Equal(SeamClipRole.Entry, plan.Segments[0].Kind);
        Assert.DoesNotContain(plan.Segments, segment => segment.Kind == SeamClipRole.Confined);
        Assert.Equal("wf", plan.Segments[^1].Role);
        Assert.All(plan.Segments, segment => Assert.NotEqual(SeamClipRole.Exit, segment.Kind));
        Assert.True(
            plan.PredictedEnd.Position.DistanceTo(station.Position) <= 0.3f,
            $"exact predicted end misses the straight station by {plan.PredictedEnd.Position.DistanceTo(station.Position) * 1000:0.#} mm");
    }

    /// <summary>
    /// CORNER-02 regression (loop boundary): a walk run whose crossfade window plus solo run crosses the walk clip's
    /// loop length must still be materialised as the FULL run. Independent arithmetic: the entry clip travels at most
    /// 0.5 m/s × 1.2 s = 0.6 m, so a station 1.5 m ahead solves a walk run of at least 0.9 s; the entry→walk
    /// crossfade contributes 8/60 s of playback from the segment's entry seek, so the walk segment's playback
    /// (posmod(exit − entry, 1 s) + cycles · 1 s) is at least 8/60 + 0.9 = 1.033 s and MUST cross the loop with
    /// ExitCycles ≥ 1. Measuring the run from the post-fade settled position instead drops exactly one whole cycle
    /// (~1 m at the walk's 1 m/s), leaving the exact predicted end that far from the station — outside the declared
    /// 0.3 m window.
    /// </summary>
    [Fact]
    public void PlanSection_WalkRunCrossingTheLoopBoundary_MaterialisesTheFullRun()
    {
        var rig = Rig.Build();
        const float walkLengthSeconds = 1f;
        double settleSeconds = (SequencePredictor.CrossfadeTicks(0.15d, 1d / 60d) - 1) / 60d;
        var station = new ArrivalTarget(new Vector2(0f, -1.5f), 0f);

        ArrivalPlan plan = rig.Planner.PlanSection(IdleStart(rig), station, promptRedirect: true);

        Assert.True(plan.Status == ArrivalPlanStatus.Planned, $"{plan.Failure}: {plan.FailureDetail}");
        Assert.Equal("wf", plan.Segments[^1].Role);
        ArrivalPlanSegment walk = plan.Segments[^1];
        double playback = walk.ExitSeconds - walk.EntrySeconds;
        if (playback < 0d)
        {
            playback += walkLengthSeconds;
        }

        playback += walk.ExitCycles * (double)walkLengthSeconds;
        // Independent lower bound: solved run >= 1.5 − 0.6 = 0.9 s, WalkRuns also yields one tick below the solve
        // (>= 0.883 s), and the entry-seek playback adds the 8/60 s crossfade window: >= 1.0167 s, crossing the loop.
        Assert.True(playback >= walkLengthSeconds,
            $"walk segment playback {playback:0.####} s from entry {walk.EntrySeconds:0.####} must cover settle + run >= {settleSeconds:0.####} + 0.883 s");
        Assert.True(walk.ExitCycles >= 1, $"a playback of {playback:0.####} s over a {walkLengthSeconds} s loop needs ExitCycles >= 1");
        Assert.True(plan.PlanarResidual.Length() <= 0.3f,
            $"exact predicted end misses the station by {plan.PlanarResidual.Length() * 1000f:0.##} mm > 300 mm (the declared endpoint window)");
    }

    /// <summary>
    /// CORNER-02 rejection case (independent arithmetic): from Idle, the walk run is capped at
    /// MaxWalkCycles (12) × 1 s at 1 m/s = 12 m, plus at most the entry's 0.5 m/s × 1.2 s = 0.6 m of head travel, so
    /// no composition reaches past 12.6 m; a station 13.5 m ahead is at least 0.9 m out of every composition's reach
    /// — beyond the 0.3 m window — and the section must fail honestly instead of adopting an out-of-bound endpoint.
    /// </summary>
    [Fact]
    public void PlanSection_StationBeyondEveryComposition_FailsWithinTheDeclaredWindow()
    {
        var rig = Rig.Build();
        var station = new ArrivalTarget(new Vector2(0f, -13.5f), 0f);

        ArrivalPlan plan = rig.Planner.PlanSection(IdleStart(rig), station, promptRedirect: true);

        Assert.Equal(ArrivalPlanStatus.Failed, plan.Status);
        Assert.Equal(ArrivalPlanFailure.NoAlignedPlan, plan.Failure);
    }

    /// <summary>A station behind a travelling walk cannot be reached by any travelling composition: the section fails
    /// honestly instead of turning around or stopping.</summary>
    [Fact]
    public void PlanSection_FromWalk_StationBehind_FailsCleanly()
    {
        var rig = Rig.Build();
        var start = new ArrivalStart(_origin, "wf", 0.4d);
        var station = new ArrivalTarget(new Vector2(0f, 1.5f), Mathf.Pi);

        ArrivalPlan plan = rig.Planner.PlanSection(start, station, promptRedirect: false);

        Assert.Equal(ArrivalPlanStatus.Failed, plan.Status);
        Assert.Equal(ArrivalPlanFailure.NoAlignedPlan, plan.Failure);
    }

    /// <summary>A travelling-only terminal plan never stops and restarts: when a travelling composition reaches the
    /// target, its first segments keep travelling (a cycle or a lane switch), never an exit-first stop prefix.</summary>
    [Fact]
    public void Plan_TravellingOnly_ContinuesTravellingWhenTheTargetIsReachable()
    {
        var rig = Rig.Build();
        var start = new ArrivalStart(_origin, "wf", 0.4d);
        var target = new ArrivalTarget(new Vector2(0.4f, -2.6f), 0.2f);

        ArrivalPlan plan = rig.Planner.Plan(start, target, travellingOnly: true);

        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        AssertAlignedJoins(rig, plan);
        Assert.Equal(SeamClipRole.Idle, plan.Segments[^1].Kind);
        Assert.NotEqual(SeamClipRole.Exit, plan.Segments[0].Kind);
        Assert.True(plan.PlanarResidual.Length() <= 0.31f, $"{plan.PlanarResidual.Length()} m");
    }

    /// <summary>
    /// CORNER-08 association regression: a travelling finalist's admission result must stay attached to that
    /// candidate, never to its rank in the sorted <c>Best</c> copy. The real travelling search for a 0.95 m target
    /// yields six finalists whose two no-face-pivot members cannot close (a 13.92° yaw residual exceeds the legal
    /// caps), so mixed eligibility exists inside one finalist array whose storage order differs from its sorted copy;
    /// the recorded defect retained both inadmissible finalists and dropped two closable ones here. The survivors must
    /// be exactly the finalists whose own production assessment closes, and the survivors' own assessments must all
    /// hold after filtering.
    /// </summary>
    [Fact]
    public void TerminalAdmission_MixedEligibility_KeepsExactlyTheClosableContinuations()
    {
        var rig = Rig.Build();
        var harness = new TerminalAdmissionHarness(rig);
        ArrivalStart start = TerminalAdmissionHarness.WalkStart;
        ArrivalTarget target = new(new Vector2(0f, -0.95f), 0.4f);
        object search = harness.TravellingSearch(start, target);
        List<object> finalists = TerminalAdmissionHarness.Best(search);
        Assert.True(finalists.Count >= 2, "SETUP: expected a populated finalist array.");
        List<object> closable = [.. finalists.Where(candidate => harness.Assesses(search, start, target, candidate))];
        Assert.True(closable.Count > 0 && closable.Count < finalists.Count,
            $"SETUP: expected mixed eligibility, got {closable.Count}/{finalists.Count} closable.");

        harness.Filter(start, target, search);

        List<object> kept = TerminalAdmissionHarness.Best(search);
        Assert.Equal(closable.Count, kept.Count);
        Assert.Equal(closable.Count, TerminalAdmissionHarness.Count(search));
        Assert.All(kept, candidate => Assert.True(harness.Assesses(search, start, target, candidate),
            $"an inadmissible finalist survived the terminal admission filter: {TerminalAdmissionHarness.Describe(candidate)}"));
        Assert.All(closable, candidate => Assert.Contains(candidate, kept));
        Assert.All(kept, candidate => Assert.Contains(candidate, closable));
        Assert.Equal([.. kept.Select(TerminalAdmissionHarness.Cost).OrderBy(cost => cost)], kept.Select(TerminalAdmissionHarness.Cost));
    }

    /// <summary>
    /// CORNER-08 association regression over both insertion orders: the same six real candidates of the mixed 0.95 m
    /// search are offered worst-first and best-first into fresh searches that share the search's own prefix list (so
    /// every recipe still materialises). Whatever the storage order, the filter must keep exactly the closable set;
    /// the recorded positional defect kept the two inadmissible worst-first offers and dropped two closable ones
    /// instead. (Full-array replacement is covered separately by the uniform-eligibility case's genuine 8-finalist
    /// searches; this mixed search evaluates 672 compositions but ends with six finalists, so its Offer calls fill
    /// at most six slots and no replacement count is claimed here.)
    /// </summary>
    [Fact]
    public void TerminalAdmission_BothInsertionOrders_KeepClosableDropInadmissible()
    {
        var rig = Rig.Build();
        var harness = new TerminalAdmissionHarness(rig);
        ArrivalStart start = TerminalAdmissionHarness.WalkStart;
        ArrivalTarget target = new(new Vector2(0f, -0.95f), 0.4f);
        object natural = harness.TravellingSearch(start, target);
        List<object> finalists = TerminalAdmissionHarness.Best(natural);
        List<object> closable = [.. finalists.Where(candidate => harness.Assesses(natural, start, target, candidate))];
        Assert.True(closable.Count > 0 && closable.Count < finalists.Count, "SETUP: expected mixed eligibility.");

        List<object>[] orders =
        [
            [.. finalists.OrderByDescending(TerminalAdmissionHarness.Cost)],
            [.. finalists.OrderBy(TerminalAdmissionHarness.Cost)],
        ];
        foreach (List<object> order in orders)
        {
            object search = TerminalAdmissionHarness.SearchSharingPrefixesOf(natural, start, target);
            foreach (object candidate in order)
            {
                TerminalAdmissionHarness.Offer(search, candidate);
            }

            Assert.Equal(finalists.Count, TerminalAdmissionHarness.Count(search));
            harness.Filter(start, target, search);
            List<object> kept = TerminalAdmissionHarness.Best(search);
            Assert.Equal(closable.Count, TerminalAdmissionHarness.Count(search));
            Assert.All(kept, candidate => Assert.True(harness.Assesses(search, start, target, candidate),
                $"inadmissible finalist kept for offer order [{string.Join(", ", order.Select(TerminalAdmissionHarness.Cost))}]: {TerminalAdmissionHarness.Describe(candidate)}"));
            Assert.All(closable, candidate => Assert.Contains(candidate, kept));
            Assert.All(kept, candidate => Assert.Contains(candidate, closable));
        }
    }

    /// <summary>
    /// CORNER-08 association regression, uniform eligibility: a search whose eight finalists (a full array after
    /// slot replacement) all close keeps every one of them, in cost order, with the full-array threshold semantics
    /// unchanged; a search none of whose finalists closes (a 0.55 m lateral target no continuation can reach within
    /// the caps) drops all of them and reopens the threshold.
    /// </summary>
    [Fact]
    public void TerminalAdmission_UniformEligibility_KeepsAllOrNoneWithTruthfulThreshold()
    {
        var rig = Rig.Build();
        var harness = new TerminalAdmissionHarness(rig);
        ArrivalStart start = TerminalAdmissionHarness.WalkStart;

        object allClosable = harness.TravellingSearch(start, new ArrivalTarget(new Vector2(0f, -1f), 0.4f));
        List<object> finalists = TerminalAdmissionHarness.Best(allClosable);
        Assert.Equal(8, finalists.Count);
        Assert.All(finalists, candidate => Assert.True(harness.Assesses(allClosable, start, new ArrivalTarget(new Vector2(0f, -1f), 0.4f), candidate)));
        double threshold = TerminalAdmissionHarness.Threshold(allClosable);
        Assert.True(double.IsFinite(threshold), "SETUP: a full finalist array has a finite entry threshold.");

        harness.Filter(start, new ArrivalTarget(new Vector2(0f, -1f), 0.4f), allClosable);

        Assert.Equal(finalists.Count, TerminalAdmissionHarness.Count(allClosable));
        Assert.Equal([.. finalists.Select(TerminalAdmissionHarness.Cost)], TerminalAdmissionHarness.Best(allClosable).Select(TerminalAdmissionHarness.Cost));
        Assert.Equal(threshold, TerminalAdmissionHarness.Threshold(allClosable));

        ArrivalTarget lateral = new(new Vector2(0.55f, -0.15f), 0f);
        object noneClosable = harness.TravellingSearch(start, lateral);
        List<object> unreachable = TerminalAdmissionHarness.Best(noneClosable);
        Assert.NotEmpty(unreachable);
        Assert.All(unreachable, candidate => Assert.False(harness.Assesses(noneClosable, start, lateral, candidate)));

        harness.Filter(start, lateral, noneClosable);

        Assert.Equal(0, TerminalAdmissionHarness.Count(noneClosable));
        Assert.Empty(TerminalAdmissionHarness.Best(noneClosable));
        Assert.True(double.IsPositiveInfinity(TerminalAdmissionHarness.Threshold(noneClosable)));
    }

    /// <summary>
    /// End-to-end admission guard through the public planning API: whatever the travelling finalist mix, a
    /// travellingOnly terminal request must return a plan whose own exact prediction closes within the arrival
    /// tolerances under the legal capped schedule (the production admission assessment applied to the returned plan).
    /// </summary>
    [Theory]
    [InlineData(0.95f, 0.4f)]
    [InlineData(1f, 0.4f)]
    [InlineData(1.3f, 0.4f)]
    public void Plan_TravellingOnly_ReturnsAContinuationWhoseCappedScheduleCloses(float distance, float yaw)
    {
        var rig = Rig.Build();
        var start = new ArrivalStart(_origin, "wf", 0.4d);
        var target = new ArrivalTarget(new Vector2(0f, -distance), yaw);

        ArrivalPlan plan = rig.Planner.Plan(start, target, travellingOnly: true);

        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        SequencePrediction prediction = rig.Predictor.Predict(plan.SequenceStart!, plan.SequenceSegments);
        Assert.True(prediction.IsValid, prediction.Rejection?.ToString());
        TerminalCorrectionOutcome outcome = TerminalCorrectionSchedule.Simulate(
            prediction, target, new SwingCorrectionAllocator(new SwingCorrectionCaps()));
        Assert.True(outcome.ClosesWithin(0.01f, Mathf.DegToRad(1f)),
            $"the selected {plan.Kind} plan does not close under the legal capped correction: {outcome.PositionErrorMetres * 1000:0.###} mm / {Mathf.RadToDeg(outcome.YawErrorRadians):0.###} deg.");
    }

    /// <summary>A non-prompt terminal replan from a travelling walk may now bend once through an arc prefix: the
    /// final approach keeps its exit, Idle tail and facing pivot.</summary>
    [Fact]
    public void Plan_FromTravellingWalk_BentTarget_MayRedirectThroughArcPrefix()
    {
        var rig = Rig.Build();
        var start = new ArrivalStart(_origin, "wf", 0.4d);
        var target = new ArrivalTarget(new Vector2(1.2f, -1.3f), -0.4f);

        ArrivalPlan plan = rig.Planner.Plan(start, target);

        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        AssertAlignedJoins(rig, plan);
        Assert.Equal(SeamClipRole.Idle, plan.Segments[^1].Kind);
        Assert.True(plan.PlanarResidual.Length() <= 0.31f, $"{plan.PlanarResidual.Length()} m");
        Assert.Contains(plan.Segments, segment => segment.Role == "ar_l");
    }

    /// <summary>A complete alternating swing cycle must remain available as a repeated pivot.</summary>
    [Fact]
    public void Plan_GeneratesOffersAndMaterialisesRepeatedFullFootworkWithinNativeReserves()
    {
        var rig = Rig.BuildFootworkContinuation();
        ArrivalPlan plan = rig.Planner.Plan(IdleStart(rig), new ArrivalTarget(Vector2.Zero, -Mathf.Pi));
        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        Assert.False(plan.Statistics.BudgetExhausted);
        Assert.InRange(plan.Statistics.Compositions, 1, 60000);
        Assert.InRange(Math.Abs(Mathf.RadToDeg(plan.YawResidual)), 0f, 1f);
        Assert.InRange(plan.TickCount, 1, 240);
        AssertAlignedJoins(rig, plan);
        Assert.True(plan.Segments.Count(segment => segment.Role == "pr45") >= 3,
            $"Expected real Offer to select repeated phase-complete units: {string.Join(';', plan.Segments)}");
        SequencePrediction prediction = rig.Predictor.Predict(plan.SequenceStart!, plan.SequenceSegments);
        Assert.True(prediction.IsValid, prediction.Rejection.ToString());
        Assert.Equal(plan.TickCount, prediction.Ticks.Length);
        Assert.InRange(Math.Abs(Mathf.RadToDeg(prediction.EndPose.Yaw + Mathf.Pi)), 0f, 1f);
    }

    /// <summary>
    /// Independent arithmetic of the repeated-pivot units on the footwork rig (S4-D2): every unit progresses in
    /// the pivot's native direction with a finite positive charge, and its incoming clock — the restart fade plus
    /// the native solo back to the seam's outgoing time — crosses BOTH feet's complete swing windows (the
    /// fixture's pr45 swings left over [0.2, 0.4) and right over [0.6, 0.8) per loop), so a one-swing micro-repeat
    /// such as the 0.4 s → 2/30 s seam is not a unit. The 24/30 s → 29/30 s unit's charge is hand-derived: its fade
    /// blends 6/60 s of active turn at weights 4/9…9/9 (4.0625°), its solo runs 0.7 s at 45°/0.8 s wrapping back to
    /// 24/30 s (39.375°), and its entry seek adds one tick — 43.4375° over exactly 51/60 s.
    /// </summary>
    [Fact]
    public void RepeatedPivotUnits_AreDirectionalCompleteAlternatingSwingCycles()
    {
        var rig = Rig.BuildFootworkContinuation();

        (double A, double B, double Yaw, double Charge)[] units =
        [
            .. rig.Planner.RepeatedPivotUnits.Select(unit => (A: unit.OutgoingSeconds, B: unit.IncomingSeconds, Yaw: unit.UnitYawRadians, Charge: unit.UnitChargeSeconds))
        ];

        Assert.NotEmpty(units);
        Assert.All(units, static unit => Assert.True(unit.Yaw < 0d, "pr45 turns right; a repeat unit must not reverse it."));
        Assert.All(units, static unit => Assert.True(double.IsFinite(unit.Charge) && unit.Charge > 0d));
        Assert.All(units, unit =>
        {
            // The advancing incoming window [B, B + charge − seek) must fully contain one swing copy of each foot.
            double span = unit.Charge - (1d / 60d);
            Assert.True(CoversSwingWindow(unit.B, span, 0.2d, 0.4d), $"left swing incomplete: {unit.A:R} → {unit.B:R}");
            Assert.True(CoversSwingWindow(unit.B, span, 0.6d, 0.8d), $"right swing incomplete: {unit.A:R} → {unit.B:R}");
        });

        // The fixture's one-swing micro-repeat (its window misses the right swing entirely) is rejected.
        Assert.DoesNotContain(units, unit => Math.Abs(unit.A - 0.4d) < 1e-9d && Math.Abs(unit.B - (2d / 30d)) < 1e-9d);

        (double A, double B, double Yaw, double Charge) single = Assert.Single(units,
            unit => Math.Abs(unit.A - (24d / 30d)) < 1e-9d && Math.Abs(unit.B - (29d / 30d)) < 1e-9d);
        (double a, double b, double yaw, double charge) = single;
        Assert.Equal(-Mathf.DegToRad(43.4375f), (float)yaw, 6);
        Assert.Equal(51d / 60d, charge, 9);
    }

    /// <summary>Whether a looping 1 s clip window covers one full [start, end) swing copy (integer loop offsets).</summary>
    private static bool CoversSwingWindow(double from, double seconds, double start, double end)
    {
        for (long cycle = (long)Math.Floor(from - end) - 1; start + cycle <= from + seconds + 1e-6d; cycle++)
        {
            if (from <= start + cycle + 1e-6d && end + cycle <= from + seconds + 1e-6d)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every generated repeated-pivot chain charges its additional restarts and extra native loops to the SAME
    /// extra-native reserve on BOTH axes — at most four native loops' worth of yaw (4 × 45°) and of ticks (4 × 1 s),
    /// each unit's entry seek included — per chain, with nothing shared or re-granted between chains. The
    /// 24/30 s → 29/30 s seam admits at most four extra restarts (its 43.4375°/51-tick unit gives
    /// min(⌊180°/43.4375°⌋, ⌊4 s·60/51⌋) = 4), and the near-target row the search ranks first — one extra restart
    /// plus one extra native loop after the last restart, entering at 0 and exiting at 1/30 s — is generated.
    /// </summary>
    [Fact]
    public void Search_GeneratedRepeatedPivotCharges_ShareTheExtraNativeReservePerChain()
    {
        var rig = Rig.BuildFootworkContinuation();
        Type searchType = typeof(ArrivalPlanner).GetNestedType("Search", BindingFlags.NonPublic | BindingFlags.Public)!;
        ConstructorInfo constructor = Assert.Single(searchType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
        object search = constructor.Invoke(
            [new ArrivalPlannerConfig(), PlanarMotion.Between(_origin, new SequencePose(Vector2.Zero, -Mathf.Pi)), true]);
        _ = typeof(ArrivalPlanner).GetMethod("SearchPivotContinuations", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig.Planner, [search]);

        var generated = (IList)searchType.GetField("_generatedPivotContinuations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(search)!;
        Assert.NotEmpty(generated);
        PropertyInfo joinA = Assert.Single(ArrivalPlannerJoinType().GetProperties(), property => property.Name == "A");
        PropertyInfo joinB = Assert.Single(ArrivalPlannerJoinType().GetProperties(), property => property.Name == "B");
        const double quarterTurn = Math.PI / 4d;
        bool witnessRow = false;
        foreach (object option in generated)
        {
            int repeats = IntField(option, "Repeats");
            int loops = IntField(option, "BeforeCycles") + IntField(option, "AfterCycles");
            double unitYaw = Math.Abs(DoubleField(option, "UnitYaw"));
            double unitSeconds = DoubleField(option, "UnitSeconds");
            Assert.True(repeats >= 2, "only repeated chains are generated by the request-time family.");
            Assert.True((((repeats - 1) * unitYaw) + (loops * quarterTurn)) <= (4d * quarterTurn) + 1e-6d,
                $"yaw reserve exceeded: {repeats} repeats, {loops} loops.");
            Assert.True((((repeats - 1) * unitSeconds) + loops) <= 4d + 1e-9d,
                $"tick reserve exceeded: {repeats} repeats, {loops} loops.");

            object restart = option.GetType().GetProperty("Restart")!.GetValue(option)!;
            if (Math.Abs((double)joinA.GetValue(restart)! - (24d / 30d)) < 1e-9d
                && Math.Abs((double)joinB.GetValue(restart)! - (29d / 30d)) < 1e-9d)
            {
                Assert.True(repeats <= 5, "the 24/30 s → 29/30 s unit admits at most four extra restarts.");
                if (repeats == 2 && IntField(option, "AfterCycles") == 1 && IntField(option, "BeforeCycles") == 0)
                {
                    object entry = option.GetType().GetProperty("Entry")!.GetValue(option)!;
                    object exit = option.GetType().GetProperty("Exit")!.GetValue(option)!;
                    witnessRow = Math.Abs((double)joinB.GetValue(entry)! - 0d) < 1e-9d
                        && Math.Abs((double)joinA.GetValue(exit)! - (1d / 30d)) < 1e-9d;
                }
            }
        }

        Assert.True(witnessRow, "the one-extra-restart, one-extra-loop chain through the 24/30 s → 29/30 s seam was not generated.");
        return;

        static Type ArrivalPlannerJoinType()
        {
            return typeof(ArrivalPlanner).GetNestedType("Join", BindingFlags.NonPublic | BindingFlags.Public)!;
        }

        static int IntField(object instance, string name)
        {
            return (int)instance.GetType().GetProperty(name)!.GetValue(instance)!;
        }

        static double DoubleField(object instance, string name)
        {
            return (double)instance.GetType().GetProperty(name)!.GetValue(instance)!;
        }
    }

    /// <summary>
    /// The materialised repeated-footwork plan is the offline-rational witness: three pr45 segments joined at the
    /// 24/30 s → 29/30 s self seam — entered from Idle at 0, a middle WRAPPED PARTIAL (entry 29/30 s, exit 24/30 s)
    /// whose ExitCycles stay zero because its wrap lives inside the partial, and a last segment exiting at 1/30 s
    /// whose ExitCycles 2 are its necessary wrap plus one explicit extra native loop (elapsed 8/60 + 28/30 + 1 s
    /// over a 2/30 s partial), never a copied count — then Idle to 8/60 s. Every entry seek is charged (playback
    /// 48 + 50 + 124 + 8 ticks plus four seeks = 234), and the fading pr45 root still turns through the final fade
    /// (3.75°), so the exact end yaw is −(42.083̄ + 43.4375 + 90.3125 + 3.75)° = −179.583̄°, derived by hand from the
    /// fixture's 45°/0.8 s turn under 9-tick fades at weights k/9 and matched by the independent predictor.
    /// </summary>
    [Fact]
    public void Plan_RepeatedFootwork_MaterialisesTheWitnessWithTruthfulCyclesSeeksAndFadingRoot()
    {
        var rig = Rig.BuildFootworkContinuation();

        ArrivalPlan plan = rig.Planner.Plan(IdleStart(rig), new ArrivalTarget(Vector2.Zero, -Mathf.Pi));

        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        Assert.Equal(4, plan.Segments.Length);
        Assert.Equal("pr45", plan.Segments[0].Role);
        Assert.Equal(0d, plan.Segments[0].EntrySeconds, 9);
        Assert.Equal(24d / 30d, plan.Segments[0].ExitSeconds, 9);
        Assert.Equal(0, plan.Segments[0].ExitCycles);
        Assert.Equal("pr45", plan.Segments[1].Role);
        Assert.Equal(29d / 30d, plan.Segments[1].EntrySeconds, 9);
        Assert.Equal(24d / 30d, plan.Segments[1].ExitSeconds, 9);
        Assert.Equal(0, plan.Segments[1].ExitCycles);
        Assert.True(plan.Segments[1].EntrySeconds > plan.Segments[1].ExitSeconds, "the middle segment wraps inside its partial.");
        Assert.Equal("pr45", plan.Segments[2].Role);
        Assert.Equal(29d / 30d, plan.Segments[2].EntrySeconds, 9);
        Assert.Equal(1d / 30d, plan.Segments[2].ExitSeconds, 9);
        Assert.Equal(2, plan.Segments[2].ExitCycles);
        Assert.Equal("idle", plan.Segments[3].Role);
        Assert.Equal(0d, plan.Segments[3].EntrySeconds, 9);
        Assert.Equal(8d / 60d, plan.Segments[3].ExitSeconds, 9);
        Assert.Equal(0, plan.Segments[3].ExitCycles);

        // Every seek charged: Σ playback + one seek per segment is the whole tick count.
        double playback = 0d;
        foreach (ArrivalPlanSegment segment in plan.Segments)
        {
            double loop = segment.Kind == SeamClipRole.Confined ? 1d : 2d;
            double partial = segment.ExitSeconds - segment.EntrySeconds;
            if (partial < 0d)
            {
                partial += loop;
            }

            playback += partial + (segment.ExitCycles * loop);
        }

        Assert.Equal(234, plan.TickCount);
        Assert.Equal(playback * 60d, plan.TickCount - plan.Segments.Length, 6);

        // The final segment's cycles are derived, not copied: its real elapsed (fade 8/60 + solo 28/30 wrapping +
        // one explicit loop = 124/60 s) over its 2/30 s partial gives exactly round((124/60 − 2/30)/1) = 2.
        double elapsed = (8d / 60d) + (28d / 30d) + 1d;
        double partialSeconds = (1d / 30d) - (29d / 30d) + 1d;
        Assert.Equal(2, (int)Math.Round(elapsed - partialSeconds));

        double analytic = AnalyticRepeatedFootworkEndYawDegrees(plan);
        Assert.Equal(-179.58333333333334d, analytic, 4);
        SequencePrediction prediction = rig.Predictor.Predict(plan.SequenceStart!, plan.SequenceSegments);
        Assert.True(prediction.IsValid, prediction.Rejection.ToString());
        Assert.Equal(plan.TickCount, prediction.Ticks.Length);
        Assert.Equal(Mathf.RadToDeg(prediction.EndPose.Yaw), (float)analytic, 3);
        Assert.Equal(0f, prediction.EndPose.Position.X, 6);
        Assert.Equal(0f, prediction.EndPose.Position.Y, 6);
    }

    /// <summary>
    /// Hand evaluation of the fixture's end yaw in degrees: pr45's unwrapped yaw is −45° per loop plus
    /// −(45°/0.8 s)·min(fraction, 0.8) inside it and idle adds none; a 9-tick .15 s fade blends the incoming (which
    /// seeks on tick 1 and advances on ticks 2…9) against the outgoing (which advances on every tick) at weight
    /// k/9, then the incoming plays its remaining playback solo.
    /// </summary>
    private static double AnalyticRepeatedFootworkEndYawDegrees(ArrivalPlan plan)
    {
        const double dt = 1d / 60d;
        double Pr45(double t)
        {
            return (-45d * Math.Floor(t)) - (56.25d * Math.Min(t - Math.Floor(t), 0.8d));
        }

        double YawOf(string role, double from, double to)
        {
            return role == "pr45" ? Pr45(to) - Pr45(from) : 0d;
        }
        string outgoingRole = "idle";
        double outgoingTime = plan.SequenceStart!.ExitSeconds;
        double yaw = 0d;
        foreach (ArrivalPlanSegment segment in plan.Segments)
        {
            for (int tick = 1; tick <= 9; tick++)
            {
                double weight = tick / 9d;
                if (tick >= 2)
                {
                    yaw += weight * YawOf(segment.Role, segment.EntrySeconds + ((tick - 2) * dt), segment.EntrySeconds + ((tick - 1) * dt));
                }

                yaw += (1d - weight) * YawOf(outgoingRole, outgoingTime + ((tick - 1) * dt), outgoingTime + (tick * dt));
            }

            double loop = segment.Kind == SeamClipRole.Confined ? 1d : 2d;
            double partial = segment.ExitSeconds - segment.EntrySeconds;
            if (partial < 0d)
            {
                partial += loop;
            }

            yaw += YawOf(segment.Role, segment.EntrySeconds + (8d * dt), segment.EntrySeconds + partial + (segment.ExitCycles * loop));
            outgoingRole = segment.Role;
            outgoingTime = segment.ExitSeconds;
        }

        return yaw;
    }

    private static void AssertAlignedJoins(Rig rig, ArrivalPlan plan)
    {
        Assert.NotNull(plan.SequenceStart);
        Assert.NotNull(plan.SequenceStart.CurrentRole);
        string previous = plan.SequenceStart.CurrentRole;
        double exit = plan.SequenceStart.ExitSeconds;
        foreach (ArrivalPlanSegment segment in plan.Segments)
        {
            Assert.True(segment.CrossfadeSeconds > 0d, $"{segment.Role} teleports.");
            Assert.True(rig.Seams.TryGetPair(previous, segment.Role, out SeamPair? pair), $"{previous} → {segment.Role} is outside the planner set.");
            Assert.Equal(pair!.CrossfadeSeconds, segment.CrossfadeSeconds, 9);
            Assert.Equal(0d, Math.Abs((exit * Rate) - Math.Round(exit * Rate)), 6);
            Assert.Equal(0d, Math.Abs((segment.EntrySeconds * Rate) - Math.Round(segment.EntrySeconds * Rate)), 6);
            Assert.True(
                rig.Seams.TryEvaluateSeam(previous, segment.Role, exit, segment.EntrySeconds, out SeamCandidate seam)
                && pair.IsUsable(seam)
                && seam.IsAlignedWithin(Tolerance),
                FormattableString.Invariant($"{previous}@{exit} → {segment.Role}@{segment.EntrySeconds} is not an aligned seam."));
            previous = segment.Role;
            exit = segment.ExitSeconds;
        }
    }

    /// <summary>The synthetic rig: its seam table, predictor and planner.</summary>
    private sealed record Rig(SeamTable Seams, SequencePredictor Predictor, ArrivalPlanner Planner)
    {
        public static Rig BuildFootworkContinuation()
        {
            // The first 0.8 s turns 45° with complete alternating swing intervals; the last 0.2 s
            // supplies no yaw. Crossing 0.2→0.4 and 0.6→0.8, unlike a faster 0.05→0.15 micro-repeat,
            // is required of EVERY repeated unit. All ball samples coincide, so seams test time/phase alone.
            (ClipTrajectory Trajectory, SeamClipRole Kind, PlantedClipIntervals Intervals)[] clips =
            [
                (Trajectory("idle", null, 2d, _ => Vector2.Zero), SeamClipRole.Idle, AllPlanted("idle", 2d)),
                (Trajectory("pr45", StandingLocomotionCapability.ConfinedPivotRight, 1d, _ => Vector2.Zero,
                    time => -Mathf.Pi / 4f * (float)Math.Min(time / 0.8d, 1d)), SeamClipRole.Confined, Manoeuvre("pr45"))
            ];
            var seams = SeamTable.Build(clips.Select(clip => SeamClip.FromTrajectory(clip.Trajectory, clip.Kind, clip.Intervals)));
            var predictor = new SequencePredictor(seams,
                clips.ToDictionary(clip => clip.Trajectory.Role, clip => clip.Trajectory), ["idle", "pr45"], Tolerance);
            return new Rig(seams, predictor, new ArrivalPlanner(seams, predictor));
        }

        public static Rig BuildSpliceDiversity()
        {
            static PlantedClipIntervals Moving(string role)
            {
                return Intervals(role, false, 1.2d,
                    [Seg(0d, 0.3d, true), Seg(0.3d, 1d, false), Seg(1d, 1.2d, true)],
                    [Seg(0d, 1.2d, true)]);
            }
            (ClipTrajectory Trajectory, SeamClipRole Kind, PlantedClipIntervals Intervals)[] clips =
            [
                (Trajectory("idle", null, 2d, _ => Vector2.Zero), SeamClipRole.Idle, AllPlanted("idle", 2d)),
                (Trajectory("i2f_r", StandingLocomotionCapability.EntryForwardRight, 1.2d, time => new Vector2(0f, 0.5f * (float)time)), SeamClipRole.Entry, Moving("i2f_r")),
                (Trajectory("f2i_l", StandingLocomotionCapability.ExitForwardLeft, 1.2d, Exit,
                    ballShift: time => new Vector3(0f, 0f, 0.01f * (float)time)), SeamClipRole.Exit, Moving("f2i_l"))
            ];
            var seams = SeamTable.Build(clips.Select(clip => SeamClip.FromTrajectory(clip.Trajectory, clip.Kind, clip.Intervals)));
            var predictor = new SequencePredictor(seams, clips.ToDictionary(clip => clip.Trajectory.Role, clip => clip.Trajectory), ["idle"], Tolerance);
            ArrivalPlannerConfig config = ArrivalPlannerConfig.Default with
            {
                WalkEntryOptions = 1,
                ExitToIdleOptions = 1,
                SeamOptionsPerPair = 0,
                MaxCompositions = 60000
            };
            return new Rig(seams, predictor, new ArrivalPlanner(seams, predictor, config));
        }

        public static Rig Build(ArrivalPlannerConfig? config = null, Vector3? exitBallOffset = null, float tolerance = Tolerance, Func<double, Vector3>? pivotBallShift = null)
        {
            Vector3 offset = exitBallOffset ?? Vector3.Zero;
            (ClipTrajectory Trajectory, SeamClipRole Kind, PlantedClipIntervals Intervals)[] clips =
            [
                (Trajectory("idle", null, 2d, _ => Vector2.Zero), SeamClipRole.Idle, AllPlanted("idle", 2d)),
                (Trajectory("i2f_l", StandingLocomotionCapability.EntryForwardLeft, 1.2d, time => new Vector2(0f, 0.5f * (float)time)), SeamClipRole.Entry, Entry("i2f_l")),
                (Trajectory("i2f_r", StandingLocomotionCapability.EntryForwardRight, 1.2d, time => new Vector2(0f, 0.5f * (float)time)), SeamClipRole.Entry, Entry("i2f_r")),
                (Trajectory("wf", StandingLocomotionCapability.CyclicForwardPrimary, 1d, time => new Vector2(0f, (float)time)), SeamClipRole.Cycle, Gait("wf", 1d, true)),
                (Arc("al_r", StandingLocomotionCapability.CyclicArcLeftRightLane, 0.6f), SeamClipRole.Cycle, Gait("al_r", 1d, true)),
                (Arc("ar_l", StandingLocomotionCapability.CyclicArcRightLeftLane, -0.6f), SeamClipRole.Cycle, Gait("ar_l", 1d, true)),
                (Trajectory("f2i_l", StandingLocomotionCapability.ExitForwardLeft, 1.2d, Exit, offset: offset), SeamClipRole.Exit, ExitIntervals("f2i_l")),
                (Trajectory("f2i_r", StandingLocomotionCapability.ExitForwardRight, 1.2d, Exit, offset: offset), SeamClipRole.Exit, ExitIntervals("f2i_r")),
                (Trajectory("pl45", StandingLocomotionCapability.ConfinedPivotLeft, 1d, _ => Vector2.Zero, time => Mathf.Pi / 4f * (float)time, ballShift: pivotBallShift), SeamClipRole.Confined, Manoeuvre("pl45")),
                (Trajectory("pr45", StandingLocomotionCapability.ConfinedPivotRight, 1d, _ => Vector2.Zero, time => -Mathf.Pi / 4f * (float)time, ballShift: pivotBallShift), SeamClipRole.Confined, Manoeuvre("pr45")),
                (Trajectory("sl", StandingLocomotionCapability.ConfinedSideStepLeft, 1d, time => new Vector2(-0.3f * (float)time, 0f)), SeamClipRole.Confined, Manoeuvre("sl")),
                (Trajectory("sr", StandingLocomotionCapability.ConfinedSideStepRight, 1d, time => new Vector2(0.3f * (float)time, 0f)), SeamClipRole.Confined, Manoeuvre("sr")),
            ];
            var seams = SeamTable.Build(clips.Select(clip => SeamClip.FromTrajectory(clip.Trajectory, clip.Kind, clip.Intervals)));
            var predictor = new SequencePredictor(
                seams,
                clips.ToDictionary(clip => clip.Trajectory.Role, clip => clip.Trajectory),
                ["idle", "wf", "al_r", "ar_l", "pl45", "pr45", "sl", "sr"],
                tolerance);
            return new Rig(seams, predictor, new ArrivalPlanner(seams, predictor, config));
        }

        /// <summary>An exit walks at 0.5 m/s until its final double support at 1 s, then stands.</summary>
        private static Vector2 Exit(double time) => new(0f, 0.5f * (float)Math.Min(time, 1d));

        /// <summary>A 1 s arc cycle at 0.8 m/s turning at a constant rate (positive left).</summary>
        private static ClipTrajectory Arc(string role, StandingLocomotionCapability capability, float rate)
            => Trajectory(
                role,
                capability,
                1d,
                time => new Vector2(0.8f / rate * (Mathf.Cos(rate * (float)time) - 1f), 0.8f / rate * Mathf.Sin(rate * (float)time)),
                time => rate * (float)time);

        private static ClipTrajectory Trajectory(
            string role,
            StandingLocomotionCapability? capability,
            double duration,
            Func<double, Vector2> planar,
            Func<double, float>? yaw = null,
            Vector3? offset = null,
            Func<double, Vector3>? ballShift = null)
        {
            int count = ClipTrajectory.ExpectedSampleCount(duration, Rate);
            double[] times = [.. Enumerable.Range(0, count).Select(index => ClipTrajectory.SampleTimeOf(index, duration, Rate))];
            Vector3 shift = offset ?? Vector3.Zero;
            return new ClipTrajectory(
                role,
                role,
                capability,
                duration,
                Rate,
                [.. times.Select(planar)],
                [.. times.Select(time => yaw?.Invoke(time) ?? 0f)],
                [.. times.Select(time => _leftBall + shift + (ballShift?.Invoke(time) ?? Vector3.Zero))],
                [.. times.Select(time => _rightBall + shift + (ballShift?.Invoke(time) ?? Vector3.Zero))],
                1f,
                new ClipTrajectoryErrorBounds(0f, 0f, 0f));
        }

        /// <summary>The shared gait phase: left swings over [0.3, 0.5), right over [0.8, 1.0) of each second.</summary>
        private static PlantedClipIntervals Gait(string identity, double duration, bool looping)
            => Intervals(identity, looping, duration, [Seg(0d, 0.3d, true), Seg(0.3d, 0.5d, false), Seg(0.5d, duration, true)], [Seg(0d, 0.8d, true), Seg(0.8d, 1d, false), Seg(1d, duration, true)]);

        /// <summary>An entry: the gait from standing, then double support to its end.</summary>
        private static PlantedClipIntervals Entry(string identity) => Gait(identity, 1.2d, false);

        /// <summary>An exit: the gait, then double support from 1 s to its end.</summary>
        private static PlantedClipIntervals ExitIntervals(string identity) => Gait(identity, 1.2d, false);

        /// <summary>A looping 1 s manoeuvre: left swings over [0.2, 0.4), right over [0.6, 0.8).</summary>
        private static PlantedClipIntervals Manoeuvre(string identity)
            => Intervals(identity, true, 1d, [Seg(0d, 0.2d, true), Seg(0.2d, 0.4d, false), Seg(0.4d, 1d, true)], [Seg(0d, 0.6d, true), Seg(0.6d, 0.8d, false), Seg(0.8d, 1d, true)]);

        private static PlantedClipIntervals AllPlanted(string identity, double duration)
            => Intervals(identity, true, duration, [Seg(0d, duration, true)], [Seg(0d, duration, true)]);

        private static PlantedClipIntervals Intervals(
            string identity,
            bool looping,
            double duration,
            ImmutableArray<PlantedIntervalSegment> left,
            ImmutableArray<PlantedIntervalSegment> right)
            => new(identity, identity, "synthetic", looping, duration, left, right);

        private static PlantedIntervalSegment Seg(double start, double end, bool planted) => new(start, end, planted);
    }
    /// <summary>
    /// Reflection harness for the private terminal-admission path: it builds a travelling-candidate SUBSET of the
    /// search <see cref="ArrivalPlanner.Plan(ArrivalStart, ArrivalTarget, bool)"/> builds for a mid-clip
    /// travellingOnly request (same <c>StartPrefixes</c> enumeration and the same per-prefix
    /// <c>Origin</c>/<c>Kind</c>/<c>Consider</c> calls, keeping only continuing prefixes and a default recipe the
    /// caller's fields then populate), offers the real candidates into fresh searches sharing that search's own
    /// prefix list in a chosen order, and drives the real <c>RetainClosableContinuations</c> filter. No feasibility
    /// rule is duplicated: each candidate's admission is the production materialisation plus capped-schedule
    /// assessment the filter itself applies to that candidate. This is focused filter coverage, not full
    /// public-search equivalence.
    /// </summary>
    private sealed class TerminalAdmissionHarness(Rig rig)
    {
        private const double WalkSeconds = 0.4d;

        private static readonly BindingFlags _flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static readonly Type _searchType = typeof(ArrivalPlanner).GetNestedType("Search", BindingFlags.NonPublic)!;

        private static readonly Type _candidateType = typeof(ArrivalPlanner).GetNestedType("Candidate", BindingFlags.NonPublic)!;

        private static readonly Type _recipeType = typeof(ArrivalPlanner).GetNestedType("Recipe", BindingFlags.NonPublic)!;

        public static ArrivalStart WalkStart => new(_origin, "wf", WalkSeconds);

        public object TravellingSearch(ArrivalStart start, ArrivalTarget target)
        {
            object search = SearchFor(start, target);
            object?[] arguments = [rig.Seams.Clips[start.Role], start.PositionSeconds, start.PositionSeconds, start.Prompt, search, null];
            var prefixes = (IList)typeof(ArrivalPlanner).GetMethod("StartPrefixes", _flags)!.Invoke(rig.Planner, arguments)!;
            bool travelling = (bool)arguments[5]!;
            _searchType.GetProperty("Prefixes")!.SetValue(search, prefixes);
            for (int index = 0; travelling && index < prefixes.Count; index++)
            {
                object prefix = prefixes[index]!;
                if (!(bool)prefix.GetType().GetProperty("Continues")!.GetValue(prefix)!)
                {
                    continue;
                }

                _ = _searchType.GetMethod("Origin")!.Invoke(search, [index, PlanarMotion.Identity]);
                _searchType.GetProperty("Kind")!.SetValue(search, ArrivalMovementKind.ContinueTravel);
                object recipe = Activator.CreateInstance(_recipeType)!;
                _recipeType.GetProperty("Kind")!.SetValue(recipe, ArrivalMovementKind.ContinueTravel);
                _ = typeof(ArrivalPlanner).GetMethod("Consider", _flags)!.Invoke(
                    rig.Planner, [search, prefix.GetType().GetProperty("Motion")!.GetValue(prefix)!, recipe, 0, 0f, 0d, true, null, Type.Missing, Type.Missing]);
            }

            return search;
        }

        public static object SearchSharingPrefixesOf(object source, ArrivalStart start, ArrivalTarget target)
        {
            object search = SearchFor(start, target);
            _searchType.GetProperty("Prefixes")!.SetValue(search, _searchType.GetProperty("Prefixes")!.GetValue(source));
            return search;
        }

        public static void Offer(object search, object candidate) => _searchType.GetMethod("Offer")!.Invoke(search, [candidate]);

        public void Filter(ArrivalStart start, ArrivalTarget target, object search) =>
            typeof(ArrivalPlanner).GetMethod("RetainClosableContinuations", _flags)!.Invoke(rig.Planner, [start, target, WalkSeconds, search, null]);

        public static List<object> Best(object search) => [.. (IEnumerable)_searchType.GetProperty("Best")!.GetValue(search)!];

        public static int Count(object search) => (int)_searchType.GetProperty("Count")!.GetValue(search)!;

        public static double Threshold(object search) => (double)_searchType.GetProperty("Threshold")!.GetValue(search)!;

        public static double Cost(object candidate) => (double)_candidateType.GetProperty("Cost")!.GetValue(candidate)!;

        public static string Describe(object candidate)
        {
            object recipe = _candidateType.GetProperty("Recipe")!.GetValue(candidate)!;
            return FormattableString.Invariant(
                $"cost={Cost(candidate):R} prefix={_recipeType.GetProperty("Prefix")!.GetValue(recipe)} face={_recipeType.GetProperty("Face")!.GetValue(recipe)}");
        }

        public bool Assesses(object search, ArrivalStart start, ArrivalTarget target, object candidate)
        {
            object recipe = _candidateType.GetProperty("Recipe")!.GetValue(candidate)!;
            MethodInfo materialise = typeof(ArrivalPlanner).GetMethod("TryMaterialise", _flags)!;
            object?[] arguments = [start, WalkSeconds, search, recipe, null, null];
            // No carried physical-phase budget: the harness's searches model cold-start admission, whose first
            // covering window establishes the allowance basis exactly as the runtime does without prior history.
            return (bool)materialise.Invoke(rig.Planner, arguments)! && (bool)typeof(ArrivalPlanner).GetMethod("ClosesUnderCappedCorrection", _flags)!.Invoke(rig.Planner, [arguments[4]!, arguments[5]!, target, null])!;
        }

        private static object SearchFor(ArrivalStart start, ArrivalTarget target) =>
            Activator.CreateInstance(_searchType, new ArrivalPlannerConfig(), PlanarMotion.Between(start.Pose, new SequencePose(target.Position, target.Yaw)), false)!;
    }
}
