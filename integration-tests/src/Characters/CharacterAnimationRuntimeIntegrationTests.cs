using System.Reflection;
using AlleyCat.Body.Eyes;
using AlleyCat.Body.Hands;
using AlleyCat.Body.Voice;
using AlleyCat.Character;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core.Installer;
using AlleyCat.Rigging;
using AlleyCat.Rigging.Installation;
using AlleyCat.Rigging.Physics;
using AlleyCat.Speech.Generation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Characters;

/// <summary>
/// Runtime regression coverage for reference-character animation mixer materialisation.
/// </summary>
public sealed partial class CharacterAnimationRuntimeIntegrationTests
{
    private const string AllyScenePath = "res://assets/characters/reference/ally.tscn";
    private const string VadimScenePath = "res://assets/characters/reference/vadim.tscn";
    private const string PlayerScenePath = "res://assets/characters/reference/player.tscn";
    private const string MaleNpcAnimationTreeRootPath = "res://assets/characters/templates/animation/animation_tree_root_reference_male_npc.tres";
    private static readonly StringName _eyesLibraryName = new("eyes");

    /// <summary>
    /// Verifies the NPC reference installer leaves the shared animation tree able to drive skeletal body motion.
    /// </summary>
    [Headless]
    [Fact]
    public async Task AllyScene_RuntimeAnimationTree_AdvancesNpcBodyPose()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = LoadPackedScene(AllyScenePath).Instantiate();
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 12);
            EnsureCharacterRuntimeInstalled(root);

            AnimationTree animationTree = root.GetNode<AnimationTree>("AnimationTree");
            AnimationPlayer animationPlayer = root.GetNode<AnimationPlayer>("AnimationPlayer");
            Skeleton3D skeleton = root.GetNode<Skeleton3D>("Female/GeneralSkeleton");

            Assert.Equal(new NodePath("../Female"), animationTree.RootNode);
            Assert.Equal(new NodePath("../Female"), animationPlayer.RootNode);
            Assert.True(animationPlayer.HasAnimationLibrary(_eyesLibraryName), "Expected runtime installation to preserve the eye animation library used by the tree.");
            Assert.True(animationPlayer.HasAnimation(new StringName("eyes/Eyes Blink")), "Expected the tree-referenced blink animation to be registered.");
            AssertRuntimeEyeAnimationTracksResolve(animationPlayer);

            Assert.NotEmpty(skeleton.GetBoneName(0).ToString());
            string[] animationNames = animationPlayer.GetAnimationList();
            Assert.Contains("Walk Forward", animationNames);
            Assert.Contains("eyes/Eyes Up Down", animationNames);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    /// <summary>
    /// Verifies the reference male uses male eye animations and filter targets rather than the shared female set.
    /// </summary>
    [Headless]
    [Fact]
    public async Task VadimScene_RuntimeAnimationTree_UsesCompleteMaleBlinkTargets()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = LoadPackedScene(VadimScenePath).Instantiate();
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 12);
            EnsureCharacterRuntimeInstalled(root);

            AnimationTree animationTree = root.GetNode<AnimationTree>("AnimationTree");
            AnimationPlayer animationPlayer = root.GetNode<AnimationPlayer>("AnimationPlayer");

            Assert.Equal(new NodePath("../Male"), animationTree.RootNode);
            Assert.Equal(new NodePath("../Male"), animationPlayer.RootNode);
            Assert.Equal(MaleNpcAnimationTreeRootPath, animationTree.TreeRoot.ResourcePath);
            AssertRuntimeEyeAnimationTracksResolve(animationPlayer);
            AssertBlinkAnimationTargetsBodyMesh(animationPlayer, "GeneralSkeleton/Male_body");
            AssertBodyMeshBlinkShapesDeform(animationPlayer, "GeneralSkeleton/Male_body");
            AssertBlinkFilterTargetsMaleMeshes(animationTree);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    /// <summary>
    /// Verifies Vadim's locally authored voice identity and backend override survive automatic and explicit role installation.
    /// </summary>
    [Headless]
    [Fact]
    public async Task VadimScene_RoleInstallation_PreservesLocalVoiceOverrides()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = LoadPackedScene(VadimScenePath).Instantiate();
        CharacterHub character = Assert.IsType<CharacterHub>(root);
        Voice voice = root.GetNode<Voice>("Male/GeneralSkeleton/Head/Voice");
        OpenAISpeechGenerator speechGenerator = root.GetNode<OpenAISpeechGenerator>("Male/GeneralSkeleton/Head/Voice/SpeechGenerator");

        Assert.Equal("Vadim", character.Id);
        Assert.Equal("Vadim", voice.Id);
        Assert.Equal("Ian.wav", speechGenerator.VoiceOverride);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 12);
            Assert.Equal("Vadim", character.Id);
            Assert.Equal("Vadim", voice.Id);
            Assert.Equal("Ian.wav", speechGenerator.VoiceOverride);

            RigRoleTemplateSceneInstaller installer = root.GetNode<RigRoleTemplateSceneInstaller>("NPCCharacterInstaller");
            SceneInstallationResult reinstall = installer.Install(new SceneInstallationContext(root));

            Assert.True(reinstall.Succeeded, string.Join('\n', reinstall.Errors));
            Assert.Equal("Vadim", character.Id);
            Assert.Equal("Vadim", voice.Id);
            Assert.Equal("Ian.wav", speechGenerator.VoiceOverride);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    /// <summary>
    /// Verifies the player hand can apply a grab animation to the hand pose node, blend parameter, and finger bone pose.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerScene_GrabAnimationPose_ChangesRightHandPoseState()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = LoadPackedScene(PlayerScenePath).Instantiate();
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 12);
            EnsureCharacterRuntimeInstalled(root);

            AnimationTree animationTree = root.GetNode<AnimationTree>("AnimationTree");
            AnimationPlayer animationPlayer = root.GetNode<AnimationPlayer>("AnimationPlayer");
            Node rightHand = root.GetNode<Node>("Hands/RightHand");
            Animation grabAnimation = animationPlayer.GetAnimation(new StringName("Grab-ball-40"));

            InvokeScriptVoidMethod(rightHand, "SetPose", grabAnimation, null, true);
            await WaitForFramesAsync(sceneTree, 20);
            animationTree.Advance(0.5d);

            Assert.Same(grabAnimation, GetScriptProperty<object>(rightHand, "CurrentPose"));
            Assert.Equal("Grab-ball-40", ResolveHandPoseNode(animationTree, LimbSide.Right).Animation.ToString());
            Assert.True(animationTree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Right)).AsSingle() > 0.5f);

            InvokeScriptVoidMethod(rightHand, "ClearPose", true);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    /// <summary>
    /// Verifies required eye animations cannot be present-but-empty after import.
    /// </summary>
    [Headless]
    [Fact]
    public void RuntimeInstaller_EmptyRequiredEyeAnimation_FailsValidation()
    {
        using var fixture = RuntimeEyeAnimationFixture.CreateValid();
        fixture.ReplaceEyeAnimation(EyesAnimationTreePaths.VerticalLookAnimationName, new Animation());

        SceneInstallationResult result = fixture.Install();

        Assert.False(result.Succeeded);
        Assert.Contains("at least one blend-shape track", string.Join('\n', result.Errors), StringComparison.Ordinal);
        Assert.Contains(EyesAnimationTreePaths.VerticalLookAnimationName, string.Join('\n', result.Errors), StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies blink animation import is rejected unless both eyelid targets are available.
    /// </summary>
    [Headless]
    [Fact]
    public void RuntimeInstaller_BlinkAnimationMissingOneEye_FailsValidation()
    {
        using var fixture = RuntimeEyeAnimationFixture.CreateValid();
        fixture.ReplaceEyeAnimation(
            EyesAnimationTreePaths.BlinkAnimationName,
            fixture.CreateBlendShapeAnimation(EyesAnimationTreePaths.EyeBlinkLeftBlendShapeName));

        SceneInstallationResult result = fixture.Install();

        Assert.False(result.Succeeded);
        Assert.Contains(EyesAnimationTreePaths.EyeBlinkRightBlendShapeName, string.Join('\n', result.Errors), StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies look animations require axis-specific eye look blend-shape tracks, not merely any eye track.
    /// </summary>
    [Headless]
    [Fact]
    public void RuntimeInstaller_LookAnimationWithoutAxisTrack_FailsValidation()
    {
        using var fixture = RuntimeEyeAnimationFixture.CreateValid();
        fixture.ReplaceEyeAnimation(
            EyesAnimationTreePaths.HorizontalLookAnimationName,
            fixture.CreateBlendShapeAnimation(EyesAnimationTreePaths.EyeBlinkLeftBlendShapeName));

        SceneInstallationResult result = fixture.Install();

        Assert.False(result.Succeeded);
        Assert.Contains("horizontal look blend-shape track", string.Join('\n', result.Errors), StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies required eye animations cannot contain non-blend-shape tracks.
    /// </summary>
    [Headless]
    [Fact]
    public void RuntimeInstaller_NonBlendShapeEyeTrack_FailsValidation()
    {
        using var fixture = RuntimeEyeAnimationFixture.CreateValid();
        Animation animation = new();
        int trackIndex = animation.AddTrack(Animation.TrackType.Value);
        animation.TrackSetPath(trackIndex, new NodePath("Face:position"));
        fixture.ReplaceEyeAnimation(EyesAnimationTreePaths.VerticalLookAnimationName, animation);

        SceneInstallationResult result = fixture.Install();

        Assert.False(result.Succeeded);
        Assert.Contains("only blend-shape tracks", string.Join('\n', result.Errors), StringComparison.Ordinal);
    }

    private static AnimationNodeAnimation ResolveHandPoseNode(AnimationTree animationTree, LimbSide side)
    {
        AnimationNodeBlendTree rootTree = Assert.IsType<AnimationNodeBlendTree>(animationTree.TreeRoot, exactMatch: false);
        return Assert.IsType<AnimationNodeAnimation>(
            rootTree.GetNode(HandPoseAnimationTreePaths.GetPoseAnimationNodeName(side)),
            exactMatch: false);
    }

    private static void AssertRuntimeEyeAnimationTracksResolve(AnimationPlayer animationPlayer)
    {
        Node rootNode = animationPlayer.GetNodeOrNull(animationPlayer.RootNode)
            ?? throw new Xunit.Sdk.XunitException($"Expected AnimationPlayer root '{animationPlayer.RootNode}' to resolve.");

        AssertBlendShapeTracksResolve(animationPlayer, EyesAnimationTreePaths.BlinkAnimationName, rootNode);
        AssertBlendShapeTracksResolve(animationPlayer, EyesAnimationTreePaths.HorizontalLookAnimationName, rootNode);
        AssertBlendShapeTracksResolve(animationPlayer, EyesAnimationTreePaths.VerticalLookAnimationName, rootNode);

        Animation blinkAnimation = animationPlayer.GetAnimation(new StringName(EyesAnimationTreePaths.BlinkAnimationName));
        HashSet<string> blinkShapes = GetTrackBlendShapeNames(blinkAnimation);
        Assert.Contains(EyesAnimationTreePaths.EyeBlinkLeftBlendShapeName, blinkShapes);
        Assert.Contains(EyesAnimationTreePaths.EyeBlinkRightBlendShapeName, blinkShapes);
    }

    private static void AssertBlinkAnimationTargetsBodyMesh(AnimationPlayer animationPlayer, string bodyMeshPath)
    {
        Animation blinkAnimation = animationPlayer.GetAnimation(new StringName(EyesAnimationTreePaths.BlinkAnimationName));
        HashSet<string> trackPaths = GetTrackPaths(blinkAnimation);

        Assert.Contains($"{bodyMeshPath}:{EyesAnimationTreePaths.EyeBlinkLeftBlendShapeName}", trackPaths);
        Assert.Contains($"{bodyMeshPath}:{EyesAnimationTreePaths.EyeBlinkRightBlendShapeName}", trackPaths);
    }

    private static void AssertBodyMeshBlinkShapesDeform(AnimationPlayer animationPlayer, string bodyMeshPath)
    {
        Node rootNode = animationPlayer.GetNodeOrNull(animationPlayer.RootNode)
            ?? throw new Xunit.Sdk.XunitException($"Expected AnimationPlayer root '{animationPlayer.RootNode}' to resolve.");
        MeshInstance3D bodyMesh = rootNode.GetNodeOrNull<MeshInstance3D>(new NodePath(bodyMeshPath))
            ?? throw new Xunit.Sdk.XunitException($"Expected body mesh '{bodyMeshPath}' to resolve from '{rootNode.GetPath()}'.");

        Assert.True(
            GetBlendShapeMaxVertexDelta(bodyMesh, EyesAnimationTreePaths.EyeBlinkLeftBlendShapeName) > 0.0f,
            "Expected Male_body eyeBlinkLeft to contain non-zero vertex deformation, not a no-op target.");
        Assert.True(
            GetBlendShapeMaxVertexDelta(bodyMesh, EyesAnimationTreePaths.EyeBlinkRightBlendShapeName) > 0.0f,
            "Expected Male_body eyeBlinkRight to contain non-zero vertex deformation, not a no-op target.");
    }

    private static void AssertBlinkFilterTargetsMaleMeshes(AnimationTree animationTree)
    {
        AnimationNodeBlendTree rootTree = Assert.IsType<AnimationNodeBlendTree>(animationTree.TreeRoot, exactMatch: false);
        AnimationNodeOneShot blinkNode = Assert.IsType<AnimationNodeOneShot>(
            rootTree.GetNode(EyesAnimationTreePaths.BlinkOneShotNode),
            exactMatch: false);

        Assert.True(blinkNode.IsPathFiltered(new NodePath("GeneralSkeleton/Male_body:eyeBlinkLeft")));
        Assert.True(blinkNode.IsPathFiltered(new NodePath("GeneralSkeleton/Male_body:eyeBlinkRight")));
        Assert.True(blinkNode.IsPathFiltered(new NodePath("GeneralSkeleton/Male_eyebrow002:eyeBlinkLeft")));
        Assert.True(blinkNode.IsPathFiltered(new NodePath("GeneralSkeleton/Male_eyelashes01:eyeBlinkRight")));
        Assert.False(blinkNode.IsPathFiltered(new NodePath("GeneralSkeleton/Female_body:eyeBlinkLeft")));
    }

    private static HashSet<string> GetTrackPaths(Animation animation)
    {
        HashSet<string> paths = new(StringComparer.Ordinal);
        for (int trackIndex = 0; trackIndex < animation.GetTrackCount(); trackIndex++)
        {
            _ = paths.Add(animation.TrackGetPath(trackIndex).ToString());
        }

        return paths;
    }

    private static void AssertBlendShapeTracksResolve(AnimationPlayer animationPlayer, string animationName, Node rootNode)
    {
        Animation animation = animationPlayer.GetAnimation(new StringName(animationName));
        Assert.NotEqual(0, animation.GetTrackCount());

        for (int trackIndex = 0; trackIndex < animation.GetTrackCount(); trackIndex++)
        {
            Assert.Equal(Animation.TrackType.BlendShape, animation.TrackGetType(trackIndex));
            string trackPath = animation.TrackGetPath(trackIndex).ToString();
            int subnameSeparator = trackPath.IndexOf(':', StringComparison.Ordinal);
            Assert.True(subnameSeparator > 0, $"Expected '{animationName}' track '{trackPath}' to include a blend-shape subname.");

            string meshPath = trackPath[..subnameSeparator];
            string blendShapeName = trackPath[(subnameSeparator + 1)..];
            MeshInstance3D meshInstance = rootNode.GetNodeOrNull<MeshInstance3D>(new NodePath(meshPath))
                ?? throw new Xunit.Sdk.XunitException($"Expected '{animationName}' track mesh '{meshPath}' to resolve from '{rootNode.GetPath()}'.");
            Assert.True(
                MeshHasBlendShape(meshInstance, blendShapeName),
                $"Expected '{animationName}' track blend shape '{blendShapeName}' to exist on '{meshInstance.GetPath()}'.");
        }
    }

    private static HashSet<string> GetTrackBlendShapeNames(Animation animation)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        for (int trackIndex = 0; trackIndex < animation.GetTrackCount(); trackIndex++)
        {
            string trackPath = animation.TrackGetPath(trackIndex).ToString();
            int subnameSeparator = trackPath.IndexOf(':', StringComparison.Ordinal);
            if (subnameSeparator > 0 && subnameSeparator < trackPath.Length - 1)
            {
                _ = names.Add(trackPath[(subnameSeparator + 1)..]);
            }
        }

        return names;
    }

    private static bool MeshHasBlendShape(MeshInstance3D meshInstance, string blendShapeName)
    {
        if (meshInstance.Mesh is not ArrayMesh mesh)
        {
            return false;
        }

        for (int blendShapeIndex = 0; blendShapeIndex < mesh.GetBlendShapeCount(); blendShapeIndex++)
        {
            if (string.Equals(mesh.GetBlendShapeName(blendShapeIndex).ToString(), blendShapeName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static float GetBlendShapeMaxVertexDelta(MeshInstance3D meshInstance, string blendShapeName)
    {
        if (meshInstance.Mesh is not ArrayMesh mesh)
        {
            return -1.0f;
        }

        int blendShapeIndex = -1;
        for (int index = 0; index < mesh.GetBlendShapeCount(); index++)
        {
            if (string.Equals(mesh.GetBlendShapeName(index).ToString(), blendShapeName, StringComparison.Ordinal))
            {
                blendShapeIndex = index;
                break;
            }
        }

        if (blendShapeIndex < 0)
        {
            return -1.0f;
        }

        float maxDelta = 0.0f;
        for (int surfaceIndex = 0; surfaceIndex < mesh.GetSurfaceCount(); surfaceIndex++)
        {
            Godot.Collections.Array baseArrays = mesh.SurfaceGetArrays(surfaceIndex);
            Vector3[] baseVertices = baseArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Godot.Collections.Array<Godot.Collections.Array> blendShapeArrays = mesh.SurfaceGetBlendShapeArrays(surfaceIndex);
            Godot.Collections.Array targetArrays = blendShapeArrays[blendShapeIndex];
            Vector3[] targetVertices = targetArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            int vertexCount = Math.Min(baseVertices.Length, targetVertices.Length);
            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                maxDelta = Math.Max(maxDelta, baseVertices[vertexIndex].DistanceTo(targetVertices[vertexIndex]));
            }
        }

        return maxDelta;
    }

    private sealed class RuntimeEyeAnimationFixture : IDisposable
    {
        private readonly CharacterHub _targetRoot;
        private readonly Node3D _templateRoot;
        private readonly Skeleton3D _templateSkeleton;
        private readonly AnimationLibrary _eyeAnimationLibrary;

        private RuntimeEyeAnimationFixture(
            CharacterHub targetRoot,
            Node3D templateRoot,
            Skeleton3D templateSkeleton,
            MeshInstance3D faceMesh,
            AnimationLibrary eyeAnimationLibrary)
        {
            _targetRoot = targetRoot;
            _templateRoot = templateRoot;
            _templateSkeleton = templateSkeleton;
            FaceMesh = faceMesh;
            _eyeAnimationLibrary = eyeAnimationLibrary;
        }

        private MeshInstance3D FaceMesh
        {
            get;
        }

        public static RuntimeEyeAnimationFixture CreateValid()
        {
            CharacterHub targetRoot = new()
            {
                Name = "Character"
            };
            AnimationTree animationTree = CreateAnimationTreeFixture();
            targetRoot.AddChild(animationTree);

            AnimationPlayer animationPlayer = new()
            {
                Name = "AnimationPlayer"
            };
            targetRoot.AddChild(animationPlayer);

            Node3D modelRoot = new()
            {
                Name = "Model"
            };
            targetRoot.AddChild(modelRoot);

            Skeleton3D skeleton = new()
            {
                Name = "Skeleton"
            };
            modelRoot.AddChild(skeleton);

            MeshInstance3D faceMesh = new()
            {
                Name = "Face",
                Mesh = CreateArrayMeshWithBlendShapes(),
            };
            modelRoot.AddChild(faceMesh);

            AddCharacterRuntimeComponentFixture(targetRoot, animationTree, modelRoot, LimbSide.Right);

            CharacterHub templateRoot = new()
            {
                Name = "Template"
            };
            Skeleton3D templateSkeleton = new()
            {
                Name = "Skeleton"
            };
            templateRoot.AddChild(templateSkeleton);
            AddCharacterRuntimeComponentFixture(templateRoot, CreateAnimationTreeFixture(), new Node3D { Name = "Model" }, LimbSide.Right);

            AnimationLibrary eyeAnimationLibrary = new();
            RuntimeEyeAnimationFixture fixture = new(
                targetRoot,
                templateRoot,
                templateSkeleton,
                faceMesh,
                eyeAnimationLibrary);

            _ = eyeAnimationLibrary.AddAnimation(
                StripEyesLibraryPrefix(EyesAnimationTreePaths.BlinkAnimationName),
                fixture.CreateBlendShapeAnimation(
                    EyesAnimationTreePaths.EyeBlinkLeftBlendShapeName,
                    EyesAnimationTreePaths.EyeBlinkRightBlendShapeName));
            _ = eyeAnimationLibrary.AddAnimation(
                StripEyesLibraryPrefix(EyesAnimationTreePaths.HorizontalLookAnimationName),
                fixture.CreateBlendShapeAnimation(EyesAnimationTreePaths.EyeLookInLeftBlendShapeName));
            _ = eyeAnimationLibrary.AddAnimation(
                StripEyesLibraryPrefix(EyesAnimationTreePaths.VerticalLookAnimationName),
                fixture.CreateBlendShapeAnimation(EyesAnimationTreePaths.EyeLookUpLeftBlendShapeName));

            _ = animationPlayer.AddAnimationLibrary(_eyesLibraryName, eyeAnimationLibrary);
            return fixture;
        }

        public SceneInstallationResult Install()
        {
            RigInstallationContext context = new(
                _targetRoot,
                "test.character_runtime",
                _templateRoot,
                _targetRoot.GetNode<Skeleton3D>("Model/Skeleton"),
                _templateSkeleton);

            return new CharacterRuntimeSubsystemInstaller().Install(context);
        }

        public Animation CreateBlendShapeAnimation(params string[] blendShapeNames)
        {
            Animation animation = new();
            foreach (string blendShapeName in blendShapeNames)
            {
                int trackIndex = animation.AddTrack(Animation.TrackType.BlendShape);
                animation.TrackSetPath(trackIndex, new NodePath($"{FaceMesh.Name}:{blendShapeName}"));
                _ = animation.BlendShapeTrackInsertKey(trackIndex, 0.0, 0.0f);
            }

            return animation;
        }

        public void ReplaceEyeAnimation(string animationName, Animation animation)
        {
            StringName libraryAnimationName = StripEyesLibraryPrefix(animationName);
            _eyeAnimationLibrary.RemoveAnimation(libraryAnimationName);
            _ = _eyeAnimationLibrary.AddAnimation(libraryAnimationName, animation);
        }

        public void Dispose()
        {
            _targetRoot.QueueFree();
            _templateRoot.QueueFree();
        }

        private static ArrayMesh CreateArrayMeshWithBlendShapes()
        {
            ArrayMesh mesh = new();
            foreach (string blendShapeName in EyesAnimationTreePaths.EyeBlendShapeNames)
            {
                mesh.AddBlendShape(blendShapeName);
            }

            return mesh;
        }

        private static AnimationTree CreateAnimationTreeFixture()
        {
            AnimationNodeBlendTree treeRoot = new();
            treeRoot.AddNode(HandPoseAnimationTreePaths.GetPoseAnimationNodeName(LimbSide.Left), new AnimationNodeAnimation());
            treeRoot.AddNode(HandPoseAnimationTreePaths.GetPoseAnimationNodeName(LimbSide.Right), new AnimationNodeAnimation());

            return new AnimationTree
            {
                Name = "AnimationTree",
                TreeRoot = treeRoot,
            };
        }

        private static void AddCharacterRuntimeComponentFixture(
            CharacterHub root,
            AnimationTree animationTree,
            Node3D rootMotionReference,
            LimbSide rightHandSide)
        {
            if (animationTree.GetParent() is null)
            {
                root.AddChild(animationTree);
            }

            if (rootMotionReference.GetParent() is null)
            {
                root.AddChild(rootMotionReference);
            }

            CharacterLocomotion locomotion = new()
            {
                Name = "Locomotion",
                AnimationTree = animationTree,
                RootMotionReference = rootMotionReference,
            };
            root.AddChild(locomotion);

            Node3D eyeOrigin = new()
            {
                Name = "EyeOrigin",
            };
            root.AddChild(eyeOrigin);
            EyesBehaviour eyes = new()
            {
                Name = "Eyes",
                AnimationTree = animationTree,
                EyeOrigin = eyeOrigin,
            };
            root.AddChild(eyes);

            PlayerVoice voice = new()
            {
                Name = "Voice",
                Id = "fixture",
            };
            root.AddChild(voice);

            DynamicPhysicalRig physicalRig = new()
            {
                Name = "DynamicPhysicalRig",
            };
            root.AddChild(physicalRig);

            Node3D rightTarget = new()
            {
                Name = "RightHandTarget",
            };
            root.AddChild(rightTarget);
            Node3D leftTarget = new()
            {
                Name = "LeftHandTarget",
            };
            root.AddChild(leftTarget);

            BoneAttachment3D rightAttachment = new()
            {
                Name = "RightHandAttachment",
            };
            root.AddChild(rightAttachment);
            BoneAttachment3D leftAttachment = new()
            {
                Name = "LeftHandAttachment",
            };
            root.AddChild(leftAttachment);

            StaticBody3D rightCollision = new()
            {
                Name = "RightHeldCollision",
            };
            root.AddChild(rightCollision);
            StaticBody3D leftCollision = new()
            {
                Name = "LeftHeldCollision",
            };
            root.AddChild(leftCollision);

            HandPoseBehaviour rightHand = new()
            {
                Name = "RightHand",
                AnimationTree = animationTree,
                Side = rightHandSide,
                HandTargetNode = rightTarget,
                HandBoneAttachment = rightAttachment,
                HeldCollisionTarget = rightCollision,
                PhysicalRig = physicalRig,
            };
            root.AddChild(rightHand);
            HandPoseBehaviour leftHand = new()
            {
                Name = "LeftHand",
                AnimationTree = animationTree,
                Side = LimbSide.Left,
                HandTargetNode = leftTarget,
                HandBoneAttachment = leftAttachment,
                HeldCollisionTarget = leftCollision,
                PhysicalRig = physicalRig,
            };
            root.AddChild(leftHand);

            root.Locomotion = locomotion;
            root.Eyes = eyes;
            root.Voice = voice;
            root.RightHand = rightHand;
            root.LeftHand = leftHand;
        }

        private static StringName StripEyesLibraryPrefix(string animationName)
            => new(animationName[(_eyesLibraryName.ToString().Length + 1)..]);
    }

    private static T? GetScriptProperty<T>(Node node, string propertyName)
    {
        PropertyInfo property = node.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException($"Expected script property '{propertyName}' to resolve on '{node.Name}'.");
        return (T?)property.GetValue(node);
    }

    private static void InvokeScriptVoidMethod(Node node, string methodName, params object?[] arguments)
    {
        MethodInfo method = node.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException($"Expected script method '{methodName}' to resolve on '{node.Name}'.");
        _ = method.Invoke(node, arguments);
    }

}
