using AlleyCat.Animations;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Navigation;
using AlleyCat.Rigging.Installation;
using AlleyCat.Rigging.Physics;
using AlleyCat.Speech.Generation;
using AlleyCat.Speech.Voice;
using Godot;
using Xunit;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Motion;

/// <summary>Exercise the packed targets through automatic role installation, not just template instantiation.</summary>
public sealed class NPCRoleInstallationTests : IDisposable
{
    // Nothing here depends on generated motion: keep every MotionBricks node in this fixture native-free.
    private readonly NativeFreeMotion _nativeFreeMotion = NativeFreeMotion.Install(TestUtils.GetSceneTree());

    /// <inheritdoc />
    public void Dispose() => _nativeFreeMotion.Dispose();

    private const string ContentNPC = "res://content/the_price_of_dissent/characters/이만복/이만복_npc.tscn";

    /// <summary>Positive control for the original female role.</summary>
    [Fact]
    public Task ReferenceFemale_InstallsBoundNavigation()
        => CheckInstallation("res://assets/characters/reference/ally_npc.tscn");

    /// <summary>The male role must publish the real installed driver too.</summary>
    [Fact]
    public Task ReferenceMale_InstallsBoundNavigation()
        => CheckInstallation("res://assets/characters/reference/vadim_npc.tscn");

    /// <summary>A different model node name must not redirect or disable the mount.</summary>
    [Fact]
    public Task ContentNPC_InstallsOnOriginalSkeleton() => CheckInstallation(ContentNPC);

    /// <summary>Changing just the root name must not change any binding.</summary>
    [Fact]
    public Task RenamedContentNPC_InstallsOnOriginalSkeleton() => CheckInstallation(ContentNPC, rename: true);

    /// <summary>Independent default override and clear survive real template installation without changing entries.</summary>
    [Fact]
    public Task TemplateChainVariantDefaults_SurviveInstallation()
        => CheckInstallation("res://tests/animation/catalogue/chain/chain_npc.tscn", catalogueCheck: catalogue =>
        {
            Assert.Equal(("slump", ""), (catalogue.DefaultIdle, catalogue.DefaultLocomotion));
            Assert.Equal(["BaseVariants", "LocalVariants"],
                catalogue.GetChildren().OfType<AnimationVariantLayer>().Select(layer => layer.Name.ToString()));
            Assert.Equal(["idle/sad", "locomotion/feminine", "idle/slump"],
                catalogue.Entries.Select(entry => $"{entry.Category.ToName()}/{entry.ID}"));
            Assert.Equal("slump", catalogue.Idle.Effective.ID);
            Assert.Equal("feminine", catalogue.Locomotion.Effective.ID);
            catalogue.Idle.Preference = "sad";
            Assert.Equal("sad", catalogue.Idle.Effective.ID);
            Assert.Equal("feminine", catalogue.Locomotion.Effective.ID);
        });

    // Reference NPC templates: base declares the locomotion default and the NPC role the idle default.
    private static void AssertReferenceCatalogue(AnimationCatalogue catalogue)
    {
        Assert.Equal(("sad", "feminine"), (catalogue.DefaultIdle, catalogue.DefaultLocomotion));
        Assert.Equal("sad", catalogue.Idle.Effective.ID);
        Assert.Equal("feminine", catalogue.Locomotion.Effective.ID);
    }

    private static async Task CheckInstallation(string scene, bool rename = false, Action<AnimationCatalogue>? catalogueCheck = null)
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var viewport = new SubViewport { OwnWorld3D = true };
        MotionBricksNavigation? installed = null;
        tree.Root.AddChild(viewport);
        try
        {
            Rid map = viewport.FindWorld3D().NavigationMap;
            using var mesh = new NavigationMesh
            {
                Vertices = [new(-4, 0, -4), new(4, 0, -4), new(4, 0, 4), new(-4, 0, 4)]
            };
            mesh.AddPolygon([0, 1, 2, 3]);
            var region = new NavigationRegion3D { NavigationMesh = mesh };
            NavigationServer3D.MapSetUseAsyncIterations(map, false);
            NavigationServer3D.RegionSetUseAsyncIterations(region.GetRid(), false);
            viewport.AddChild(region);
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
            Assert.NotEmpty(NavigationServer3D.MapGetPath(map, Vector3.Zero, Vector3.Forward, true));

            CharacterHub actor = TestUtils.LoadPackedScene(scene).Instantiate<CharacterHub>();
            Skeleton3D skeleton = Assert.IsType<Skeleton3D>(Assert.Single(actor.FindChildren("*", "Skeleton3D", owned: false)));
            Node model = skeleton.GetParent();
            string identity = actor.Id;
            RigRoleTemplateSceneInstaller installer = actor.GetNode<RigRoleTemplateSceneInstaller>("NPCCharacterInstaller");
            BodyColliderProfile? profile = installer.ColliderProfile;
            Node3D? eyes = skeleton.GetNodeOrNull<Node3D>("BoneAttachment3D/Eyes");
            if (rename)
            {
                actor.Name = "InstallationProbeRoot";
            }
            viewport.AddChild(actor);
            Assert.True(actor.IsNodeReady());
            await TestUtils.WaitForNextFrameAsync(tree);
            await TestUtils.WaitForNextPhysicsFrameAsync(tree);

            Assert.Same(skeleton, model.GetNode("SOMA/Native/GeneralSkeleton"));
            _ = Assert.IsType<RetargetModifier3D>(skeleton.GetParent());
            _ = Assert.IsType<Skeleton3D>(skeleton.GetParent().GetParent());
            MotionBricksNavigation navigation = Assert.IsType<MotionBricksNavigation>(actor.Navigation);
            installed = navigation;
            Assert.Same(navigation, Assert.Single(actor.GetChildren().OfType<NavigationBase>()));
            Assert.Equal(identity, actor.Id);
            (catalogueCheck ?? AssertReferenceCatalogue)(AnimationCatalogue.RequireFor(actor));
            Assert.Same(profile, installer.ColliderProfile);
            if (scene == ContentNPC)
            {
                Assert.NotNull(eyes);
                Assert.Same(eyes, skeleton.GetNode("BoneAttachment3D/Eyes"));
                Assert.False(eyes.Visible);
                Assert.Equal("res://assets/characters/eyes/materials/eye_brown.tres",
                    eyes.GetNode<MeshInstance3D>("RightEye").GetSurfaceOverrideMaterial(0).ResourcePath);
                SupertonicSpeechGenerator speech = Assert.IsType<SupertonicSpeechGenerator>(Assert.IsType<AIVoice>(actor.Voice).SpeechGenerator);
                Assert.Equal("M2", speech.Voice);
                Assert.Equal("ko", speech.Language);
            }
            Assert.Equal(NavigationDestinationResult.Accepted,
                navigation.SetDestination(new Transform3D(Basis.Identity, Vector3.Forward)));
            await TestUtils.WaitForNextPhysicsFrameAsync(tree);
            Assert.Equal(string.Empty, navigation.Failure);
            Assert.True(navigation.SamplesRecorded > 0);
        }
        finally
        {
            Task? worker = installed?.WorkerCompletion;
            viewport.Free();
            if (worker is not null)
            {
                await worker.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }
}
