using System.Text;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Navigation.NAV001ArrivalOutcomeIntegrationTests;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>
/// Gate for <see cref="AnimationGraphPlantedStateRecorder"/> before its planted state is trusted for NAV-001 A2. On
/// real NPC runs in the navigation test scene it checks that every tick pairs a graph record with the rendered pose of
/// the same tick, and that on every tick the computed contributions reproduce the mixer's output local rotation of the
/// oracle bones (Hips and both toes), read before any skeleton modifier (CharacterIK) runs so that foot planting cannot
/// mask or cause a mapping error: each clip's rotation track is sampled directly from its <see cref="Animation"/> at the
/// computed source time and blended with the computed weights the way Godot's mixer blends skeleton rotations
/// (rest-relative, in blend order). Single-clip ticks (normalised weight ≥ 0.9999: Idle, entries, cycles, exits,
/// pivots, and side-steps, each a native-speed state) are checked per clip; crossfade and interrupted crossfade ticks
/// together. The mapped error must stay within
/// <see cref="MaximumMappedErrorDegrees"/>, and for each clip the gate proves its own discrimination: shifting every
/// source time by ±1 physics tick (or, if a clip cannot discriminate at one tick, by the smallest whole number of ticks
/// that does, reported) must push the worst error above the same threshold.
/// </summary>
public sealed class NAV001PlantedStateRecorderValidationIntegrationTests
{
    private const string EvidenceDirectory = "res://temp/nav001-a2-recorder";
    private const double SingleClipWeight = 0.9999d;

    // Above the measured float floor of the exact mapping (0.0396° worst case) with margin, and below the error a
    // one-tick time shift produces on every clip.
    private const double MaximumMappedErrorDegrees = 0.1d;
    private const int MaximumPerturbationTicks = 6;
    private const int MinimumSingleClipTicks = 10;
    private const int RequestTimeoutTicks = 1200;
    private const string MultiClip = "multi-clip";

    // Required single-clip coverage is keyed by the package-declared ANIM-002 capability, not by authored clip names.
    private const string ForwardCycle = "cyclic_forward_primary";
    private const string PivotLeft = "confined_pivot_left";
    private const string SideStepRight = "confined_side_step_right";
    private const string Idle = "idle_contact_free";

    /// <summary>Idle → forward entry → forward cycle → exit → Idle.</summary>
    [Fact]
    public Task ForwardWalk_SourceTimeMatchesRenderedClipPose()
        => ValidateAsync("forward-walk", new ArrivalRequest(new Vector3(0f, 0f, -4f), 0f), [ForwardCycle, Idle]);

    /// <summary>Idle → pivot clips → Idle.</summary>
    [Fact]
    public Task TurnInPlace_SourceTimeMatchesRenderedClipPose()
        => ValidateAsync("turn-in-place", new ArrivalRequest(Vector3.Zero, Mathf.Pi / 2f), [PivotLeft, Idle]);

    /// <summary>Idle → side-step clip → Idle.</summary>
    [Fact]
    public Task SideStep_SourceTimeMatchesRenderedClipPose()
        => ValidateAsync("side-step", new ArrivalRequest(new Vector3(0.5f, 0f, 0f), 0f), [SideStepRight, Idle]);

    /// <summary>
    /// A distant lateral walk: every crossfade of its sequence (Idle ↔ entry, entry → cycle, cycle → exit, exit →
    /// Idle) blends two clips, whose computed crossfade weights must reproduce the rendered pose.
    /// </summary>
    [Fact]
    public Task DistantLateralWalk_CrossfadeWeightsMatchRenderedPose()
        => ValidateAsync("distant-lateral-walk", new ArrivalRequest(new Vector3(3f, 0f, 0f), 0f), [Idle], requireCrossfades: true);

    private static async Task ValidateAsync(
        string scenario,
        ArrivalRequest request,
        string[] requiredCapabilities,
        bool requireCrossfades = false)
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

            NavigationRequestIdentity identity = IssueRequest(navigation, request, scenario, 0);
            ulong windowStart = Engine.GetPhysicsFrames();
            NavigationTerminalOutcomeRecord? outcome = null;
            for (int tick = 0; tick < RequestTimeoutTicks && outcome is null; tick++)
            {
                await WaitForNextPhysicsFrameAsync(sceneTree);
                outcome = FindOutcome(navigation, identity);
            }

            await WaitForPhysicsFramesAsync(sceneTree, Engine.PhysicsTicksPerSecond);
            ulong windowEnd = Engine.GetPhysicsFrames();
            await WaitForFramesAsync(sceneTree, 1);
            recorder.Detach();

            Assert.True(outcome is not null, $"SETUP: [{scenario}] request reported no outcome; the run did not exercise its clips.");
            PlantedStateSample[] window = RequireCompleteWindow(recorder, windowStart, windowEnd);
            Evaluate(scenario, recorder, window, requiredCapabilities, requireCrossfades, 1d / Engine.PhysicsTicksPerSecond);
        }
        finally
        {
            recorder?.Detach();
            Engine.MaxPhysicsStepsPerFrame = previousStepLimit;
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    private static void Evaluate(
        string scenario,
        AnimationGraphPlantedStateRecorder recorder,
        IReadOnlyList<PlantedStateSample> window,
        string[] requiredCapabilities,
        bool requireCrossfades,
        double tickSeconds)
    {
        IReadOnlyList<string> bones = AnimationGraphPlantedStateRecorder.OracleBones;
        Quaternion[] rests = recorder.OracleRestRotations;
        var perClip = new SortedDictionary<string, ClipErrors>(StringComparer.Ordinal);
        var blended = new ClipErrors(bones.Count);
        var trackCache = new Dictionary<(string Animation, int Bone), int>();
        double[] equalWeightErrors = new double[bones.Count];
        double[] ikDelta = new double[bones.Count];
        float maxBallToToeDistance = 0f;
        foreach (PlantedStateSample sample in window)
        {
            maxBallToToeDistance = Math.Max(maxBallToToeDistance, Math.Max(
                sample.LeftBall.DistanceTo(sample.LeftToesOrigin),
                sample.RightBall.DistanceTo(sample.RightToesOrigin)));
            ClipContribution dominant = sample.Contributions.MaxBy(contribution => contribution.Weight);
            bool single = dominant.Weight >= SingleClipWeight;
            ClipErrors errors = single
                ? perClip.TryGetValue(dominant.Intervals.Capability, out ClipErrors? existing)
                    ? existing
                    : perClip[dominant.Intervals.Capability] = new ClipErrors(bones.Count)
                : blended;
            for (int bone = 0; bone < bones.Count; bone++)
            {
                // Pre-modifier mixer output, independent of any CharacterIK foot or leg modifier.
                Quaternion mixed = sample.OracleBoneRotations[bone];
                ikDelta[bone] = Math.Max(ikDelta[bone], AngleDegrees(mixed, sample.PostIKOracleBoneRotations[bone]));
                Quaternion Predict(double offset, bool equalWeights)
                {
                    // Godot 4.7 AnimationMixer: rotation = rest · Π slerp(identity, rest⁻¹ · q_i, w_i), in blend order.
                    Quaternion result = rests[bone];
                    foreach (ClipContribution contribution in sample.Contributions)
                    {
                        Quaternion key = TrackRotation(recorder, trackCache, contribution, bone, Shift(contribution, contribution.SourceTimeSeconds, offset));
                        float weight = equalWeights ? 1f / sample.Contributions.Length : (float)contribution.RawWeight;
                        result = (result * Quaternion.Identity.Slerp((rests[bone].Inverse() * key).Normalized(), weight)).Normalized();
                    }

                    return result;
                }

                double[] perturbed = new double[MaximumPerturbationTicks];
                for (int ticks = 1; ticks <= MaximumPerturbationTicks; ticks++)
                {
                    perturbed[ticks - 1] = Math.Max(
                        AngleDegrees(mixed, Predict(ticks * tickSeconds, false)),
                        AngleDegrees(mixed, Predict(-ticks * tickSeconds, false)));
                }

                errors.Add(bone, AngleDegrees(mixed, Predict(0d, false)), perturbed);
                if (!single)
                {
                    equalWeightErrors[bone] = Math.Max(equalWeightErrors[bone], AngleDegrees(mixed, Predict(0d, true)));
                }
            }
        }

        int interrupted = window.Count(sample => sample.Graph.Interruption is not null);
        int stricter = window.Count(sample => sample.Planted != sample.ExactPlanted);
        var report = new StringBuilder();
        _ = report.AppendLine(FormattableString.Invariant(
            $"scenario={scenario} ticks={window.Count} single_clip_ticks={window.Count - blended.Count} multi_clip_ticks={blended.Count} unpaired={recorder.UnpairedTicks} repeated_skeleton_updates={recorder.RepeatedSkeletonUpdates} max_ball_to_toe_origin={maxBallToToeDistance * 1000:0.####} mm"));
        _ = report.AppendLine(FormattableString.Invariant(
            $"interrupted crossfade ticks={interrupted} (conservative planted stricter than exact on {stricter}); undetermined transition starts (whole recording)={recorder.UndeterminedTransitionStarts}"));
        _ = report.AppendLine($"layer filters verified at bind (leg bones {string.Join(", ", recorder.LegBones)} excluded): {string.Join(", ", recorder.VerifiedLayers)}");
        _ = report.AppendLine(FormattableString.Invariant(
            $"oracle bones: {string.Join(", ", bones)} (pre-IK mixer-output local rotation vs the clip tracks sampled with Animation.RotationTrackInterpolate at the computed source times and blended with the computed weights; threshold {MaximumMappedErrorDegrees}°; perturbation = worst of ±k physics ticks of {tickSeconds:0.#####} s)"));
        _ = report.AppendLine(FormattableString.Invariant(
            $"pre-IK vs post-IK (report only) max difference: {string.Join(", ", bones.Select((name, bone) => FormattableString.Invariant($"{name}={ikDelta[bone]:0.#####}°")))}"));
        var failures = new List<string>();
        void Check(string label, ClipErrors errors)
        {
            for (int bone = 0; bone < bones.Count; bone++)
            {
                (double max, double median) = errors.Mapped(bone);
                _ = report.AppendLine(FormattableString.Invariant(
                    $"  {label} {bones[bone]}: ticks={errors.Count} mapped_error max={max:0.#####}° median={median:0.#####}° perturbed_1tick max={errors.PerturbedMax(bone, 1):0.###}°"));
                if (max > MaximumMappedErrorDegrees)
                {
                    failures.Add(FormattableString.Invariant($"{label} {bones[bone]}: mapped pose error {max:0.####}° > {MaximumMappedErrorDegrees}°."));
                }
            }

            int? discriminating = null;
            for (int ticks = 1; ticks <= MaximumPerturbationTicks && discriminating is null; ticks++)
            {
                if (errors.PerturbedMax(ticks) > MaximumMappedErrorDegrees)
                {
                    discriminating = ticks;
                }
            }

            _ = report.AppendLine(discriminating switch
            {
                1 => FormattableString.Invariant(
                    $"  {label} discrimination: ±1 tick worst error {errors.PerturbedMax(1):0.###}° > {MaximumMappedErrorDegrees}°"),
                { } ticks => FormattableString.Invariant(
                    $"  {label} discrimination: NOT at ±1 tick (worst {errors.PerturbedMax(1):0.#####}°); smallest discriminating perturbation ±{ticks} ticks, worst {errors.PerturbedMax(ticks):0.###}°"),
                null => FormattableString.Invariant(
                    $"  {label} discrimination: none up to ±{MaximumPerturbationTicks} ticks (worst {errors.PerturbedMax(MaximumPerturbationTicks):0.#####}°)"),
            });
            if (discriminating is null)
            {
                failures.Add(FormattableString.Invariant(
                    $"{label}: a ±{MaximumPerturbationTicks}-tick source-time perturbation stays within {MaximumMappedErrorDegrees}°, so the gate cannot discriminate mis-mapped time."));
            }
        }

        foreach ((string clip, ClipErrors errors) in perClip)
        {
            Check(clip, errors);
        }

        if (blended.Count > 0)
        {
            Check(MultiClip, blended);
            _ = report.AppendLine(FormattableString.Invariant(
                $"  {MultiClip} equal-weight (report only) max error: {string.Join(", ", bones.Select((name, bone) => FormattableString.Invariant($"{name}={equalWeightErrors[bone]:0.###}°")))}"));
        }

        // Crossfade ticks where both states each decide planted state (weight > 0.1).
        int crossfadeMixtureTicks = window.Count(sample => sample.Graph.Fading is not null
            && sample.Contributions.Count(contribution => contribution.Weight > 0.1d) >= 2);
        _ = report.AppendLine(FormattableString.Invariant($"crossfade mixture ticks (both states above 0.1): {crossfadeMixtureTicks}"));
        if (requireCrossfades && crossfadeMixtureTicks < MinimumSingleClipTicks)
        {
            failures.Add($"fewer than {MinimumSingleClipTicks} crossfade mixture ticks were observed.");
        }

        foreach (string capability in requiredCapabilities)
        {
            if (!perClip.TryGetValue(capability, out ClipErrors? errors) || errors.Count < MinimumSingleClipTicks)
            {
                failures.Add($"{capability}: fewer than {MinimumSingleClipTicks} single-clip ticks were observed.");
            }
        }

        string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(EvidenceDirectory));
        string text = report.ToString();
        File.WriteAllText(Path.Combine(directory, $"{scenario}.txt"), text);
        File.WriteAllText(Path.Combine(directory, $"{scenario}.csv"), FormatSamplesCsv(window));
        Assert.True(failures.Count == 0, $"[{scenario}] recorder validation failed:\n  {string.Join("\n  ", failures)}\n{text}");
    }

    private static Quaternion TrackRotation(
        AnimationGraphPlantedStateRecorder recorder,
        Dictionary<(string Animation, int Bone), int> cache,
        ClipContribution contribution,
        int bone,
        double time)
    {
        Animation animation = recorder.AnimationOf(contribution);
        if (!cache.TryGetValue((contribution.Animation, bone), out int track))
        {
            string name = AnimationGraphPlantedStateRecorder.OracleBones[bone];
            track = animation.FindTrack($"%GeneralSkeleton:{name}", Animation.TrackType.Rotation3D);
            Assert.True(track >= 0, $"SETUP: '{contribution.Animation}' has no rotation track for '{name}'.");
            cache[(contribution.Animation, bone)] = track;
        }

        return animation.RotationTrackInterpolate(track, time);
    }

    private static double Shift(ClipContribution contribution, double time, double offset)
    {
        double shifted = time + offset;
        double duration = contribution.Intervals.DurationSeconds;
        return contribution.Intervals.IsLooping
            ? ((shifted % duration) + duration) % duration
            : Math.Clamp(shifted, 0d, duration);
    }

    private static double AngleDegrees(Quaternion a, Quaternion b)
    {
        float dot = Math.Min(1f, Math.Abs(a.Normalized().Dot(b.Normalized())));
        return Mathf.RadToDeg(2d * Math.Acos(dot));
    }

    /// <summary>Per-bone mapped errors and, per perturbation size, the worst perturbed error.</summary>
    private sealed class ClipErrors(int bones)
    {
        private readonly List<double>[] _mapped = [.. Enumerable.Range(0, bones).Select(_ => new List<double>())];
        private readonly double[][] _perturbedMax = [.. Enumerable.Range(0, bones).Select(_ => new double[MaximumPerturbationTicks])];

        public int Count => _mapped[0].Count;

        public void Add(int bone, double mapped, double[] perturbed)
        {
            _mapped[bone].Add(mapped);
            for (int index = 0; index < perturbed.Length; index++)
            {
                _perturbedMax[bone][index] = Math.Max(_perturbedMax[bone][index], perturbed[index]);
            }
        }

        public (double Max, double Median) Mapped(int bone) => (_mapped[bone].Max(), Median(_mapped[bone]));

        /// <summary>Worst error of one bone with every source time shifted by ±<paramref name="ticks"/> ticks.</summary>
        public double PerturbedMax(int bone, int ticks) => _perturbedMax[bone][ticks - 1];

        /// <summary>Worst error over all oracle bones with every source time shifted by ±<paramref name="ticks"/> ticks.</summary>
        public double PerturbedMax(int ticks) => _perturbedMax.Max(bone => bone[ticks - 1]);

        private static double Median(List<double> values)
        {
            double[] sorted = [.. values.Order()];
            return sorted[sorted.Length / 2];
        }
    }
}
