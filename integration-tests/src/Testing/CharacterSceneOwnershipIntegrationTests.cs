using System.Collections;
using System.Reflection;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Speech;
using AlleyCat.Speech.Transcription;
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
    private const string ReferenceFemaleBaseScenePath = "res://assets/characters/templates/reference_female/reference_female_base.tscn";
    private const string ReferenceMaleBaseScenePath = "res://assets/characters/templates/reference_male/reference_male_base.tscn";
    private const string AgenticMindTypeName = "AlleyCat.Mind.AI.AgenticMind";
    private const string AIVoiceTypeName = "AlleyCat.Speech.Voice.AIVoice";
    private const string A2FLipSyncPlayerTypeName = "AlleyCat.Speech.LipSync.A2FLipSyncPlayer";
    private const string SupertonicSpeechGeneratorTypeName = "AlleyCat.Speech.Generation.SupertonicSpeechGenerator";
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
        AssertCharacterBaseTemplatesDoNotAuthorContextSources();
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
        Assert.Contains(
            "uid=\"uid://cmo0rjfoojh7u\" path=\"res://src/Speech/Transcription/OpenAITranscriber.cs\"",
            sceneText,
            StringComparison.Ordinal);
        Assert.Contains("metadata/_custom_type_script = \"uid://cmo0rjfoojh7u\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("metadata/_custom_type_script = \"uid://dyffnsg0122vb\"", sceneText, StringComparison.Ordinal);
        // The transcriber node pins only local input behaviour: backend host, model, and hints stay config-file-owned.
        Assert.DoesNotContain("RESTEndpoint", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("WebSocketURI", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("Hotwords", sceneText, StringComparison.Ordinal);

        Node player = LoadPackedScene(ReferenceFemalePlayerScenePath).Instantiate();
        try
        {
            Node voice = RequireScriptedNode(player, "Female/GeneralSkeleton/Head/Voice", PlayerVoiceTypeName);
            Node transcriber = RequireScriptedNode(player, "OpenAITranscriber", OpenAITranscriberTypeName);

            Assert.Equal("reference_female_player", GetPropertyValue<string>(voice, "Id"));
            Assert.Same(transcriber, GetPropertyValue<Node>(voice, "Transcriber"));
            Assert.Equal(new NodePath("../../../../OpenAITranscriber"), voice.GetPathTo(transcriber));
            Assert.Equal(VoiceInputMode.ButtonAndAutomatic, GetPropertyValue<VoiceInputMode>(transcriber, "InputMode"));
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
        Assert.Contains("uid=\"uid://cs50pi5oc7ofn\" path=\"res://src/Speech/Generation/Supertonic/SupertonicSpeechGenerator.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://cs50pi5oc7ofn\" path=\"res://src/Speech/Generation/Supertonic/SupertonicSpeechGenerator.cs\"", maleSceneText, StringComparison.Ordinal);
        Assert.Contains("metadata/_custom_type_script = \"uid://cs50pi5oc7ofn\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("metadata/_custom_type_script = \"uid://cs50pi5oc7ofn\"", maleSceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://cjjllyn8qs4nk\" path=\"res://src/Speech/LipSync/A2FLipSyncPlayer.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://hadsjgek6b2p\" path=\"res://src/Mind/AI/AgenticMind.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://dvw63im28183y\" path=\"res://assets/characters/prompts/generic_npc_prompt_stack.tres\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://dvw63im28183y\" path=\"res://assets/characters/prompts/generic_npc_prompt_stack.tres\"", maleSceneText, StringComparison.Ordinal);
        Assert.Contains("SystemInstruction = ExtResource(\"9_beijb\")", sceneText, StringComparison.Ordinal);
        Assert.Contains(
            "EventHistoryPath = \"res://prompts/event_history.md\"",
            sceneText,
            StringComparison.Ordinal);
        Assert.Contains(
            "EventHistoryPath = \"res://prompts/event_history.md\"",
            maleSceneText,
            StringComparison.Ordinal);
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
            Node speechGenerator = RequireScriptedNode(femaleNpc, "Female/GeneralSkeleton/Head/Voice/SpeechGenerator", SupertonicSpeechGeneratorTypeName);
            Node maleSpeechGenerator = RequireScriptedNode(maleNpc, "Male/GeneralSkeleton/Head/Voice/SpeechGenerator", SupertonicSpeechGeneratorTypeName);
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
            Assert.Equal("F1", GetPropertyValue<string>(speechGenerator, "Voice"));
            Assert.Equal("M1", GetPropertyValue<string>(maleSpeechGenerator, "Voice"));
            Assert.Equal(string.Empty, GetPropertyValue<string>(speechGenerator, "VoiceOverride"));
            Assert.Equal(string.Empty, GetPropertyValue<string>(maleSpeechGenerator, "VoiceOverride"));
            Assert.Equal(0.6f, GetPropertyValue<float>(lipSyncPlayer, "InputStrength"), 4);
            Assert.True(GetPropertyValue<bool>(lipSyncPlayer, "ConstantNoise"));
            Assert.Equal(0.15f, GetPropertyValue<float>(lipSyncPlayer, "EyeRotationToBlendshapeScale"), 4);
            Assert.Equal(16000, GetNonPublicPropertyValue<int>(lipSyncPlayer, "BackendSampleRate"));
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
            Assert.Equal(
                "res://prompts/event_history.md",
                GetPropertyValue<string>(maleMind, "EventHistoryPath"));
        }
        finally
        {
            femaleNpc.Free();
            maleNpc.Free();
        }
    }

    /// <summary>
    /// Character base templates author no context-source collections; the retired wiring stays absent.
    /// </summary>
    private static void AssertCharacterBaseTemplatesDoNotAuthorContextSources()
    {
        string femaleBaseText = ReadResourceText(ReferenceFemaleBaseScenePath);
        string maleBaseText = ReadResourceText(ReferenceMaleBaseScenePath);

        // No character template authors a ContextSources collection; the retired wiring stays absent.
        Assert.DoesNotContain("ContextSources", femaleBaseText, StringComparison.Ordinal);
        Assert.DoesNotContain("CharacterCardContextSource.cs", femaleBaseText, StringComparison.Ordinal);
        Assert.DoesNotContain("ContextSources", maleBaseText, StringComparison.Ordinal);
        Assert.DoesNotContain("CharacterCardContextSource.cs", maleBaseText, StringComparison.Ordinal);
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

        // Event history is authored as the standalone fragment file wired by path on AgenticMind (AI-003 TR-12).
        string eventHistoryPath = GetPropertyValue<string>(mind, "EventHistoryPath");
        Assert.Equal("res://prompts/event_history.md", eventHistoryPath);
        string eventHistory = ReadResourceText(eventHistoryPath);
        Assert.Contains("<!-- event-history: speech.observed -->", eventHistory, StringComparison.Ordinal);
        Assert.Contains("<!-- event-history: fallback -->", eventHistory, StringComparison.Ordinal);
        Assert.Contains("{% if ActorId != blank %}", eventHistory, StringComparison.Ordinal);
        Assert.Contains("ActorId == character.FullId", eventHistory, StringComparison.Ordinal);
        Assert.Contains("I said: {{ Content }}", eventHistory, StringComparison.Ordinal);
        Assert.Contains("Heard {{ ActorId }} say: {{ Content }}", eventHistory, StringComparison.Ordinal);
        Assert.Contains("Heard an unknown speaker say: {{ Content }}", eventHistory, StringComparison.Ordinal);
        Assert.DoesNotContain("VoiceId", eventHistory, StringComparison.Ordinal);
        Assert.Equal(
            "((Received {{ TypeKey }} event.)){% if ObservedAt != blank %} (at {{ nf(ObservedAt, 1) }}s game time){% endif %}\n",
            GetEventHistoryFallbackSource(eventHistory));
        Assert.DoesNotContain("VoiceId", GetEventHistoryFallbackSource(eventHistory), StringComparison.Ordinal);

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

    private static T GetNonPublicPropertyValue<T>(object source, string propertyName)
    {
        PropertyInfo property = source.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException(
                $"Expected non-public property '{propertyName}' on '{source.GetType().FullName}' to be present.");
        object? value = property.GetValue(source);
        return value is not null
            ? Assert.IsAssignableFrom<T>(value)
            : throw new Xunit.Sdk.XunitException(
                $"Expected non-public property '{propertyName}' on '{source.GetType().FullName}' to be non-null.");
    }

    private static object GetRequiredPropertyValue(object source, string propertyName)
    {
        object? value = source.GetType().GetProperty(propertyName)?.GetValue(source);
        return value ?? throw new Xunit.Sdk.XunitException(
            $"Expected property '{propertyName}' on '{source.GetType().FullName}' to be present and non-null.");
    }

    private static string GetEventHistoryFallbackSource(string eventHistoryFileText)
    {
        const string fallbackDelimiter = "<!-- event-history: fallback -->\n";
        int delimiterEnd = eventHistoryFileText.IndexOf(fallbackDelimiter, StringComparison.Ordinal);
        Assert.True(
            delimiterEnd >= 0,
            "Expected the authored event-history file to contain a fallback section.");
        return eventHistoryFileText[(delimiterEnd + fallbackDelimiter.Length)..];
    }

    private static string ReadResourceText(string path)
    {
        string text = Godot.FileAccess.GetFileAsString(path);
        return !string.IsNullOrEmpty(text)
            ? text
            : throw new Xunit.Sdk.XunitException($"Expected text resource '{path}' to be readable.");
    }
}
