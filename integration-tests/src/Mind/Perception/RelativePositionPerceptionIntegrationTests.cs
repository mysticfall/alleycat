using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.Observation;
using AlleyCat.Mind.Perception;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.Perception;

/// <summary>
/// Runtime contracts for relative-position perception of the active look subject: immediate attach-time sampling,
/// material-change-only emissions, reciprocal ground-plane direction classification, per-subject suppression state,
/// tunable validation, and durable Mind commits (AI-006 TR-46..TR-52).
/// </summary>
[Headless]
public sealed class RelativePositionPerceptionIntegrationTests
{
    /// <summary>
    /// The attach-time sample emits on the first process frame after a look-target percept without waiting for the
    /// poll interval, and later frames before the interval emit nothing further (AI-006 TR-46/47).
    /// </summary>
    [Fact]
    public async Task ImmediateSample_EmitsOnFirstFrameAfterAttach_WithoutWaitingForTheInterval()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var observer = new ObserverCharacter();
        var subject = new TestSubject("subject");
        var cue = new StaticVisualCue();
        subject.AddChild(cue);
        root.AddChild(observer);
        root.AddChild(subject);
        var perception = new RelativePositionPerception { PollIntervalSeconds = 60d };
        root.AddChild(perception);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);
        perception.SetProcess(false);
        List<ObservedRelativePosition> emissions = CollectEmissions(perception);

        try
        {
            subject.GlobalPosition = new Vector3(0f, 0f, -2f);
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, cue), CreateContext(observer), CancellationToken.None);

            perception._Process(0.016d);
            ObservedRelativePosition observation = Assert.Single(emissions);
            Assert.Equal("test:subject", observation.SubjectId);
            Assert.Equal(2f, observation.Distance, 5);
            Assert.Equal(RelativeDirection.Front, observation.SubjectDirection);
            Assert.Equal(RelativeDirection.Back, observation.ObserverDirection);

            perception._Process(0.016d);
            perception._Process(0.016d);
            _ = Assert.Single(emissions);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// In-tree <see cref="Node"/> providers need only implement <see cref="ISpatial"/>: neither participant needs
    /// to derive from <see cref="Node3D"/> for the faculty to sample their trait transforms (AI-006 TR-48/51/52;
    /// VISION-001 TR-44).
    /// </summary>
    [Fact]
    public async Task NodeBackedSpatialTraits_NotDerivedFromNode3D_EmitTheCorrectObservation()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var observer = new NodeSpatialObserver();
        var subject = new NodeSpatialSubject("subject")
        {
            GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0f, 0f, -2f)),
        };
        var cue = new StaticVisualCue();
        subject.AddChild(cue);
        root.AddChild(observer);
        root.AddChild(subject);
        var perception = new RelativePositionPerception { PollIntervalSeconds = 60d };
        root.AddChild(perception);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);
        perception.SetProcess(false);
        List<ObservedRelativePosition> emissions = CollectEmissions(perception);

        try
        {
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, cue), CreateContext(observer), CancellationToken.None);

            perception._Process(0.016d);

            ObservedRelativePosition observation = Assert.Single(emissions);
            Assert.Equal("test:subject", observation.SubjectId);
            Assert.Equal(2f, observation.Distance, 5);
            Assert.Equal(RelativeDirection.Front, observation.SubjectDirection);
            Assert.Equal(RelativeDirection.Back, observation.ObserverDirection);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// An otherwise valid node-backed observer cannot be sampled once it leaves the tree (AI-006 TR-48).
    /// </summary>
    [Fact]
    public async Task NodeBackedSpatialProvider_OutOfTree_ProducesNoObservation()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var observer = new NodeSpatialObserver();
        var subject = new NodeSpatialSubject("subject")
        {
            GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0f, 0f, -2f)),
        };
        var cue = new StaticVisualCue();
        subject.AddChild(cue);
        root.AddChild(observer);
        root.AddChild(subject);
        var perception = new RelativePositionPerception { PollIntervalSeconds = 60d };
        root.AddChild(perception);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);
        perception.SetProcess(false);
        List<ObservedRelativePosition> emissions = CollectEmissions(perception);

        try
        {
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, cue), CreateContext(observer), CancellationToken.None);
            root.RemoveChild(observer);

            perception._Process(0.016d);

            Assert.Empty(emissions);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// A disposed Godot spatial provider is rejected before its transform can be dereferenced (AI-006 TR-48).
    /// </summary>
    [Fact]
    public async Task NodeBackedSpatialProvider_Disposed_ProducesNoObservation()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var observer = new NodeSpatialObserver();
        var subject = new NodeSpatialSubject("subject")
        {
            GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0f, 0f, -2f)),
        };
        var cue = new StaticVisualCue();
        subject.AddChild(cue);
        root.AddChild(observer);
        root.AddChild(subject);
        var perception = new RelativePositionPerception { PollIntervalSeconds = 60d };
        root.AddChild(perception);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);
        perception.SetProcess(false);
        List<ObservedRelativePosition> emissions = CollectEmissions(perception);

        try
        {
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, cue), CreateContext(observer), CancellationToken.None);
            observer.Free();

            perception._Process(0.016d);

            Assert.Empty(emissions);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// Periodic re-examination suppresses sub-threshold distance drift relative to the last emitted snapshot while
    /// emitting exactly once when the drift reaches <c>MinimumDistanceChange</c> (AI-006 TR-46/49).
    /// </summary>
    [Fact]
    public async Task Polling_SubThresholdDriftDoesNotEmit_AndThresholdCrossingEmitsOnce()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var observer = new ObserverCharacter();
        var subject = new TestSubject("subject");
        var cue = new StaticVisualCue();
        subject.AddChild(cue);
        root.AddChild(observer);
        root.AddChild(subject);
        var perception = new RelativePositionPerception
        {
            PollIntervalSeconds = 0.1d,
            MinimumDistanceChange = 0.5f,
        };
        root.AddChild(perception);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);
        perception.SetProcess(false);
        List<ObservedRelativePosition> emissions = CollectEmissions(perception);

        try
        {
            subject.GlobalPosition = new Vector3(0f, 0f, -2f);
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, cue), CreateContext(observer), CancellationToken.None);

            perception._Process(0.2d);
            _ = Assert.Single(emissions);
            Assert.Equal(2f, emissions[0].Distance, 5);

            subject.GlobalPosition = new Vector3(0f, 0f, -2.3f);
            perception._Process(0.2d);
            _ = Assert.Single(emissions);

            subject.GlobalPosition = new Vector3(0f, 0f, -2.8f);
            perception._Process(0.2d);
            Assert.Equal(2, emissions.Count);
            Assert.Equal(2.8f, emissions[1].Distance, 3);
            Assert.Equal(RelativeDirection.Front, emissions[1].SubjectDirection);
            Assert.Equal(RelativeDirection.Back, emissions[1].ObserverDirection);

            subject.GlobalPosition = new Vector3(0f, 0f, -2.9f);
            perception._Process(0.2d);
            Assert.Equal(2, emissions.Count);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// Subject motion crossing the front-to-left boundary emits both reciprocal direction changes even when the
    /// distance drift is immaterial (AI-006 TR-49/51).
    /// </summary>
    [Fact]
    public async Task DirectionChange_SubjectCrossingFromFrontToLeft_EmitsReciprocalDirections()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var observer = new ObserverCharacter();
        var subject = new TestSubject("subject");
        var cue = new StaticVisualCue();
        subject.AddChild(cue);
        root.AddChild(observer);
        root.AddChild(subject);
        var perception = new RelativePositionPerception
        {
            PollIntervalSeconds = 0.1d,
            MinimumDistanceChange = 10f,
        };
        root.AddChild(perception);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);
        perception.SetProcess(false);
        List<ObservedRelativePosition> emissions = CollectEmissions(perception);

        try
        {
            subject.GlobalPosition = new Vector3(0f, 0f, -2f);
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, cue), CreateContext(observer), CancellationToken.None);

            perception._Process(0.2d);
            _ = Assert.Single(emissions);
            Assert.Equal(RelativeDirection.Front, emissions[0].SubjectDirection);
            Assert.Equal(RelativeDirection.Back, emissions[0].ObserverDirection);

            subject.GlobalPosition = new Vector3(-2f, 0f, -0.1f);
            perception._Process(0.2d);
            Assert.Equal(2, emissions.Count);
            Assert.Equal(RelativeDirection.Left, emissions[1].SubjectDirection);
            Assert.Equal(RelativeDirection.Right, emissions[1].ObserverDirection);
            Assert.Equal(2.002f, emissions[1].Distance, 3);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// Observer rotation and subject rotation change the reciprocal direction classifications even though the
    /// distance never changes, and both count as material changes (AI-006 TR-49).
    /// </summary>
    [Fact]
    public async Task DirectionChange_ObserverAndSubjectRotation_EmitReciprocalDirectionChanges()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var observer = new ObserverCharacter();
        var subject = new TestSubject("subject");
        var cue = new StaticVisualCue();
        subject.AddChild(cue);
        root.AddChild(observer);
        root.AddChild(subject);
        var perception = new RelativePositionPerception
        {
            PollIntervalSeconds = 0.1d,
            MinimumDistanceChange = 10f,
        };
        root.AddChild(perception);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);
        perception.SetProcess(false);
        List<ObservedRelativePosition> emissions = CollectEmissions(perception);

        try
        {
            subject.GlobalPosition = new Vector3(0f, 0f, -2f);
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, cue), CreateContext(observer), CancellationToken.None);

            perception._Process(0.2d);
            _ = Assert.Single(emissions);
            Assert.Equal(RelativeDirection.Front, emissions[0].SubjectDirection);
            Assert.Equal(RelativeDirection.Back, emissions[0].ObserverDirection);

            observer.GlobalTransform = new Transform3D(Basis.Identity.Rotated(Vector3.Up, Mathf.Pi), Vector3.Zero);
            perception._Process(0.2d);
            Assert.Equal(2, emissions.Count);
            Assert.Equal(RelativeDirection.Back, emissions[1].SubjectDirection);
            Assert.Equal(RelativeDirection.Back, emissions[1].ObserverDirection);
            Assert.Equal(2f, emissions[1].Distance, 5);

            subject.GlobalTransform = new Transform3D(
                Basis.Identity.Rotated(Vector3.Up, Mathf.Pi), new Vector3(0f, 0f, -2f));
            perception._Process(0.2d);
            Assert.Equal(3, emissions.Count);
            Assert.Equal(RelativeDirection.Back, emissions[2].SubjectDirection);
            Assert.Equal(RelativeDirection.Front, emissions[2].ObserverDirection);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// Observer translation changing only the distance emits exactly like subject motion, demonstrating that observer
    /// motion counts identically to subject motion (AI-006 TR-49).
    /// </summary>
    [Fact]
    public async Task DistanceChange_FromObserverTranslation_EmitsUpdatedDistance()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var observer = new ObserverCharacter();
        var subject = new TestSubject("subject");
        var cue = new StaticVisualCue();
        subject.AddChild(cue);
        root.AddChild(observer);
        root.AddChild(subject);
        var perception = new RelativePositionPerception
        {
            PollIntervalSeconds = 0.1d,
            MinimumDistanceChange = 0.5f,
        };
        root.AddChild(perception);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);
        perception.SetProcess(false);
        List<ObservedRelativePosition> emissions = CollectEmissions(perception);

        try
        {
            subject.GlobalPosition = new Vector3(0f, 0f, -2f);
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, cue), CreateContext(observer), CancellationToken.None);

            perception._Process(0.2d);
            _ = Assert.Single(emissions);

            observer.GlobalPosition = new Vector3(0f, 0f, -1f);
            perception._Process(0.2d);
            Assert.Equal(2, emissions.Count);
            Assert.Equal(1f, emissions[1].Distance, 5);
            Assert.Equal(RelativeDirection.Front, emissions[1].SubjectDirection);
            Assert.Equal(RelativeDirection.Back, emissions[1].ObserverDirection);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// Zero horizontal separation — a subject directly above the observer — classifies deterministically as Front for
    /// both reciprocal directions (AI-006 TR-51).
    /// </summary>
    [Fact]
    public async Task ZeroHorizontalSeparation_SubjectDirectlyAbove_ClassifiesAsFront()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var observer = new ObserverCharacter();
        var subject = new TestSubject("subject");
        var cue = new StaticVisualCue();
        subject.AddChild(cue);
        root.AddChild(observer);
        root.AddChild(subject);
        var perception = new RelativePositionPerception { PollIntervalSeconds = 60d };
        root.AddChild(perception);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);
        perception.SetProcess(false);
        List<ObservedRelativePosition> emissions = CollectEmissions(perception);

        try
        {
            subject.GlobalPosition = new Vector3(0f, 3f, 0f);
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, cue), CreateContext(observer), CancellationToken.None);

            perception._Process(0.016d);
            ObservedRelativePosition observation = Assert.Single(emissions);
            Assert.Equal(3f, observation.Distance, 5);
            Assert.Equal(RelativeDirection.Front, observation.SubjectDirection);
            Assert.Equal(RelativeDirection.Front, observation.ObserverDirection);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// Re-focusing an immaterially moved subject creates no duplicate memory while a materially moved re-focus emits
    /// immediately on the first frame, and cleared focus performs no sampling at all (AI-006 TR-47/49).
    /// </summary>
    [Fact]
    public async Task RefocusSuppression_ImmaterialRefocusEmitsNothing_MaterialRefocusEmitsImmediately()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var observer = new ObserverCharacter();
        var subject = new TestSubject("subject");
        var cue = new StaticVisualCue();
        subject.AddChild(cue);
        root.AddChild(observer);
        root.AddChild(subject);
        var perception = new RelativePositionPerception
        {
            PollIntervalSeconds = 60d,
            MinimumDistanceChange = 0.5f,
        };
        root.AddChild(perception);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);
        perception.SetProcess(false);
        List<ObservedRelativePosition> emissions = CollectEmissions(perception);

        try
        {
            subject.GlobalPosition = new Vector3(0f, 0f, -2f);
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, cue), CreateContext(observer), CancellationToken.None);
            perception._Process(0.016d);
            _ = Assert.Single(emissions);

            await perception.PerceiveAsync(
                new LookTargetChangedPercept(cue, null), CreateContext(observer), CancellationToken.None);
            perception._Process(0.016d);
            _ = Assert.Single(emissions);

            subject.GlobalPosition = new Vector3(0f, 0f, -2.2f);
            perception._Process(0.016d);
            _ = Assert.Single(emissions);

            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, cue), CreateContext(observer), CancellationToken.None);
            perception._Process(0.016d);
            _ = Assert.Single(emissions);

            await perception.PerceiveAsync(
                new LookTargetChangedPercept(cue, null), CreateContext(observer), CancellationToken.None);
            subject.GlobalPosition = new Vector3(0f, 0f, -3f);
            perception._Process(0.016d);
            _ = Assert.Single(emissions);

            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, cue), CreateContext(observer), CancellationToken.None);
            perception._Process(0.016d);
            Assert.Equal(2, emissions.Count);
            Assert.Equal(3f, emissions[1].Distance, 5);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// Emission-suppression state is retained per canonical subject FullId, so interleaved focus cycles between two
    /// subjects compare against each subject's own last emitted state (AI-006 TR-49).
    /// </summary>
    [Fact]
    public async Task SubjectState_IsIndependentPerSubject()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var observer = new ObserverCharacter();
        var firstSubject = new TestSubject("first");
        var firstCue = new StaticVisualCue();
        firstSubject.AddChild(firstCue);
        var secondSubject = new TestSubject("second");
        var secondCue = new StaticVisualCue();
        secondSubject.AddChild(secondCue);
        root.AddChild(observer);
        root.AddChild(firstSubject);
        root.AddChild(secondSubject);
        var perception = new RelativePositionPerception
        {
            PollIntervalSeconds = 60d,
            MinimumDistanceChange = 0.5f,
        };
        root.AddChild(perception);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);
        perception.SetProcess(false);
        List<ObservedRelativePosition> emissions = CollectEmissions(perception);

        try
        {
            firstSubject.GlobalPosition = new Vector3(0f, 0f, -2f);
            secondSubject.GlobalPosition = new Vector3(0f, 0f, -5f);
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, firstCue), CreateContext(observer), CancellationToken.None);
            perception._Process(0.016d);
            _ = Assert.Single(emissions);
            Assert.Equal("test:first", emissions[0].SubjectId);
            Assert.Equal(2f, emissions[0].Distance, 5);

            await perception.PerceiveAsync(
                new LookTargetChangedPercept(firstCue, secondCue), CreateContext(observer), CancellationToken.None);
            perception._Process(0.016d);
            Assert.Equal(2, emissions.Count);
            Assert.Equal("test:second", emissions[1].SubjectId);
            Assert.Equal(5f, emissions[1].Distance, 5);

            firstSubject.GlobalPosition = new Vector3(0f, 0f, -2.2f);
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(secondCue, firstCue), CreateContext(observer), CancellationToken.None);
            perception._Process(0.016d);
            Assert.Equal(2, emissions.Count);

            secondSubject.GlobalPosition = new Vector3(0f, 0f, -6f);
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(firstCue, secondCue), CreateContext(observer), CancellationToken.None);
            perception._Process(0.016d);
            Assert.Equal(3, emissions.Count);
            Assert.Equal("test:second", emissions[2].SubjectId);
            Assert.Equal(6f, emissions[2].Distance, 5);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// Clearing focus, replacing the subject, and tree exit stop sampling without stale emissions, while a
    /// replacement subject is sampled immediately (AI-006 TR-47).
    /// </summary>
    [Fact]
    public async Task ClearAndReplacement_StopSamplingWithoutStaleEmissions()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var observer = new ObserverCharacter();
        var firstSubject = new TestSubject("first");
        var firstCue = new StaticVisualCue();
        firstSubject.AddChild(firstCue);
        var secondSubject = new TestSubject("second");
        var secondCue = new StaticVisualCue();
        secondSubject.AddChild(secondCue);
        root.AddChild(observer);
        root.AddChild(firstSubject);
        root.AddChild(secondSubject);
        var perception = new RelativePositionPerception
        {
            PollIntervalSeconds = 0.1d,
            MinimumDistanceChange = 0.5f,
        };
        root.AddChild(perception);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);
        perception.SetProcess(false);
        List<ObservedRelativePosition> emissions = CollectEmissions(perception);

        try
        {
            firstSubject.GlobalPosition = new Vector3(0f, 0f, -2f);
            secondSubject.GlobalPosition = new Vector3(0f, 0f, -4f);
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, firstCue), CreateContext(observer), CancellationToken.None);
            perception._Process(0.2d);
            _ = Assert.Single(emissions);
            Assert.Equal("test:first", emissions[0].SubjectId);

            await perception.PerceiveAsync(
                new LookTargetChangedPercept(firstCue, null), CreateContext(observer), CancellationToken.None);
            firstSubject.GlobalPosition = new Vector3(0f, 0f, -5f);
            perception._Process(0.5d);
            perception._Process(0.5d);
            _ = Assert.Single(emissions);

            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, secondCue), CreateContext(observer), CancellationToken.None);
            perception._Process(0.2d);
            Assert.Equal(2, emissions.Count);
            Assert.Equal("test:second", emissions[1].SubjectId);
            Assert.Equal(4f, emissions[1].Distance, 5);

            root.RemoveChild(perception);
            secondSubject.GlobalPosition = new Vector3(0f, 0f, -6f);
            perception._Process(0.5d);
            Assert.Equal(2, emissions.Count);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// Invalid distance and angular tunables fail clearly when a subject attaches, before any sampling runs
    /// (AI-006 TR-50).
    /// </summary>
    [Fact]
    public async Task InvalidTunables_FailClearlyOnSubjectAttach_BeforeSampling()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var observer = new ObserverCharacter();
        var subject = new TestSubject("subject");
        var cue = new StaticVisualCue();
        subject.AddChild(cue);
        root.AddChild(observer);
        root.AddChild(subject);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 1);

        (Action<RelativePositionPerception> Configure, string ExpectedFragment)[] cases =
        [
            (p => p.MinimumDistanceChange = -0.1f, "MinimumDistanceChange"),
            (p => p.MinimumDistanceChange = float.NaN, "MinimumDistanceChange"),
            (p => p.MinimumDistanceChange = float.PositiveInfinity, "MinimumDistanceChange"),
            (p => p.FrontAngleThresholdDegrees = -10f, "FrontAngleThresholdDegrees"),
            (p => p.FrontAngleThresholdDegrees = float.NaN, "FrontAngleThresholdDegrees"),
            (p =>
                {
                    p.FrontAngleThresholdDegrees = 90f;
                    p.BackAngleThresholdDegrees = 60f;
                },
                "FrontAngleThresholdDegrees"),
            (p => p.BackAngleThresholdDegrees = 200f, "BackAngleThresholdDegrees"),
        ];

        try
        {
            subject.GlobalPosition = new Vector3(0f, 0f, -2f);
            foreach ((Action<RelativePositionPerception> configure, string expectedFragment) in cases)
            {
                var perception = new RelativePositionPerception();
                configure(perception);
                List<ObservedRelativePosition> emissions = CollectEmissions(perception);

                InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => perception.PerceiveAsync(
                        new LookTargetChangedPercept(null, cue), CreateContext(observer), CancellationToken.None)
                        .AsTask());
                Assert.Contains(expectedFragment, failure.Message);

                perception._Process(1d);
                Assert.Empty(emissions);
                perception.Free();
            }
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// Emissions commit through the owning Mind as durable timeline entries with the exact type key
    /// <c>vision.relative_position</c> (AI-006 TR-46).
    /// </summary>
    [Fact]
    public async Task Emissions_CommitThroughMind_AsDurableRelativePositionObservations()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var observer = new ObserverCharacter();
        var subject = new TestSubject("subject");
        var cue = new StaticVisualCue();
        subject.AddChild(cue);
        var mind = new TestMind(new TestCharacter());
        mind.SetSceneContextLoaderForTesting(() => TestSceneContext.Instance);
        var perception = new RelativePositionPerception
        {
            PollIntervalSeconds = 0.1d,
            MinimumDistanceChange = 0.5f,
        };
        mind.AddChild(perception);
        root.AddChild(observer);
        root.AddChild(subject);
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);
        perception.SetProcess(false);

        try
        {
            subject.GlobalPosition = new Vector3(0f, 0f, -2f);
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, cue), CreateContext(observer), CancellationToken.None);
            perception._Process(0.2d);

            subject.GlobalPosition = new Vector3(0f, 0f, -3f);
            perception._Process(0.2d);

            await mind.DrainPerceptionsForTestingAsync();
            ObservedRelativePosition[] committed = [.. mind.Timeline.OfType<ObservedRelativePosition>()];
            Assert.Equal(2, committed.Length);
            Assert.All(committed, observation => Assert.Equal("vision.relative_position", observation.TypeKey));
            Assert.Equal(ObservedRelativePosition.TypeKeyValue, "vision.relative_position");
            Assert.Equal(["test:subject", "test:subject"], committed.Select(observation => observation.SubjectId));
            Assert.Equal(2f, committed[0].Distance, 5);
            Assert.Equal(3f, committed[1].Distance, 5);
            Assert.All(
                committed,
                observation =>
                {
                    Assert.Equal(RelativeDirection.Front, observation.SubjectDirection);
                    Assert.Equal(RelativeDirection.Back, observation.ObserverDirection);
                });
            perception.QueueFree();
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    private static PerceptionContext CreateContext(ICharacter observer) => new(observer, TestSceneContext.Instance);

    private static List<ObservedRelativePosition> CollectEmissions(PerceptionNode perception)
    {
        List<ObservedRelativePosition> emissions = [];
        perception.Observed += observation =>
        {
            if (observation is ObservedRelativePosition position)
            {
                emissions.Add(position);
            }
        };

        return emissions;
    }

    private static void AddToTree(SceneTree tree, Node node) => (tree.CurrentScene ?? tree.Root).AddChild(node);

    private sealed partial class TestSubject(string id) : Node3D, IVisualSubject
    {
        public string Id { get; set; } = id;
        public string Type => "test";
        public IReadOnlyList<VisualCue> VisualCues { get; set; } = [];
    }

    private sealed partial class NodeSpatialSubject(string id) : Node, IVisualSubject
    {
        public string Id { get; set; } = id;
        public string Type => "test";
        public IReadOnlyList<VisualCue> VisualCues { get; set; } = [];
        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

    /// <summary>An observing character backed by a real Node3D transform so perception geometry resolves in-tree.</summary>
    private sealed partial class ObserverCharacter : Node3D, ICharacter
    {
        public string Id { get; set; } = "observer";
        public IReadOnlyList<IComponent> Components { get; } = [];
        public IReadOnlyList<VisualCue> VisualCues { get; } = [];
    }

    private sealed partial class NodeSpatialObserver : Node, ICharacter
    {
        public string Id { get; set; } = "observer";
        public IReadOnlyList<IComponent> Components { get; } = [];
        public IReadOnlyList<VisualCue> VisualCues { get; } = [];
        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

    private sealed class TestCharacter : ICharacter
    {
        public string Id { get; set; } = "observer";
        public IReadOnlyList<IComponent> Components { get; } = [];
        public IReadOnlyList<VisualCue> VisualCues { get; } = [];
        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

    private sealed class TestSceneContext : ISceneContext
    {
        public static TestSceneContext Instance { get; } = new();
        public ICharacter Player => new TestCharacter();
        public IReadOnlyCollection<ICharacter> Characters => [];
        public ContentContext Content => ContentContext.Default;
        public IIdentifiable? Find(string fullId) => null;
        public IIdentifiable Resolve(string fullId) => throw new InvalidOperationException();
    }

    private sealed partial class TestMind(ICharacter owner) : MindBase
    {
        public IReadOnlyList<AgentObservation> Timeline => GetObservationTimelineSnapshot();
        protected override ICharacter ResolveOwningCharacter() => owner;
    }
}
