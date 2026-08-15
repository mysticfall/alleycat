using AlleyCat.Common;
using AlleyCat.Core;
using AlleyCat.Core.Installer;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.Observation;
using AlleyCat.Mind.Perception;
using AlleyCat.Rigging.Installation;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Character;

/// <summary>Composition contracts for role-authored senses and Mind faculties.</summary>
[Headless]
public sealed class PerceptionCompositionIntegrationTests
{
    private const string VadimNPCScenePath = "res://assets/characters/reference/vadim_npc.tscn";

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
    public void NpcTemplates_RebaseHearingAndReferencesExposeSensesDeterministicallyAndKeepPlayerSemantics()
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

            Assert.Same(npc, hearing.GetParent());
            Assert.Equal([typeof(SpeechPerception), typeof(VisualSurveyPerception)], mind.Perceptions.Select(faculty => faculty.GetType()));
            Assert.Equal(["CharacterLocomotion", "LocomotiveNavigation", "EyesBehaviour", "AIVoice", "Hearing", "HandPoseBehaviour", "HandPoseBehaviour"], npc.Components.Select(component => component.GetType().Name));
            _ = Assert.Single(npc.Components.OfType<Hearing>());
            Assert.Null(typeof(CharacterHub).Assembly.GetType("AlleyCat.Character.CharacterPerception"));
            Assert.Null(typeof(CharacterHub).Assembly.GetType("AlleyCat.Character.MindStimulus"));

            Assert.Equal("reference_female_npc", npc.Voice!.Id);
            Assert.Equal("voice", npc.Voice.Type);
            Assert.Equal("voice:reference_female_npc", npc.Voice.FullId);
            IdentityValidator.Validate(npc.Voice, nameof(npc.Voice));

            CharacterHub maleNpc = Assert.IsType<CharacterHub>(maleNpcNode, exactMatch: false);
            Assert.Equal("reference_male_npc", maleNpc.Voice!.Id);
            Assert.Equal("voice", maleNpc.Voice.Type);
            Assert.Equal("voice:reference_male_npc", maleNpc.Voice.FullId);
            IdentityValidator.Validate(maleNpc.Voice, nameof(maleNpc.Voice));

            CharacterHub vadim = Assert.IsType<CharacterHub>(vadimNode, exactMatch: false);
            Assert.Equal("vadim", vadim.Voice!.Id);
            Assert.Equal("voice", vadim.Voice.Type);
            Assert.Equal("voice:vadim", vadim.Voice.FullId);
            IdentityValidator.Validate(vadim.Voice, nameof(vadim.Voice));

            CharacterHub player = Assert.IsType<CharacterHub>(playerNode, exactMatch: false);
            Assert.Null(player.Hearing);
            Assert.DoesNotContain(player.GetChildren(), node => node is AgenticMind or Hearing);
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
    /// Verifies Vadim's authored speech faculty activates against the composed Hearing sense in a fresh scene tree.
    /// </summary>
    [Fact]
    public async Task VadimNPCScene_LiveTree_ActivatesSpeechPerceptionForHearing()
    {
        SceneTree sceneTree = GetSceneTree();
        Node vadimNode = LoadPackedScene(VadimNPCScenePath).Instantiate();
        CharacterHub vadim = Assert.IsType<CharacterHub>(vadimNode, exactMatch: false);
        RigRoleTemplateSceneInstaller installer = vadimNode.RequireNode<RigRoleTemplateSceneInstaller>("NPCCharacterInstaller");
        Assert.True(installer.AutoInstallOnReady);
        TaskCompletionSource componentProjectionCommitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vadim.ComponentsRefreshed += OnComponentProjectionCommitted;
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(vadimNode);

        try
        {
            await componentProjectionCommitted.Task;

            Assert.True(vadim.HasComponentProjection);
            Hearing hearing = Assert.IsType<Hearing>(vadim.Hearing);
            AgenticMind mind = Assert.IsType<AgenticMind>(vadim.GetNode("Mind"), exactMatch: false);
            SpeechPerception speechPerception = Assert.IsType<SpeechPerception>(
                Assert.Single(mind.Perceptions, perception => perception is SpeechPerception));

            Assert.Contains(hearing, vadim.Components);
            Assert.True(mind.Enabled);
            Assert.Equal([typeof(SpeechPercept)], hearing.PerceptTypes);
            Assert.Equal(typeof(SpeechPercept), speechPerception.PerceptType);

            List<SpeechPercept> published = [];
            List<ObservedSpeech> committed = [];
            hearing.Perceived += percept => published.Add(Assert.IsType<SpeechPercept>(percept));
            mind.ObservationCommitted += observation => committed.Add(Assert.IsType<ObservedSpeech>(observation));

            hearing.ReceiveVoice("runtime activation evidence", new TestVoice("external_test_voice"));

            SpeechPercept publishedPercept = Assert.Single(published);
            Assert.Equal("runtime activation evidence", publishedPercept.Content);
            Assert.Equal("external_test_voice", publishedPercept.SourceVoiceID);
            ObservedSpeech committedObservation = Assert.Single(committed);
            Assert.Equal("runtime activation evidence", committedObservation.Content);
            Assert.Equal("external_test_voice", committedObservation.VoiceId);

            vadim.RefreshComponents();
            hearing.ReceiveVoice("second runtime activation evidence", new TestVoice("external_test_voice"));
            Assert.Equal(2, committed.Count);
            Assert.Equal("second runtime activation evidence", committed[1].Content);

            hearing.ReceiveVoice("self speech", vadim.Voice!);
            Assert.Equal(2, committed.Count);

        }
        finally
        {
            vadim.ComponentsRefreshed -= OnComponentProjectionCommitted;
            vadimNode.QueueFree();
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

        public void Speak(string speech)
        {
        }

        public ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
