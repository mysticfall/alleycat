using System.Collections;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Speech;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Testing;

/// <summary>
/// Integration coverage for reference-female character scene ownership of voice and mind nodes.
/// </summary>
public sealed class CharacterSceneOwnershipIntegrationTests
{
    private const string AllyPlayerScenePath = "res://assets/characters/reference/ally_player.tscn";
    private const string ReferenceFemalePlayerScenePath = "res://assets/characters/templates/reference_female/reference_female_player.tscn";
    private const string AllyNpcScenePath = "res://assets/characters/reference/ally_npc.tscn";
    private const string ReferenceFemaleNpcScenePath = "res://assets/characters/templates/reference_female/reference_female_npc.tscn";
    private const string AgenticMindTypeName = "AlleyCat.Mind.AI.AgenticMind";
    private const string AIVoiceTypeName = "AlleyCat.Speech.Voice.AIVoice";
    private const string A2FLipSyncPlayerTypeName = "AlleyCat.Speech.LipSync.A2FLipSyncPlayer";
    private const string OpenAISpeechGeneratorTypeName = "AlleyCat.Speech.Generation.OpenAISpeechGenerator";
    private const string OpenAITranscriberTypeName = "AlleyCat.Speech.Transcription.OpenAITranscriber";
    private const string PlayerVoiceTypeName = "AlleyCat.Speech.Voice.PlayerVoice";
    private static readonly string _hearingTypeName = typeof(Hearing).FullName!;

    /// <summary>
    /// Voice and mind components live with the reference-female player/NPC character scenes.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemaleCharacterScenes_OwnVoiceAndMindNodes()
    {
        AssertReferencePlayerSceneDoesNotSerialiseConversationNodes();
        AssertReferenceNpcSceneDoesNotSerialiseConversationNodes();
        AssertReferenceFemalePlayerVoice();
        AssertNpcVoiceAndSharedMindPrompt();
    }

    private static void AssertReferencePlayerSceneDoesNotSerialiseConversationNodes()
    {
        string sceneText = ReadResourceText(AllyPlayerScenePath);

        Assert.DoesNotContain("PlayerVoice", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayerVoice.cs", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAITranscriber.cs", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("uid://dyffnsg0122vb", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("[editable path=\"VRIK\"]", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("CharacterPerception", sceneText, StringComparison.Ordinal);
    }

    private static void AssertReferenceNpcSceneDoesNotSerialiseConversationNodes()
    {
        string sceneText = ReadResourceText(AllyNpcScenePath);

        Assert.DoesNotContain("AIVoice.cs", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("AgenticMind.cs", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("[node name=\"Voice\"", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("[node name=\"Mind\"", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("CharacterPerception", sceneText, StringComparison.Ordinal);
    }

    private static void AssertReferenceFemalePlayerVoice()
    {
        string sceneText = ReadResourceText(ReferenceFemalePlayerScenePath);
        Assert.Contains("uid=\"uid://dyffnsg0122vb\" path=\"res://src/Speech/Voice/PlayerVoice.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("Voice = NodePath(\"Female/GeneralSkeleton/Head/Voice\")", sceneText, StringComparison.Ordinal);
        Assert.Contains("[node name=\"Voice\" type=\"Node3D\" parent=\"Female/GeneralSkeleton/Head\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("Transcriber = NodePath(\"../../../../OpenAITranscriber\")", sceneText, StringComparison.Ordinal);
        Assert.Contains("metadata/_custom_type_script = \"uid://dyffnsg0122vb\"", sceneText, StringComparison.Ordinal);

        Node player = LoadPackedScene(ReferenceFemalePlayerScenePath).Instantiate();
        try
        {
            Node voice = RequireScriptedNode(player, "Female/GeneralSkeleton/Head/Voice", PlayerVoiceTypeName);
            Node transcriber = RequireScriptedNode(player, "OpenAITranscriber", OpenAITranscriberTypeName);

            Assert.Equal("reference_female_player", GetPropertyValue<string>(voice, "Id"));
            Assert.Same(transcriber, GetPropertyValue<Node>(voice, "Transcriber"));
            Assert.Equal(new NodePath("../../../../OpenAITranscriber"), voice.GetPathTo(transcriber));
        }
        finally
        {
            player.Free();
        }
    }

    private static void AssertNpcVoiceAndSharedMindPrompt()
    {
        string sceneText = ReadResourceText(ReferenceFemaleNpcScenePath);
        string maleSceneText = ReadResourceText("res://assets/characters/templates/reference_male/reference_male_npc.tscn");

        Assert.Contains("uid=\"uid://cwfjtq7oif2yk\" path=\"res://src/Speech/Voice/AIVoice.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://rqxjkfgkwfpc\" path=\"res://src/Speech/Generation/OpenAISpeechGenerator.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://cjjllyn8qs4nk\" path=\"res://src/Speech/LipSync/A2FLipSyncPlayer.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://hadsjgek6b2p\" path=\"res://src/Mind/AI/AgenticMind.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://dvw63im28183y\" path=\"res://assets/characters/prompts/generic_npc_prompt_stack.tres\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://dvw63im28183y\" path=\"res://assets/characters/prompts/generic_npc_prompt_stack.tres\"", maleSceneText, StringComparison.Ordinal);
        Assert.Contains("SystemInstruction = ExtResource(\"9_beijb\")", sceneText, StringComparison.Ordinal);
        Assert.Contains(
            "uid=\"uid://dv8k4pqrmxe1n\" path=\"res://assets/characters/prompts/npc_event_history.tres\"",
            sceneText,
            StringComparison.Ordinal);
        Assert.Contains(
            "uid=\"uid://dv8k4pqrmxe1n\" path=\"res://assets/characters/prompts/npc_event_history.tres\"",
            maleSceneText,
            StringComparison.Ordinal);
        Assert.Contains("EventHistory = ExtResource(\"10_history\")", sceneText, StringComparison.Ordinal);
        Assert.Contains("EventHistory = ExtResource(\"10_history\")", maleSceneText, StringComparison.Ordinal);
        // The production tool inventory is created internally by AgenticMind without scene authoring (AI-002
        // TR-16): neither template authors tool resources.
        Assert.DoesNotContain("SpeechTool.cs", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("Tools = ", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("SpeechTool.cs", maleSceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("You are Alley", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("Vadim", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("../../../Female/Female/GeneralSkeleton", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("[node name=\"Mind\" type=\"Node\" parent=\".\" index=\"9\" unique_id=917502219 node_paths=PackedStringArray(\"Voice\")", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("Voice = NodePath(\"../Female/GeneralSkeleton/Head/Voice\")", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("Voice = NodePath(\"../Male/GeneralSkeleton/Head/Voice\")", maleSceneText, StringComparison.Ordinal);

        Assert.Contains("Skeleton = NodePath(\"../../..\")", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("Meshes = [NodePath", sceneText, StringComparison.Ordinal);

        Node femaleNpc = LoadPackedScene(ReferenceFemaleNpcScenePath).Instantiate();
        Node maleNpc = LoadPackedScene("res://assets/characters/templates/reference_male/reference_male_npc.tscn").Instantiate();
        try
        {
            Node voice = RequireScriptedNode(femaleNpc, "Female/GeneralSkeleton/Head/Voice", AIVoiceTypeName);
            Node mind = RequireScriptedNode(femaleNpc, "Mind", AgenticMindTypeName);
            Node maleMind = RequireScriptedNode(maleNpc, "Mind", AgenticMindTypeName);
            Node femaleHearing = RequireScriptedNode(femaleNpc, "Hearing", _hearingTypeName);
            Node maleHearing = RequireScriptedNode(maleNpc, "Hearing", _hearingTypeName);
            Node speechGenerator = RequireScriptedNode(femaleNpc, "Female/GeneralSkeleton/Head/Voice/SpeechGenerator", OpenAISpeechGeneratorTypeName);
            Node lipSyncPlayer = RequireScriptedNode(femaleNpc, "Female/GeneralSkeleton/Head/Voice/LipSyncPlayer", A2FLipSyncPlayerTypeName);
            AudioStreamPlayer3D audioPlayer = Assert.IsType<AudioStreamPlayer3D>(
                femaleNpc.GetNodeOrNull("Female/GeneralSkeleton/Head/Voice/AudioStreamPlayer3D"),
                exactMatch: false);

            Assert.Equal("reference_female_npc", GetPropertyValue<string>(voice, "Id"));
            Assert.Same(speechGenerator, GetPropertyValue<Node>(voice, "SpeechGenerator"));
            Assert.Same(lipSyncPlayer, GetPropertyValue<Node>(voice, "LipSyncPlayer"));
            Assert.Same(femaleNpc, femaleHearing.GetParent());
            Assert.Same(maleNpc, maleHearing.GetParent());
            _ = Assert.Single(femaleNpc.GetChildren(), child => child.GetType().FullName == _hearingTypeName);
            _ = Assert.Single(maleNpc.GetChildren(), child => child.GetType().FullName == _hearingTypeName);
            Assert.Equal("Elena.wav", GetPropertyValue<string>(speechGenerator, "VoiceOverride"));
            Assert.Equal(16000, GetPropertyValue<int>(speechGenerator, "TargetSampleRate"));
            Assert.Equal(0.6f, GetPropertyValue<float>(lipSyncPlayer, "InputStrength"), 4);
            Assert.True(GetPropertyValue<bool>(lipSyncPlayer, "ConstantNoise"));
            Assert.Equal(0.15f, GetPropertyValue<float>(lipSyncPlayer, "EyeRotationToBlendshapeScale"), 4);
            Assert.Same(femaleNpc.GetNode<Skeleton3D>("Female/GeneralSkeleton"), GetPropertyValue<Skeleton3D>(lipSyncPlayer, "Skeleton"));
            Assert.Same(audioPlayer, GetPropertyValue<AudioStreamPlayer3D>(lipSyncPlayer, "AudioPlayer"));
            Assert.Equal(new NodePath("../../.."), lipSyncPlayer.GetPathTo(GetPropertyValue<Skeleton3D>(lipSyncPlayer, "Skeleton")));
            Assert.Equal(new NodePath("../AudioStreamPlayer3D"), lipSyncPlayer.GetPathTo(audioPlayer));
            AssertNpcMindPromptAndTools(mind);
            PromptStack promptByUID = Assert.IsType<PromptStack>(
                ResourceLoader.Load("uid://dvw63im28183y"),
                exactMatch: false);
            Assert.Same(promptByUID, GetRequiredPropertyValue(mind, "SystemInstruction"));
            Assert.Same(
                GetRequiredPropertyValue(mind, "SystemInstruction"),
                GetRequiredPropertyValue(maleMind, "SystemInstruction"));
            Assert.Same(
                GetRequiredPropertyValue(mind, "EventHistory"),
                GetRequiredPropertyValue(maleMind, "EventHistory"));
        }
        finally
        {
            femaleNpc.Free();
            maleNpc.Free();
        }
    }

    private static void AssertNpcMindPromptAndTools(Node mind)
    {
        object systemInstruction = GetRequiredPropertyValue(mind, "SystemInstruction");
        Assert.Equal("AlleyCat.Mind.AI.Prompting.PromptStack", systemInstruction.GetType().FullName);

        Array sections = Assert.IsAssignableFrom<Array>(GetRequiredPropertyValue(systemInstruction, "Sections"));
        object[] orderedSections = [.. sections.Cast<object>()];
        Assert.Equal(4, orderedSections.Length);
        object instructionSection = orderedSections[0];
        Assert.Equal("AlleyCat.Mind.AI.Prompting.FilePromptSection", instructionSection.GetType().FullName);
        Assert.Equal("Instructions", GetPropertyValue<string>(instructionSection, "Name"));
        string sectionFilePath = GetPropertyValue<string>(instructionSection, "FilePath");
        Assert.Equal("res://prompts/mind.md", sectionFilePath);
        string sectionText = ReadResourceText(sectionFilePath);
        Assert.Contains("You are {{ character.FullId }}", sectionText, StringComparison.Ordinal);
        Assert.Contains("every response you give is a tool call", sectionText, StringComparison.Ordinal);
        Assert.Contains("seconds of in-game time since the game began", sectionText, StringComparison.Ordinal);
        Assert.DoesNotContain("Alley", sectionText, StringComparison.Ordinal);
        Assert.DoesNotContain("Vadim", sectionText, StringComparison.Ordinal);
        Assert.Equal("AlleyCat.Mind.AI.Prompting.EssentialLorePromptSection", orderedSections[1].GetType().FullName);
        Assert.Equal("Lore", GetPropertyValue<string>(orderedSections[1], "Name"));
        Assert.Equal("AlleyCat.Mind.AI.Prompting.CharacterLorePromptSection", orderedSections[2].GetType().FullName);
        Assert.Equal("Characters", GetPropertyValue<string>(orderedSections[2], "Name"));
        object scenarioSection = orderedSections[3];
        Assert.Equal("AlleyCat.Mind.AI.Prompting.FilePromptSection", scenarioSection.GetType().FullName);
        Assert.Equal("res://prompts/scenario.md", GetPropertyValue<string>(scenarioSection, "FilePath"));
        Assert.Equal("Scenario", GetPropertyValue<string>(scenarioSection, "Name"));
        // The shared generic NPC prompt stack contains no event-history section (AI-003 TR-23).
        Assert.DoesNotContain(
            orderedSections,
            section => section.GetType().FullName == "AlleyCat.Mind.AI.Prompting.EventHistory");

        // Event history is authored as the standalone EventHistory resource exported by AgenticMind (AI-003 TR-12).
        object eventHistory = GetRequiredPropertyValue(mind, "EventHistory");
        Assert.Equal("AlleyCat.Mind.AI.Prompting.EventHistory", eventHistory.GetType().FullName);
        Array fragments = Assert.IsAssignableFrom<Array>(GetRequiredPropertyValue(eventHistory, "Fragments"));
        Assert.Equal(["speech.observed"], fragments.Cast<object>()
            .Select(fragment => GetPropertyValue<string>(fragment, "TypeKey")));
        object speechFragment = Assert.Single(fragments.Cast<object>());
        string speechSource = GetPropertyValue<string>(speechFragment, "Source");
        Assert.Contains("eqOrdinal ActorId @root.character.FullId", speechSource, StringComparison.Ordinal);
        Assert.Contains("I said: {{Content}}", speechSource, StringComparison.Ordinal);
        Assert.Contains("Heard {{ActorId}} say: {{Content}}", speechSource, StringComparison.Ordinal);
        Assert.Contains("Heard an unknown speaker say: {{Content}}", speechSource, StringComparison.Ordinal);
        Assert.DoesNotContain("VoiceId", speechSource, StringComparison.Ordinal);
        string fallbackSource = GetPropertyValue<string>(eventHistory, "FallbackSource");
        Assert.Equal(
            "((Received {{TypeKey}} event.)){{#if ObservedAt}} (at {{nf ObservedAt 1}}s game time){{/if}}\n",
            fallbackSource);
        Assert.DoesNotContain("VoiceId", fallbackSource, StringComparison.Ordinal);

        // The production tool inventory (speak, wait, history) is created internally without scene authoring
        // (AI-002 TR-16): authored tools remain an extension point and are empty in the shared templates.
        IEnumerable tools = Assert.IsAssignableFrom<IEnumerable>(GetRequiredPropertyValue(mind, "Tools"));
        Assert.Empty(tools.Cast<object>());
    }

    private static Node RequireScriptedNode(Node root, string path, string expectedTypeName)
    {
        Node node = root.GetNodeOrNull(path)
            ?? throw new Xunit.Sdk.XunitException($"Expected scene node '{path}' to exist.");
        Assert.Equal(expectedTypeName, node.GetType().FullName);
        return node;
    }

    private static T GetPropertyValue<T>(object source, string propertyName)
    {
        object value = GetRequiredPropertyValue(source, propertyName);
        return Assert.IsAssignableFrom<T>(value);
    }

    private static object GetRequiredPropertyValue(object source, string propertyName)
    {
        object? value = source.GetType().GetProperty(propertyName)?.GetValue(source);
        return value ?? throw new Xunit.Sdk.XunitException(
            $"Expected property '{propertyName}' on '{source.GetType().FullName}' to be present and non-null.");
    }

    private static string ReadResourceText(string path)
    {
        string text = Godot.FileAccess.GetFileAsString(path);
        return !string.IsNullOrEmpty(text)
            ? text
            : throw new Xunit.Sdk.XunitException($"Expected text resource '{path}' to be readable.");
    }
}
