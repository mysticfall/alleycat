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
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.Perception;

/// <summary>Focused runtime coverage for transition-driven visual-description interpretation.</summary>
[Headless]
public sealed class VisualDescriptionPerceptionIntegrationTests
{
    /// <summary>An Eyes transition reaches the composed Mind faculty and commits one focused description.</summary>
    [Fact]
    public async Task EyesToMindTransition_CommitsOneFocusedDescriptionEndToEnd()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var eyes = new EyesBehaviour { VisualSurveyIntervalSeconds = 30d };
        var owner = new EndToEndCharacter(eyes);
        var mind = new EndToEndMind(owner);
        mind.AddChild(new VisualSurveyPerception());
        mind.AddChild(new VisualDescriptionPerception());
        owner.AddChild(eyes);
        owner.AddChild(mind);
        root.AddChild(owner);
        var subject = new TestSubject("subject");
        var cue = new TestCue((_, _) => ValueTask.FromResult("Focused description."));
        subject.AddChild(cue);
        subject.VisualCues = [cue];
        root.AddChild(subject);
        mind.SetSceneContextLoaderForTesting(() => new EndToEndSceneContext(owner));
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            eyes.SetLookTarget(cue);
            await mind.DrainPerceptionsForTestingAsync();

            ObservedVisualDescription observation = Assert.IsType<ObservedVisualDescription>(Assert.Single(mind.Retained));
            Assert.Equal("test:subject", observation.SubjectId);
            Assert.Equal("Focused description.", observation.Description);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>A valid cue receives the exact scene/observer context and emits its associated canonical subject.</summary>
    [Fact]
    public async Task ValidCue_DescribesWithContextAndEmitsOneCanonicalObservation()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var subject = new TestSubject("subject");
        var cue = new TestCue((scene, observer) =>
        {
            Assert.Same(TestSceneContext.Instance, scene);
            Assert.Same(TestCharacter.Instance, observer);
            return ValueTask.FromResult("A weathered red coat.");
        });
        subject.AddChild(cue);
        subject.VisualCues = [cue];
        root.AddChild(subject);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 1);

        try
        {
            var perception = new VisualDescriptionPerception();
            List<Observation> emissions = [];
            perception.Observed += emissions.Add;

            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, cue),
                CreateContext(),
                CancellationToken.None);

            ObservedVisualDescription observation = Assert.IsType<ObservedVisualDescription>(Assert.Single(emissions));
            Assert.Equal("test:subject", observation.SubjectId);
            Assert.Equal("A weathered red coat.", observation.Description);
            Assert.Empty(observation.GetAttentionEffects(new ObservationContext(TestCharacter.Instance)));
            Assert.Same(cue, perception.ActiveCue);
            Assert.Same(subject, perception.ActiveSubject);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>Clear and unusable cues update independent state without producing observations.</summary>
    [Fact]
    public async Task ClearFreedAndUnassociatedCues_ReturnNoObservationsAndKeepFacultyStateIsolated()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var unassociated = new TestCue((_, _) => ValueTask.FromResult("must not run"));
        var freed = new TestCue((_, _) => ValueTask.FromResult("must not run"));
        root.AddChild(unassociated);
        root.AddChild(freed);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 1);
        freed.Free();

        try
        {
            var first = new VisualDescriptionPerception();
            var second = new VisualDescriptionPerception();
            List<Observation> firstEmissions = [];
            List<Observation> secondEmissions = [];
            first.Observed += firstEmissions.Add;
            second.Observed += secondEmissions.Add;

            await first.PerceiveAsync(
                new LookTargetChangedPercept(null, unassociated), CreateContext(), CancellationToken.None);
            await second.PerceiveAsync(
                new LookTargetChangedPercept(null, freed), CreateContext(), CancellationToken.None);
            await first.PerceiveAsync(
                new LookTargetChangedPercept(unassociated, null), CreateContext(), CancellationToken.None);

            Assert.Empty(firstEmissions);
            Assert.Empty(secondEmissions);
            Assert.Null(first.ActiveCue);
            Assert.Null(first.ActiveSubject);
            Assert.Same(freed, second.ActiveCue);
            Assert.Null(second.ActiveSubject);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// Reparenting during Describe invalidates association and cancellation prevents a completed emission; neither
    /// route commits an observation through the owning Mind (AI-006 TR-5).
    /// </summary>
    [Fact]
    public async Task ReparentingAndCancellationDuringDescribe_ProduceNoCommittedObservation()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var original = new TestSubject("original");
        var replacement = new TestSubject("replacement");
        TestCue? reparentingCue = null;
        reparentingCue = new TestCue((_, _) =>
        {
            reparentingCue!.Reparent(replacement);
            return ValueTask.FromResult("stale association");
        });
        original.AddChild(reparentingCue);
        original.VisualCues = [reparentingCue];
        root.AddChild(original);
        root.AddChild(replacement);

        var completion = new TaskCompletionSource<string>();
        var delayedCue = new TestCue((_, _) => new ValueTask<string>(completion.Task));
        replacement.AddChild(delayedCue);
        replacement.VisualCues = [delayedCue];
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 1);

        try
        {
            var perception = new VisualDescriptionPerception();
            List<Observation> emissions = [];
            perception.Observed += emissions.Add;
            var mind = new EndToEndMind(TestCharacter.Instance);
            mind.AddChild(perception);
            mind.SetSceneContextLoaderForTesting(() => TestSceneContext.Instance);
            root.AddChild(mind);
            await TestUtils.WaitForFramesAsync(tree, 2);

            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, reparentingCue), CreateContext(), CancellationToken.None);
            await mind.DrainPerceptionsForTestingAsync();
            Assert.Empty(emissions);
            Assert.Empty(mind.Timeline);

            using var cancellation = new CancellationTokenSource();
            Task pending = perception.PerceiveAsync(
                new LookTargetChangedPercept(reparentingCue, delayedCue), CreateContext(), cancellation.Token).AsTask();
            cancellation.Cancel();
            completion.SetResult("cancelled description");
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await mind.DrainPerceptionsForTestingAsync();
            Assert.Empty(emissions);
            Assert.Empty(mind.Timeline);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    private static PerceptionContext CreateContext() => new(TestCharacter.Instance, TestSceneContext.Instance);

    private static void AddToTree(SceneTree tree, Node node) => (tree.CurrentScene ?? tree.Root).AddChild(node);

    private sealed partial class TestCue(
        Func<ISceneContext, IHasVision, ValueTask<string>> describe) : VisualCue
    {
        public override ValueTask<string> Describe(ISceneContext scene, IHasVision observer) => describe(scene, observer);
    }

    private sealed partial class TestSubject(string id) : Node3D, IVisualSubject
    {
        public string Id { get; set; } = id;
        public string Type => "test";
        public IReadOnlyList<VisualCue> VisualCues { get; set; } = [];
    }

    private sealed class TestCharacter : ICharacter
    {
        public static TestCharacter Instance { get; } = new();
        public string Id { get; set; } = "observer";
        public IReadOnlyList<IComponent> Components { get; } = [];
        public IReadOnlyList<VisualCue> VisualCues { get; } = [];
        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

    private sealed class TestSceneContext : ISceneContext
    {
        public static TestSceneContext Instance { get; } = new();
        public ICharacter Player => TestCharacter.Instance;
        public IReadOnlyCollection<ICharacter> Characters => [TestCharacter.Instance];
        public ContentContext Content => ContentContext.Default;
        public IIdentifiable? Find(string fullId) => null;
        public IIdentifiable Resolve(string fullId) => throw new InvalidOperationException();
    }

    private sealed partial class EndToEndCharacter(EyesBehaviour eyes) : Node3D, ICharacter
    {
        public string Id { get; set; } = "observer";
        public IReadOnlyList<IComponent> Components { get; } = [eyes];
        public IReadOnlyList<VisualCue> VisualCues { get; } = [];
    }

    private sealed partial class EndToEndMind(ICharacter owner) : MindBase
    {
        public IReadOnlyList<Observation> Timeline => GetObservationTimelineSnapshot();
        public IReadOnlyList<Observation> Retained =>
            [.. GetRetainedObservationSnapshot().Select(static entry => entry.Payload)];
        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    private sealed class EndToEndSceneContext(ICharacter owner) : ISceneContext
    {
        public ICharacter Player => owner;
        public IReadOnlyCollection<ICharacter> Characters => [owner];
        public ContentContext Content => ContentContext.Default;
        public IIdentifiable? Find(string fullId) => null;
        public IIdentifiable Resolve(string fullId) => throw new InvalidOperationException();
    }
}
