using System.Reflection;
using AlleyCat.Interaction;
using AlleyCat.Interaction.Hands;
using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.XR;

/// <summary>
/// CTRL-002 / XR-002 ownership regressions using the real hand lifecycle and modifier arbitration fixture. The
/// reflection adapter only reuses its focused world-space hand-target/bone placement helper.
/// </summary>
public sealed class OpticalGrabOwnershipReadinessIntegrationTests
{
    private const string AnimationDirectory = "res://assets/characters/reference/female/animations/";
    /// <summary>Negative controls for no removal and an unrelated opposite-side teardown.</summary>
    [Headless]
    [Fact]
    public async Task Controls_NoRemovalAndOppositeSideRemoval_PreservePublisher()
    {
        foreach (bool held in new[] { false, true })
        {
            await ObserveRemovalAsync(held, remove: false, LimbSide.Right);
            await ObserveRemovalAsync(held, remove: true, LimbSide.Left);
        }
    }

    /// <summary>
    /// An idle same-side hand's real tree teardown cannot revoke a player's pending or held publication, change the
    /// player's grab lifecycle, or affect item parenting.
    /// </summary>
    [Headless]
    [Fact]
    public async Task UnrelatedSameSideTeardown_PreservesPlayerPendingAndHeldPublisher()
    {
        await ObserveRemovalAsync(held: false, remove: true, LimbSide.Right);
        await ObserveRemovalAsync(held: true, remove: true, LimbSide.Right);
    }

    /// <summary>
    /// A fresh optical attempt replaces the same hand's prior publication; the prior owner token cannot revoke the
    /// replacement. Cancelling the current pending attempt still releases its own publication through the ordinary
    /// source-exit path.
    /// </summary>
    [Headless]
    [Fact]
    public async Task RegrabReplacement_RejectsStaleOwnerClearAndCurrentSourceExitStillClears()
    {
        SceneTree sceneTree = GetSceneTree();
        Fixture fixture = await Fixture.CreateAsync(sceneTree);
        try
        {
            _ = fixture.AddBall();
            _ = fixture.Hand.BeginGrab(HandGrabInputSource.Optical);
            OpticalGrabPresentationOwner firstOwner = Assert.IsType<OpticalGrabPresentationOwner>(fixture.CurrentPresentation.Owner);

            Assert.True(fixture.Hand.CancelPendingGrab(), "Expected the first optical pending attempt to cancel normally.");
            Assert.Equal(OpticalGrabPresentationState.Tracking, fixture.Presentation);

            _ = fixture.Hand.BeginGrab(HandGrabInputSource.Optical);
            OpticalGrabHandPresentation replacement = fixture.CurrentPresentation;
            OpticalGrabPresentationOwner replacementOwner = Assert.IsType<OpticalGrabPresentationOwner>(replacement.Owner);
            Assert.NotEqual(firstOwner, replacementOwner);

            Assert.False(
                fixture.XRManager.OpticalGrabArbiter.TryClearOpticalGrab(firstOwner),
                "A stale publication owner must not revoke its hand's replacement attempt.");
            Assert.Equal(OpticalGrabPresentationState.PendingAssistance, fixture.Presentation);
            Assert.Equal(replacementOwner, fixture.CurrentPresentation.Owner);

            Assert.True(fixture.Hand.CancelPendingGrab(), "Expected the current pending source-exit path to cancel.");
            Assert.Equal(OpticalGrabPresentationState.Tracking, fixture.Presentation);
            Assert.Null(fixture.CurrentPresentation.Owner);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>Explicit release restores item parenting and clears only the releasing hand's held publication.</summary>
    [Headless]
    [Fact]
    public async Task OwnerRelease_ClearsHeldPublicationAndRestoresItemParenting()
    {
        SceneTree sceneTree = GetSceneTree();
        Fixture fixture = await Fixture.CreateAsync(sceneTree);
        try
        {
            GrabbableNode ball = fixture.AddBall();
            _ = fixture.Hand.BeginGrab(HandGrabInputSource.Optical);
            await fixture.CommitAsync(sceneTree);
            Assert.Equal(OpticalGrabPresentationState.Held, fixture.Presentation);

            fixture.Hand.Release();

            Assert.Equal(HandGrabLifecycleState.None, fixture.Hand.GrabLifecycle);
            Assert.Null(fixture.Hand.CurrentGrabbed);
            Assert.Same(fixture.Root, ball.GetParent());
            Assert.Equal(OpticalGrabPresentationState.Tracking, fixture.Presentation);
            Assert.Null(fixture.CurrentPresentation.Owner);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Owner teardown is component-scoped because player and NPC scenes inherit the same base hand behaviour. Both
    /// pending and held owners must release their own publication when Godot dispatches the real <c>_ExitTree</c>.
    /// </summary>
    [Headless]
    [Fact]
    public async Task OwnerTreeTeardown_ClearsItsPendingAndHeldPublication()
    {
        foreach (bool held in new[] { false, true })
        {
            SceneTree sceneTree = GetSceneTree();
            Fixture fixture = await Fixture.CreateAsync(sceneTree);
            try
            {
                _ = fixture.AddBall();
                _ = fixture.Hand.BeginGrab(HandGrabInputSource.Optical);
                if (held)
                {
                    await fixture.CommitAsync(sceneTree);
                }

                Assert.Equal(
                    held ? OpticalGrabPresentationState.Held : OpticalGrabPresentationState.PendingAssistance,
                    fixture.Presentation);
                Node parent = fixture.Hand.GetParent() ?? throw new InvalidOperationException("Expected the player hand to be parented.");
                parent.RemoveChild(fixture.Hand);
                fixture.Hand.Free();
                await WaitForNextFrameAsync(sceneTree);

                Assert.Equal(OpticalGrabPresentationState.Tracking, fixture.Presentation);
                Assert.Null(fixture.CurrentPresentation.Owner);
            }
            finally
            {
                await fixture.DisposeAsync(sceneTree);
            }
        }
    }

    /// <summary>Reference-female NPC scenes inherit the same component path covered by the teardown regression.</summary>
    [Headless]
    [Fact]
    public void ReferenceFemaleNpc_InheritsSharedHandPoseBehaviourPath()
    {
        PackedScene scene = ResourceLoader.Load<PackedScene>("res://assets/characters/templates/reference_female/reference_female_npc.tscn");
        Node npc = scene.Instantiate();
        try
        {
            _ = Assert.IsType<HandPoseBehaviour>(npc.GetNode("Hands/RightHand"), exactMatch: false);
            _ = Assert.IsType<HandPoseBehaviour>(npc.GetNode("Hands/LeftHand"), exactMatch: false);
        }
        finally
        {
            npc.Free();
        }
    }

    private static async Task ObserveRemovalAsync(bool held, bool remove, LimbSide idleSide)
    {
        SceneTree sceneTree = GetSceneTree();
        Fixture fixture = await Fixture.CreateAsync(sceneTree);
        try
        {
            GrabbableNode ball = fixture.AddBall();
            HandPoseBehaviour idle = new()
            {
                Name = "UnrelatedIdleHandDiagnostic",
                Side = idleSide
            };
            fixture.Root.AddChild(idle);
            Assert.Equal(HandGrabLifecycleState.None, idle.GrabLifecycle);
            Assert.Null(idle.CurrentGrabbed);
            _ = fixture.Hand.BeginGrab(HandGrabInputSource.Optical);
            Assert.Equal(HandGrabLifecycleState.Pending, fixture.Hand.GrabLifecycle);
            if (held)
            {
                await fixture.CommitAsync(sceneTree);
            }

            await WaitForSecondsAsync(sceneTree, 0.5);
            OpticalGrabPresentationState expected = held ? OpticalGrabPresentationState.Held : OpticalGrabPresentationState.PendingAssistance;
            Assert.Equal(expected, fixture.Presentation);
            OpticalGrabPoseBlendPhase before = fixture.Phase;
            Assert.Equal(held ? OpticalGrabPoseBlendPhase.HeldSuppressed : OpticalGrabPoseBlendPhase.PendingAssistance, before);
            if (remove)
            {
                // Actual Godot removal dispatches the real production _ExitTree; never call arbiter.Clear.
                fixture.Root.RemoveChild(idle);
                idle.Free();
            }

            OpticalGrabPresentationState immediate = fixture.Presentation;
            List<string> sequence = [$"before={expected}/{before}; immediate={immediate}"];
            for (int frame = 1; frame <= 40; frame++)
            {
                await WaitForNextFrameAsync(sceneTree);
                sequence.Add($"f{frame}={fixture.Presentation}/{fixture.Phase}");
                Assert.Equal(held ? HandGrabLifecycleState.Held : HandGrabLifecycleState.Pending, fixture.Hand.GrabLifecycle);
                if (held)
                {
                    Assert.Same(ball, fixture.Hand.CurrentGrabbed);
                    Assert.Same(fixture.Hand.HandBoneAttachment, ball.GetParent());
                }
                else
                {
                    Assert.Null(fixture.Hand.CurrentGrabbed);
                    Assert.Same(fixture.Root, ball.GetParent());
                }
            }

            string evidence = $"held={held}, remove={remove}, idleSide={idleSide}, lifecycle={fixture.Hand.GrabLifecycle}, " + string.Join("; ", sequence);
            if (remove && idleSide == LimbSide.Right)
            {
                Assert.True(immediate == expected && fixture.Presentation == expected && fixture.Phase == before, evidence);
            }
            else
            {
                Assert.True(immediate == expected && fixture.Presentation == expected && fixture.Phase == before, evidence);
            }
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// The modifier retains authored assistance until the actual AnimationTree has the current reference instance
    /// at the intended weight, then makes the hand zero-write suppressed.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Handoff_WaitsForCurrentReferenceAndEvaluatedTreeReadiness()
    {
        List<string> violations = [];
        await ObserveReadinessAsync(0.2f, 1f, existingPose: false, rapidRegrab: false, sameNameCollision: false, violations);
        await ObserveReadinessAsync(0.2f, 1f, existingPose: true, rapidRegrab: false, sameNameCollision: false, violations);
        await ObserveReadinessAsync(0.45f, 0.65f, existingPose: true, rapidRegrab: false, sameNameCollision: false, violations);
        await ObserveReadinessAsync(0.2f, 1f, existingPose: true, rapidRegrab: true, sameNameCollision: false, violations);
        await ObserveReadinessAsync(0.45f, 0.65f, existingPose: true, rapidRegrab: false, sameNameCollision: true, violations);
        Assert.Empty(violations);
    }

    private static async Task ObserveReadinessAsync(
        float duration,
        float weight,
        bool existingPose,
        bool rapidRegrab,
        bool sameNameCollision,
        List<string> violations)
    {
        SceneTree sceneTree = GetSceneTree();
        Fixture fixture = await Fixture.CreateAsync(sceneTree);
        try
        {
            Animation target;
            Animation? previous;
            if (sameNameCollision)
            {
                previous = (Animation)ResourceLoader.Load<Animation>(AnimationDirectory + "Grab-pipe-10.tres").Duplicate();
                target = (Animation)ResourceLoader.Load<Animation>(AnimationDirectory + "Grab-ball-40.tres").Duplicate();
                previous.ResourceName = "ReadinessIdentityCollision";
                target.ResourceName = previous.ResourceName;
                Assert.NotSame(previous, target);
                Assert.Equal(previous.ResourceName, target.ResourceName);
            }
            else
            {
                previous = ResourceLoader.Load<Animation>(AnimationDirectory + "Grab-pipe-10.tres");
                target = ResourceLoader.Load<Animation>(AnimationDirectory + "Grab-ball-40.tres");
            }

            GrabbableNode ball = fixture.AddBall();
            Node grabPoint = ball.GetChildren().Single(child => child.GetType().GetProperty("Animation") is not null);
            grabPoint.GetType().GetProperty("Animation")!.SetValue(grabPoint, target);
            fixture.Hand.TransitionDuration = duration;
            if (existingPose)
            {
                fixture.Hand.SetPose(previous, weight, immediate: true);
                await WaitForFramesAsync(sceneTree, 3);
                Assert.Same(previous, fixture.Hand.CurrentPose);
                Assert.Equal(weight, fixture.Weight, 4);
            }

            if (rapidRegrab)
            {
                _ = fixture.Hand.BeginGrab(HandGrabInputSource.Optical);
                await fixture.CommitAsync(sceneTree);
                fixture.Hand.Release();
                await WaitForNextFrameAsync(sceneTree);
            }

            _ = fixture.Hand.BeginGrab(HandGrabInputSource.Optical);
            await fixture.CommitAsync(sceneTree);
            List<string> sequence = [];
            bool readySeen = false;
            bool suppressedAfterReadiness = false;
            double elapsed = 0;
            for (int frame = 0; frame < 300; frame++)
            {
                bool identityReady = ReferenceEquals(target, fixture.Hand.CurrentPose);
                bool treeIdentityReady = ReferenceEquals(target, fixture.TreeReference);
                bool ready = identityReady && treeIdentityReady && MathF.Abs(fixture.Weight - weight) < 0.001f;
                bool suppressed = fixture.Phase == OpticalGrabPoseBlendPhase.HeldSuppressed;
                readySeen |= ready;
                suppressedAfterReadiness |= suppressed && ready;
                sequence.Add($"f{frame}@{elapsed:F4}s={fixture.Phase},targetIdentity={identityReady},treeIdentity={treeIdentityReady},w={fixture.Weight:F4},ready={ready}");
                Assert.Equal(HandGrabLifecycleState.Held, fixture.Hand.GrabLifecycle);
                if (suppressed && !ready)
                {
                    violations.Add($"suppressed-before-ready: {string.Join("; ", sequence)}");
                    break;
                }

                if (suppressedAfterReadiness && frame > 2)
                {
                    break;
                }

                await WaitForNextFrameAsync(sceneTree);
                elapsed += fixture.Hand.GetProcessDeltaTime();
            }

            Assert.True(readySeen, "Fixture never reached the requested pose/weight: " + string.Join("; ", sequence));
            Assert.True(suppressedAfterReadiness, "Modifier never relinquished writes after tree readiness: " + string.Join("; ", sequence));
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>Focused adapter over the established component fixture.</summary>
    private sealed class Fixture(object inner)
    {
        private static readonly Type _fixtureType = typeof(OpticalGrabPoseArbitrationIntegrationTests)
            .GetNestedType("ArbitrationFixture", BindingFlags.NonPublic)!;

        public Node Root => Get<Node>("Root");
        public XRManager XRManager => Get<XRManager>("XRManager");
        public HandPoseBehaviour Hand => Get<HandPoseBehaviour>("RightHand");
        public OpticalFingerTrackingModifier Modifier => Get<OpticalFingerTrackingModifier>("Modifier");
        private AnimationTree Tree => Get<AnimationTree>("AnimationTree");
        public float Weight => Tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Right)).AsSingle();
        public Animation TreeReference
        {
            get
            {
                var poseNode = (AnimationNodeAnimation)((AnimationNodeBlendTree)Tree.TreeRoot)
                    .GetNode(HandPoseAnimationTreePaths.RightHandPoseNode);
                AnimationPlayer player = Tree.GetNode<AnimationPlayer>(Tree.AnimPlayer);
                return player.GetAnimation(poseNode.Animation);
            }
        }
        public OpticalGrabHandPresentation CurrentPresentation => Get<XRManager>("XRManager").OpticalGrabArbiter.GetPresentation(LimbSide.Right);
        public OpticalGrabPresentationState Presentation => CurrentPresentation.State;
        public OpticalGrabPoseBlendPhase Phase => Modifier.GetGrabPoseBlendPhase(LimbSide.Right);

        private T Get<T>(string name) => (T)_fixtureType.GetProperty(name)!.GetValue(inner)!;
        private object? Call(string name, params object[] arguments) => _fixtureType.GetMethod(name)!.Invoke(inner, arguments);
        public GrabbableNode AddBall() => (GrabbableNode)Call("AddRightBall")!;
        public Task DisposeAsync(SceneTree sceneTree) => (Task)Call("DisposeAsync", sceneTree)!;

        public static async Task<Fixture> CreateAsync(SceneTree sceneTree)
        {
            var task = (Task)_fixtureType.GetMethod("CreateAsync")!.Invoke(null, [sceneTree])!;
            await task;
            Fixture fixture = new(task.GetType().GetProperty("Result")!.GetValue(task)!);
            // Restore the required 8 mm gate rather than the reused fixture's relaxed 20 mm value.
            // Angular tolerance and two-process-frame stability remain production defaults.
            fixture.Hand.GrabCommitDistanceMetres = 0.008f;
            return fixture;
        }

        public async Task CommitAsync(SceneTree sceneTree)
        {
            for (int frame = 0; frame < 60 && Hand.GrabLifecycle != HandGrabLifecycleState.Held; frame++)
            {
                _ = Call("SettleHandTarget", LimbSide.Right);
                await WaitForNextFrameAsync(sceneTree);
            }

            Assert.Equal(HandGrabLifecycleState.Held, Hand.GrabLifecycle);
        }
    }
}
