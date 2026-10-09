using AlleyCat.Navigation.Arrival;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Navigation.Arrival.NAV001ArrivalPlannerIntegrationTests;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation.Arrival;

/// <summary>
/// Numerical replay render-size isolation and per-physics-tick pose pairing: one rig-agnostic failure-cleanup fact
/// and one equivalence guardian on the male rig, whose strictly-superset assertions (exact planted state, pre-IK
/// balls, full graph state, and contribution evidence) cover the retired female pairing twin.
/// </summary>
public sealed class NAV001ReplayPacingIntegrationTests
{
    /// <summary>A failed test body still restores the shared renderer and physics-step limit.</summary>
    [Fact]
    public Task ReducedRenderSize_WhenTestBodyFails_RestoresSessionWindow()
        => VerifyFailureCleanupAsync(NPCNavigationRig.Female);

    private static async Task VerifyFailureCleanupAsync(NPCNavigationRig rig)
    {
        SceneTree sceneTree = GetSceneTree();
        Vector2I previousSize = sceneTree.Root.Size;
        int previousSteps = Engine.MaxPhysicsStepsPerFrame;
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(
                rig, useReducedRenderSize: true);
            Assert.Equal(new Vector2I(256, 256), sceneTree.Root.Size);
            throw new InvalidOperationException("Intentional fixture-body failure.");
        });
        Assert.Equal("Intentional fixture-body failure.", failure.Message);
        Assert.Equal(previousSize, sceneTree.Root.Size);
        Assert.Equal(previousSteps, Engine.MaxPhysicsStepsPerFrame);
    }

    /// <summary>
    /// The real male rig retains all 120 paired native samples and exact full-size pose, planted-state, graph, and
    /// contribution evidence at the reduced raster size.
    /// </summary>
    [Fact]
    public Task Male_ReducedRenderSize_PreservesPairedTicksAndRestoresSessionWindow() => VerifyPairingAsync();

    private static async Task VerifyPairingAsync()
    {
        SceneTree sceneTree = GetSceneTree();
        Vector2I previousSize = sceneTree.Root.Size;
        int previousSteps = Engine.MaxPhysicsStepsPerFrame;
        (EngineTick[] fullTicks, PlantedStateSample[] fullSamples) = await RecordAsync(false);
        Assert.Equal(previousSize, sceneTree.Root.Size);
        Assert.Equal(previousSteps, Engine.MaxPhysicsStepsPerFrame);
        (EngineTick[] reducedTicks, PlantedStateSample[] reducedSamples) = await RecordAsync(true);
        Assert.Equal(previousSize, sceneTree.Root.Size);
        Assert.Equal(previousSteps, Engine.MaxPhysicsStepsPerFrame);
        Assert.Equal(fullTicks, reducedTicks);
        for (int index = 0; index < fullSamples.Length; index++)
        {
            PlantedStateSample full = fullSamples[index];
            PlantedStateSample reduced = reducedSamples[index];
            Assert.Equal(full.Actor, reduced.Actor);
            Assert.Equal(full.LeftBall, reduced.LeftBall);
            Assert.Equal(full.RightBall, reduced.RightBall);
            Assert.Equal(full.LeftToesOrigin, reduced.LeftToesOrigin);
            Assert.Equal(full.RightToesOrigin, reduced.RightToesOrigin);
            Assert.Equal(full.Planted, reduced.Planted);
            Assert.Equal(full.OracleBoneRotations, reduced.OracleBoneRotations);
            Assert.Equal(full.PostIKOracleBoneRotations, reduced.PostIKOracleBoneRotations);
            Assert.Equal(full.ExactPlanted, reduced.ExactPlanted);
            Assert.Equal(full.PreIKLeftBallInActor, reduced.PreIKLeftBallInActor);
            Assert.Equal(full.PreIKRightBallInActor, reduced.PreIKRightBallInActor);
            Assert.Equal(full.Graph.Current, reduced.Graph.Current);
            Assert.Equal(full.Graph.Fading, reduced.Graph.Fading);
            Assert.Equal(full.Graph.FadingPosition, reduced.Graph.FadingPosition);
            Assert.Equal(full.Graph.FadingLength, reduced.Graph.FadingLength);
            Assert.Equal(full.Graph.CurrentBlend, reduced.Graph.CurrentBlend);
            Assert.Equal(full.Graph.FadingBlend, reduced.Graph.FadingBlend);
            Assert.Equal(full.Graph.CurrentPositions, reduced.Graph.CurrentPositions);
            Assert.Equal(full.Graph.FadingPositions, reduced.Graph.FadingPositions);
            Assert.Null(full.Graph.Interruption);
            Assert.Null(reduced.Graph.Interruption);
            Assert.Equal(full.Contributions.Select(ContributionEvidence), reduced.Contributions.Select(ContributionEvidence));
        }
    }

    private static (string State, string? BlendPoint, string Animation, string Identity,
        double NodePosition, double SourceTime, double RawWeight, double Weight) ContributionEvidence(ClipContribution contribution)
        => (contribution.State, contribution.BlendPoint, contribution.Animation, contribution.Intervals.Identity.ToString(),
            contribution.NodePositionSeconds, contribution.SourceTimeSeconds, contribution.RawWeight, contribution.Weight);

    private static async Task<(EngineTick[] Ticks, PlantedStateSample[] Samples)> RecordAsync(bool reduced)
    {
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(
            NPCNavigationRig.Male, useReducedRenderSize: reduced);
        Assert.Equal(1, Engine.MaxPhysicsStepsPerFrame);
        Assert.Equal(60, Engine.PhysicsTicksPerSecond);
        if (reduced)
        {
            Assert.Equal(new Vector2I(256, 256), fixture.Actor.GetTree().Root.Size);
        }

        await fixture.StepAsync(30);
        Binding binding = Bind(fixture);
        await TeleportToIdleAtOriginAsync(fixture, binding);
        ArrivalPlan plan = binding.Planner.Plan(IdleStart(fixture, binding), new ArrivalTarget(new Vector2(0f, -4f), 0f));
        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        SequencePrediction prediction = binding.Planner.Predict(plan);
        Assert.True(prediction.Ticks.Length >= 120);
        using AnimationGraphPlantedStateRecorder recorder = fixture.BindRecorder();
        recorder.Attach();
        var evaluations = new Dictionary<ulong, long>();
        var skeletonSamples = new Dictionary<ulong, (long Evaluation, Vector3 Left, Vector3 Right)>();
        int left = fixture.Skeleton.FindBone("LeftToes");
        int right = fixture.Skeleton.FindBone("RightToes");
        void OnMixer()
        {
            evaluations[Engine.GetPhysicsFrames()] = fixture.Driver.EvaluationCount + 1;
        }

        void OnSkeleton()
        {
            Transform3D world = fixture.Skeleton.GlobalTransform;
            skeletonSamples[Engine.GetPhysicsFrames()] = (fixture.Driver.EvaluationCount,
                (world * fixture.Skeleton.GetBoneGlobalPose(left)).Origin,
                (world * fixture.Skeleton.GetBoneGlobalPose(right)).Origin);
        }

        fixture.Tree.MixerApplied += OnMixer;
        fixture.Skeleton.SkeletonUpdated += OnSkeleton;
        ulong firstPhysics = Engine.GetPhysicsFrames() + 1;
        long firstEvaluation = fixture.Driver.EvaluationCount + 1;
        EngineRun run;
        try
        {
            run = await RunAsync(fixture, binding, plan, prediction, 120);
        }
        finally
        {
            fixture.Tree.MixerApplied -= OnMixer;
            fixture.Skeleton.SkeletonUpdated -= OnSkeleton;
            recorder.Detach();
        }

        Assert.Equal(120, run.Ticks.Count);
        Assert.Equal(120, recorder.Samples.Count);
        for (int index = 0; index < 120; index++)
        {
            ulong physics = firstPhysics + (ulong)index;
            long evaluation = firstEvaluation + index;
            PlantedStateSample sample = recorder.Samples[index];
            Assert.Equal(physics, sample.Tick);
            Assert.Equal(evaluation, evaluations[physics]);
            Assert.Equal(evaluation, skeletonSamples[physics].Evaluation);
            Assert.Equal(sample.LeftToesOrigin, skeletonSamples[physics].Left);
            Assert.Equal(sample.RightToesOrigin, skeletonSamples[physics].Right);
        }

        // Keep any queued preparation-tick notification; it is not one of the 120 recorder samples.
        // Every observed key must still correspond to its own native evaluation, including that prior tick.
        foreach ((ulong physics, (long Evaluation, Vector3 Left, Vector3 Right) observation) in skeletonSamples)
        {
            long offset = (long)physics - (long)firstPhysics;
            Assert.InRange(offset, -1, 119);
            Assert.Equal(firstEvaluation + offset, observation.Evaluation);
        }

        return ([.. run.Ticks], [.. recorder.Samples]);
    }
}
