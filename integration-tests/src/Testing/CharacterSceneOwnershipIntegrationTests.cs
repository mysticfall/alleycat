using System.Collections;
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
    private const string PlayerScenePath = "res://assets/characters/reference/player.tscn";
    private const string ReferenceFemalePlayerScenePath = "res://assets/characters/templates/reference_female/reference_female_player.tscn";
    private const string AllyScenePath = "res://assets/characters/reference/ally.tscn";
    private const string ReferenceFemaleNpcScenePath = "res://assets/characters/templates/reference_female/reference_female_npc.tscn";
    private const string AgenticMindTypeName = "AlleyCat.Mind.AI.AgenticMind";
    private const string AIVoiceTypeName = "AlleyCat.Body.Voice.AIVoice";
    private const string A2FLipSyncPlayerTypeName = "AlleyCat.Speech.LipSync.A2FLipSyncPlayer";
    private const string OpenAISpeechGeneratorTypeName = "AlleyCat.Speech.Generation.OpenAISpeechGenerator";
    private const string OpenAITranscriberTypeName = "AlleyCat.Speech.Transcription.OpenAITranscriber";
    private const string PlayerVoiceTypeName = "AlleyCat.Body.Voice.PlayerVoice";

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
        AssertAllyVoiceAndMind();
    }

    private static void AssertReferencePlayerSceneDoesNotSerialiseConversationNodes()
    {
        string sceneText = ReadResourceText(PlayerScenePath);

        Assert.DoesNotContain("PlayerVoice", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayerVoice.cs", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAITranscriber.cs", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("uid://dyffnsg0122vb", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("[editable path=\"VRIK\"]", sceneText, StringComparison.Ordinal);
    }

    private static void AssertReferenceNpcSceneDoesNotSerialiseConversationNodes()
    {
        string sceneText = ReadResourceText(AllyScenePath);

        Assert.DoesNotContain("AIVoice.cs", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("AgenticMind.cs", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("[node name=\"Voice\"", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("[node name=\"Mind\"", sceneText, StringComparison.Ordinal);
    }

    private static void AssertReferenceFemalePlayerVoice()
    {
        string sceneText = ReadResourceText(ReferenceFemalePlayerScenePath);
        Assert.Contains("uid=\"uid://dyffnsg0122vb\" path=\"res://src/Body/Voice/PlayerVoice.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("Voice = NodePath(\"Female/GeneralSkeleton/Head/Voice\")", sceneText, StringComparison.Ordinal);
        Assert.Contains("[node name=\"Voice\" type=\"Node3D\" parent=\"Female/GeneralSkeleton/Head\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("Transcriber = NodePath(\"../../../../OpenAITranscriber\")", sceneText, StringComparison.Ordinal);
        Assert.Contains("metadata/_custom_type_script = \"uid://dyffnsg0122vb\"", sceneText, StringComparison.Ordinal);

        Node player = LoadPackedScene(ReferenceFemalePlayerScenePath).Instantiate();
        try
        {
            Node voice = RequireScriptedNode(player, "Female/GeneralSkeleton/Head/Voice", PlayerVoiceTypeName);
            Node transcriber = RequireScriptedNode(player, "OpenAITranscriber", OpenAITranscriberTypeName);

            Assert.Equal("player", GetPropertyValue<string>(voice, "Id"));
            Assert.Same(transcriber, GetPropertyValue<Node>(voice, "Transcriber"));
            Assert.Equal(new NodePath("../../../../OpenAITranscriber"), voice.GetPathTo(transcriber));
        }
        finally
        {
            player.Free();
        }
    }

    private static void AssertAllyVoiceAndMind()
    {
        string sceneText = ReadResourceText(ReferenceFemaleNpcScenePath);

        Assert.Contains("uid=\"uid://cwfjtq7oif2yk\" path=\"res://src/Body/Voice/AIVoice.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://rqxjkfgkwfpc\" path=\"res://src/Speech/Generation/OpenAISpeechGenerator.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://cjjllyn8qs4nk\" path=\"res://src/Speech/LipSync/A2FLipSyncPlayer.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://hadsjgek6b2p\" path=\"res://src/Mind/AI/AgenticMind.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://bcokws68yoalk\" path=\"res://src/Mind/AI/Prompting/PromptStack.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://d0put3qinfuxa\" path=\"res://src/Mind/AI/Tool/SpeechTool.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://sy610kavjr0b\" path=\"res://src/Mind/AI/Prompting/TextPromptSection.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("SystemInstruction = SubResource(\"Resource_pt7qm\")", sceneText, StringComparison.Ordinal);
        Assert.Contains("Tools = Array[ExtResource(\"10_v2tt5\")]([SubResource(\"Resource_agentic_speech_tool\")])", sceneText, StringComparison.Ordinal);
        Assert.Contains("You are Alley, a warm, observant person standing with the player in a VR room.", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("AlleyVoice", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("../../../Female/Female/GeneralSkeleton", sceneText, StringComparison.Ordinal);

        Assert.Contains("Skeleton = NodePath(\"../../..\")", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("Meshes = [NodePath", sceneText, StringComparison.Ordinal);

        Node ally = LoadPackedScene(ReferenceFemaleNpcScenePath).Instantiate();
        try
        {
            Node voice = RequireScriptedNode(ally, "Female/GeneralSkeleton/Head/Voice", AIVoiceTypeName);
            Node mind = RequireScriptedNode(ally, "Mind", AgenticMindTypeName);
            Node speechGenerator = RequireScriptedNode(ally, "Female/GeneralSkeleton/Head/Voice/SpeechGenerator", OpenAISpeechGeneratorTypeName);
            Node lipSyncPlayer = RequireScriptedNode(ally, "Female/GeneralSkeleton/Head/Voice/LipSyncPlayer", A2FLipSyncPlayerTypeName);
            AudioStreamPlayer3D audioPlayer = Assert.IsType<AudioStreamPlayer3D>(
                ally.GetNodeOrNull("Female/GeneralSkeleton/Head/Voice/AudioStreamPlayer3D"),
                exactMatch: false);

            Assert.Equal("Elena.wav", GetPropertyValue<string>(voice, "Id"));
            Assert.Same(speechGenerator, GetPropertyValue<Node>(voice, "SpeechGenerator"));
            Assert.Same(lipSyncPlayer, GetPropertyValue<Node>(voice, "LipSyncPlayer"));
            Assert.Same(voice, GetPropertyValue<Node>(mind, "Voice"));
            Assert.Equal(new NodePath("../Female/GeneralSkeleton/Head/Voice"), mind.GetPathTo(voice));
            Assert.Equal("Elena.wav", GetPropertyValue<string>(speechGenerator, "VoiceOverride"));
            Assert.Equal(16000, GetPropertyValue<int>(speechGenerator, "TargetSampleRate"));
            Assert.Equal(0.6f, GetPropertyValue<float>(lipSyncPlayer, "InputStrength"), 4);
            Assert.True(GetPropertyValue<bool>(lipSyncPlayer, "ConstantNoise"));
            Assert.Equal(0.15f, GetPropertyValue<float>(lipSyncPlayer, "EyeRotationToBlendshapeScale"), 4);
            Assert.Same(ally.GetNode<Skeleton3D>("Female/GeneralSkeleton"), GetPropertyValue<Skeleton3D>(lipSyncPlayer, "Skeleton"));
            Assert.Same(audioPlayer, GetPropertyValue<AudioStreamPlayer3D>(lipSyncPlayer, "AudioPlayer"));
            Assert.Equal(new NodePath("../../.."), lipSyncPlayer.GetPathTo(GetPropertyValue<Skeleton3D>(lipSyncPlayer, "Skeleton")));
            Assert.Equal(new NodePath("../AudioStreamPlayer3D"), lipSyncPlayer.GetPathTo(audioPlayer));
            AssertAllyMindPromptAndTools(mind);
        }
        finally
        {
            ally.Free();
        }
    }

    private static void AssertAllyMindPromptAndTools(Node mind)
    {
        object systemInstruction = GetRequiredPropertyValue(mind, "SystemInstruction");
        Assert.Equal("AlleyCat.Mind.AI.Prompting.PromptStack", systemInstruction.GetType().FullName);

        Array sections = Assert.IsAssignableFrom<Array>(GetRequiredPropertyValue(systemInstruction, "Sections"));
        object section = Assert.Single(sections.Cast<object>());
        Assert.Equal("AlleyCat.Mind.AI.Prompting.TextPromptSection", section.GetType().FullName);
        Assert.Equal("Instructions", GetPropertyValue<string>(section, "Name"));
        string sectionText = GetPropertyValue<string>(section, "Text");
        Assert.Contains("You are Alley, a warm, observant person standing with the player in a VR room.", sectionText, StringComparison.Ordinal);
        Assert.Contains("call the speak tool exactly once", sectionText, StringComparison.Ordinal);

        IEnumerable tools = Assert.IsAssignableFrom<IEnumerable>(GetRequiredPropertyValue(mind, "Tools"));
        object tool = Assert.Single(tools.Cast<object>());
        Assert.Equal("AlleyCat.Mind.AI.Tool.SpeechTool", tool.GetType().FullName);
        Assert.Equal("speak", GetPropertyValue<string>(tool, "ToolName"));
        Assert.Equal("Speak the supplied text aloud through the configured voice.", GetPropertyValue<string>(tool, "ToolDescription"));
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
