using System.Text;
using AlleyCat.Control.Locomotion;
using AlleyCat.IK;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>
/// NAV-001 NAV-TAC-001 (Technical Requirement 16) mid-clip entry on the real NPC of the navigation test scene. A 4 m walk
/// enters the forward cycle mid-clip at its stitch-mapped join, through the navigation's <c>Travel → Advance(0) → seek
/// → Advance(dt)</c> entry. On every mid-clip entry tick the test checks, from engine-level observation and the
/// independent recorder:
/// <list type="bullet">
/// <item>the recorded playback position of the entered clip equals the slice start (no pose pop);</item>
/// <item>the tick's root motion equals the outgoing clip's weighted table delta alone, so the entered clip contributes
/// zero root motion (no root-motion jump);</item>
/// <item>exactly one IK solve (foot-provider stage run), one planted-state step, one recorded sample, and one
/// root-motion read, and exactly two mixer evaluations (the pre-evaluation and the final one);</item>
/// <item>all of them reflect the final evaluation: the foot provider's input ball and the planted state match the
/// final evaluation, and the planted-state source evaluated the entered clip at the slice start;</item>
/// <item>pose continuity: the recorded pre-IK ball step matches the step predicted by the clips' bind-time ball tables
/// blended by their recorded weights (the pose-pop metric), with the pre-evaluation alternative reported.</item>
/// </list>
/// </summary>
public sealed class NAV001MidClipEntryIntegrationTests
{
    private const string EvidenceDirectory = "res://temp/nav001-p4-u2/mid-clip-entry";
    private const double PositionToleranceSeconds = 1e-5d;
    private const int RequestTimeoutTicks = 1200;

    /// <summary>Root-motion match: the table's declared 0.1 mm root bound plus float rounding.</summary>
    private const float RootMotionToleranceMetres = 0.0002f;

    /// <summary>
    /// Pose-pop bound on the pre-IK ball step: twice the tables' declared 5 mm ball interpolation bound (the same gate
    /// as the NAV-TAC-008 frame-integrity check), covering the linear blend of two table balls against the engine's
    /// rotation blend at crossfade weights.
    /// </summary>
    private const float PosePopToleranceMetres = 0.01f;

    /// <summary>Largest separation between the IK provider's input ball and the final evaluation's pre-IK ball.</summary>
    private const float ProviderInputToleranceMetres = 0.001f;

    private static readonly StandingLocomotionAnatomicalSide[] _feet =
        [StandingLocomotionAnatomicalSide.Left, StandingLocomotionAnatomicalSide.Right];

    /// <summary>NAV-TAC-001 mid-clip entry on the female rig.</summary>
    [Fact]
    public Task Female_MidClipEntry_EntersAtSliceStartOnTheFinalEvaluationOnly() => MidClipEntryAsync(NPCNavigationRig.Female);

    /// <summary>NAV-TAC-001 mid-clip entry on the male rig.</summary>
    [Fact]
    public Task Male_MidClipEntry_EntersAtSliceStartOnTheFinalEvaluationOnly() => MidClipEntryAsync(NPCNavigationRig.Male);

    private static async Task MidClipEntryAsync(NPCNavigationRig rig)
    {
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(rig);
        using AnimationGraphPlantedStateRecorder recorder = fixture.BindRecorder();
        var probe = new EntryProbe(fixture);
        recorder.Attach();
        try
        {
            // CharacterIK builds its modifier stages once the rig is running.
            await fixture.StepAsync(NAV001ArrivalOutcomeIntegrationTests.WarmUpPhysicsTicks);
            probe.Attach();
            await fixture.StepAsync(2);
            ulong terminal = await fixture.RunRequestAsync(new Vector3(0f, 0f, -4f), 0f, $"mid-clip-entry-{rig}", RequestTimeoutTicks);
            await fixture.SettleAsync();
            Assert.True(terminal != 0, $"{rig}: SETUP: the walk reported no outcome.");
        }
        finally
        {
            probe.Detach();
            recorder.Detach();
        }

        NPCClipTransitionRecord[] entries = [.. fixture.Navigation.IssuedClipTransitions
            .Where(transition => transition.EntryPositionSeconds > PositionToleranceSeconds && transition.CrossfadeSeconds > 0d)];
        Assert.True(entries.Length > 0, $"{rig}: SETUP: the walk made no mid-clip entry: {string.Join("; ", fixture.Navigation.IssuedClipTransitions)}.");

        var samples = recorder.Samples.ToDictionary(sample => sample.Tick);
        var failures = new List<string>();
        var report = new StringBuilder(FormattableString.Invariant($"{rig}: {entries.Length} mid-clip entr(y/ies); transitions: {string.Join("; ", fixture.Navigation.IssuedClipTransitions)}\n"));
        foreach (NPCClipTransitionRecord entry in entries)
        {
            CheckEntry(entry, samples, recorder, probe, failures, report);
        }

        // Pre-evaluations happen on entry ticks only, one per issued transition.
        HashSet<ulong> issued = [.. fixture.Navigation.IssuedClipTransitions.Select(transition => transition.PhysicsTick)];
        foreach ((ulong tick, TickObservation observation) in probe.Ticks)
        {
            int expected = issued.Contains(tick) ? 2 : 1;
            if (observation.Evaluations.Count != expected && samples.ContainsKey(tick))
            {
                failures.Add($"tick {tick}: {observation.Evaluations.Count} mixer evaluations, expected {expected}.");
            }
        }

        string text = report.ToString();
        string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(EvidenceDirectory));
        File.WriteAllText(Path.Combine(directory, $"{rig}.txt"), text + string.Join('\n', failures));
        Assert.True(failures.Count == 0, $"{string.Join('\n', failures)}\n{text}");
    }

    private static void CheckEntry(
        NPCClipTransitionRecord entry,
        Dictionary<ulong, PlantedStateSample> samples,
        AnimationGraphPlantedStateRecorder recorder,
        EntryProbe probe,
        List<string> failures,
        StringBuilder report)
    {
        ulong tick = entry.PhysicsTick;
        string label = FormattableString.Invariant($"entry {entry.From}→{entry.To}@{entry.EntryPositionSeconds:0.#####}s tick {tick}");
        if (!samples.TryGetValue(tick, out PlantedStateSample? sample)
            || !samples.TryGetValue(tick - 1, out PlantedStateSample? previous)
            || !probe.Ticks.TryGetValue(tick, out TickObservation? observation)
            || !probe.Ticks.TryGetValue(tick - 1, out TickObservation? before))
        {
            failures.Add($"{label}: SETUP: the entry tick or the tick before it was not recorded.");
            return;
        }

        // Recorded sample: the entered clip at the slice start, fading from the outgoing clip.
        GraphRecord graph = sample.Graph;
        if (graph.Current != entry.To || graph.Fading != entry.From
            || Math.Abs(graph.CurrentPositions[0] - entry.EntryPositionSeconds) > PositionToleranceSeconds)
        {
            failures.Add($"{label}: recorded {graph.Describe()}, expected {entry.To} at the slice start fading from {entry.From}.");
            return;
        }

        // Exactly one of each observable per tick, and two mixer evaluations (pre-evaluation + final).
        CheckCount(failures, label, "IK solve (foot-provider stage run)", observation.ProviderStageRuns, 1);
        CheckCount(failures, label, "skeleton update", observation.SkeletonUpdates, 1);
        CheckCount(failures, label, "planted-state step", (int)(observation.PlantedSteps - before.PlantedSteps), 1);
        CheckCount(failures, label, "root-motion read", (int)(observation.RootMotionReads - before.RootMotionReads), 1);
        CheckCount(failures, label, "mixer evaluation", observation.Evaluations.Count, 2);
        CheckCount(failures, label, "recorded sample", recorder.Samples.Count(candidate => candidate.Tick == tick), 1);
        if (observation.ProviderStageEvaluations != observation.Evaluations.Count)
        {
            failures.Add($"{label}: the foot-provider stage ran after {observation.ProviderStageEvaluations} of {observation.Evaluations.Count} evaluations, not after the final one.");
        }

        if (observation.RootMotionReadTick != tick || observation.PlantedEvaluationTick != tick)
        {
            failures.Add($"{label}: the root-motion read (tick {observation.RootMotionReadTick}) or the planted step (tick {observation.PlantedEvaluationTick}) is not from this tick.");
        }

        // Planted state: the source evaluated the entered clip at the slice start and agrees with the recorder.
        PlantedClipContribution[] source = observation.PlantedContributions;
        bool sourceSawEntry = source.Any(contribution => Math.Abs(contribution.SourceTimeSeconds - entry.EntryPositionSeconds) <= PositionToleranceSeconds
            && contribution.Clip.Identity == sample.Contributions.First(clip => clip.State == entry.To).Intervals.Identity);
        if (!sourceSawEntry || observation.PlantedState != sample.Planted)
        {
            failures.Add($"{label}: planted-state source [{string.Join("; ", source.Select(contribution => FormattableString.Invariant($"{contribution.Clip.Identity}@{contribution.SourceTimeSeconds:0.#####}")))}] = {observation.PlantedState} vs recorder {sample.Planted}.");
        }

        ClipContribution entered = sample.Contributions.First(clip => clip.State == entry.To);
        ClipContribution outgoing = sample.Contributions.First(clip => clip.State == entry.From);
        ClipContribution outgoingBefore = previous.Contributions.First(clip => clip.State == entry.From && clip.Animation == outgoing.Animation);
        ClipTrajectory enteredTable = recorder.Trajectories[entered.Animation];
        ClipTrajectory outgoingTable = recorder.Trajectories[outgoing.Animation];

        // Root motion: the outgoing clip's weighted delta alone; the counterfactual jump is the entered clip's 0 → p.
        float outgoingDelta = SlicePlanar(outgoingTable, outgoingBefore.SourceTimeSeconds, outgoing.SourceTimeSeconds) * (float)outgoing.Weight;
        Vector3 rootMotion = observation.Evaluations[^1].RootMotion;
        float recordedDelta = new Vector2(rootMotion.X, rootMotion.Z).Length();
        float jump = SlicePlanar(enteredTable, 0d, entered.SourceTimeSeconds) * (float)entered.Weight;
        if (Math.Abs(recordedDelta - outgoingDelta) > RootMotionToleranceMetres)
        {
            failures.Add(FormattableString.Invariant(
                $"{label}: root motion {recordedDelta * 1000:0.####} mm ≠ outgoing weighted delta {outgoingDelta * 1000:0.####} mm (w={outgoing.Weight:0.#####}); a jump from 0 would add {jump * 1000:0.####} mm."));
        }

        // Pose-pop metric and IK provider input, per foot.
        var line = new StringBuilder(FormattableString.Invariant(
            $"{label}: recorded {graph.Describe()}; root motion {recordedDelta * 1000:0.####} mm vs outgoing {outgoingDelta * 1000:0.####} mm (jump alternative +{jump * 1000:0.####} mm); counts ik={observation.ProviderStageRuns} planted={observation.PlantedSteps - before.PlantedSteps} rm_reads={observation.RootMotionReads - before.RootMotionReads} evaluations={observation.Evaluations.Count}"));
        foreach (StandingLocomotionAnatomicalSide foot in _feet)
        {
            Vector3 predicted = Blend(sample.Contributions, recorder, foot, null) - Blend(previous.Contributions, recorder, foot, null);
            Vector3 pop = Blend(sample.Contributions, recorder, foot, entry.To) - Blend(previous.Contributions, recorder, foot, null);
            Vector3 recorded = sample.PreIKBallInActor(foot) - previous.PreIKBallInActor(foot);
            float error = recorded.DistanceTo(predicted);
            float popError = recorded.DistanceTo(pop);
            Vector3 preEvaluation = observation.Evaluations[0].Ball(foot);
            Vector3 final = observation.Evaluations[^1].Ball(foot);
            Vector3 providerInput = sample.Actor.AffineInverse() * observation.ProviderInputBall(foot);
            float providerError = providerInput.DistanceTo(final);
            float providerToPreEvaluation = providerInput.DistanceTo(preEvaluation);
            _ = line.Append(FormattableString.Invariant(
                $"; {foot}: pre-IK step error {error * 1000:0.###} mm (entered-at-0 alternative {popError * 1000:0.###} mm), provider input vs final {providerError * 1000:0.###} mm vs pre-evaluation {providerToPreEvaluation * 1000:0.###} mm, final vs pre-evaluation ball {final.DistanceTo(preEvaluation) * 1000:0.###} mm, provider planted={observation.ProviderPlanted(foot)}"));
            if (error > PosePopToleranceMetres)
            {
                failures.Add(FormattableString.Invariant(
                    $"{label} {foot}: pre-IK ball step deviates {error * 1000:0.###} mm from the table-predicted step (> {PosePopToleranceMetres * 1000:0} mm): pose pop."));
            }

            if (providerError > ProviderInputToleranceMetres)
            {
                failures.Add(FormattableString.Invariant(
                    $"{label} {foot}: the IK provider's input ball is {providerError * 1000:0.###} mm from the final evaluation's pre-IK ball ({providerToPreEvaluation * 1000:0.###} mm from the pre-evaluation)."));
            }

            if (observation.ProviderPlanted(foot) != sample.Planted.IsPlanted(foot))
            {
                failures.Add($"{label} {foot}: the IK provider used planted={observation.ProviderPlanted(foot)}, the final evaluation's rule gives {sample.Planted.IsPlanted(foot)}.");
            }
        }

        _ = report.AppendLine(line.ToString());
    }

    private static void CheckCount(List<string> failures, string label, string what, int count, int expected)
    {
        if (count != expected)
        {
            failures.Add($"{label}: {count} {what}(s), expected {expected}.");
        }
    }

    /// <summary>Runtime planar root travel of a clip between two source times, across the loop end if needed.</summary>
    private static float SlicePlanar(ClipTrajectory trajectory, double from, double to)
        => to >= from
            ? trajectory.Slice(from, to).Planar.Length()
            : trajectory.Slice(from, trajectory.DurationSeconds).Planar.Length() + trajectory.Slice(0d, to).Planar.Length();

    /// <summary>
    /// Table balls of the contributions blended by their recorded weights; <paramref name="atStart" /> evaluates that
    /// state's clip at position 0 instead (the pre-evaluation alternative).
    /// </summary>
    private static Vector3 Blend(
        ClipContribution[] contributions,
        AnimationGraphPlantedStateRecorder recorder,
        StandingLocomotionAnatomicalSide foot,
        string? atStart)
    {
        Vector3 sum = Vector3.Zero;
        foreach (ClipContribution contribution in contributions)
        {
            double time = contribution.State == atStart ? 0d : contribution.SourceTimeSeconds;
            sum += recorder.Trajectories[contribution.Animation].Ball(foot, time) * (float)contribution.Weight;
        }

        return sum;
    }

    private readonly record struct Evaluation(Vector3 RootMotion, Vector3 LeftBall, Vector3 RightBall)
    {
        public Vector3 Ball(StandingLocomotionAnatomicalSide foot) => foot == StandingLocomotionAnatomicalSide.Left ? LeftBall : RightBall;
    }

    private sealed class TickObservation
    {
        public List<Evaluation> Evaluations { get; } = [];

        public int ProviderStageRuns
        {
            get; set;
        }

        public int ProviderStageEvaluations
        {
            get; set;
        }

        public int SkeletonUpdates
        {
            get; set;
        }

        public long PlantedSteps
        {
            get; set;
        }

        public ulong PlantedEvaluationTick
        {
            get; set;
        }

        public PlantedFeetState PlantedState
        {
            get; set;
        }

        public PlantedClipContribution[] PlantedContributions { get; set; } = [];

        public long RootMotionReads
        {
            get; set;
        }

        public ulong RootMotionReadTick
        {
            get; set;
        }

        public FootPlantingTickDiagnostic Left
        {
            get; set;
        }

        public FootPlantingTickDiagnostic Right
        {
            get; set;
        }

        public Vector3 ProviderInputBall(StandingLocomotionAnatomicalSide foot)
            => foot == StandingLocomotionAnatomicalSide.Left ? Left.AnimatedBall : Right.AnimatedBall;

        public bool ProviderPlanted(StandingLocomotionAnatomicalSide foot)
            => foot == StandingLocomotionAnatomicalSide.Left ? Left.MetadataPlanted : Right.MetadataPlanted;
    }

    /// <summary>
    /// Engine-level per-tick observation: each mixer evaluation's root motion and pre-IK balls (root-relative, as the
    /// recorder reads them), the foot-provider stage runs and how many evaluations preceded them, skeleton updates, the
    /// providers' tick diagnostics, and, after the tick, the planted-state source's and navigation's counters.
    /// </summary>
    private sealed class EntryProbe(NPCNavigationRigFixture fixture)
    {
        private readonly int _leftToes = fixture.Skeleton.FindBone("LeftToes");
        private readonly int _rightToes = fixture.Skeleton.FindBone("RightToes");
        private readonly CharacterIK _characterIK = fixture.Actor.GetNode<CharacterIK>("CharacterIK");
        private SkeletonModifier3D? _providerStage;
        private FootPlantingTargetProvider? _left;
        private FootPlantingTargetProvider? _right;

        public Dictionary<ulong, TickObservation> Ticks { get; } = [];

        public void Attach()
        {
            // CharacterIK's foot-provider stage modifier resolves both feet's targets: one run is one IK solve.
            _providerStage = fixture.Skeleton.GetChildren()
                .OfType<SkeletonModifier3D>()
                .SingleOrDefault(child => child.GetType().Name == "FootProviderStageModifier")
                ?? throw new InvalidOperationException(
                    $"SETUP: no foot-provider stage under the skeleton: {string.Join(", ", fixture.Skeleton.GetChildren().Select(child => $"{child.Name}:{child.GetType().Name}"))}.");
            _left = Assert.IsType<FootPlantingTargetProvider>(_characterIK.LeftFootTargetIntentProvider);
            _right = Assert.IsType<FootPlantingTargetProvider>(_characterIK.RightFootTargetIntentProvider);
            fixture.Tree.MixerApplied += OnMixerApplied;
            _providerStage.ModificationProcessed += OnProviderStage;
            fixture.Skeleton.SkeletonUpdated += OnSkeletonUpdated;
            _left.TickRecorded += OnLeft;
            _right.TickRecorded += OnRight;
        }

        public void Detach()
        {
            if (GodotObject.IsInstanceValid(fixture.Tree))
            {
                fixture.Tree.MixerApplied -= OnMixerApplied;
            }

            if (_providerStage is not null && GodotObject.IsInstanceValid(_providerStage))
            {
                _providerStage.ModificationProcessed -= OnProviderStage;
            }

            if (GodotObject.IsInstanceValid(fixture.Skeleton))
            {
                fixture.Skeleton.SkeletonUpdated -= OnSkeletonUpdated;
            }

            if (_left is not null)
            {
                _left.TickRecorded -= OnLeft;
            }

            if (_right is not null)
            {
                _right.TickRecorded -= OnRight;
            }
        }

        private TickObservation Current()
        {
            ulong tick = Engine.GetPhysicsFrames();
            if (!Ticks.TryGetValue(tick, out TickObservation? observation))
            {
                observation = new TickObservation();
                Ticks[tick] = observation;
            }

            return observation;
        }

        private void OnMixerApplied()
        {
            Skeleton3D skeleton = fixture.Skeleton;
            Transform3D actorFromSkeleton = fixture.Actor.GlobalTransform.AffineInverse() * skeleton.GlobalTransform;
            Current().Evaluations.Add(new Evaluation(
                fixture.Tree.GetRootMotionPosition(),
                actorFromSkeleton * skeleton.GetBoneGlobalPose(_leftToes).Origin,
                actorFromSkeleton * skeleton.GetBoneGlobalPose(_rightToes).Origin));
        }

        private void OnProviderStage()
        {
            TickObservation observation = Current();
            observation.ProviderStageRuns++;
            observation.ProviderStageEvaluations = observation.Evaluations.Count;
        }

        private void OnSkeletonUpdated()
        {
            TickObservation observation = Current();
            observation.SkeletonUpdates++;
            AnimationGraphPlantedStateSource source = fixture.PlantedSource;
            observation.PlantedSteps = source.EvaluationCount;
            observation.PlantedEvaluationTick = source.EvaluationTick;
            observation.PlantedState = source.LastState;
            observation.PlantedContributions = source.LastContributions.ToArray();
            observation.RootMotionReads = fixture.Navigation.RootMotionReadCount;
            observation.RootMotionReadTick = fixture.Navigation.LastRootMotionReadTick;
        }

        private void OnLeft(FootPlantingTickDiagnostic diagnostic) => Current().Left = diagnostic;

        private void OnRight(FootPlantingTickDiagnostic diagnostic) => Current().Right = diagnostic;
    }
}
