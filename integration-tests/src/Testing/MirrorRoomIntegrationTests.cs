using System.Collections;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Testing;

/// <summary>
/// Integration coverage for the mirror-room test scene and its character template installation path.
/// </summary>
public sealed class MirrorRoomIntegrationTests
{
    private const string MirrorRoomScenePath = "res://assets/testing/mirror_room/mirror_room.tscn";
    private const string PlayerScenePath = "res://assets/characters/reference/player.tscn";
    private const string ReferenceFemalePlayerScenePath = "res://assets/characters/templates/reference_female/reference_female_player.tscn";
    private const string AllyScenePath = "res://assets/characters/reference/ally.tscn";
    private const string ReferenceFemaleBaseScenePath = "res://assets/characters/templates/reference_female/reference_female_base.tscn";
    private const string StaleViewpointOverrideNodeHeader = "[node name=\"Viewpoint\" parent=\"Female/GeneralSkeleton/Head\" index=\"0\"]";
    private const string VanishedViewpointParentWarning = "Parent path './Female/GeneralSkeleton/Head' for node 'Viewpoint' has vanished";
    private const string VanishedViewpointModificationWarning = "Node './Viewpoint' was modified from inside an instance, but it has vanished";
    private const string AgenticMindTypeName = "AlleyCat.Mind.AI.AgenticMind";
    private const string AIVoiceTypeName = "AlleyCat.Body.Voice.AIVoice";
    private const string A2FLipSyncPlayerTypeName = "AlleyCat.Speech.LipSync.A2FLipSyncPlayer";
    private const string OpenAISpeechGeneratorTypeName = "AlleyCat.Speech.Generation.OpenAISpeechGenerator";
    private const string OpenAITranscriberTypeName = "AlleyCat.Speech.Transcription.OpenAITranscriber";
    private const string PlayerVoiceTypeName = "AlleyCat.Body.Voice.PlayerVoice";
    private const float BasisTolerance = 0.0002f;

    private static readonly Basis _expectedPlayerRootBasis = Basis.Identity;
    private static readonly Vector3 _expectedPlayerRootPosition = Vector3.Zero;
    private static readonly Basis _expectedAllyRootBasis = new(
        new Vector3(-0.7993387f, 0f, 0.6008807f),
        Vector3.Up,
        new Vector3(-0.6008807f, 0f, -0.7993387f));
    private static readonly Vector3 _expectedAllyRootPosition = new(-1.324f, 0f, -0.868f);
    private static readonly Basis _referenceFemaleModelCompensationBasis = new(
        new Vector3(-1f, 0f, 8.742278e-08f),
        Vector3.Up,
        new Vector3(-8.742278e-08f, 0f, -1f));
    private static readonly Basis _referenceFemaleViewpointBasis = new(
        new Vector3(-1f, 0f, 8.742278e-08f),
        Vector3.Up,
        new Vector3(-8.742278e-08f, 0f, -1f));

    private static readonly string[] _expectedAllyLipSyncMeshPaths =
    [
        "../../Female/Female/GeneralSkeleton/Female_body",
        "../../Female/Female/GeneralSkeleton/Female_eyebrow006",
        "../../Female/Female/GeneralSkeleton/Female_eyelashes01",
        "../../Female/Female/GeneralSkeleton/Female_high-poly",
        "../../Female/Female/GeneralSkeleton/Female_teeth_shape01",
        "../../Female/Female/GeneralSkeleton/Female_tongue01",
    ];

    /// <summary>
    /// The mirror-room scene starts cleanly and keeps actor-root facing separate from reference-model compensation.
    /// </summary>
    [Headless]
    [Fact]
    public async Task MirrorRoom_LoadsWithoutIKTargetDisposalSpam_AndPreservesRootAndModelOrientationFrames()
    {
        AssertNoStaleViewpointOverrideSource(PlayerScenePath);
        AssertNoStaleViewpointOverrideSource(AllyScenePath);
        AssertNoStaleViewpointOverrideSource(ReferenceFemaleBaseScenePath);

        SceneTree sceneTree = GetSceneTree();
        Node mirrorRoom = LoadPackedScene(MirrorRoomScenePath).Instantiate();

        try
        {
            sceneTree.Root.AddChild(mirrorRoom);
            EnsureCharacterRuntimeInstalled(mirrorRoom.GetNode("Actors/Player"));
            EnsureCharacterRuntimeInstalled(mirrorRoom.GetNode("Actors/Female"));
            await WaitForFramesAsync(sceneTree, 8);

            Node3D playerRoot = mirrorRoom.GetNode<Node3D>("Actors/Player");
            Assert.True(playerRoot.HasNode("IKTargets"), "Expected mirror-room player installer to materialise IKTargets.");
            Node3D allyRoot = mirrorRoom.GetNode<Node3D>("Actors/Female");

            AssertActorRootTransform(
                "player",
                playerRoot,
                _expectedPlayerRootBasis,
                _expectedPlayerRootPosition);
            AssertActorRootTransform(
                "ally",
                allyRoot,
                _expectedAllyRootBasis,
                _expectedAllyRootPosition);

            AssertReferenceModelCompensation("player", playerRoot, playerRoot.GetNode<Node3D>("Female"));
            AssertReferenceModelCompensation("ally", allyRoot, allyRoot.GetNode<Node3D>("Female"));
            AssertHeadViewpointInstalled("player", playerRoot);
            AssertHeadViewpointInstalled("ally", allyRoot);
            AssertPlayerRuntimeControlAndGrabBindings(playerRoot);
            AssertRuntimeVisibleFaceDirection("player", playerRoot, Vector3.Forward, minimumDot: 0.95f);
            AssertRuntimeVisibleFaceDirection(
                "ally",
                allyRoot,
                (playerRoot.Transform.Origin - allyRoot.Transform.Origin).Normalized(),
                minimumDot: 0.85f);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(mirrorRoom))
            {
                mirrorRoom.QueueFree();
                await WaitForFramesAsync(sceneTree, 2);
            }
        }
    }

    /// <summary>
    /// Voice and mind components live with the player/NPC character scenes rather than the mirror-room harness.
    /// </summary>
    [Headless]
    [Fact]
    public void CharacterScenes_OwnVoiceAndMindNodes_AndMirrorRoomNoLongerSerialisesThem()
    {
        AssertMirrorRoomDoesNotSerialiseConversationNodes();
        AssertReferenceFemalePlayerVoice();
        AssertAllyVoiceAndMind();
    }

    private static void AssertMirrorRoomDoesNotSerialiseConversationNodes()
    {
        string sceneText = ReadResourceText(MirrorRoomScenePath);

        Assert.DoesNotContain("PlayerVoice", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("AlleyVoice", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("AgenticMind.cs", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayerVoice.cs", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("AIVoice.cs", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAISpeechGenerator.cs", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("A2FLipSyncPlayer.cs", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("uid://hadsjgek6b2p", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("uid://dyffnsg0122vb", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("uid://cwfjtq7oif2yk", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("uid://rqxjkfgkwfpc", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("uid://cjjllyn8qs4nk", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("[editable path=\"Actors/Player/VRIK\"]", sceneText, StringComparison.Ordinal);
    }

    private static void AssertReferenceFemalePlayerVoice()
    {
        string sceneText = ReadResourceText(ReferenceFemalePlayerScenePath);
        Assert.Contains("uid=\"uid://dyffnsg0122vb\" path=\"res://src/Body/Voice/PlayerVoice.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("[node name=\"Voice\" type=\"Node3D\" parent=\".\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("Transcriber = NodePath(\"../OpenAITranscriber\")", sceneText, StringComparison.Ordinal);
        Assert.Contains("metadata/_custom_type_script = \"uid://dyffnsg0122vb\"", sceneText, StringComparison.Ordinal);

        Node player = LoadPackedScene(ReferenceFemalePlayerScenePath).Instantiate();
        try
        {
            Node voice = RequireScriptedNode(player, "Voice", PlayerVoiceTypeName);
            Node transcriber = RequireScriptedNode(player, "OpenAITranscriber", OpenAITranscriberTypeName);

            Assert.Equal("player", GetPropertyValue<string>(voice, "Id"));
            Assert.Same(transcriber, GetPropertyValue<Node>(voice, "Transcriber"));
            Assert.Equal(new NodePath("../OpenAITranscriber"), voice.GetPathTo(transcriber));
        }
        finally
        {
            player.Free();
        }
    }

    private static void AssertAllyVoiceAndMind()
    {
        string sceneText = ReadResourceText(AllyScenePath);
        Assert.Contains("uid=\"uid://cwfjtq7oif2yk\" path=\"res://src/Body/Voice/AIVoice.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://rqxjkfgkwfpc\" path=\"res://src/Speech/Generation/OpenAISpeechGenerator.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://cjjllyn8qs4nk\" path=\"res://src/Speech/LipSync/A2FLipSyncPlayer.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://hadsjgek6b2p\" path=\"res://src/Mind/AI/AgenticMind.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://bcokws68yoalk\" path=\"res://src/Mind/AI/Prompting/PromptStack.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://d0put3qinfuxa\" path=\"res://src/Mind/AI/Tool/SpeechTool.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("uid=\"uid://sy610kavjr0b\" path=\"res://src/Mind/AI/Prompting/TextPromptSection.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("SystemInstruction = SubResource(\"Resource_agentic_system_instruction\")", sceneText, StringComparison.Ordinal);
        Assert.Contains("Tools = [SubResource(\"Resource_agentic_speech_tool\")]", sceneText, StringComparison.Ordinal);
        Assert.Contains("You are Alley, a warm, observant person standing with the player in a VR room.", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("AlleyVoice", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("../../../Female/Female/GeneralSkeleton", sceneText, StringComparison.Ordinal);

        foreach (string meshPath in _expectedAllyLipSyncMeshPaths)
        {
            Assert.Contains($"NodePath(\"{meshPath}\")", sceneText, StringComparison.Ordinal);
        }

        Node ally = LoadPackedScene(AllyScenePath).Instantiate();
        try
        {
            Node voice = RequireScriptedNode(ally, "Voice", AIVoiceTypeName);
            Node mind = RequireScriptedNode(ally, "Mind", AgenticMindTypeName);
            Node speechGenerator = RequireScriptedNode(ally, "Voice/SpeechGenerator", OpenAISpeechGeneratorTypeName);
            Node lipSyncPlayer = RequireScriptedNode(ally, "Voice/LipSyncPlayer", A2FLipSyncPlayerTypeName);
            AudioStreamPlayer3D audioPlayer = Assert.IsType<AudioStreamPlayer3D>(
                ally.GetNodeOrNull("Voice/AudioStreamPlayer3D"),
                exactMatch: false);

            Assert.Equal("alley", GetPropertyValue<string>(voice, "Id"));
            Assert.Same(speechGenerator, GetPropertyValue<Node>(voice, "SpeechGenerator"));
            Assert.Same(lipSyncPlayer, GetPropertyValue<Node>(voice, "LipSyncPlayer"));
            Assert.Same(voice, GetPropertyValue<Node>(mind, "Voice"));
            Assert.Equal(new NodePath("../Voice"), mind.GetPathTo(voice));
            Assert.Equal("Elena.wav", GetPropertyValue<string>(speechGenerator, "VoiceOverride"));
            Assert.Equal(16000, GetPropertyValue<int>(speechGenerator, "TargetSampleRate"));
            Assert.Equal(0.6f, GetPropertyValue<float>(lipSyncPlayer, "InputStrength"), 4);
            Assert.True(GetPropertyValue<bool>(lipSyncPlayer, "ConstantNoise"));
            Assert.Equal(0.15f, GetPropertyValue<float>(lipSyncPlayer, "EyeRotationToBlendshapeScale"), 4);
            Assert.Same(audioPlayer, GetPropertyValue<AudioStreamPlayer3D>(lipSyncPlayer, "AudioPlayer"));
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

    private static void AssertActorRootTransform(
        string actorName,
        Node3D actorRoot,
        Basis expectedBasis,
        Vector3 expectedPosition)
    {
        AssertBasisApproximately(
            expectedBasis,
            actorRoot.Transform.Basis,
            $"Expected mirror-room {actorName} gameplay root to keep its authored world/root orientation. "
            + "Do not compensate the imported model convention by rotating the whole actor root.");

        Assert.True(
            actorRoot.Transform.Origin.DistanceTo(expectedPosition) <= BasisTolerance,
            $"Expected mirror-room {actorName} gameplay root position {expectedPosition}, got {actorRoot.Transform.Origin}.");
    }

    private static void AssertReferenceModelCompensation(string actorName, Node3D actorRoot, Node3D modelContainer)
    {
        AssertBasisApproximately(
            _referenceFemaleModelCompensationBasis,
            modelContainer.Transform.Basis,
            $"Expected mirror-room {actorName} installed Female model/container to preserve the reference template's "
            + "180° yaw compensation. The actor root uses Godot -Z forward; the imported skeleton/model subtree "
            + "keeps skeleton-local +Z avatar-forward semantics through this child-container transform.");

        Assert.NotSame(actorRoot, modelContainer);
    }

    private static void AssertHeadViewpointInstalled(string actorName, Node3D actorRoot)
    {
        Marker3D viewpoint = Assert.IsType<Marker3D>(
            actorRoot.GetNodeOrNull("Female/GeneralSkeleton/Head/Viewpoint"),
            exactMatch: false);

        Assert.Equal(
            new NodePath("Female/GeneralSkeleton/Head/Viewpoint"),
            actorRoot.GetPathTo(viewpoint));
        Assert.True(
            viewpoint.Name == "Viewpoint",
            $"Expected mirror-room {actorName} to install exactly one authored Head/Viewpoint marker.");

        AssertBasisApproximately(
            _referenceFemaleViewpointBasis,
            viewpoint.Transform.Basis,
            $"Expected mirror-room {actorName} viewpoint to preserve the authored face/camera frame used by VRIK. "
            + "The marker must live in the authoritative template, not as a stale vanished scene override.");
    }

    private static void AssertPlayerRuntimeControlAndGrabBindings(Node3D playerRoot)
    {
        Node controller = playerRoot.GetNode("PlayerController");
        Node locomotion = playerRoot.GetNode("Locomotion");
        Node hands = playerRoot.GetNode("Hands");
        Node playerVRIK = playerRoot.GetNode("VRIK");
        Node rightHand = playerRoot.GetNode("Hands/RightHand");
        Node leftHand = playerRoot.GetNode("Hands/LeftHand");
        Node rightGrabProvider = playerVRIK.GetNode("RightHandGrabProvider");
        Node leftGrabProvider = playerVRIK.GetNode("LeftHandGrabProvider");

        Assert.Same(locomotion, GetPropertyValue<Node>(controller, "LocomotionNode"));
        Assert.Same(hands, GetPropertyValue<Node>(controller, "HandHolderNode"));
        Assert.Same(rightGrabProvider, GetPropertyValue<Node>(playerVRIK, "RightHandIKTargetIntentProvider"));
        Assert.Same(leftGrabProvider, GetPropertyValue<Node>(playerVRIK, "LeftHandIKTargetIntentProvider"));
        Assert.Same(rightGrabProvider, GetPropertyValue<Node>(rightHand, "GrabTargetProvider"));
        Assert.Same(leftGrabProvider, GetPropertyValue<Node>(leftHand, "GrabTargetProvider"));
        Assert.Same(playerVRIK.GetNode("RightHandFallbackIntentProvider"), GetPropertyValue<Node>(rightGrabProvider, "DefaultProvider"));
        Assert.Same(playerVRIK.GetNode("LeftHandFallbackIntentProvider"), GetPropertyValue<Node>(leftGrabProvider, "DefaultProvider"));
    }

    private static void AssertRuntimeVisibleFaceDirection(
        string actorName,
        Node3D actorRoot,
        Vector3 expectedFacingDirection,
        float minimumDot)
    {
        Vector3 visibleFaceDirection = ResolveVisibleFaceDirection(actorRoot);
        Vector3 flatExpected = new(expectedFacingDirection.X, 0f, expectedFacingDirection.Z);
        Assert.True(flatExpected.LengthSquared() > 0.000001f, $"Expected mirror-room {actorName} facing direction must be horizontal and non-zero.");
        flatExpected = flatExpected.Normalized();

        float facingDot = visibleFaceDirection.Dot(flatExpected);
        Assert.True(
            facingDot >= minimumDot,
            $"Expected mirror-room {actorName} visible face landmarks to point toward {flatExpected}, got {visibleFaceDirection} "
            + $"(dot {facingDot:F3}). This compares head-to-face mesh landmarks after template materialisation, "
            + "so it fails when the rendered character is visually 180° backwards even if actor-root or container bases look plausible.");
    }

    private static Vector3 ResolveVisibleFaceDirection(Node3D actorRoot)
    {
        Node3D model = actorRoot.GetNode<Node3D>("Female");
        Skeleton3D skeleton = actorRoot.GetNode<Skeleton3D>("Female/GeneralSkeleton");
        Node3D head = actorRoot.GetNode<Node3D>("Female/GeneralSkeleton/Head");
        Vector3 headPosition = ComposeOrigin(actorRoot, model, skeleton, head);
        Vector3 faceCentre = Vector3.Zero;
        string[] frontLandmarks = ["Female_teeth_shape01", "Female_tongue01", "Female_eyelashes01"];

        foreach (string name in frontLandmarks)
        {
            MeshInstance3D? mesh = skeleton.GetNodeOrNull<MeshInstance3D>(name);
            Assert.NotNull(mesh);

            Aabb aabb = mesh.GetAabb();
            Vector3 localCenter = aabb.Position + (aabb.Size * 0.5f);
            Transform3D meshTransform = actorRoot.Transform * model.Transform * skeleton.Transform * mesh.Transform;
            faceCentre += meshTransform * localCenter;
        }

        faceCentre /= frontLandmarks.Length;
        Vector3 faceDirection = faceCentre - headPosition;
        Vector3 flatFaceDirection = new(faceDirection.X, 0f, faceDirection.Z);
        Assert.True(
            flatFaceDirection.LengthSquared() > 0.000001f,
            $"Expected visible face landmarks for '{actorRoot.Name}' to produce a non-zero horizontal facing vector.");
        return flatFaceDirection.Normalized();
    }

    private static Vector3 ComposeOrigin(params Node3D[] nodes)
    {
        Transform3D transform = Transform3D.Identity;
        foreach (Node3D node in nodes)
        {
            transform *= node.Transform;
        }

        return transform.Origin;
    }

    private static void AssertNoStaleViewpointOverrideSource(string scenePath)
    {
        string sceneText = ReadResourceText(scenePath);

        Assert.DoesNotContain(
            StaleViewpointOverrideNodeHeader,
            sceneText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            VanishedViewpointParentWarning,
            sceneText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            VanishedViewpointModificationWarning,
            sceneText,
            StringComparison.Ordinal);
    }

    private static void AssertBasisApproximately(Basis expected, Basis actual, string message)
    {
        Assert.True(expected.X.DistanceTo(actual.X) <= BasisTolerance, $"{message} Expected X basis {expected.X}, got {actual.X}.");
        Assert.True(expected.Y.DistanceTo(actual.Y) <= BasisTolerance, $"{message} Expected Y basis {expected.Y}, got {actual.Y}.");
        Assert.True(expected.Z.DistanceTo(actual.Z) <= BasisTolerance, $"{message} Expected Z basis {expected.Z}, got {actual.Z}.");
    }

    private static string ReadResourceText(string path)
    {
        string text = Godot.FileAccess.GetFileAsString(path);
        return !string.IsNullOrEmpty(text)
            ? text
            : throw new Xunit.Sdk.XunitException($"Expected text resource '{path}' to be readable.");
    }

}
