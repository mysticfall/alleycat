using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
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

    /// <summary>Voice broadcasts grouped automatic segments through Hearing into an immutable percept snapshot.</summary>
    [Fact]
    public async Task Voice_GroupedSpeechPublication_ReachesHearingWithUnchangedImmutableMetadata()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node { Name = "GroupedSpeechFixture" };
        var voice = new PublishingTestVoice();
        var hearing = new Hearing();
        root.AddChild(voice);
        root.AddChild(hearing);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            List<SpeechPercept> received = [];
            hearing.Perceived += percept => received.Add(Assert.IsType<SpeechPercept>(percept));
            var metadata = new SpeechSegmentMetadata("automatic-group", 1);

            voice.PublishCompletedSpeech("  continued automatic segment  ", metadata);

            SpeechPercept percept = Assert.Single(received);
            Assert.Equal("continued automatic segment", percept.Content);
            Assert.Equal(voice.Id, percept.SourceVoiceID);
            Assert.Equal("automatic-group", percept.SpeechGroupID);
            Assert.Equal(1, percept.SegmentIndex);
            Assert.True(percept.Continued);
            Assert.All(typeof(SpeechPercept).GetProperties(), property => Assert.False(property.CanWrite));
        }
        finally
        {
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

            Assert.Equal([typeof(VisualSurveyPercept), typeof(LookTargetChangedPercept)], eyes.PerceptTypes);
            // Stop the live physics loop so only the manual _PhysicsProcess(1d) drives the survey; without this a
            // second live survey can fire during the measurement window and cause a 2-percept flake under windowed runs.
            eyes.SetPhysicsProcess(false);
            await TriggerPhysicsSurveyOnceAsync(tree, eyes);

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

    /// <summary>Eyes publishes only effective cue-identity transitions through its single synchronous bridge.</summary>
    [Fact]
    public void Eyes_LookTargetTransitions_PublishExactlyOnceAndSuppressEquivalentAssignmentsAndNullClears()
    {
        var eyes = new EyesBehaviour();
        var first = new StaticVisualCue();
        var second = new StaticVisualCue();
        List<IPercept> received = [];
        eyes.Perceived += received.Add;

        eyes.SetLookTarget(first);
        eyes.SetLookTarget(first);
        eyes.SetLookTarget(second);
        eyes.ClearLookTarget();
        eyes.ClearLookTarget();

        Assert.Collection(
            received.Cast<LookTargetChangedPercept>(),
            transition =>
            {
                Assert.Null(transition.Previous);
                Assert.Same(first, transition.Current);
            },
            transition =>
            {
                Assert.Same(first, transition.Previous);
                Assert.Same(second, transition.Current);
            },
            transition =>
            {
                Assert.Same(second, transition.Previous);
                Assert.Null(transition.Current);
            });
    }

    /// <summary>Convention fallback accepts only authored visual cues and ignores arbitrary named anchors.</summary>
    [Fact]
    public async Task Eyes_ConventionLookTarget_AcceptsOnlyVisualCue()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var invalidRoot = new Node3D();
        invalidRoot.AddChild(new Node3D { Name = "LookTarget" });
        var invalidEyes = new EyesBehaviour();
        invalidRoot.AddChild(invalidEyes);
        var validRoot = new Node3D();
        var cue = new StaticVisualCue { Name = "LookTarget" };
        validRoot.AddChild(cue);
        var validEyes = new EyesBehaviour();
        List<LookTargetChangedPercept> received = [];
        validEyes.Perceived += percept => received.Add(Assert.IsType<LookTargetChangedPercept>(percept));
        validRoot.AddChild(validEyes);
        AddToTree(tree, invalidRoot);
        AddToTree(tree, validRoot);

        try
        {
            await TestUtils.WaitForFramesAsync(tree, 2);

            Assert.Null(invalidEyes.LookTarget);
            Assert.Same(cue, validEyes.LookTarget);
            LookTargetChangedPercept transition = Assert.Single(received);
            Assert.Null(transition.Previous);
            Assert.Same(cue, transition.Current);
        }
        finally
        {
            invalidRoot.QueueFree();
            validRoot.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>Speech attribution uses ordinal voice IDs, preserves unknown speech, and rejects ambiguity atomically.</summary>
    [Fact]
    public async Task SpeechPerception_UsesVoiceIDsForSelfUnknownRecognisedAndAmbiguousSources()
    {
        var observerVoice = new TestVoice("observer");
        var observer = new TestCharacter("observer", observerVoice);
        var recognised = new TestCharacter("recognised", new TestVoice("speaker"));
        var perception = new SpeechPerception();
        List<Observation> emissions = [];
        perception.Observed += emissions.Add;

        await perception.PerceiveAsync(new SpeechPercept("self", "observer"), CreateContext(observer, [recognised]), CancellationToken.None);
        Assert.Empty(emissions);

        await perception.PerceiveAsync(new SpeechPercept("unknown", "missing"), CreateContext(observer, [recognised]), CancellationToken.None);
        ObservedSpeech unknownSpeech = Assert.IsType<ObservedSpeech>(Assert.Single(emissions));
        Assert.Null(unknownSpeech.ActorId);
        Assert.Equal("missing", unknownSpeech.VoiceId);
        Assert.Equal("unknown", unknownSpeech.Content);
        Assert.Empty(unknownSpeech.GetAttentionEffects(new ObservationContext(observer)));

        emissions.Clear();
        await perception.PerceiveAsync(new SpeechPercept("recognised", "speaker"), CreateContext(observer, [recognised]), CancellationToken.None);
        ObservedSpeech recognisedSpeech = Assert.IsType<ObservedSpeech>(Assert.Single(emissions));
        Assert.Equal("char:recognised", recognisedSpeech.ActorId);
        var observationContext = new ObservationContext(observer);
        AttentionEffect attentionEffect = Assert.Single(recognisedSpeech.GetAttentionEffects(observationContext));
        Assert.Equal("char:recognised", attentionEffect.SubjectFullId);
        Assert.Equal(0.5f, attentionEffect.Contribution);

        var duplicate = new TestCharacter("duplicate", new TestVoice("speaker"));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await perception.PerceiveAsync(new SpeechPercept("ambiguous", "speaker"), CreateContext(observer, [recognised, duplicate]), CancellationToken.None));
    }

    /// <summary>Visual survey emits ordered transient presence observations with duplicate subjects preserved in order.</summary>
    [Fact]
    public async Task VisualSurveyPerception_EmitsOrderedTransientPresenceObservationsPreservingDuplicateSubjects()
    {
        var observer = new TestCharacter("observer", new TestVoice("observer"));
        var perception = new VisualSurveyPerception();
        List<Observation> emissions = [];
        perception.Observed += emissions.Add;

        await perception.PerceiveAsync(
            new VisualSurveyPercept(["char:second", "char:first", "char:second"]),
            CreateContext(observer, []),
            CancellationToken.None);

        var observationContext = new ObservationContext(observer);
        Assert.Collection(
            emissions,
            emission => AssertTransientPresence(emission, "char:second", observationContext),
            emission => AssertTransientPresence(emission, "char:first", observationContext),
            emission => AssertTransientPresence(emission, "char:second", observationContext));

        static void AssertTransientPresence(Observation emission, string expectedSubjectId, ObservationContext context)
        {
            ObservedVisualPresence presence = Assert.IsType<ObservedVisualPresence>(emission);
            Assert.Equal("vision.presence", presence.TypeKey);
            Assert.Equal(ObservationRetention.Transient, presence.Retention);
            Assert.Equal(expectedSubjectId, presence.SubjectId);
            AttentionEffect effect = Assert.Single(presence.GetAttentionEffects(context));
            Assert.Equal(expectedSubjectId, effect.SubjectFullId);
            Assert.Equal(0.25f, effect.Contribution);
        }
    }

    private static PerceptionContext CreateContext(ICharacter observer, IReadOnlyCollection<ICharacter> characters)
        => new(observer, new TestSceneContext(characters));

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

    /// <summary>
    /// Runs one physics frame and drives <see cref="EyesBehaviour._PhysicsProcess"/> from within it so the survey
    /// resolves a direct space state during the physics step rather than from a process frame. The 1d-then-0d cadence
    /// fires exactly one survey (the large delta triggers publication and resets the interval, then the zero delta
    /// preserves the single-publish contract without delayed-frame catch-up).
    /// </summary>
    private static async Task TriggerPhysicsSurveyOnceAsync(SceneTree tree, params EyesBehaviour[] eyes)
    {
        var completed = new TaskCompletionSource();
        void OnPhysicsFrame()
        {
            tree.PhysicsFrame -= OnPhysicsFrame;
            foreach (EyesBehaviour eye in eyes)
            {
                eye._PhysicsProcess(1d);
                eye._PhysicsProcess(0d);
            }

            completed.SetResult();
        }

        tree.PhysicsFrame += OnPhysicsFrame;
        await completed.Task;
    }

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

        public bool IsSpeaking => false;

#pragma warning disable CS0067
        public event Action<IVoice>? SpeechStarted;

        public event Action<IVoice>? SpeechEnded;
#pragma warning restore CS0067

        public void Speak(string speech)
        {
        }

        public ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SpeakCancellableAsync(string speech, CancellationToken cancellationToken = default)
            => SpeakAsync(speech, cancellationToken);
    }

    private sealed partial class PublishingTestVoice : Voice
    {
        public void PublishCompletedSpeech(string speech, SpeechSegmentMetadata metadata)
            => PublishSpeech(speech, metadata);
    }

    private sealed class TestCharacter(string id, IVoice voice) : ICharacter
    {
        public string Id { get; set; } = id;

        public IReadOnlyList<IComponent> Components { get; } = [voice];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

    private sealed class TestSceneContext(IReadOnlyCollection<ICharacter> characters) : ISceneContext
    {
        public ICharacter Player => throw new InvalidOperationException(
            "Scene context contains no player character. Scene authoring guarantees the player is present.");

        public IReadOnlyCollection<ICharacter> Characters => characters;

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId) => null;

        public IIdentifiable Resolve(string fullId) => throw new InvalidOperationException();
    }
}
