using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Mind.Perception;
using AlleyCat.Scene;
using AlleyCat.Sense;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.Perception;

/// <summary>Runtime contracts for Mind-owned perception registration, delivery, and transactional ingestion.</summary>
[Headless]
public sealed class PerceptionMindIntegrationTests
{
    /// <inheritdoc/>
    [Fact]
    public async Task Registry_UsesExactTypesRejectsInvalidMappingsBeforeActivationAndUnsubscribesOnExit()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var firstSense = new TestSense(typeof(FirstPercept));
        var secondSense = new TestSense(typeof(SecondPercept));
        var owner = new TestCharacter(firstSense, secondSense);
        var mind = new TestMind(owner)
        {
            Perceptions = [new FirstFaculty(), new SecondFaculty()],
        };
        var root = new Node();
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            Assert.Equal(1, firstSense.SubscriptionCount);
            Assert.Equal(1, secondSense.SubscriptionCount);
            Assert.Equal(1, owner.ComponentsRefreshedHandlerCount);
            firstSense.Publish(new FirstPercept());
            secondSense.Publish(new SecondPercept());
            Assert.Equal(2, mind.Ingested.Count);

            mind.Perceptions = [new SecondFaculty()];
            owner.RefreshComponents(secondSense);
            Assert.Equal(0, firstSense.SubscriptionCount);
            Assert.Equal(1, secondSense.SubscriptionCount);
            firstSense.Publish(new FirstPercept());
            secondSense.Publish(new SecondPercept());
            Assert.Equal(3, mind.Ingested.Count);

            owner.RefreshComponents(secondSense);
            Assert.Equal(1, secondSense.SubscriptionCount);
            secondSense.Publish(new SecondPercept());
            Assert.Equal(4, mind.Ingested.Count);

            root.RemoveChild(mind);
            firstSense.Publish(new FirstPercept());
            secondSense.Publish(new SecondPercept());
            Assert.Equal(4, mind.Ingested.Count);
            Assert.Equal(0, firstSense.SubscriptionCount);
            Assert.Equal(0, secondSense.SubscriptionCount);
            Assert.Equal(0, owner.ComponentsRefreshedHandlerCount);
        }
        finally
        {
            mind.QueueFree();
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }

        AssertActivationFails(new TestCharacter(new TestSense(typeof(FirstPercept))), []);
        AssertActivationFails(
            new TestCharacter(new TestSense(typeof(FirstPercept))),
            [new FirstFaculty(), new FirstFaculty()]);
    }

    /// <inheritdoc/>
    [Fact]
    public async Task Results_ValidateCompletelyBeforeMutationAndApplyOrderedEffectsAtomically()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var sense = new TestSense(typeof(FirstPercept));
        var owner = new TestCharacter(sense);
        var faculty = new ResultFaculty();
        var mind = new TestMind(owner)
        {
            AttentionDecayPerSecond = 0.1f,
            ObservationImportanceThreshold = 100f,
            Perceptions = [faculty],
        };
        var root = new Node();
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            double timestamp = 0d;
            mind.SetAttentionClockForTesting(() => timestamp);
            faculty.Result = new PerceptionResult(
                [new AttentionEffect("char:existing", 1f)],
                [new TestObservation(0.1f)]);
            sense.Publish(new FirstPercept());

            AttentionSnapshot attentionBeforeFailure = mind.GetAttentionSnapshot();
            IReadOnlyList<AgentObservation> timelineBeforeFailure = mind.Timeline;

            timestamp = 5d;
            faculty.Result = new PerceptionResult(
                [new AttentionEffect("char:valid", 0.5f)],
                [new TestObservation(float.NaN)]);
            _ = Assert.Throws<InvalidOperationException>(() => sense.Publish(new FirstPercept()));

            // Resetting the test clock permits a non-mutating snapshot comparison. Before this regression fix,
            // handling the invalid result had already decayed the existing entry from 1.0 to 0.5.
            timestamp = 0d;
            mind.SetAttentionClockForTesting(() => timestamp);
            AttentionSnapshot attentionAfterFailure = mind.GetAttentionSnapshot();
            Assert.Equal(attentionBeforeFailure.Timestamp, attentionAfterFailure.Timestamp);
            Assert.Equal(attentionBeforeFailure.Values, attentionAfterFailure.Values);
            Assert.Equal(1f, mind.GetAttention("char:existing"));
            Assert.Equal(timelineBeforeFailure, mind.Timeline);
            _ = Assert.Single(mind.Ingested);

            faculty.Result = new PerceptionResult(
                [new AttentionEffect("char:subject", 0.5f), new AttentionEffect("char:subject", 0.5f)],
                [new TestObservation(0.1f), new TestObservation(0.2f)]);
            sense.Publish(new FirstPercept());

            Assert.Equal(0.75f, mind.GetAttention("char:subject"), 3);
            Assert.Equal([0.1f, 0.1f, 0.2f], mind.Timeline.Cast<TestObservation>().Select(observation => observation.Importance));
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <inheritdoc/>
    [Fact]
    public async Task Conversation_HearingIsTheOnlyVoiceListenerAndExternalSpeechCreatesOneObservation()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var hearing = new Hearing();
        var owner = new TestCharacter(hearing);
        var mind = new TestMind(owner) { Perceptions = [new HearingFaculty()] };
        var source = new TestVoice { Id = "external" };
        var root = new Node();
        root.AddChild(hearing);
        root.AddChild(mind);
        root.AddChild(source);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            Assert.Contains(hearing, tree.GetNodesInGroup(IHearing.GroupName));
            Assert.DoesNotContain(mind, tree.GetNodesInGroup(IHearing.GroupName));
            source.Speak("external speech");
            Assert.Equal("external speech", Assert.IsType<ObservedSpeech>(Assert.Single(mind.Timeline)).Content);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    private static void AssertActivationFails(TestCharacter owner, PerceptionResource[] perceptions)
    {
        var mind = new TestMind(owner) { Perceptions = perceptions };
        _ = Assert.Throws<InvalidOperationException>(mind._Ready);
        Assert.All(owner.Components.OfType<TestSense>(), sense => Assert.Equal(0, sense.SubscriptionCount));
        mind.Free();
    }

    private static void AddToTree(SceneTree tree, Node node) => (tree.CurrentScene ?? tree.Root).AddChild(node);

    private sealed class FirstPercept : IPercept;
    private sealed class SecondPercept : IPercept;

    private sealed partial class TestSense(params Type[] perceptTypes) : Node, ISense
    {
        private Action<IPercept>? _perceived;
        public int SubscriptionCount
        {
            get; private set;
        }
        public event Action<IPercept>? Perceived
        {
            add
            {
                _perceived += value;
                SubscriptionCount++;
            }
            remove
            {
                _perceived -= value;
                SubscriptionCount--;
            }
        }
        public IReadOnlyList<Type> PerceptTypes { get; } = perceptTypes;
        public void Publish(IPercept percept) => _perceived?.Invoke(percept);
    }

    private sealed class TestCharacter(params IComponent[] components) : ICharacter, IComponentProjectionNotifier
    {
        private IComponent[] _components = components;
        private Action? _componentsRefreshed;

        public string Id { get; set; } = "owner";
        public IReadOnlyList<IComponent> Components => _components;
        public bool HasComponentProjection { get; private set; } = true;
        public IReadOnlyList<VisualCue> VisualCues => [];
        public int ComponentsRefreshedHandlerCount
        {
            get; private set;
        }
        public event Action? ComponentsRefreshed
        {
            add
            {
                _componentsRefreshed += value;
                ComponentsRefreshedHandlerCount++;
            }
            remove
            {
                _componentsRefreshed -= value;
                ComponentsRefreshedHandlerCount--;
            }
        }
        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer) => new Dictionary<string, object?>();
        public void RefreshComponents(params IComponent[] components)
        {
            _components = components;
            HasComponentProjection = true;
            _componentsRefreshed?.Invoke();
        }
    }

    private sealed partial class TestMind(ICharacter owner) : MindBase
    {
        public List<AgentObservation> Ingested { get; } = [];
        public IReadOnlyList<AgentObservation> Timeline => GetObservationTimelineSnapshot();
        protected override ICharacter ResolveOwningCharacter() => owner;
        protected override void OnObservationIngested(AgentObservation observation) => Ingested.Add(observation);
    }

    private sealed partial class FirstFaculty : Perception<FirstPercept>
    {
        public override PerceptionResult Perceive(FirstPercept percept, PerceptionContext context) => new([], [new TestObservation(0f)]);
    }
    private sealed partial class SecondFaculty : Perception<SecondPercept>
    {
        public override PerceptionResult Perceive(SecondPercept percept, PerceptionContext context) => new([], [new TestObservation(0f)]);
    }
    private sealed partial class ResultFaculty : Perception<FirstPercept>
    {
        public PerceptionResult Result { get; set; } = new([], []);
        public override PerceptionResult Perceive(FirstPercept percept, PerceptionContext context) => Result;
    }
    private sealed partial class HearingFaculty : Perception<SpeechPercept>
    {
        public override PerceptionResult Perceive(SpeechPercept percept, PerceptionContext context) => new([], [new ObservedSpeech(null, percept.SourceVoiceID, percept.Content)]);
    }
    private sealed record TestObservation(float Importance) : AgentObservation
    {
        public override string TypeKey => "test";
        public override float CalculateImportance(ObservationContext context) => Importance;
    }
    private sealed partial class TestVoice : Voice
    {
        public override void Speak(string speech) => base.Speak(speech);
    }
}
