using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.Observation;
using AlleyCat.Mind.Perception;
using AlleyCat.Scene;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Sense;

/// <summary>Focused runtime coverage for sense-owned synchronous acquisition and cadence.</summary>
[Headless]
public sealed class PerceptSensingIntegrationTests
{
    /// <summary>Hearing owns its group lifecycle and publishes only accepted speech synchronously.</summary>
    [Fact]
    public async Task Hearing_LifecycleAndPublication_RegistersOnceRejectsOnlyBlankSpeechAndSnapshotsSynchronously()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node { Name = "HearingFixture" };
        var hearing = new Hearing();
        root.AddChild(hearing);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            var source = new TestVoice("same-id-as-observer");
            List<SpeechPercept> received = [];
            hearing.Perceived += percept => received.Add(Assert.IsType<SpeechPercept>(percept));

            Assert.True(hearing.IsInGroup(IHearing.GroupName));
            Assert.Equal([typeof(SpeechPercept)], hearing.PerceptTypes);
            hearing.ReceiveVoice(" \t", source);
            hearing.ReceiveVoice("  accepted speech  ", source);
            source.Id = "changed-after-publication";

            SpeechPercept percept = Assert.Single(received);
            Assert.Equal("accepted speech", percept.Content);
            Assert.Equal("same-id-as-observer", percept.SourceVoiceID);
        }
        finally
        {
            root.RemoveChild(hearing);
            Assert.False(hearing.IsInGroup(IHearing.GroupName));
            hearing.QueueFree();
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>Eyes emits one ordered identity snapshot per elapsed interval without delayed-frame catch-up.</summary>
    [Fact]
    public async Task Eyes_PeriodicSurvey_PublishesOneOrderedIdentityOnlySnapshotWithoutCatchUp()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D { Name = "EyesFixture" };
        var observer = new TestVisualSubject("observer") { Position = Vector3.Zero };
        var eyes = new EyesBehaviour { VisualSurveyIntervalSeconds = 0.05d };
        observer.AddChild(eyes);
        root.AddChild(observer);
        TestVisualSubject first = CreateVisibleSubject("first", new Vector3(0f, 0f, -2f));
        TestVisualSubject second = CreateVisibleSubject("second", new Vector3(0f, 0f, -3f));
        root.AddChild(first);
        root.AddChild(second);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);
        await TestUtils.WaitForPhysicsFramesAsync(tree, 2);

        try
        {
            first.AddToGroup("VisualSubjects");
            second.AddToGroup("VisualSubjects");
            List<VisualSurveyPercept> received = [];
            eyes.Perceived += percept => received.Add(Assert.IsType<VisualSurveyPercept>(percept));

            Assert.Equal([typeof(VisualSurveyPercept)], eyes.PerceptTypes);
            eyes._Process(1d);
            eyes._Process(0d);

            VisualSurveyPercept percept = Assert.Single(received);
            Assert.Equal(["test:first", "test:second"], percept.SubjectFullIDs);
            Assert.DoesNotContain(typeof(VisualSurveyPercept).GetProperties(), property =>
                typeof(Node).IsAssignableFrom(property.PropertyType) || typeof(VisualCue).IsAssignableFrom(property.PropertyType));
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>Speech attribution uses ordinal voice IDs, preserves unknown speech, and rejects ambiguity atomically.</summary>
    [Fact]
    public void SpeechPerception_UsesVoiceIDsForSelfUnknownRecognisedAndAmbiguousSources()
    {
        var observerVoice = new TestVoice("observer");
        var observer = new TestCharacter("observer", observerVoice);
        var recognised = new TestCharacter("recognised", new TestVoice("speaker"));
        var perception = new SpeechPerception();

        PerceptionResult self = perception.Perceive(new SpeechPercept("self", "observer"), CreateContext(observer, [recognised]));
        PerceptionResult unknown = perception.Perceive(new SpeechPercept("unknown", "missing"), CreateContext(observer, [recognised]));
        PerceptionResult recognisedResult = perception.Perceive(new SpeechPercept("recognised", "speaker"), CreateContext(observer, [recognised]));

        Assert.Empty(self.AttentionEffects);
        Assert.Empty(self.Observations);
        ObservedSpeech unknownSpeech = Assert.IsType<ObservedSpeech>(Assert.Single(unknown.Observations));
        Assert.Null(unknownSpeech.ActorId);
        Assert.Equal("missing", unknownSpeech.VoiceId);
        Assert.Equal("char:recognised", Assert.Single(recognisedResult.AttentionEffects).SubjectFullId);
        ObservedSpeech recognisedSpeech = Assert.IsType<ObservedSpeech>(Assert.Single(recognisedResult.Observations));
        Assert.Equal("char:recognised", recognisedSpeech.ActorId);

        var duplicate = new TestCharacter("duplicate", new TestVoice("speaker"));
        _ = Assert.Throws<InvalidOperationException>(() =>
            perception.Perceive(new SpeechPercept("ambiguous", "speaker"), CreateContext(observer, [recognised, duplicate])));
    }

    /// <summary>Visual faculties preserve each canonical ID and duplicate in percept order without observations.</summary>
    [Fact]
    public void VisualSurveyPerception_ReturnsOrderedDuplicateReinforcementsWithoutObservations()
    {
        var perception = new VisualSurveyPerception();
        var percept = new VisualSurveyPercept(["char:second", "char:first", "char:second"]);

        PerceptionResult result = perception.Perceive(percept, CreateContext(new TestCharacter("observer", new TestVoice("observer")), []));

        Assert.Equal(["char:second", "char:first", "char:second"], result.AttentionEffects.Select(effect => effect.SubjectFullId));
        Assert.Empty(result.Observations);
    }

    private static PerceptionContext CreateContext(ICharacter observer, IReadOnlyCollection<ICharacter> characters)
        => new(observer, new TestSceneContext(characters), null!);

    private static TestVisualSubject CreateVisibleSubject(string id, Vector3 position)
    {
        var subject = new TestVisualSubject(id) { Position = position };
        var cue = new StaticVisualCue { Prominence = 1f };
        subject.AddChild(cue);
        subject.VisualCues = [cue];
        return subject;
    }

    private static void AddToTree(SceneTree tree, Node node)
        => (tree.CurrentScene ?? tree.Root).AddChild(node);

    private sealed partial class TestVisualSubject(string id) : Node3D, IVisualSubject
    {
        public string Id { get; set; } = id;

        public string Type => "test";

        public IReadOnlyList<VisualCue> VisualCues { get; set; } = [];
    }

    private sealed class TestVoice(string id) : IVoice
    {
        public string Id { get; set; } = id;

        public Vector3 Origin => Vector3.Zero;

        public void Speak(string speech)
        {
        }

        public ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class TestCharacter(string id, IVoice voice) : ICharacter
    {
        public string Id { get; set; } = id;

        public IReadOnlyList<IComponent> Components { get; } = [voice];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();
    }

    private sealed class TestSceneContext(IReadOnlyCollection<ICharacter> characters) : ISceneContext
    {
        public IReadOnlyCollection<ICharacter> Characters => characters;

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId) => null;

        public IIdentifiable Resolve(string fullId) => throw new InvalidOperationException();
    }
}
