using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Core.Time;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Mind.Perception;
using AlleyCat.Scene;
using AlleyCat.Sense;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.Perception;

/// <summary>
/// Runtime contracts for the Mind percept/observation stream: subscription ownership, enqueue-order serialisation,
/// transient retention, and per-observation atomicity (AI-001 TR-8, AI-006 TR-1/2).
/// </summary>
[Headless]
public sealed class PerceptionStreamMindIntegrationTests
{
    /// <summary>
    /// Mind owns every bound faculty's observation stream for its node lifetime: emissions raised outside percept
    /// dispatch commit through Mind, and tree exit unsubscribes so later emissions never commit (AI-001 TR-8/11,
    /// AI-006 TR-1/2).
    /// </summary>
    [Fact]
    public async Task StreamOwnership_EmissionsOutsidePerceptDispatch_CommitThroughMindAndStopAfterExit()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var owner = new TestCharacter();
        var mind = new TestMind(owner);
        mind.SetSceneContextLoaderForTesting(() => TestSceneContext.Instance);
        mind.SetAttentionClockForTesting(() => 0d);
        var faculty = new EmittingFaculty();
        mind.AddChild(faculty);
        var root = new Node();
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            faculty.EmitForTest(new TestObservation("durable", 0f));
            await mind.DrainPerceptionsForTestingAsync();
            Assert.Equal("durable", Assert.IsType<TestObservation>(Assert.Single(mind.Timeline)).Value);

            faculty.EmitForTest(new ObservedVisualPresence("char:subject"));
            await mind.DrainPerceptionsForTestingAsync();
            Assert.Equal(0.25f, mind.GetAttention("char:subject"));
            _ = Assert.Single(mind.Timeline);

            root.RemoveChild(mind);
            faculty.EmitForTest(new TestObservation("post-exit", 0f));
            faculty.EmitForTest(new ObservedVisualPresence("char:post_exit"));
            await mind.DrainPerceptionsForTestingAsync();

            _ = Assert.Single(mind.Timeline);
            Assert.Equal(0f, mind.GetAttention("char:post_exit"));
            mind.QueueFree();
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// One serial queue merges percept work and observations in enqueue order: emissions raised during in-flight
    /// percept work commit after that work finishes, publication callbacks return before any commit, and emissions
    /// commit in emission order (AI-001 TR-8, AI-006 TR-2).
    /// </summary>
    [Fact]
    public async Task EnqueueOrder_EmissionsDuringPerceptProcessingCommitAfterInFlightWorkInEmissionOrder()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var sense = new TestSense(typeof(StreamPercept));
        var owner = new TestCharacter(sense);
        var mind = new TestMind(owner);
        mind.SetSceneContextLoaderForTesting(() => TestSceneContext.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var emitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DelegatingFaculty? faculty = null;
        faculty = new DelegatingFaculty(async (percept, _, _) =>
        {
            if (percept.Id == "first")
            {
                _ = started.TrySetResult();
                faculty!.EmitForTest(new TestObservation("emission:a", 0f));
                faculty.EmitForTest(new TestObservation("emission:b", 0f));
                _ = emitted.TrySetResult();
                await gate.Task;
            }
            else
            {
                faculty!.EmitForTest(new TestObservation($"percept:{percept.Id}", 0f));
            }
        });
        mind.AddChild(faculty);
        var root = new Node();
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            sense.Publish(new StreamPercept("first"));
            sense.Publish(new StreamPercept("second"));

            // Publication callbacks returned while the first percept is still in flight and nothing has committed.
            await emitted.Task;
            Assert.Empty(mind.Timeline);
            Assert.Equal(3, mind.GetPendingPerceptionCountForTesting());

            _ = gate.TrySetResult();
            await mind.DrainPerceptionsForTestingAsync();

            // The gated percept work settles first, then the previously queued second percept, then the two
            // observations in emission order, and finally the emission raised while processing the second percept.
            Assert.Equal(["emission:a", "emission:b", "percept:second"], Values(mind.Timeline));
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// Visual presence alone is attention-only. Legacy retention declarations do not bypass policy-owned acceptance:
    /// an undeclared observation receives the fallback retained/event policy (AI-001 TR-2/3).
    /// </summary>
    [Fact]
    public async Task VisualPresence_IsAttentionOnlyWhileLegacyRetentionDoesNotBypassPolicyAcceptance()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var owner = new TestCharacter();
        var mind = new TestMind(owner) { ObservationImportanceThreshold = 100f };
        mind.SetSceneContextLoaderForTesting(() => TestSceneContext.Instance);
        mind.SetAttentionClockForTesting(() => 0d);
        var clock = new CountingGameClock { CurrentSeconds = 10d };
        mind.SetGameClockLoaderForTesting(() => clock);
        int notableSignals = 0;
        mind.DeliverySignalForTest(_ => notableSignals++);
        var faculty = new EmittingFaculty();
        mind.AddChild(faculty);
        var root = new Node();
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            var presence = new ObservedVisualPresence("char:subject");
            faculty.EmitForTest(presence);
            await mind.DrainPerceptionsForTestingAsync();

            Assert.Null(presence.ObservedAt);
            Assert.Empty(mind.Timeline);
            Assert.Empty(mind.Ingested);
            Assert.Equal(0, notableSignals);
            Assert.Equal(0, clock.ReadCount);
            KeyValuePair<string, float> attentionEntry = Assert.Single(mind.GetAttentionSnapshot().Values);
            Assert.Equal("char:subject", attentionEntry.Key);
            Assert.Equal(0.25f, attentionEntry.Value);

            faculty.EmitForTest(new ObservedVisualPresence("char:subject"));
            await mind.DrainPerceptionsForTestingAsync();
            Assert.Equal(0.4375f, mind.GetAttention("char:subject"));
            Assert.Empty(mind.Timeline);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// Invalid attention behaviour rolls back only its own observation: a mixed or non-finite effect list mutates
    /// nothing, while earlier and later observations still commit (AI-001 TR-8, AI-006 TR-2).
    /// </summary>
    [Fact]
    public async Task Atomicity_InvalidAttentionBehaviour_MutatesNothingForThatObservationOnly()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var owner = new TestCharacter();
        var mind = new TestMind(owner) { ObservationImportanceThreshold = 100f };
        mind.SetSceneContextLoaderForTesting(() => TestSceneContext.Instance);
        mind.SetAttentionClockForTesting(() => 0d);
        var faculty = new EmittingFaculty();
        mind.AddChild(faculty);
        var root = new Node();
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            faculty.EmitForTest(new TestObservation("before", 0f));
            faculty.EmitForTest(new EffectsObservation(
                "mixed",
                new AttentionEffect("char:target", 0.5f),
                new AttentionEffect("invalid-id", 0.5f)));
            faculty.EmitForTest(new EffectsObservation("non-finite", new AttentionEffect("char:target2", float.NaN)));
            faculty.EmitForTest(new ObservedVisualPresence("char:transient"));
            faculty.EmitForTest(new TestObservation("after", 0f));
            await mind.DrainPerceptionsForTestingAsync();

            Assert.Equal(["before", "after"], Values(mind.Timeline));
            Assert.Equal(2, mind.Ingested.Count);
            Assert.Equal(0f, mind.GetAttention("char:target"));
            Assert.Equal(0f, mind.GetAttention("char:target2"));
            Assert.Equal(0.25f, mind.GetAttention("char:transient"));
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    private static IReadOnlyList<string> Values(IReadOnlyList<AgentObservation> observations)
        => [.. observations.Select(observation => observation switch
        {
            TestObservation test => test.Value,
            _ => observation.TypeKey,
        })];

    private static void AddToTree(SceneTree tree, Node node) => (tree.CurrentScene ?? tree.Root).AddChild(node);

    private sealed record StreamPercept(string Id) : IPercept;

    private sealed partial class TestSense(params Type[] perceptTypes) : Node, ISense
    {
        private Action<IPercept>? _perceived;

        public event Action<IPercept>? Perceived
        {
            add => _perceived += value;
            remove => _perceived -= value;
        }
        public IReadOnlyList<Type> PerceptTypes { get; } = perceptTypes;
        public void Publish(IPercept percept) => _perceived?.Invoke(percept);
    }

    private sealed class CountingGameClock : IGameClock
    {
        public double CurrentSeconds
        {
            get; set;
        }
        public int ReadCount
        {
            get; private set;
        }

        public double NowSeconds
        {
            get
            {
                ReadCount++;
                return CurrentSeconds;
            }
        }
    }

    private sealed class TestCharacter(params IComponent[] components) : ICharacter
    {
        public string Id { get; set; } = "owner";
        public IReadOnlyList<IComponent> Components { get; } = components;
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
        public List<AgentObservation> Ingested { get; } = [];
        public IReadOnlyList<AgentObservation> Timeline => GetObservationTimelineSnapshot();
        public void DeliverySignalForTest(Action<ObservationDeliverySignal> handler)
            => ObservationDeliverySignalled += handler;
        protected override ICharacter ResolveOwningCharacter() => owner;
        protected override void OnObservationIngested(AgentObservation observation) => Ingested.Add(observation);
    }

    /// <summary>A faculty whose observations are raised manually, simulating a polling faculty's cadence.</summary>
    private sealed partial class EmittingFaculty : Perception<StreamPercept>
    {
        public void EmitForTest(AgentObservation observation) => Emit(observation);

        public override ValueTask PerceiveAsync(
            StreamPercept percept,
            PerceptionContext context,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed partial class DelegatingFaculty(
        Func<StreamPercept, PerceptionContext, CancellationToken, ValueTask> handler) : Perception<StreamPercept>
    {
        public void EmitForTest(AgentObservation observation) => Emit(observation);

        public override ValueTask PerceiveAsync(
            StreamPercept percept,
            PerceptionContext context,
            CancellationToken cancellationToken)
            => handler(percept, context, cancellationToken);
    }

    private sealed record TestObservation(string Value, float Importance) : AgentObservation
    {
        public override string TypeKey => "test";
        public override float CalculateImportance(ObservationContext context) => Importance;
    }

    private sealed record EffectsObservation(string Value, params AttentionEffect[] Effects) : AgentObservation
    {
        public override string TypeKey => "effects.test";
        public override float CalculateImportance(ObservationContext context) => 0f;

        public override IReadOnlyList<AttentionEffect> GetAttentionEffects(ObservationContext context) => Effects;
    }
}
