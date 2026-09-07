using System.Reflection;
using AlleyCat.Control;
using AlleyCat.IntegrationTests.Control;
using AlleyCat.Interaction;
using AlleyCat.Interaction.Hands;
using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using AlleyCat.XR.Mock;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.XR;

/// <summary>
/// Acceptance coverage for instance-exact candidate references.
/// Synthetic component rigs, mutable candidate nodes and calibrated source injection are reused through a bounded
/// reflection adapter; lifecycle, resource sampling, coordinator scheduling and animation registration are production.
/// No assets are modified. Cases run sequentially within each method; run this class alone.
/// </summary>
public sealed class OpticalGrabReferenceIdentityIntegrationTests
{
    private const string Directory = "res://assets/characters/reference/female/animations/";

    /// <summary>Instance sampling versus path-only derivation, plus actual tree resource and post-modifier bone output.</summary>
    [Headless]
    [Fact]
    public async Task ReferenceIdentity_PathlessAndSameNameResourcesAgreeAcrossRecognitionPresentationAndPlayback()
    {
        List<string> traces = [];
        bool pathlessAccepted;
        SceneTree tree = GetSceneTree();
        Adapter coordinator = await Adapter.CreateAsync(tree, arbitration: false);
        try
        {
            Animation original = ResourceLoader.Load<Animation>(Directory + "Grab-ball-40.tres");
            using var copy = (Animation)original.Duplicate();
            Assert.True(AuthoredHandPoseReferenceSampler.TrySample(copy, LimbSide.Right, out AuthoredHandPoseSideReference sampled, out string error), error);
            Assert.Empty(copy.ResourcePath);
            GrabbableNode candidate = coordinator.AddBall();
            Node point = candidate.GetChildren().Single(child => child.GetType().GetProperty("Animation") is not null);
            point.GetType().GetProperty("Animation")!.SetValue(point, copy);
            await WaitForPhysicsFramesAsync(tree, 12);
            Assert.True(coordinator.Coordinator.TryGetOpticalGripMeasurement(LimbSide.Right, out OpticalGripMeasurement measurement));
            Assert.Same(copy, measurement.ReferenceAnimation);
            pathlessAccepted = measurement.RejectionCategory != OpticalGrabEvaluationReason.ProfileUnavailable;
            traces.Add($"file control accepted; original={Identity(original)}; pathless={Identity(copy)}; sampler=True; firstBone={sampled.Poses[0]}; coordinator={pathlessAccepted}; scheduledMeasurement={measurement}");
        }
        finally
        {
            await coordinator.DisposeAsync(tree);
        }

        bool collisionAgrees = true;
        foreach (bool sameName in new[] { false, true })
        {
            Adapter fixture = await Adapter.CreateAsync(tree, arbitration: true);
            try
            {
                using var a = (Animation)ResourceLoader.Load<Animation>(Directory + "Grab-pipe-10.tres").Duplicate();
                using var b = (Animation)ResourceLoader.Load<Animation>(Directory + "Grab-ball-40.tres").Duplicate();
                a.ResourceName = "DiagnosticPoseA";
                b.ResourceName = sameName ? a.ResourceName : "DiagnosticPoseB";
                Assert.True(AuthoredHandPoseReferenceSampler.TrySample(a, LimbSide.Right, out AuthoredHandPoseSideReference referenceA, out string error), error);
                Assert.True(AuthoredHandPoseReferenceSampler.TrySample(b, LimbSide.Right, out AuthoredHandPoseSideReference referenceB, out error), error);
                Quaternion[] posesA = referenceA.Poses.ToArray();
                Quaternion[] posesB = referenceB.Poses.ToArray();
                int index = Enumerable.Range(0, posesA.Length).MaxBy(i => posesA[i].AngleTo(posesB[i]));
                traces.Add($"sampledBone={AuthoredHandPoseSideReference.GetCanonicalBoneName(LimbSide.Right, XRHandJoints.DestinationJoints[index])}");
                Assert.True(posesA[index].AngleTo(posesB[index]) > 0.1f);
                GrabbableNode ball = fixture.AddBall();
                Node point = ball.GetChildren().Single(child => child.GetType().GetProperty("Animation") is not null);
                point.GetType().GetProperty("Animation")!.SetValue(point, b);
                fixture.Hand.SetPose(a, 1f, immediate: true);
                await WaitForFramesAsync(tree, 3);
                _ = fixture.Hand.BeginGrab(HandGrabInputSource.Optical);
                Assert.Equal(HandGrabLifecycleState.Pending, fixture.Hand.GrabLifecycle);
                OpticalGrabHandPresentation pending = fixture.Get<XRManager>("XRManager").OpticalGrabArbiter
                    .GetPresentation(LimbSide.Right);
                Assert.Same(b, pending.GrabReference?.Animation);
                fixture.Hand.SetPose(b, 1f, immediate: true);
                await WaitForFramesAsync(tree, 2);
                AnimationTree animationTree = fixture.Get<AnimationTree>("AnimationTree");
                var node = (AnimationNodeAnimation)((AnimationNodeBlendTree)animationTree.TreeRoot)
                    .GetNode(HandPoseAnimationTreePaths.RightHandPoseNode);
                AnimationPlayer player = animationTree.GetNode<AnimationPlayer>(animationTree.AnimPlayer);
                Animation played = player.GetAnimation(node.Animation);
                bool agrees = ReferenceEquals(played, b) && ReferenceEquals(fixture.Hand.CurrentPose, b);
                traces.Add($"sameName={sameName}; A={Identity(a)}; B={Identity(b)}; pendingReference={Identity(pending.GrabReference!.Animation)}; current={Identity(fixture.Hand.CurrentPose!)}; treeKey={node.Animation}; played={Identity(played)}; boneIndex={index}; Akey={posesA[index]}; Bkey={posesB[index]}; agrees={agrees}");
                if (sameName)
                {
                    collisionAgrees = agrees;
                }
                else
                {
                    Assert.True(agrees, string.Join("\n", traces));
                }
            }
            finally
            {
                await fixture.DisposeAsync(tree);
            }
        }

        Assert.True(pathlessAccepted && collisionAgrees, string.Join("\n", traces));
    }

    /// <summary>CTRL-002 TR14–15: genuine resolver failure must cancel pending, preserve held, and not permit late commit.</summary>
    [Headless]
    [Fact]
    public async Task DependencyLoss_CancelsPendingAndPreservesHeld()
    {
        SceneTree tree = GetSceneTree();
        List<string> traces = [];
        bool valid = true;
        foreach ((bool runtimeLoss, bool modeSwitch) in new[] { (false, false), (true, false), (false, true) })
        {
            foreach (bool held in new[] { false, true })
            {
                Adapter fixture = await Adapter.CreateAsync(tree, arbitration: false);
                try
                {
                    _ = fixture.AddBall();
                    object calibration = fixture.Call("CalibrateRight", Directory + "Grab-ball-40.tres")!;
                    await (Task)Adapter.ControlType.GetMethod("BeginPendingGrabAsync", BindingFlags.NonPublic | BindingFlags.Static)!
                        .Invoke(null, [tree, fixture.Inner, LimbSide.Right, calibration])!;
                    if (held)
                    {
                        await fixture.CommitAsync(tree);
                    }

                    HandGrabLifecycleState before = fixture.Hand.GrabLifecycle;
                    int evaluations = 0;
                    fixture.Coordinator.OpticalGrabEvaluated += _ => evaluations++;
                    if (runtimeLoss)
                    {
                        // Synthetic service availability loss through the manager's runtime property, not coordinator fields.
                        // Keep the runtime node alive so hand processing and cleanup can safely continue.
                        object manager = fixture.Get<XRManager>("XRManager");
                        typeof(XRManager).GetProperty("Runtime")!.SetValue(manager, null);
                    }
                    else
                    {
                        // Free, rather than merely detach: production accepts cached instances while still valid.
                        fixture.Get<OpticalFingerTrackingModifier>("Modifier").Free();
                    }

                    await WaitForPhysicsFramesAsync(tree, 12);
                    bool pipelineReady = (bool)typeof(HandGrabInputCoordinator).GetField("_pipelineReady", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .GetValue(fixture.Coordinator)!;
                    Assert.False(pipelineReady, "Dependency removal must genuinely fail production resolution.");
                    Assert.Equal(0, evaluations);
                    HandGrabLifecycleState afterLoss = fixture.Hand.GrabLifecycle;
                    if (modeSwitch)
                    {
                        _ = fixture.Get<MockXRRuntimeNode>("Runtime").SetHandObservations(
                            XRHandSourceObservation.Controller, XRHandSourceObservation.Controller);
                    }
                    else
                    {
                        // Move only the synthetic wrist/attachment, never call hand._Process or the commit implementation.
                        fixture.Settle();
                    }

                    await WaitForFramesAsync(tree, 5);
                    HandGrabLifecycleState afterSettle = fixture.Hand.GrabLifecycle;
                    bool expected = modeSwitch ? afterSettle == HandGrabLifecycleState.None : held
                        ? afterLoss == HandGrabLifecycleState.Held && afterSettle == HandGrabLifecycleState.Held
                        : afterLoss == HandGrabLifecycleState.None && afterSettle == HandGrabLifecycleState.None;
                    valid &= expected;
                    bool measurement = fixture.Coordinator.TryGetOpticalGripMeasurement(LimbSide.Right, out OpticalGripMeasurement last);
                    traces.Add($"loss={(runtimeLoss ? "runtime" : "modifier")}; modeSwitch={modeSwitch}; before={before}; pipelineReady={pipelineReady}; evaluationEvents={evaluations}; after12Physics={afterLoss}; afterAction5Process={afterSettle}; presentation={fixture.Get<XRManager>("XRManager").OpticalGrabArbiter.GetPresentation(LimbSide.Right).State}; measurementRetained={measurement}; measurement={last}; expected={expected}");
                    if (runtimeLoss)
                    {
                        object manager = fixture.Get<XRManager>("XRManager");
                        typeof(XRManager).GetProperty("Runtime")!.SetValue(manager, fixture.Get<Node>("Runtime"));
                    }
                }
                finally
                {
                    await fixture.DisposeAsync(tree);
                }
            }
        }

        Assert.True(valid, string.Join("\n", traces));
    }

    private static string Identity(Animation animation) => $"{animation.GetInstanceId()}:'{animation.ResourceName}'@'{animation.ResourcePath}'";
    private sealed class Adapter(object inner, bool arbitration)
    {
        public static readonly Type ControlType = typeof(HandGrabInputCoordinatorIntegrationTests);
        public object Inner => inner;
        public HandPoseBehaviour Hand => Get<HandPoseBehaviour>("RightHand");
        public HandGrabInputCoordinator Coordinator => Get<HandGrabInputCoordinator>("Coordinator");
        public T Get<T>(string name) => (T)inner.GetType().GetProperty(name)!.GetValue(inner)!;
        public object? Call(string name, params object[] arguments) => inner.GetType().GetMethod(name)!.Invoke(inner, arguments);
        public GrabbableNode AddBall() => (GrabbableNode)Call("AddRightBall")!;
        public Quaternion[] Capture() => (Quaternion[])Call("CaptureFingerRotations", LimbSide.Right)!;
        public Task DisposeAsync(SceneTree tree) => (Task)Call("DisposeAsync", tree)!;
        public void Settle()
        {
            _ = arbitration
                ? Call("SettleHandTarget", LimbSide.Right)
                : ControlType.GetMethod("SettleAndCommit", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [inner, LimbSide.Right]);
        }

        public async Task CommitAsync(SceneTree tree)
        {
            for (int frame = 0; frame < 60 && Hand.GrabLifecycle != HandGrabLifecycleState.Held; frame++)
            {
                Settle();
                await WaitForNextFrameAsync(tree);
            }

            Assert.Equal(HandGrabLifecycleState.Held, Hand.GrabLifecycle);
        }

        public static async Task<Adapter> CreateAsync(SceneTree tree, bool arbitration)
        {
            Type owner = arbitration ? typeof(OpticalGrabPoseArbitrationIntegrationTests) : ControlType;
            Type type = owner.GetNestedType(arbitration ? "ArbitrationFixture" : "CoordinatorFixture", BindingFlags.NonPublic)!;
            var task = (Task)type.GetMethod("CreateAsync")!.Invoke(null, [tree])!;
            await task;
            Adapter result = new(task.GetType().GetProperty("Result")!.GetValue(task)!, arbitration);
            result.Hand.GrabCommitDistanceMetres = 0.008f;
            return result;
        }
    }
}
