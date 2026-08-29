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
/// Runtime contracts for periodic re-examination of the active look subject: interval cadence without catch-up,
/// in-flight skipping, live-subject guards, stop conditions, and interval validation (AI-006 TR-31).
/// </summary>
[Headless]
public sealed class PollingActiveLookPerceptionIntegrationTests
{
    /// <summary>
    /// Polling runs at most once per frame at the configured interval without delayed-frame catch-up, restarts the
    /// interval from each poll, stops on clear, replacement, and exit, and commits durable poll observations
    /// through the owning Mind (AI-006 TR-31/34).
    /// </summary>
    [Fact]
    public async Task Polling_RunsOncePerFrameAtIntervalWithoutCatchUp_AndStopsOnClearReplacementAndExit()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var firstSubject = new TestSubject("first");
        var firstCue = new StaticVisualCue();
        firstSubject.AddChild(firstCue);
        var secondSubject = new TestSubject("second");
        var secondCue = new StaticVisualCue();
        secondSubject.AddChild(secondCue);
        root.AddChild(firstSubject);
        root.AddChild(secondSubject);
        var mind = new TestMind(new TestCharacter());
        mind.SetSceneContextLoaderForTesting(() => TestSceneContext.Instance);
        var perception = new TestPollingPerception { PollIntervalSeconds = 0.1d };
        mind.AddChild(perception);
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);
        perception.SetProcess(false);

        try
        {
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, firstCue), CreateContext(), CancellationToken.None);

            perception._Process(0.05d);
            Assert.Equal(0, perception.PollCount);
            perception._Process(0.05d);
            Assert.Equal(1, perception.PollCount);
            perception._Process(0.2d);
            Assert.Equal(2, perception.PollCount);
            perception._Process(0.05d);
            Assert.Equal(2, perception.PollCount);

            await perception.PerceiveAsync(
                new LookTargetChangedPercept(firstCue, null), CreateContext(), CancellationToken.None);
            perception._Process(0.5d);
            Assert.Equal(2, perception.PollCount);

            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, secondCue), CreateContext(), CancellationToken.None);
            perception._Process(0.05d);
            Assert.Equal(2, perception.PollCount);
            perception._Process(0.2d);
            Assert.Equal(3, perception.PollCount);
            Assert.Equal(["test:first", "test:first", "test:second"], perception.PolledSubjectIds);

            mind.RemoveChild(perception);
            perception._Process(0.5d);
            Assert.Equal(3, perception.PollCount);

            await mind.DrainPerceptionsForTestingAsync();
            Assert.Equal(
                ["test:first", "test:first", "test:second"],
                mind.Timeline.OfType<TestPollObservation>().Select(observation => observation.SubjectId));
            perception.QueueFree();
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// A poll in flight skips later frames, and clearing the look target cancels the activation so the in-flight
    /// poll observes cancellation and polling stops (AI-006 TR-31).
    /// </summary>
    [Fact]
    public async Task Polling_SkipsWhileAPollIsInFlight_AndSurfacesActivationCancellationToTheInFlightPoll()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var subject = new TestSubject("subject");
        var cue = new StaticVisualCue();
        subject.AddChild(cue);
        root.AddChild(subject);
        var perception = new TestPollingPerception { PollIntervalSeconds = 0.1d };
        root.AddChild(perception);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);
        perception.SetProcess(false);
        List<string> outcomes = [];

        try
        {
            var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            perception.PollHandler = async (_, _, cancellationToken) =>
            {
                try
                {
                    await firstRelease.Task.WaitAsync(cancellationToken);
                    outcomes.Add("completed");
                }
                catch (OperationCanceledException)
                {
                    outcomes.Add("cancelled");
                }
                finally
                {
                    _ = firstSettled.TrySetResult();
                }
            };

            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, cue), CreateContext(), CancellationToken.None);
            perception._Process(0.2d);
            Assert.Equal(1, perception.PollCount);
            perception._Process(0.2d);
            perception._Process(0.2d);
            Assert.Equal(1, perception.PollCount);

            _ = firstRelease.TrySetResult();
            await firstSettled.Task;
            Assert.Equal(["completed"], outcomes);

            var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            perception.PollHandler = async (_, _, cancellationToken) =>
            {
                try
                {
                    await secondRelease.Task.WaitAsync(cancellationToken);
                    outcomes.Add("completed");
                }
                catch (OperationCanceledException)
                {
                    outcomes.Add("cancelled");
                }
                finally
                {
                    _ = secondSettled.TrySetResult();
                }
            };

            perception._Process(0.2d);
            Assert.Equal(2, perception.PollCount);
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(cue, null), CreateContext(), CancellationToken.None);
            await secondSettled.Task;
            Assert.Equal(["completed", "cancelled"], outcomes);

            perception._Process(0.2d);
            Assert.Equal(2, perception.PollCount);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>Polling continues only while the active cue keeps a live, attached subject ancestor.</summary>
    [Fact]
    public async Task Polling_PollsOnlyWhileTheCueKeepsALiveAttachedSubject()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var subject = new TestSubject("subject");
        var cue = new StaticVisualCue();
        subject.AddChild(cue);
        root.AddChild(subject);
        var perception = new TestPollingPerception { PollIntervalSeconds = 0.1d };
        root.AddChild(perception);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);
        perception.SetProcess(false);

        try
        {
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, cue), CreateContext(), CancellationToken.None);
            perception._Process(0.2d);
            Assert.Equal(1, perception.PollCount);

            cue.Reparent(root);
            perception._Process(0.2d);
            Assert.Equal(1, perception.PollCount);

            cue.Reparent(subject);
            perception._Process(0.2d);
            Assert.Equal(2, perception.PollCount);

            cue.Free();
            perception._Process(0.2d);
            Assert.Equal(2, perception.PollCount);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>A non-finite or non-positive poll interval fails clearly when a subject attaches, before polling.</summary>
    [Fact]
    public async Task Polling_InvalidInterval_FailsClearlyOnSubjectAttach()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var subject = new TestSubject("subject");
        var cue = new StaticVisualCue();
        subject.AddChild(cue);
        root.AddChild(subject);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 1);

        try
        {
            double[] invalidIntervals = [0d, -1d, double.NaN, double.PositiveInfinity];
            foreach (double invalidInterval in invalidIntervals)
            {
                var perception = new TestPollingPerception { PollIntervalSeconds = invalidInterval };
                InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => perception.PerceiveAsync(
                        new LookTargetChangedPercept(null, cue), CreateContext(), CancellationToken.None).AsTask());
                Assert.Contains("PollIntervalSeconds", failure.Message);

                perception._Process(1d);
                Assert.Equal(0, perception.PollCount);
                perception.Free();
            }
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    private static PerceptionContext CreateContext() => new(new TestCharacter(), TestSceneContext.Instance);

    private static void AddToTree(SceneTree tree, Node node) => (tree.CurrentScene ?? tree.Root).AddChild(node);

    private sealed partial class TestSubject(string id) : Node3D, IVisualSubject
    {
        public string Id { get; set; } = id;
        public string Type => "test";
        public IReadOnlyList<VisualCue> VisualCues { get; set; } = [];
    }

    private sealed class TestCharacter : ICharacter
    {
        public string Id { get; set; } = "observer";
        public IReadOnlyList<IComponent> Components { get; } = [];
        public IReadOnlyList<VisualCue> VisualCues { get; } = [];
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

    /// <summary>Polling faculty emitting one durable observation per completed poll for Mind-commit assertions.</summary>
    private sealed partial class TestPollingPerception : PollingActiveLookPerception
    {
        public List<string> PolledSubjectIds { get; } = [];

        public int PollCount => PolledSubjectIds.Count;

        public Func<IVisualSubject, PerceptionContext, CancellationToken, ValueTask>? PollHandler
        {
            get; set;
        }

        protected override ValueTask PollActiveSubjectAsync(
            IVisualSubject subject,
            PerceptionContext context,
            CancellationToken cancellationToken)
        {
            PolledSubjectIds.Add(subject.FullId);
            Emit(new TestPollObservation(subject.FullId));
            return PollHandler?.Invoke(subject, context, cancellationToken) ?? ValueTask.CompletedTask;
        }
    }

    private sealed record TestPollObservation(string SubjectId) : AgentObservation
    {
        public override string TypeKey => "poll.test";
        public override float CalculateImportance(ObservationContext context) => 0f;
    }
}
