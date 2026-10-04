using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AlleyCat.Control.Locomotion;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Arrival;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation.Arrival;

/// <summary>
/// The bind-time NAV-001 seam table and seam report, built for the bare reference female and male models from the
/// real female locomotion and idle packages. Every clip's ball table, Idle included (as an uncatalogued clip), comes
/// from the production <see cref="ClipTrajectoryTableBuilder"/> path. The table is checked against independent engine
/// evaluation: for every pair's best seam and a spread of other phase-agreeing seams, both clips are seek-evaluated on
/// the rig at 120 Hz through the whole blend window as the runtime plays it (the outgoing clip one entry lag ahead),
/// and the planted intervals are read at the same ticks. The written seam report is the measured seam-separation
/// evidence for the user's tolerance agreement.
/// </summary>
[Headless]
public sealed class SeamTableIntegrationTests
{
    private const string FemaleModelPath = "res://assets/characters/reference/female/reference_female.blend";
    private const string MaleModelPath = "res://assets/characters/reference/male/reference_male.blend";
    private const string EvidenceDirectory = "res://temp/nav001-p4-u5b-i/seam-table";
    private const string LocomotionLibraryName = "locomotion";
    private const string IdleLibraryName = "idle";
    private const double OracleRateHz = 120d;
    private const int OracleSeamsPerPair = 8;
    private const double PhaseProbeOffsetSeconds = 1e-5d;
    private const float SadToleranceMarginMetres = 0.005f;
    private const string SadToleranceConfigPath = "res://assets/characters/templates/navigation/seam_tolerance_sad.tres";

    private static readonly NodePath _rootMotionTrack = new("%GeneralSkeleton:Root");

    /// <summary>Candidate seam tolerances swept for the user, in metres.</summary>
    private static readonly float[] _candidateTolerances = [0.02f, 0.03f, 0.05f, 0.075f, 0.1f, 0.125f, 0.15f];

    /// <inheritdoc/>
    [Fact]
    public async Task FemaleRig_SeamTableMatchesEngineEvaluationAndReportIsWritten()
        => await VerifyRigAsync("female", FemaleModelPath);

    /// <inheritdoc/>
    [Fact]
    public async Task MaleRig_SeamTableMatchesEngineEvaluationAndReportIsWritten()
        => await VerifyRigAsync("male", MaleModelPath);

    private static async Task VerifyRigAsync(string rig, string modelPath)
    {
        SceneTree sceneTree = GetSceneTree();
        StandingLocomotionCatalogue catalogue = ProductionContentBindings.ParseReferenceFemaleLocomotionCatalogue();
        AnimationLibrary library = ResourceLoader.Load<AnimationLibrary>(catalogue.AnimationLibraryPath)
            ?? throw new InvalidOperationException($"SETUP: package library '{catalogue.AnimationLibraryPath}' did not load.");
        string intervalsPath = $"{catalogue.AnimationLibraryPath[..(catalogue.AnimationLibraryPath.LastIndexOf('/') + 1)]}{PerFootPlantedIntervals.FileName}";
        var intervals = PerFootPlantedIntervals.Load(intervalsPath);
        intervals.ValidateAgainst(catalogue);

        string idleCataloguePath = ProductionContentBindings.ResolveReferenceFemaleIdleCataloguePath();
        var idleIntervals = PerFootPlantedIntervals.LoadValidatedIdleCatalogue(idleCataloguePath);
        IdleClip idle = ReadIdleClip(idleCataloguePath);
        AnimationLibrary idleLibrary = ResourceLoader.Load<AnimationLibrary>(idle.LibraryPath)
            ?? throw new InvalidOperationException($"SETUP: idle library '{idle.LibraryPath}' did not load.");

        Node3D model = Assert.IsType<Node3D>(LoadPackedScene(modelPath).Instantiate(), exactMatch: false);
        try
        {
            sceneTree.Root.AddChild(model);
            Skeleton3D skeleton = Assert.IsType<Skeleton3D>(model.FindChild("GeneralSkeleton", true, false));
            // The package tracks address the skeleton by unique name from the player root, as in the NPC templates.
            skeleton.Owner = model;
            skeleton.UniqueNameInOwner = true;
            var oracle = new PoseOracle(model, skeleton, library, idleLibrary);
            await WaitForFramesAsync(sceneTree, 1);

            UncataloguedClip idleSource = new(idle.Identity, idle.Role, idle.DurationSeconds, idleLibrary.GetAnimation(idle.Identity));
            ClipTrajectoryTable trajectories = ClipTrajectoryTableBuilder.Build(catalogue, library, skeleton, _rootMotionTrack, [idleSource]);
            List<SeamClip> clips = [];
            foreach (StandingLocomotionClip clip in catalogue.Clips.Values.OrderBy(value => value.Role, StringComparer.Ordinal))
            {
                clips.Add(SeamClip.FromTrajectory(clip, trajectories[clip.Role], intervals[clip.Identity]));
            }

            var idleClip = SeamClip.FromTrajectory(trajectories[idle.Role], SeamClipRole.Idle, idleIntervals[idle.Identity]);
            clips.Add(idleClip);

            var stopwatch = Stopwatch.StartNew();
            var table = SeamTable.Build(clips);
            stopwatch.Stop();
            SeamToleranceConfig config = ResourceLoader.Load<SeamToleranceConfig>(SadToleranceConfigPath)
                ?? throw new InvalidOperationException($"SETUP: seam tolerance config '{SadToleranceConfigPath}' did not load.");
            var tolerance = config.ToSeamTolerance(catalogue.PackageVariant);
            float chain = oracle.LegChainLength();
            var reachLog = new StringBuilder(FormattableString.Invariant($"rig={rig} chain_m={chain:0.####}\n"));
            _ = reachLog.AppendLine("outgoing,incoming,a,b,foot,peak_usage,baseline_usage");
            List<SeamReachMeasurement> reach = [];
            foreach (SeamPair pair in table.Pairs)
            {
                if (pair.Best is not { } best)
                {
                    continue;
                }

                foreach (StandingLocomotionAnatomicalSide foot in Enum.GetValues<StandingLocomotionAnatomicalSide>())
                {
                    if (MeasureReach(oracle, trajectories, pair, best, foot, chain) is { } measurement)
                    {
                        reach.Add(measurement);
                        _ = reachLog.AppendLine(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{pair.Outgoing.Role},{pair.Incoming.Role},{best.OutgoingTime:0.####},{best.IncomingTime:0.####},{foot},{measurement.PeakUsage:0.####},{measurement.BaselineUsage:0.####}"));
                    }
                }
            }

            var report = SeamReport.Create(rig, table, tolerance, _candidateTolerances, reach);

            List<string> failures = [];
            var oracleLog = new StringBuilder(FormattableString.Invariant(
                $"rig={rig} motion_scale={skeleton.MotionScale} build_ms={stopwatch.Elapsed.TotalMilliseconds:0.#} pairs={table.Pairs.Length} idle_ball_range_mm={IdleBallRange(idleClip) * 1000f:0.###}\n"));
            _ = oracleLog.AppendLine("kind,outgoing,incoming,a,b,table_left_mm,table_right_mm,engine_left_mm,engine_right_mm,phase_ticks");

            foreach (SeamKind kind in Enum.GetValues<SeamKind>())
            {
                if (!table.Pairs.Any(pair => pair.Kind == kind))
                {
                    failures.Add($"no {kind} pair was derived from the catalogue roles");
                }
            }

            foreach (SeamPair pair in table.Pairs)
            {
                if (pair.EvaluatedCount == 0)
                {
                    failures.Add($"{pair.Outgoing.Role}->{pair.Incoming.Role}: no window fits both clips");
                }

                foreach (SeamCandidate candidate in OracleSample(pair))
                {
                    VerifyAgainstEngine(oracle, pair, candidate, oracleLog, failures);
                }
            }

            string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(EvidenceDirectory));
            File.WriteAllText(Path.Combine(directory, $"seam_report_{rig}.txt"), report.ToText());
            File.WriteAllText(Path.Combine(directory, $"seam_report_{rig}.json"), report.ToJson());
            File.WriteAllText(Path.Combine(directory, $"engine_oracle_{rig}.csv"), oracleLog.ToString());
            File.WriteAllText(Path.Combine(directory, $"seam_candidates_{rig}.csv"), CandidateDump(table));
            File.WriteAllText(Path.Combine(directory, $"reach_{rig}.csv"), reachLog.ToString());
            File.WriteAllText(Path.Combine(directory, $"planner_set_transitions_{rig}.json"), TransitionManifest(table, catalogue, idle.Role));

            // Every ANIM-002 clip is either in the planner set or excluded with a reason; lateral cycles have no
            // entries or exits of their own and are excluded from planning, not unavailable.
            string[] lateral =
            [
                .. catalogue.Clips.Values
                    .Where(clip => clip.Capability is StandingLocomotionCapability.CyclicLateralLeftLeftLane
                        or StandingLocomotionCapability.CyclicLateralLeftRightLane
                        or StandingLocomotionCapability.CyclicLateralRightLeftLane
                        or StandingLocomotionCapability.CyclicLateralRightRightLane)
                    .Select(clip => clip.Role)
                    .Order(StringComparer.Ordinal),
            ];
            Assert.Equal(4, lateral.Length);
            Assert.Equal(
                clips.Select(clip => clip.Role).Order(StringComparer.Ordinal),
                report.PlannerSetClips.Concat(report.ExcludedClips.Select(clip => clip.Role)).Order(StringComparer.Ordinal));
            Assert.Empty(report.PlannerSetClips.Intersect(report.ExcludedClips.Select(clip => clip.Role)));
            Assert.Equal(lateral, report.ExcludedClips.Select(clip => clip.Role));
            Assert.All(report.ExcludedClips, clip => Assert.Equal(SeamExclusionReason.LateralCycle, clip.Reason));
            Assert.Contains(report.ExcludedPairs, pair => pair.Reason == SeamExclusionReason.TurningSideLaneArcSwitch);
            Assert.All(report.ExcludedPairs, pair => Assert.False(table.IsInPlannerSet(pair.Pair.OutgoingRole, pair.Pair.IncomingRole)));
            Assert.All(table.Pairs, pair => Assert.True(table.IsInPlannerSet(pair.Outgoing.Role, pair.Incoming.Role)));
            // Cyclic availability covers the planner-set cycles and the looping confined manoeuvres (all four side-steps
            // and pivots loop in the 'sad' package), whose exits are their seams into Idle.
            string[] manoeuvres =
            [
                .. catalogue.Clips.Values
                    .Where(clip => clip.BoundaryKind == StandingLocomotionBoundaryKind.ConfinedManoeuvre)
                    .Select(clip => clip.Role),
            ];
            Assert.Equal(4, manoeuvres.Length);
            Assert.All(manoeuvres, role => Assert.True(table.Clips[role].WrapsPlayback, $"{role} does not loop"));
            string[] plannedCycles =
            [
                .. clips.Where(clip => (clip.Kind == SeamClipRole.Cycle && !lateral.Contains(clip.Role)) || manoeuvres.Contains(clip.Role))
                    .Select(clip => clip.Role)
                    .Order(StringComparer.Ordinal),
            ];
            Assert.Equal(plannedCycles, report.Cycles.Select(cycle => cycle.CycleRole));
            Assert.All(
                report.Cycles.Where(cycle => manoeuvres.Contains(cycle.CycleRole)),
                cycle => Assert.True(cycle.Kind == SeamClipRole.Confined && cycle.Exits.All(exit => exit.ExitRole == idleClip.Role)));
            Assert.All(lateral, role => Assert.False(table.IsPlannable(role, tolerance.ToleranceMetres)));
            Assert.All(plannedCycles, role => Assert.Equal(
                !report.UnavailableCycles.Contains(role),
                table.IsPlannable(role, tolerance.ToleranceMetres)));
            // Slice heads reach the entry's first lift-off, so no slice seam is the standing start.
            Assert.All(
                table.Pairs.Where(pair => pair.Kind == SeamKind.SliceJoin),
                pair => Assert.All(pair.Candidates, seam => Assert.True(seam.OutgoingTime >= SeamPairPolicy.FirstLiftOffSeconds(pair.Outgoing) - 1e-6)));
            Assert.Equal(SeamToleranceAgreementStatus.Agreed, report.Tolerance.Status);
            // The agreed 'sad' tolerance admits every relevant pair, so every cyclic clip and looping manoeuvre has an
            // aligned exit.
            Assert.Empty(report.UnalignedPairs);
            // With the stated 5 mm margin over this rig's worst best seam.
            Assert.All(report.Pairs, pair => Assert.True(
                pair.Minimum <= tolerance.ToleranceMetres - SadToleranceMarginMetres + 1e-6f,
                FormattableString.Invariant($"{pair.OutgoingRole}->{pair.IncomingRole} best {pair.Minimum * 1000f:0.0} mm leaves less than the 5 mm margin")));
            Assert.Empty(report.UnavailableCycles);
            Assert.True(failures.Count == 0, string.Join('\n', failures));
        }
        finally
        {
            model.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    /// <summary>The pair's best seam plus seams spread evenly across its phase-agreeing candidates.</summary>
    private static IEnumerable<SeamCandidate> OracleSample(SeamPair pair)
    {
        if (pair.Candidates.IsEmpty)
        {
            yield break;
        }

        yield return pair.Best!.Value;
        int step = Math.Max(1, pair.Candidates.Length / OracleSeamsPerPair);
        for (int index = step / 2; index < pair.Candidates.Length; index += step)
        {
            yield return pair.Candidates[index];
        }
    }

    /// <summary>
    /// Seek-evaluates both clips at 120 Hz through the blend window as the runtime plays seam <c>(a, b)</c> (outgoing
    /// from <c>a + λ</c>, incoming from <c>b</c>, over <c>d − λ</c>) and requires (i) the same planted/swing state per
    /// foot in both clips at every tick before the window end, under the package intervals, and (ii) the engine's
    /// per-foot maximum separation within twice the declared ball bound of the table's value (each clip's table
    /// differs from the engine by at most its bound, and the table's peak offsets lie on the 120 Hz grid: 30 Hz keys
    /// offset by the 1/60 s lag).
    /// </summary>
    private static void VerifyAgainstEngine(
        PoseOracle oracle,
        SeamPair pair,
        SeamCandidate candidate,
        StringBuilder log,
        List<string> failures)
    {
        int ticks = (int)Math.Round(pair.WindowSeconds * OracleRateHz);
        double blendStart = candidate.OutgoingTime + pair.EntryLagSeconds;
        float left = 0f;
        float right = 0f;
        int phaseTicks = 0;
        for (int tick = 0; tick <= ticks; tick++)
        {
            double tau = tick / OracleRateHz;
            double outgoingTime = Wrap(pair.Outgoing, blendStart + tau);
            double incomingTime = Wrap(pair.Incoming, candidate.IncomingTime + tau);
            (Vector3 outLeft, Vector3 outRight) = oracle.Balls(pair.Outgoing, outgoingTime);
            (Vector3 inLeft, Vector3 inRight) = oracle.Balls(pair.Incoming, incomingTime);
            left = Math.Max(left, outLeft.DistanceTo(inLeft));
            right = Math.Max(right, outRight.DistanceTo(inRight));
            if (tick == ticks)
            {
                continue;
            }

            phaseTicks++;
            // Planted state just after the tick, so boundaries that coincide in both clips do not tie on float
            // rounding at the tick instant itself.
            double outgoingState = Wrap(pair.Outgoing, blendStart + tau + PhaseProbeOffsetSeconds);
            double incomingState = Wrap(pair.Incoming, candidate.IncomingTime + tau + PhaseProbeOffsetSeconds);
            foreach (StandingLocomotionAnatomicalSide foot in Enum.GetValues<StandingLocomotionAnatomicalSide>())
            {
                if (pair.Outgoing.Intervals.IsPlanted(foot, outgoingState) != pair.Incoming.Intervals.IsPlanted(foot, incomingState))
                {
                    failures.Add(FormattableString.Invariant(
                        $"{pair.Outgoing.Role}@{candidate.OutgoingTime:0.###}->{pair.Incoming.Role}@{candidate.IncomingTime:0.###}: {foot} phase differs at τ={tau:0.####}"));
                }
            }
        }

        float bound = pair.Outgoing.BallErrorBound + pair.Incoming.BallErrorBound;
        _ = log.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{pair.Kind},{pair.Outgoing.Role},{pair.Incoming.Role},{candidate.OutgoingTime:0.####},{candidate.IncomingTime:0.####},{candidate.LeftSeparation * 1000f:0.###},{candidate.RightSeparation * 1000f:0.###},{left * 1000f:0.###},{right * 1000f:0.###},{phaseTicks}"));
        if (Math.Abs(left - candidate.LeftSeparation) > bound || Math.Abs(right - candidate.RightSeparation) > bound)
        {
            failures.Add(FormattableString.Invariant(
                $"{pair.Outgoing.Role}@{candidate.OutgoingTime:0.###}->{pair.Incoming.Role}@{candidate.IncomingTime:0.###}: engine separation {left * 1000f:0.#}/{right * 1000f:0.#} mm vs table {candidate.LeftSeparation * 1000f:0.#}/{candidate.RightSeparation * 1000f:0.#} mm"));
        }
    }

    /// <summary>
    /// Every planner-set pair as the NPC tree transition that must author it (NAV-001 Technical Requirement 16): its
    /// states and its seam-table crossfade duration. The NPC tree asset operation applies this manifest.
    /// </summary>
    private static string TransitionManifest(SeamTable table, StandingLocomotionCatalogue catalogue, string idleRole)
    {
        Dictionary<string, string> stateOf = new(StringComparer.Ordinal)
        {
            [idleRole] = NPCAnimationProgramme.IdleState.ToString()
        };
        foreach ((StringName state, StandingLocomotionCapability capability) in NPCAnimationProgramme.StateCapabilities)
        {
            stateOf[catalogue[capability].Role] = state.ToString();
        }

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("entry_lag_seconds", table.EntryLagSeconds);
            json.WriteStartArray("transitions");
            foreach (SeamPair pair in table.Pairs)
            {
                json.WriteStartObject();
                json.WriteString("kind", pair.Kind.ToString());
                json.WriteString("outgoing", pair.Outgoing.Role);
                json.WriteString("incoming", pair.Incoming.Role);
                json.WriteString("from", stateOf[pair.Outgoing.Role]);
                json.WriteString("to", stateOf[pair.Incoming.Role]);
                json.WriteNumber("crossfade_seconds", pair.CrossfadeSeconds);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Every phase-agreeing seam of every pair, as the full measured data behind the report.</summary>
    private static string CandidateDump(SeamTable table)
    {
        var dump = new StringBuilder("kind,outgoing,incoming,a,b,left_mm,right_mm,max_mm,support_throughout\n");
        foreach (SeamPair pair in table.Pairs)
        {
            foreach (SeamCandidate candidate in pair.Candidates)
            {
                _ = dump.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{pair.Kind},{pair.Outgoing.Role},{pair.Incoming.Role},{candidate.OutgoingTime:0.####},{candidate.IncomingTime:0.####},{candidate.LeftSeparation * 1000f:0.###},{candidate.RightSeparation * 1000f:0.###},{candidate.MaxSeparation * 1000f:0.###},{candidate.HasSupportThroughout}"));
            }
        }

        return dump.ToString();
    }

    /// <summary>Largest distance of any Idle ball sample from the first one, per foot (a stillness diagnostic).</summary>
    private static float IdleBallRange(SeamClip idle)
    {
        float range = 0f;
        foreach (StandingLocomotionAnatomicalSide foot in Enum.GetValues<StandingLocomotionAnatomicalSide>())
        {
            Vector3 first = idle.Ball(foot, 0d);
            for (int index = 1; index < idle.SampleCount; index++)
            {
                range = Math.Max(range, idle.Ball(foot, idle.GridTime(index), true).DistanceTo(first));
            }
        }

        return range;
    }

    /// <summary>
    /// Peak planted-leg usage (hip-to-ankle distance over the leg chain) from the crossfade end until the incoming clip
    /// lifts <paramref name="foot"/>, with the ball pinned where the outgoing clip had it at the crossfade end, and the
    /// baseline with the pin where the incoming clip has it. The pin is fixed in the world: the incoming root moves by
    /// its own root motion. The foot keeps its animated orientation about the ball. <see langword="null"/> when the foot
    /// is not planted at the crossfade end.
    /// </summary>
    private static SeamReachMeasurement? MeasureReach(
        PoseOracle oracle,
        ClipTrajectoryTable trajectories,
        SeamPair pair,
        SeamCandidate seam,
        StandingLocomotionAnatomicalSide foot,
        float chain)
    {
        SeamClip incoming = pair.Incoming;
        double end = seam.IncomingTime + pair.WindowSeconds;
        if (!incoming.IsPlanted(foot, end - PhaseProbeOffsetSeconds) || !incoming.IsPlanted(foot, end))
        {
            return null;
        }

        Vector3 seamPin = oracle.Leg(pair.Outgoing, Wrap(pair.Outgoing, seam.OutgoingTime + pair.CrossfadeSeconds), foot).Ball;
        Vector3 ownPin = oracle.Leg(incoming, Wrap(incoming, end), foot).Ball;
        float peak = 0f;
        float baseline = 0f;
        for (double tau = 0d; tau < incoming.DurationSeconds && incoming.IsPlanted(foot, end + tau); tau += 1d / OracleRateHz)
        {
            (Vector2 moved, float turned) = RootMotion(trajectories, incoming, end, tau);
            (Vector3 hip, Vector3 ankle, Vector3 ball) = oracle.Leg(incoming, Wrap(incoming, end + tau), foot);
            peak = Math.Max(peak, Usage(seamPin));
            baseline = Math.Max(baseline, Usage(ownPin));

            float Usage(Vector3 pin)
            {
                Vector2 relative = ClipTrajectory.RotateSemantic(new Vector2(pin.X, -pin.Z) - moved, -turned);
                var required = new Vector3(relative.X, pin.Y, -relative.Y);
                return (required + (ankle - ball) - hip).Length() / chain;
            }
        }

        return new SeamReachMeasurement(pair.Outgoing.Role, incoming.Role, foot, peak, baseline);
    }

    /// <summary>
    /// Incoming root displacement (semantic, in the root frame at <paramref name="start"/>) and yaw change over
    /// <paramref name="tau"/> of playback; wrapping clips continue across one loop.
    /// </summary>
    private static (Vector2 Planar, float Yaw) RootMotion(ClipTrajectoryTable trajectories, SeamClip clip, double start, double tau)
    {
        ClipTrajectory trajectory = trajectories[clip.Role];
        double from = clip.WrapsPlayback ? clip.Normalise(start) : start;
        double to = from + tau;
        if (!clip.WrapsPlayback || to <= clip.DurationSeconds)
        {
            ClipTrajectorySlice slice = trajectory.Slice(from, to);
            return (slice.Planar, slice.Yaw);
        }

        ClipTrajectorySlice head = trajectory.Slice(from, clip.DurationSeconds);
        ClipTrajectorySlice tail = trajectory.Slice(0d, to - clip.DurationSeconds);
        return (head.Planar + ClipTrajectory.RotateSemantic(tail.Planar, head.Yaw), head.Yaw + tail.Yaw);
    }

    /// <summary>Wrapping clips continue from their start; others are inside their window by construction.</summary>
    private static double Wrap(SeamClip clip, double time) => clip.WrapsPlayback ? clip.Normalise(time) : time;

    private static IdleClip ReadIdleClip(string cataloguePath)
    {
        using var document = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(cataloguePath));
        JsonElement root = document.RootElement;
        JsonElement clip = Assert.Single(root.GetProperty("clips").EnumerateArray());
        string libraryPath = $"{cataloguePath[..(cataloguePath.LastIndexOf('/') + 1)]}{root.GetProperty("animation_library").GetString()}";
        return new IdleClip(
            clip.GetProperty("identity").GetString()!,
            clip.GetProperty("role").GetString()!,
            clip.GetProperty("duration_seconds").GetDouble(),
            libraryPath);
    }

    private sealed record IdleClip(string Identity, string Role, double DurationSeconds, string LibraryPath);

    /// <summary>
    /// Seek-evaluated root-relative ball positions on the bare rig, from non-looping copies of the package clips so a
    /// seek to the clip end evaluates the final key.
    /// </summary>
    private sealed class PoseOracle
    {
        private readonly AnimationPlayer _player;
        private readonly Skeleton3D _skeleton;
        private readonly int _root;
        private readonly int _leftBall;
        private readonly int _rightBall;

        public PoseOracle(Node3D model, Skeleton3D skeleton, AnimationLibrary locomotion, AnimationLibrary idle)
        {
            _skeleton = skeleton;
            _root = skeleton.FindBone(_rootMotionTrack.GetSubName(0));
            _leftBall = skeleton.FindBone(ClipTrajectoryTableBuilder.LeftBallBone);
            _rightBall = skeleton.FindBone(ClipTrajectoryTableBuilder.RightBallBone);
            Assert.True(_root >= 0 && _leftBall >= 0 && _rightBall >= 0, "SETUP: the rig lacks the root or ball bones.");
            _player = new AnimationPlayer { Name = "SeamOraclePlayer" };
            model.AddChild(_player);
            _player.RootNode = _player.GetPathTo(model);
            _player.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
            _ = _player.AddAnimationLibrary(LocomotionLibraryName, NonLooping(locomotion));
            _ = _player.AddAnimationLibrary(IdleLibraryName, NonLooping(idle));
        }

        public (Vector3 Left, Vector3 Right) Balls(SeamClip clip, double time)
            => Evaluate(clip.Kind == SeamClipRole.Idle ? $"{IdleLibraryName}/{clip.Identity}" : $"{LocomotionLibraryName}/{clip.Identity}", time);

        /// <summary>Rest length of the thigh plus the shin: the largest hip-to-ankle distance.</summary>
        public float LegChainLength()
            => _skeleton.GetBoneRest(_skeleton.FindBone("LeftLowerLeg")).Origin.Length()
                + _skeleton.GetBoneRest(_skeleton.FindBone("LeftFoot")).Origin.Length();

        /// <summary>Root-relative hip, ankle and ball joints of one leg, in actor-local axes.</summary>
        public (Vector3 Hip, Vector3 Ankle, Vector3 Ball) Leg(SeamClip clip, double time, StandingLocomotionAnatomicalSide foot)
        {
            _ = Balls(clip, time);
            string side = foot == StandingLocomotionAnatomicalSide.Left ? "Left" : "Right";
            Transform3D inverseRoot = _skeleton.GetBoneGlobalPose(_root).AffineInverse();
            Vector3 Joint(string bone)
            {
                return SkeletonToActorLocal(inverseRoot * _skeleton.GetBoneGlobalPose(_skeleton.FindBone(bone)).Origin);
            }

            return (Joint($"{side}UpperLeg"), Joint($"{side}Foot"), Joint($"{side}Toes"));
        }

        private (Vector3 Left, Vector3 Right) Evaluate(string animation, double time)
        {
            if (_player.CurrentAnimation != animation)
            {
                _player.Play(animation);
            }

            _player.Seek(time, true);
            Transform3D inverseRoot = _skeleton.GetBoneGlobalPose(_root).AffineInverse();
            return (
                SkeletonToActorLocal(inverseRoot * _skeleton.GetBoneGlobalPose(_leftBall).Origin),
                SkeletonToActorLocal(inverseRoot * _skeleton.GetBoneGlobalPose(_rightBall).Origin));
        }

        private static AnimationLibrary NonLooping(AnimationLibrary source)
        {
            var copy = new AnimationLibrary();
            foreach (StringName name in source.GetAnimationList())
            {
                var animation = (Animation)source.GetAnimation(name).Duplicate(true);
                animation.LoopMode = Animation.LoopModeEnum.None;
                _ = copy.AddAnimation(name, animation);
            }

            return copy;
        }

        // Reference-rig skeleton space: avatar forward is +Z and avatar right is -X.
        private static Vector3 SkeletonToActorLocal(Vector3 value) => new(-value.X, value.Y, -value.Z);
    }
}
