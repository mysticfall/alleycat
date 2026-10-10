using AlleyCat.Control.Locomotion;
using AlleyCat.IK;
using AlleyCat.IntegrationTests.Navigation;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using AlleyCat.Testing;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>Checks actual engine time admission independently of the planting commit stage.</summary>
public sealed class FootPlantingElapsedClockIntegrationTests
{
    /// <summary>The installed production driver and earlier replay both supply time before the zero-delta stage.</summary>
    [Fact]
    public async Task NPCAndReplay_AdmitActualDeltaBeforeFirstProviderStep()
    {
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(NPCNavigationRig.Female);
        var observer = new FootPlantingPhysicsDeltaObserver();
        fixture.Actor.AddChild(observer);
        await fixture.StepAsync(2);
        await fixture.SettleAsync();
        CharacterIK ik = fixture.Actor.GetNode<CharacterIK>("CharacterIK");
        FootPlantingTargetProvider foot = Assert.IsType<FootPlantingTargetProvider>(ik.LeftFootTargetIntentProvider);
        List<(ulong Tick, double Input, double Stage, double Animation)> samples = [];
        void Capture(FootPlantingTickDiagnostic sample)
        {
            samples.Add((sample.Tick,
                foot.LastCommittedInputDeltaSeconds, ik.LastFootProviderStageDeltaSeconds, fixture.PlantedSource.LastAdvanceDeltaSeconds));
        }
        foot.TickRecorded += Capture;
        try
        {
            await fixture.StepAsync(6);
            await fixture.SettleAsync();
            AssertSamples(requireZeroStage: false);
            samples.Clear();
            using var replay = new FootPlantingClockPhysicsReplay(fixture, () => _ = Assert.IsAssignableFrom<CharacterBody3D>(fixture.Actor).MoveAndCollide(Vector3.Zero));
            for (int tick = 0; tick < 6; tick++)
            {
                await replay.StepAsync();
            }

            // The first deferred publication can precede this tick's node callbacks. Retain all samples while
            // the independent callback observer reaches the existing process-frame boundary.
            await fixture.SettleAsync();
            AssertSamples(requireZeroStage: true);
        }
        finally
        {
            foot.TickRecorded -= Capture;
            observer.QueueFree();
        }

        void AssertSamples(bool requireZeroStage)
        {
            Assert.True(samples.Count >= 6);
            if (requireZeroStage)
            {
                Assert.Contains(samples, sample => sample.Stage == 0d);
            }

            foreach ((ulong tick, double input, double stage, double animation) in samples)
            {
                Assert.Equal(observer.Deltas[tick], input);
                Assert.Equal(animation, input);
                Assert.True(stage == 0d || stage == input);
            }

            WriteEvidence(requireZeroStage ? "engine-ordering-replay" : "engine-ordering-production",
                samples.Select(sample => FormattableString.Invariant(
                    $"{sample.Tick},{observer.Deltas[sample.Tick]:R},{sample.Input:R},{sample.Stage:R},{sample.Animation:R}")));
        }
    }

    /// <summary>A skipped commit stage retains independently recorded intervals across changing settings.</summary>
    [Fact]
    public async Task VariableSettings_StageSkipsPreserveActualHistory()
    {
        double originalScale = Engine.TimeScale;
        int originalRate = Engine.PhysicsTicksPerSecond;
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(NPCNavigationRig.Female);
        var observer = new FootPlantingPhysicsDeltaObserver();
        fixture.Actor.AddChild(observer);
        CharacterIK ik = fixture.Actor.GetNode<CharacterIK>("CharacterIK");
        FootPlantingTargetProvider foot = Assert.IsType<FootPlantingTargetProvider>(ik.LeftFootTargetIntentProvider);
        var contact = new ConstantContact();
        fixture.Actor.AddChild(contact);
        PlantedStateSource? originalSource = foot.PlantedStateSource;
        foot.PlantedStateSource = contact;
        List<(FootPlantingTickDiagnostic Sample, double Delta)> resumed = [];
        void Capture(FootPlantingTickDiagnostic sample)
        {
            resumed.Add((sample, foot.LastCommittedInputDeltaSeconds));
        }
        try
        {
            await fixture.StepAsync(3);
            await fixture.SettleAsync();
            ulong anchor = foot.LastDiagnostic.Tick;
            Vector3 pin = foot.LastDiagnostic.Pin;
            ik.Active = false;
            foot.TickRecorded += Capture;
            Engine.TimeScale = 0.5;
            await fixture.StepAsync(2);
            await fixture.SettleAsync();
            Engine.PhysicsTicksPerSecond = originalRate * 2;
            Engine.TimeScale = 1;
            await fixture.StepAsync(2);
            await fixture.SettleAsync();
            Engine.TimeScale = 2;
            await fixture.StepAsync(2);
            await fixture.SettleAsync();
            ik.Active = true;
            await fixture.StepAsync(1);
            await fixture.SettleAsync();
            Assert.NotEmpty(resumed);
            (FootPlantingTickDiagnostic first, double actual) = resumed[0];
            double expected = observer.Deltas.Where(pair => pair.Key > anchor && pair.Key <= first.Tick)
                .OrderBy(pair => pair.Key).Sum(pair => pair.Value);
            Assert.Equal(expected, actual);
            Assert.True(expected > 0 && expected <= foot.MaxContinuationGapSeconds);
            Assert.False(first.ContinuityReset);
            Assert.Equal(pin, first.Pin);
            Assert.True(observer.Deltas.Values.Distinct().Count() >= 2);
            WriteEvidence("engine-variable-history", observer.Deltas.OrderBy(pair => pair.Key)
                .Select(pair => FormattableString.Invariant($"{pair.Key},{pair.Value:R}"))
                .Append(FormattableString.Invariant($"anchor,{anchor},resume,{first.Tick},expected,{expected:R},actual,{actual:R}")));
        }
        finally
        {
            foot.TickRecorded -= Capture;
            foot.PlantedStateSource = originalSource;
            ik.Active = true;
            Engine.TimeScale = originalScale;
            Engine.PhysicsTicksPerSecond = originalRate;
            observer.QueueFree();
            contact.QueueFree();
        }
    }

    /// <summary>A standalone provider requires no navigation owner and preserves preview/cache/reset/rebind semantics.</summary>
    [Fact]
    public async Task GenericProvider_PreviewCacheResetAndRebind()
    {
        SceneTree tree = GetSceneTree();
        var root = new Node3D();
        Skeleton3D skeleton = CreateSkeleton();
        Skeleton3D other = CreateSkeleton();
        root.AddChild(skeleton);
        root.AddChild(other);
        var contact = new ConstantContact();
        root.AddChild(contact);
        var provider = new FootPlantingTargetProvider { Skeleton = skeleton, PlantedStateSource = contact };
        root.AddChild(provider);
        tree.Root.AddChild(root);
        try
        {
            await WaitForPhysicsFramesAsync(tree, 2);
            Assert.True(provider.TryGetTargetIntent(out _));
            Assert.Equal(0ul, provider.CommittedStepInvocationCount);
            using (FootTargetResolutionStage.Enter(skeleton))
            {
                Assert.True(provider.TryGetTargetIntent(out _));
            }

            Assert.Equal(0d, provider.LastCommittedInputDeltaSeconds);
            ulong calls = provider.CommittedStepInvocationCount;
            Assert.True(provider.TryGetTargetIntent(out IKTargetIntentResolution cached));
            Assert.Equal(calls, provider.CommittedStepInvocationCount);
            provider.ResetPin();
            Assert.True(provider.TryGetTargetIntent(out IKTargetIntentResolution afterReset));
            Assert.Equal(cached, afterReset);
            await WaitForPhysicsFramesAsync(tree, 1);
            Assert.True(provider.TryGetTargetIntent(out _));
            Assert.True(provider.TryGetTargetIntent(out _));
            Assert.Equal(calls, provider.CommittedStepInvocationCount);
            // The signal precedes provider callbacks; preview cannot admit this tick. Wait for its ordinary callback.
            await WaitForNextFrameAsync(tree);
            using (FootTargetResolutionStage.Enter(skeleton))
            {
                Assert.True(provider.TryGetTargetIntent(out _));
            }

            Assert.True(provider.LastCommittedInputDeltaSeconds > 0);
            provider.Skeleton = other;
            using (FootTargetResolutionStage.Enter(other))
            {
                Assert.True(provider.TryGetTargetIntent(out _));
            }

            Assert.Equal(0d, provider.LastCommittedInputDeltaSeconds);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(tree);
        }
    }

    /// <summary>Exiting and explicitly reactivating navigation reconstructs its complete runtime binding.</summary>
    [Fact]
    public async Task NavigationExit_ExplicitActivationReconstructsBinding()
    {
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(NPCNavigationRig.Female);
        NPCNavigation navigation = fixture.Navigation;
        Node parent = navigation.GetParent();
        NPCAnimationTreeDriver? oldDriver = navigation.AnimationDriver;
        parent.RemoveChild(navigation);
        bool activeAfterExit = navigation.IsRuntimeBindingActive;
        parent.AddChild(navigation);
        navigation.ActivateRuntimeBinding();
        // The observation window ends at explicit activation return: active must mean a usable reconstructed driver.
        WriteLifecycleEvidence("navigation", new
        {
            activeAfterExit,
            activeAfterActivation = navigation.IsRuntimeBindingActive,
            driverPresentAfterActivation = navigation.AnimationDriver is not null,
            driverReplaced = !ReferenceEquals(oldDriver, navigation.AnimationDriver),
        });
        Assert.NotNull(navigation.AnimationDriver);
        Assert.NotSame(oldDriver, navigation.AnimationDriver);
        Assert.False(activeAfterExit);
        Assert.True(navigation.IsRuntimeBindingActive);
        NPCAnimationTreeDriver newDriver = navigation.AnimationDriver;
        long evaluations = newDriver.EvaluationCount;
        navigation.ActivateRuntimeBinding();
        Assert.Same(newDriver, navigation.AnimationDriver);
        await fixture.StepAsync(3);
        await fixture.SettleAsync();
        Assert.True(newDriver.EvaluationCount >= evaluations + 3);
        using var replay = new FootPlantingClockPhysicsReplay(fixture, () => _ = Assert.IsAssignableFrom<CharacterBody3D>(fixture.Actor).MoveAndCollide(Vector3.Zero));
        await replay.StepAsync();
        FootPlantingTargetProvider foot = fixture.Actor.GetNode<FootPlantingTargetProvider>("CharacterIK/LeftFootPlanting");
        Assert.Equal(fixture.Actor.GetPhysicsProcessDeltaTime(), foot.LastCommittedInputDeltaSeconds);
    }

    /// <summary>Same-skeleton tree re-entry resets cached pose/solver history and reconnects native post-IK capture.</summary>
    [Fact]
    public async Task ProviderExit_SameSkeletonReentryReconstructsBinding()
    {
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(NPCNavigationRig.Female);
        await fixture.StepAsync(3);
        await fixture.SettleAsync();
        FootPlantingTargetProvider foot = fixture.Actor.GetNode<FootPlantingTargetProvider>("CharacterIK/LeftFootPlanting");
        Node parent = foot.GetParent();
        Transform3D originalTransform = fixture.Skeleton.GlobalTransform;
        ulong previousCalls = foot.CommittedStepInvocationCount;
        Vector3 previousPin = foot.LastDiagnostic.Pin;
        int nativeCaptures = 0;
        void Capture(FootPlantingTickDiagnostic diagnostic)
        {
            if (diagnostic.PostIKCaptured)
            {
                nativeCaptures++;
            }
        }

        foot.TickRecorded += Capture;
        try
        {
            parent.RemoveChild(foot);
            bool availableAfterExit = foot.IsPlantingAvailable;
            fixture.Skeleton.GlobalPosition += new Vector3(0.05f, 0f, 0f);
            int footBone = fixture.Skeleton.FindBone("LeftFoot");
            int toeBone = fixture.Skeleton.FindBone("LeftToes");
            Vector3 anatomicalBallInFoot = (fixture.Skeleton.GetBoneGlobalRest(footBone).AffineInverse()
                * fixture.Skeleton.GetBoneGlobalRest(toeBone)).Origin;
            Vector3 expectedPin = fixture.Skeleton.GlobalTransform * fixture.Skeleton.GetBoneGlobalPose(footBone)
                * anatomicalBallInFoot;
            expectedPin.Y = 0f;
            parent.AddChild(foot);
            using (FootTargetResolutionStage.Enter(fixture.Skeleton))
            {
                Assert.True(foot.TryGetTargetIntent(out _));
            }

            ulong firstCalls = foot.CommittedStepInvocationCount;
            double firstDelta = foot.LastCommittedInputDeltaSeconds;
            Vector3 firstPin = foot.LastDiagnostic.Pin;
            // Retain the same-skeleton rebind and three complete native ticks before judging all captured outcomes.
            await fixture.StepAsync(3);
            await fixture.SettleAsync();
            WriteLifecycleEvidence("provider", new
            {
                availableAfterExit,
                previousCalls,
                firstCalls,
                firstDelta,
                previousPin = previousPin.ToString(),
                firstPin = firstPin.ToString(),
                expectedPin = expectedPin.ToString(),
                nativeCaptures,
            });
            Assert.Equal(previousCalls + 1, firstCalls);
            Assert.Equal(0d, firstDelta);
            Assert.True(firstPin.DistanceTo(previousPin) > 0.04f);
            Assert.InRange(firstPin.DistanceTo(expectedPin), 0f, 0.00001f);
            Assert.True(nativeCaptures >= 3, "Same-skeleton rebind did not reconnect native SkeletonUpdated capture.");
            Assert.False(availableAfterExit);
        }
        finally
        {
            foot.TickRecorded -= Capture;
            fixture.Skeleton.GlobalTransform = originalTransform;
        }
    }

    private static void WriteLifecycleEvidence(string name, object values)
    {
        string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(
            "res://temp/fixed-delta-acceleration/implementation-v10/ci01-fix/lifecycle-evidence"));
        File.WriteAllText(Path.Combine(directory, name + ".json"), System.Text.Json.JsonSerializer.Serialize(values));
    }

    private static Skeleton3D CreateSkeleton()
    {
        var skeleton = new Skeleton3D();
        foreach (string side in new[] { "Left", "Right" })
        {
            int first = skeleton.GetBoneCount();
            foreach (string bone in new[] { "UpperLeg", "LowerLeg", "Foot", "Toes" })
            {
                _ = skeleton.AddBone(side + bone);
            }

            skeleton.SetBoneRest(first, new Transform3D(Basis.Identity, new Vector3(side == "Left" ? -0.2f : 0.2f, 1f, 0f)));
            for (int offset = 1; offset < 4; offset++)
            {
                skeleton.SetBoneParent(first + offset, first + offset - 1);
                skeleton.SetBoneRest(first + offset, new Transform3D(Basis.Identity,
                    offset == 3 ? new Vector3(0f, 0f, -0.1f) : new Vector3(0f, -0.4f, 0f)));
            }
        }

        return skeleton;
    }

    private static void WriteEvidence(string name, IEnumerable<string> rows)
    {
        string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(
            "res://temp/fixed-delta-acceleration/implementation-v10/clock-implementation/engine-evidence"));
        File.WriteAllLines(Path.Combine(directory, name + ".csv"), rows);
    }

    private sealed partial class ConstantContact : PlantedStateSource
    {
        public override bool TryGetPlantedState(out PlantedFeetState state)
        {
            state = new PlantedFeetState(true, true);
            return true;
        }
    }

}
