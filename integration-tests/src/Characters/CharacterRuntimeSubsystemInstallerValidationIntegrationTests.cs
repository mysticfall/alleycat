using AlleyCat.Body.Eyes;
using AlleyCat.Body.Hands;
using AlleyCat.Body.Voice;
using AlleyCat.Character;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core;
using AlleyCat.Core.Installer;
using AlleyCat.Rigging;
using AlleyCat.Rigging.Installation;
using AlleyCat.Rigging.Physics;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Characters;

/// <summary>
/// Integration coverage for character hub validation during runtime subsystem installation.
/// </summary>
public sealed class CharacterRuntimeSubsystemInstallerValidationIntegrationTests
{
    private static readonly StringName _eyesLibraryName = new("eyes");

    /// <summary>
    /// A concrete hub used as the install target root is accepted without topology discovery.
    /// </summary>
    [Headless]
    [Fact]
    public void ValidateCharacterHub_ActualRootConcreteHub_ReturnsRoot()
    {
        var targetRoot = new CharacterHub { Name = "CharacterRoot" };

        CharacterHub resolved = CharacterRuntimeSubsystemInstaller.ValidateCharacterHub(targetRoot);

        Assert.Same(targetRoot, resolved);
    }

    /// <summary>
    /// Full installation copies template-authored root references to the target and exposes the deterministic capability projection.
    /// </summary>
    [Headless]
    [Fact]
    public void Install_ActualRootConcreteHub_TransfersTemplateAuthoredCapabilityReferences()
    {
        using var fixture = RuntimeInstallFixture.CreateWithActualRootHub();
        CharacterHub targetRoot = Assert.IsType<CharacterHub>(fixture.TargetRoot, exactMatch: false);

        SceneInstallationResult result = new CharacterRuntimeSubsystemInstaller().Install(fixture.CreateContext());

        Assert.True(result.Succeeded, string.Join('\n', result.Errors));
        Assert.Equal(5, targetRoot.Components.Count);
        Assert.Same(fixture.TargetLocomotion, targetRoot.Components[0]);
        ICharacter character = targetRoot;
        Assert.Same(fixture.TargetEyes, character.RequireEyes());
        Assert.Same(fixture.TargetVoice, character.RequireVoice());
        Assert.Same(fixture.TargetLeftHand, character.RequireHand(LimbSide.Left));
        Assert.Same(fixture.TargetRightHand, character.RequireHand(LimbSide.Right));
        Assert.Same(fixture.TargetLocomotion, targetRoot.RequireComponent<ILocomotion>());
    }

    /// <summary>
    /// Non-character roots fail clearly instead of scanning descendants for migration-era hubs.
    /// </summary>
    [Headless]
    [Fact]
    public void ValidateCharacterHub_NonCharacterRoot_ThrowsClearError()
    {
        var root = new Node3D { Name = "Root" };
        root.AddChild(new CharacterHub { Name = "DescendantCharacter" });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => CharacterRuntimeSubsystemInstaller.ValidateCharacterHub(root));

        Assert.Contains("target root", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(typeof(CharacterHub).FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(CharacterBody3D), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Non-character roots fail through the full installer path before runtime binding can proceed.
    /// </summary>
    [Headless]
    [Fact]
    public void Install_NonCharacterRoot_FailsClearly()
    {
        using var fixture = RuntimeInstallFixture.CreateWithNonCharacterRoot();

        SceneInstallationResult result = new CharacterRuntimeSubsystemInstaller().Install(fixture.CreateContext());

        string error = Assert.Single(result.Errors);
        Assert.False(result.Succeeded);
        Assert.Contains("target root", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(typeof(CharacterHub).FullName!, error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Non-character roots fail before animation/template setup can affect the reported contract error.
    /// </summary>
    [Headless]
    [Fact]
    public void Install_NonCharacterRootWithoutRuntimeSetup_FailsWithCharacterRootError()
    {
        var targetRoot = new Node3D { Name = "NonCharacterRoot" };
        var templateRoot = new Node3D { Name = "Template" };
        var targetSkeleton = new Skeleton3D { Name = "Skeleton" };
        var templateSkeleton = new Skeleton3D { Name = "Skeleton" };
        try
        {
            var context = new RigInstallationContext(
                targetRoot,
                "test.character_runtime_validation",
                templateRoot,
                targetSkeleton,
                templateSkeleton);

            SceneInstallationResult result = new CharacterRuntimeSubsystemInstaller().Install(context);

            string error = Assert.Single(result.Errors);
            Assert.False(result.Succeeded);
            Assert.Contains("target root", error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(typeof(CharacterHub).FullName!, error, StringComparison.Ordinal);
        }
        finally
        {
            targetRoot.QueueFree();
            templateRoot.QueueFree();
            targetSkeleton.QueueFree();
            templateSkeleton.QueueFree();
        }
    }

    /// <summary>
    /// Non-character template roots fail before runtime binding can use topology fallbacks.
    /// </summary>
    [Headless]
    [Fact]
    public void Install_NonCharacterTemplateRoot_FailsClearly()
    {
        var targetRoot = new CharacterHub { Name = "CharacterRoot" };
        var templateRoot = new Node3D { Name = "Template" };
        var targetSkeleton = new Skeleton3D { Name = "Skeleton" };
        var templateSkeleton = new Skeleton3D { Name = "Skeleton" };
        try
        {
            var context = new RigInstallationContext(
                targetRoot,
                "test.character_runtime_validation",
                templateRoot,
                targetSkeleton,
                templateSkeleton);

            SceneInstallationResult result = new CharacterRuntimeSubsystemInstaller().Install(context);

            string error = Assert.Single(result.Errors);
            Assert.False(result.Succeeded);
            Assert.Contains("template root", error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(typeof(CharacterHub).FullName!, error, StringComparison.Ordinal);
        }
        finally
        {
            targetRoot.QueueFree();
            templateRoot.QueueFree();
            targetSkeleton.QueueFree();
            templateSkeleton.QueueFree();
        }
    }

    private sealed class RuntimeInstallFixture : IDisposable
    {
        private readonly Node3D _templateRoot;
        private readonly Skeleton3D _targetSkeleton;
        private readonly Skeleton3D _templateSkeleton;
        private readonly MeshInstance3D _faceMesh;

        private RuntimeInstallFixture(
            Node3D targetRoot,
            Node3D templateRoot,
            Skeleton3D targetSkeleton,
            Skeleton3D templateSkeleton,
            MeshInstance3D faceMesh)
        {
            TargetRoot = targetRoot;
            _templateRoot = templateRoot;
            _targetSkeleton = targetSkeleton;
            _templateSkeleton = templateSkeleton;
            _faceMesh = faceMesh;
        }

        public Node3D TargetRoot
        {
            get;
        }

        public CharacterLocomotion? TargetLocomotion
        {
            get; private init;
        }

        public EyesBehaviour? TargetEyes
        {
            get; private init;
        }

        public IVoice? TargetVoice
        {
            get; private init;
        }

        public HandPoseBehaviour? TargetLeftHand
        {
            get; private init;
        }

        public HandPoseBehaviour? TargetRightHand
        {
            get; private init;
        }

        public static RuntimeInstallFixture CreateWithActualRootHub()
        {
            var targetRoot = new CharacterHub { Name = "CharacterRoot" };
            return Create(targetRoot);
        }

        public static RuntimeInstallFixture CreateWithNonCharacterRoot()
        {
            var targetRoot = new Node3D { Name = "NonCharacterRoot" };
            return Create(targetRoot);
        }

        private static RuntimeInstallFixture Create(Node3D targetRoot)
        {
            var animationTree = new AnimationTree
            {
                Name = "AnimationTree",
                TreeRoot = CreateHandPoseBlendTree(),
            };
            targetRoot.AddChild(animationTree);

            var animationPlayer = new AnimationPlayer { Name = "AnimationPlayer" };
            targetRoot.AddChild(animationPlayer);

            var modelRoot = new Node3D { Name = "Model" };
            targetRoot.AddChild(modelRoot);

            var targetSkeleton = new Skeleton3D { Name = "Skeleton" };
            modelRoot.AddChild(targetSkeleton);

            var faceMesh = new MeshInstance3D
            {
                Name = "Face",
                Mesh = CreateArrayMeshWithBlendShapes(),
            };
            modelRoot.AddChild(faceMesh);

            var templateRoot = new CharacterHub { Name = "Template" };
            var templateSkeleton = new Skeleton3D { Name = "Skeleton" };
            templateRoot.AddChild(templateSkeleton);

            RuntimeInstallFixture fixture = new(
                targetRoot,
                templateRoot,
                targetSkeleton,
                templateSkeleton,
                faceMesh);

            if (targetRoot is CharacterHub targetCharacter)
            {
                CapabilityNodes targetCapabilities = AddCapabilityNodes(targetCharacter, animationTree, "target");
                CapabilityNodes templateCapabilities = AddCapabilityNodes(templateRoot, animationTree, "template");

                targetCharacter.Locomotion = new CharacterLocomotion { Name = "StaleLocomotion" };
                targetCharacter.Eyes = new EyesBehaviour { Name = "StaleEyes" };
                targetCharacter.Voice = new TestVoice { Name = "StaleVoice", Id = "stale_voice" };
                targetCharacter.LeftHand = new HandPoseBehaviour { Name = "StaleLeftHand", Side = LimbSide.Left };
                targetCharacter.RightHand = new HandPoseBehaviour { Name = "StaleRightHand", Side = LimbSide.Right };

                templateRoot.Locomotion = templateCapabilities.Locomotion;
                templateRoot.Eyes = templateCapabilities.Eyes;
                templateRoot.Voice = templateCapabilities.Voice;
                templateRoot.LeftHand = templateCapabilities.LeftHand;
                templateRoot.RightHand = templateCapabilities.RightHand;

                fixture = new RuntimeInstallFixture(
                    targetRoot,
                    templateRoot,
                    targetSkeleton,
                    templateSkeleton,
                    faceMesh)
                {
                    TargetLocomotion = targetCapabilities.Locomotion,
                    TargetEyes = targetCapabilities.Eyes,
                    TargetVoice = targetCapabilities.Voice,
                    TargetLeftHand = targetCapabilities.LeftHand,
                    TargetRightHand = targetCapabilities.RightHand,
                };
            }

            var eyeAnimationLibrary = new AnimationLibrary();
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

        public RigInstallationContext CreateContext()
            => new(
                TargetRoot,
                "test.character_runtime_validation",
                _templateRoot,
                _targetSkeleton,
                _templateSkeleton);

        public void Dispose()
        {
            TargetRoot.QueueFree();
            _templateRoot.QueueFree();
        }

        private Animation CreateBlendShapeAnimation(params string[] blendShapeNames)
        {
            var animation = new Animation();
            foreach (string blendShapeName in blendShapeNames)
            {
                int trackIndex = animation.AddTrack(Animation.TrackType.BlendShape);
                animation.TrackSetPath(trackIndex, new NodePath($"{_faceMesh.Name}:{blendShapeName}"));
                _ = animation.BlendShapeTrackInsertKey(trackIndex, 0.0, 0.0f);
            }

            return animation;
        }

        private static ArrayMesh CreateArrayMeshWithBlendShapes()
        {
            var mesh = new ArrayMesh();
            foreach (string blendShapeName in EyesAnimationTreePaths.EyeBlendShapeNames)
            {
                mesh.AddBlendShape(blendShapeName);
            }

            return mesh;
        }

        private static AnimationNodeBlendTree CreateHandPoseBlendTree()
        {
            var tree = new AnimationNodeBlendTree();
            tree.AddNode(HandPoseAnimationTreePaths.LeftHandPoseNode, new AnimationNodeAnimation());
            tree.AddNode(HandPoseAnimationTreePaths.RightHandPoseNode, new AnimationNodeAnimation());
            tree.AddNode(HandPoseAnimationTreePaths.LeftHandBlendNode, new AnimationNodeBlend2());
            tree.AddNode(HandPoseAnimationTreePaths.RightHandBlendNode, new AnimationNodeBlend2());
            return tree;
        }

        private static StringName StripEyesLibraryPrefix(string animationName)
            => new(animationName[(_eyesLibraryName.ToString().Length + 1)..]);

        private static CapabilityNodes AddCapabilityNodes(CharacterHub root, AnimationTree animationTree, string prefix)
        {
            var eyeOrigin = new Node3D { Name = $"{prefix}_EyeOrigin" };
            var rootMotion = new Node3D { Name = $"{prefix}_RootMotion" };
            var leftTarget = new Node3D { Name = $"{prefix}_LeftTarget" };
            var rightTarget = new Node3D { Name = $"{prefix}_RightTarget" };
            var leftAttachment = new BoneAttachment3D { Name = $"{prefix}_LeftAttachment" };
            var rightAttachment = new BoneAttachment3D { Name = $"{prefix}_RightAttachment" };
            var leftCollision = new StaticBody3D { Name = $"{prefix}_LeftCollision" };
            var rightCollision = new StaticBody3D { Name = $"{prefix}_RightCollision" };
            var physicalRig = new DynamicPhysicalRig { Name = $"{prefix}_PhysicalRig" };
            root.AddChild(eyeOrigin);
            root.AddChild(rootMotion);
            root.AddChild(leftTarget);
            root.AddChild(rightTarget);
            root.AddChild(leftAttachment);
            root.AddChild(rightAttachment);
            root.AddChild(leftCollision);
            root.AddChild(rightCollision);
            root.AddChild(physicalRig);

            var locomotion = new CharacterLocomotion
            {
                Name = "Locomotion",
                AnimationTree = animationTree,
                RootMotionReference = rootMotion,
            };
            var eyes = new EyesBehaviour
            {
                Name = "Eyes",
                AnimationTree = animationTree,
                EyeOrigin = eyeOrigin,
            };
            var voice = new TestVoice { Name = "Voice", Id = "voice" };
            var leftHand = new HandPoseBehaviour
            {
                Name = "LeftHand",
                Side = LimbSide.Left,
                AnimationTree = animationTree,
                HandTargetNode = leftTarget,
                HandBoneAttachment = leftAttachment,
                HeldCollisionTarget = leftCollision,
                PhysicalRig = physicalRig,
            };
            var rightHand = new HandPoseBehaviour
            {
                Name = "RightHand",
                Side = LimbSide.Right,
                AnimationTree = animationTree,
                HandTargetNode = rightTarget,
                HandBoneAttachment = rightAttachment,
                HeldCollisionTarget = rightCollision,
                PhysicalRig = physicalRig,
            };
            root.AddChild(locomotion);
            root.AddChild(eyes);
            root.AddChild(voice);
            root.AddChild(leftHand);
            root.AddChild(rightHand);
            return new CapabilityNodes(locomotion, eyes, voice, leftHand, rightHand);
        }

        private sealed record CapabilityNodes(
            CharacterLocomotion Locomotion,
            EyesBehaviour Eyes,
            TestVoice Voice,
            HandPoseBehaviour LeftHand,
            HandPoseBehaviour RightHand);

        private sealed partial class TestVoice : Voice
        {
            public override void Speak(string speech)
            {
            }
        }
    }
}
