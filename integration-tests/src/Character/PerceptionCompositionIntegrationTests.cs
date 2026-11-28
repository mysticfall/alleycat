using AlleyCat.Common;
using AlleyCat.Core;
using AlleyCat.Core.Installer;
using AlleyCat.Interaction.Hands;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Mind.Perception;
using AlleyCat.Rigging;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Character;

/// <summary>Composition contracts for role-authored senses and Mind faculties.</summary>
[Headless]
public sealed class PerceptionCompositionIntegrationTests
{
    /// <summary>ResourceSaver-expanded defaults must not change provider behaviour.</summary>
    [Fact]
    public void NPCTemplates_SerialisedProviderDefaultsMatchConstructor()
    {
        using var defaultMind = new AgenticMind();
        OpenAIClientProvider defaultProvider = Assert.IsType<OpenAIClientProvider>(defaultMind.ClientProvider);
        foreach (string sex in new[] { "female", "male" })
        {
            using Node root = LoadPackedScene(
                $"res://assets/characters/templates/reference_{sex}/reference_{sex}_npc.tscn").Instantiate();
            AgenticMind mind = root.GetNode<AgenticMind>("Mind");
            OpenAIClientProvider provider = Assert.IsType<OpenAIClientProvider>(mind.ClientProvider);
            Assert.Equal(defaultProvider.ConfigPath, provider.ConfigPath);
            Assert.Equal(defaultProvider.ChatClientKind, provider.ChatClientKind);
        }
    }

    /// <inheritdoc/>
    [Fact]
    public void TemplateReferenceRebaser_RebasesSenseReferencesAndRejectsUnmappableReferences()
    {
        var templateRoot = new Node { Name = "Template" };
        var templateSense = new Hearing { Name = "Hearing" };
        var templateProbe = new ReferenceProbe { Target = templateSense };
        templateRoot.AddChild(templateSense);
        templateRoot.AddChild(templateProbe);
        var targetRoot = new Node { Name = "Target" };
        var targetSense = new Hearing { Name = "Hearing" };
        var targetProbe = new ReferenceProbe();
        targetRoot.AddChild(targetSense);
        targetRoot.AddChild(targetProbe);
        var installer = new NoOpInstaller();
        try
        {
            TemplateSceneReferenceRebaser.CopyExportedPropertyValues(
                templateProbe, targetProbe, templateRoot, targetRoot, installer, failOnUnresolved: true);
            Assert.Same(targetSense, targetProbe.Target);

            targetSense.Name = "Different";
            _ = Assert.Throws<InvalidOperationException>(() => TemplateSceneReferenceRebaser.CopyExportedPropertyValues(
                templateProbe, targetProbe, templateRoot, targetRoot, installer, failOnUnresolved: true));
        }
        finally
        {
            templateRoot.Free();
            targetRoot.Free();
        }
    }

    /// <inheritdoc/>
    [Fact]
    public async Task NpcTemplates_RebaseHearingAndReferencesExposeSensesDeterministicallyAndKeepPlayerSemantics()
    {
        Node npcNode = LoadPackedScene("res://assets/characters/templates/reference_female/reference_female_npc.tscn").Instantiate();
        Node maleNpcNode = LoadPackedScene("res://assets/characters/templates/reference_male/reference_male_npc.tscn").Instantiate();
        Node vadimNode = LoadPackedScene("res://assets/characters/reference/vadim_npc.tscn").Instantiate();
        Node playerNode = LoadPackedScene("res://assets/characters/templates/reference_female/reference_female_player.tscn").Instantiate();
        try
        {
            CharacterHub npc = Assert.IsType<CharacterHub>(npcNode, exactMatch: false);
            npc.RefreshComponents();
            Hearing hearing = Assert.IsType<Hearing>(npc.Hearing!);
            AgenticMind mind = Assert.IsType<AgenticMind>(npc.GetNode("Mind"), exactMatch: false);
            AttentionGazeTargetSelector femaleSelector = Assert.IsType<AttentionGazeTargetSelector>(
                mind.GetNode("AttentionGazeTargetSelector"),
                exactMatch: false);

            Assert.Same(npc, hearing.GetParent());
            Assert.Equal(
                [
                    typeof(SpeechPerception),
                    typeof(VisualSurveyPerception),
                    typeof(VisualDescriptionPerception),
                    typeof(RelativePositionPerception),
                    typeof(AnimationChangePerception),
                ],
                mind.GetChildren().OfType<IPerception>().Select(faculty => faculty.GetType()));
            // The current NPC template authors MotionBricksNavigation (NAV-001 TR5); component order
            // follows ComposeComponents: eyes, navigation, voice, hearing, hands.
            Assert.Equal(
                ["EyesBehaviour", "MotionBricksNavigation", "AIVoice", "Hearing", "HandPoseBehaviour", "HandPoseBehaviour"],
                npc.Components.Select(component => component.GetType().Name));
            Assert.Same(mind, femaleSelector.GetParent());
            Assert.False(femaleSelector is IComponent);
            Assert.DoesNotContain(npc.Components, component => ReferenceEquals(femaleSelector, component));
            _ = Assert.Single(npc.Components.OfType<Hearing>());
            Assert.Null(typeof(CharacterHub).Assembly.GetType("AlleyCat.Character.CharacterPerception"));
            Assert.Null(typeof(CharacterHub).Assembly.GetType("AlleyCat.Character.MindStimulus"));

            Assert.Equal("reference_female_npc", npc.Voice!.Id);
            Assert.Equal("voice", npc.Voice.Type);
            Assert.Equal("voice:reference_female_npc", npc.Voice.FullId);
            IdentityValidator.Validate(npc.Voice, nameof(npc.Voice));

            CharacterHub maleNpc = Assert.IsType<CharacterHub>(maleNpcNode, exactMatch: false);
            AgenticMind maleMind = Assert.IsType<AgenticMind>(maleNpc.GetNode("Mind"), exactMatch: false);
            Assert.Equal(
                [
                    typeof(SpeechPerception),
                    typeof(VisualSurveyPerception),
                    typeof(VisualDescriptionPerception),
                    typeof(RelativePositionPerception),
                    typeof(AnimationChangePerception),
                ],
                maleMind.GetChildren().OfType<IPerception>().Select(faculty => faculty.GetType()));
            Assert.NotSame(mind.GetNode("SpeechPerception"), maleMind.GetNode("SpeechPerception"));
            Assert.NotSame(mind.GetNode("VisualSurveyPerception"), maleMind.GetNode("VisualSurveyPerception"));
            Assert.NotSame(mind.GetNode("VisualDescriptionPerception"), maleMind.GetNode("VisualDescriptionPerception"));
            Assert.NotSame(mind.GetNode("RelativePositionPerception"), maleMind.GetNode("RelativePositionPerception"));
            VisualDescriptionPerception femaleVisualDescription = mind.GetNode<VisualDescriptionPerception>("VisualDescriptionPerception");
            VisualDescriptionPerception maleVisualDescription = maleMind.GetNode<VisualDescriptionPerception>("VisualDescriptionPerception");
            var isolatedCue = new StaticVisualCue();
            npc.AddChild(isolatedCue);
            await femaleVisualDescription.PerceiveAsync(
                new LookTargetChangedPercept(null, isolatedCue),
                new PerceptionContext(npc, null!),
                CancellationToken.None);
            Assert.Same(isolatedCue, femaleVisualDescription.ActiveCue);
            Assert.Null(maleVisualDescription.ActiveCue);
            AttentionGazeTargetSelector maleSelector = Assert.IsType<AttentionGazeTargetSelector>(
                maleMind.GetNode("AttentionGazeTargetSelector"),
                exactMatch: false);
            Assert.Equal("reference_male_npc", maleNpc.Voice!.Id);
            Assert.Equal("voice", maleNpc.Voice.Type);
            Assert.Equal("voice:reference_male_npc", maleNpc.Voice.FullId);
            IdentityValidator.Validate(maleNpc.Voice, nameof(maleNpc.Voice));
            Assert.Same(maleMind, maleSelector.GetParent());
            Assert.False(maleSelector is IComponent);
            Assert.DoesNotContain(maleNpc.Components, component => ReferenceEquals(maleSelector, component));

            CharacterHub vadim = Assert.IsType<CharacterHub>(vadimNode, exactMatch: false);
            // The vadim character scene is born blend-only (CHAR-002 TR 48): its capability references and the
            // character-owned voice identity arrive from installation, which assigns the voice local Id from
            // the final root Id (CHAR-002 TR 30/53).
            Assert.Equal("vadim", vadim.Id);
            EnsureCharacterRuntimeInstalled(vadimNode);
            Assert.Equal("vadim", vadim.Voice!.Id);
            Assert.Equal("voice", vadim.Voice.Type);
            Assert.Equal("voice:vadim", vadim.Voice.FullId);
            IdentityValidator.Validate(vadim.Voice, nameof(vadim.Voice));

            CharacterHub player = Assert.IsType<CharacterHub>(playerNode, exactMatch: false);
            Assert.Null(player.Hearing);
            Assert.DoesNotContain(player.GetChildren(), node => node is AgenticMind or Hearing);
            Assert.DoesNotContain(player.GetChildren(), node => node is AttentionGazeTargetSelector);
            Assert.DoesNotContain(player.GetChildren().SelectMany(node => node.GetChildren()), node => node is AttentionGazeTargetSelector);
            Assert.DoesNotContain(player.GetChildren().SelectMany(node => node.GetChildren()), node => node is VisualDescriptionPerception);
            Assert.DoesNotContain(player.GetChildren().SelectMany(node => node.GetChildren()), node => node is RelativePositionPerception);
        }
        finally
        {
            npcNode.Free();
            maleNpcNode.Free();
            vadimNode.Free();
            playerNode.Free();
        }
    }

    /// <summary>
    /// Verifies the composed speech faculty activates against the composed Hearing sense on a minimal live
    /// character, without any production role installer or MotionBricks participation.
    /// </summary>
    [Fact]
    public async Task MinimalNpcCharacter_LiveTree_ActivatesSpeechPerceptionForHearing()
    {
        SceneTree sceneTree = GetSceneTree();

        // Minimal authored NPC: only the character shell ComposeComponents requires, plus the
        // Hearing→SpeechPerception→AgenticMind wiring this fact verifies. No installer, no scene template.
        CharacterHub npc = new()
        {
            Name = "MinimalPerceptionNpc",
            Id = "minimal_perception_npc"
        };
        EyesBehaviour eyes = new()
        {
            Name = "Eyes"
        };
        AIVoice voice = new()
        {
            Name = "Voice",
            Id = "minimal_perception_npc"
        };
        Hearing hearing = new()
        {
            Name = "Hearing"
        };
        HandPoseBehaviour leftHand = new()
        {
            Name = "LeftHand",
            Side = LimbSide.Left
        };
        HandPoseBehaviour rightHand = new()
        {
            Name = "RightHand",
            Side = LimbSide.Right
        };
        AgenticMind mind = new()
        {
            Name = "Mind"
        };
        // The Mind's activation contract requires its standard faculty set for every configured percept
        // type; the minimal fixture authors exactly those four, matching template composition.
        mind.AddChild(new SpeechPerception { Name = "SpeechPerception" });
        mind.AddChild(new VisualSurveyPerception { Name = "VisualSurveyPerception" });
        mind.AddChild(new VisualDescriptionPerception { Name = "VisualDescriptionPerception" });
        mind.AddChild(new RelativePositionPerception { Name = "RelativePositionPerception" });
        npc.AddChild(eyes);
        npc.AddChild(voice);
        npc.AddChild(hearing);
        npc.AddChild(leftHand);
        npc.AddChild(rightHand);
        npc.AddChild(mind);
        npc.Eyes = eyes;
        npc.Voice = voice;
        npc.Hearing = hearing;
        npc.LeftHand = leftHand;
        npc.RightHand = rightHand;

        TaskCompletionSource componentProjectionCommitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        npc.ComponentsRefreshed += OnComponentProjectionCommitted;
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(npc);
        npc.RefreshComponents();
        await componentProjectionCommitted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            Assert.True(npc.HasComponentProjection);
            Assert.Same(hearing, npc.Hearing);
            Assert.True(mind.Enabled);
            Assert.Equal([typeof(SpeechPercept)], hearing.PerceptTypes);
            SpeechPerception speechPerception = mind.GetNode<SpeechPerception>("SpeechPerception");
            Assert.Equal(typeof(SpeechPercept), speechPerception.PerceptType);
            _ = Assert.IsType<SpeechPerception>(
                Assert.Single(mind.GetChildren().OfType<IPerception>(), perception => perception is SpeechPerception));

            Assert.Contains(hearing, npc.Components);

            List<SpeechPercept> published = [];
            List<ObservedSpeech> committed = [];
            hearing.Perceived += percept => published.Add(Assert.IsType<SpeechPercept>(percept));
            mind.ObservationCommitted += observation => committed.Add(Assert.IsType<ObservedSpeech>(observation));

            hearing.ReceiveVoice("runtime activation evidence", new TestVoice("external_test_voice"));
            await mind.DrainPerceptionsForTestingAsync();

            SpeechPercept publishedPercept = Assert.Single(published);
            Assert.Equal("runtime activation evidence", publishedPercept.Content);
            Assert.Equal("external_test_voice", publishedPercept.SourceVoiceID);
            ObservedSpeech committedObservation = Assert.Single(committed);
            Assert.Equal("runtime activation evidence", committedObservation.Content);

            npc.RefreshComponents();
            hearing.ReceiveVoice("second runtime activation evidence", new TestVoice("external_test_voice"));
            await mind.DrainPerceptionsForTestingAsync();
            Assert.Equal(2, committed.Count);
            Assert.Equal("second runtime activation evidence", committed[1].Content);

            hearing.ReceiveVoice("self speech", npc.Voice!);
            await mind.DrainPerceptionsForTestingAsync();
            Assert.Equal(2, committed.Count);

        }
        finally
        {
            npc.ComponentsRefreshed -= OnComponentProjectionCommitted;
            npc.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }

        void OnComponentProjectionCommitted()
        {
            _ = componentProjectionCommitted.TrySetResult();
        }
    }

    private sealed partial class ReferenceProbe : Node
    {
        [Export]
        public Node? Target
        {
            get; set;
        }
    }

    private sealed class NoOpInstaller : ISceneInstaller
    {
        public SceneInstallationResult Install(SceneInstallationContext context) => SceneInstallationResult.Successful();
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
}
