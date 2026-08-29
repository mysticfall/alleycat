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
/// Runtime contracts for the reusable active-look faculty state: nearest-subject resolution, attach/detach hook
/// ordering, cue lifetime semantics, and stale-emission protection across look transitions (AI-006 TR-30/32).
/// </summary>
[Headless]
public sealed class ActiveLookPerceptionIntegrationTests
{
    /// <summary>
    /// ActiveSubject resolves the nearest IVisualSubject ancestor, the detach hook fires before replacement state
    /// publishes, and the attach hook fires after it (AI-006 TR-30).
    /// </summary>
    [Fact]
    public async Task ActiveSubject_ResolvesNearestAncestorWithDetachBeforeAndAttachAfterStatePublish()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var outer = new TestSubject("outer");
        var inner = new TestSubject("inner");
        var innerCue = new StaticVisualCue();
        inner.AddChild(innerCue);
        outer.AddChild(inner);
        var other = new TestSubject("other");
        var otherCue = new StaticVisualCue();
        other.AddChild(otherCue);
        root.AddChild(outer);
        root.AddChild(other);
        var perception = new RecordingLookPerception();
        root.AddChild(perception);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, innerCue), CreateContext(), CancellationToken.None);

            Assert.Same(innerCue, perception.ActiveCue);
            Assert.Same(inner, perception.ActiveSubject);
            Assert.Equal(["attach:test:inner"], perception.Events);
            (VisualCue? attachCue, IVisualSubject? attachSubject) = Assert.Single(perception.AttachedStates);
            Assert.Same(innerCue, attachCue);
            Assert.Same(inner, attachSubject);

            await perception.PerceiveAsync(
                new LookTargetChangedPercept(innerCue, otherCue), CreateContext(), CancellationToken.None);

            Assert.Same(otherCue, perception.ActiveCue);
            Assert.Same(other, perception.ActiveSubject);
            Assert.Equal(["attach:test:inner", "detach:test:inner", "attach:test:other"], perception.Events);
            (VisualCue? detachCue, IVisualSubject? detachSubject) = Assert.Single(perception.DetachedStates);
            Assert.Same(innerCue, detachCue);
            Assert.Same(inner, detachSubject);
            (VisualCue? replacementCue, IVisualSubject? replacementSubject) = perception.AttachedStates[^1];
            Assert.Same(otherCue, replacementCue);
            Assert.Same(other, replacementSubject);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// Attach and detach hooks fire on clear and node exit, freed cues retain ActiveCue without resolving a
    /// subject, and each transition bumps the activation generation (AI-006 TR-30/31).
    /// </summary>
    [Fact]
    public async Task ClearAndNodeExit_FireDetachHooksClearStateAndAdvanceActivationGeneration()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var holder = new TestSubject("holder");
        var liveCue = new StaticVisualCue();
        holder.AddChild(liveCue);
        var freedCue = new StaticVisualCue();
        holder.AddChild(freedCue);
        root.AddChild(holder);
        var perception = new RecordingLookPerception();
        root.AddChild(perception);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);
        freedCue.Free();

        try
        {
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, liveCue), CreateContext(), CancellationToken.None);
            Assert.Same(holder, perception.ActiveSubject);
            int generationAtActivation = perception.GenerationForTest;

            // A freed cue is retained as ActiveCue but resolves no subject and attaches nothing.
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(liveCue, freedCue), CreateContext(), CancellationToken.None);
            Assert.Same(freedCue, perception.ActiveCue);
            Assert.Null(perception.ActiveSubject);
            Assert.Equal(["attach:test:holder", "detach:test:holder"], perception.Events);
            Assert.True(perception.GenerationForTest > generationAtActivation);

            await perception.PerceiveAsync(
                new LookTargetChangedPercept(freedCue, liveCue), CreateContext(), CancellationToken.None);
            Assert.Same(holder, perception.ActiveSubject);

            // Clearing fires the detach hook and clears the published state.
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(liveCue, null), CreateContext(), CancellationToken.None);
            Assert.Null(perception.ActiveCue);
            Assert.Null(perception.ActiveSubject);
            Assert.Equal(
                ["attach:test:holder", "detach:test:holder", "attach:test:holder", "detach:test:holder"],
                perception.Events);

            // Node exit detaches the active subject, clears all state, and ends the activation.
            await perception.PerceiveAsync(
                new LookTargetChangedPercept(null, liveCue), CreateContext(), CancellationToken.None);
            int generationBeforeExit = perception.GenerationForTest;
            root.RemoveChild(perception);

            Assert.Null(perception.ActiveCue);
            Assert.Null(perception.ActiveSubject);
            Assert.Equal(
                ["attach:test:holder", "detach:test:holder", "attach:test:holder", "detach:test:holder",
                    "attach:test:holder", "detach:test:holder"],
                perception.Events);
            Assert.True(perception.GenerationForTest > generationBeforeExit);
            perception.QueueFree();
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// A newer look transition completing before an older asynchronous description's emission cancels the stale
    /// activation so the stale emission never commits through Mind (AI-006 UR-11, TR-32).
    /// </summary>
    [Fact]
    public async Task NewerTransition_CompletingBeforeOlderDescription_StaleEmissionNeverCommits()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var staleSubject = new TestSubject("stale");
        var freshSubject = new TestSubject("fresh");
        var describeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var describeCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleCue = new TestCue((_, _) =>
        {
            _ = describeStarted.TrySetResult();
            return new ValueTask<string>(describeCompletion.Task);
        });
        var freshCue = new TestCue((_, _) => ValueTask.FromResult("fresh description"));
        staleSubject.AddChild(staleCue);
        freshSubject.AddChild(freshCue);
        root.AddChild(staleSubject);
        root.AddChild(freshSubject);
        var mind = new TestMind(TestCharacter.Instance);
        var perception = new VisualDescriptionPerception();
        mind.AddChild(perception);
        mind.SetSceneContextLoaderForTesting(() => TestSceneContext.Instance);
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            Task stale = perception.PerceiveAsync(
                new LookTargetChangedPercept(null, staleCue), CreateContext(), CancellationToken.None).AsTask();
            await describeStarted.Task;

            await perception.PerceiveAsync(
                new LookTargetChangedPercept(staleCue, freshCue), CreateContext(), CancellationToken.None);
            await mind.DrainPerceptionsForTestingAsync();

            ObservedVisualDescription committed = Assert.IsType<ObservedVisualDescription>(Assert.Single(mind.Timeline));
            Assert.Equal("test:fresh", committed.SubjectId);
            Assert.Equal("fresh description", committed.Description);

            describeCompletion.SetResult("stale description");
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale);
            await mind.DrainPerceptionsForTestingAsync();
            _ = Assert.Single(mind.Timeline);
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

    private sealed partial class TestMind(ICharacter owner) : MindBase
    {
        public IReadOnlyList<AgentObservation> Timeline => GetObservationTimelineSnapshot();
        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    /// <summary>Records subject hook events with the published state observed at each hook invocation.</summary>
    private sealed partial class RecordingLookPerception : ActiveLookPerception
    {
        public List<string> Events { get; } = [];

        public List<(VisualCue? Cue, IVisualSubject? Subject)> AttachedStates { get; } = [];

        public List<(VisualCue? Cue, IVisualSubject? Subject)> DetachedStates { get; } = [];

        public int GenerationForTest => ActivationGeneration;

        protected override void OnActiveSubjectAttached(IVisualSubject subject)
        {
            Events.Add($"attach:{subject.FullId}");
            AttachedStates.Add((ActiveCue, ActiveSubject));
        }

        protected override void OnActiveSubjectDetached(IVisualSubject subject)
        {
            Events.Add($"detach:{subject.FullId}");
            DetachedStates.Add((ActiveCue, ActiveSubject));
        }
    }
}
