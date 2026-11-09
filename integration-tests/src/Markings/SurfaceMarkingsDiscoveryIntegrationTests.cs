using AlleyCat.Character;
using AlleyCat.Core.Installer;
using AlleyCat.Markings;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Markings;

/// <summary>
/// Godot-runtime coverage for texture-identity target discovery: the reference templates
/// author no <see cref="SurfaceMarkings.TargetMesh"/>, so their components install across
/// character topologies with differently-named body meshes (nothing to rebase), and the bake
/// resolves its target among the parent node's mesh descendants by albedo-base texture
/// identity. Zero and multiple matches fail loudly without mutation and without breaking the
/// host that receives the component.
/// </summary>
public sealed class SurfaceMarkingsDiscoveryIntegrationTests
{
    private const string ReferenceFemaleBaseScenePath =
        "res://assets/characters/templates/reference_female/reference_female_base.tscn";

    /// <summary>
    /// Headless-safe: the parent symptom — the reference template's authored target-mesh
    /// reference could not be rebased into a cross-topology character and aborted the whole
    /// install. The template no longer authors a target mesh, so the same production rebase
    /// call the character installer makes is a no-op, and an authored overlay bakes onto the
    /// discovered differently-named body mesh.
    /// </summary>
    [Headless]
    [Fact]
    public void CrossTopologyInstall_TemplateComponent_RebasesCleanlyAndDiscoversDifferentlyNamedBody()
    {
        SceneTree sceneTree = GetSceneTree();
        Node templateRoot = LoadPackedScene(ReferenceFemaleBaseScenePath).Instantiate();
        sceneTree.Root.AddChild(templateRoot);
        SurfaceMarkings templateMarkings = templateRoot.GetNode<SurfaceMarkings>("SurfaceMarkings");
        Assert.Null(templateMarkings.TargetMesh);

        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);
        Node3D host = new()
        {
            Name = "SyntheticCrossTopologyHost"
        };
        MeshInstance3D body = new()
        {
            Name = "Ayana_Style_Body",
            Mesh = new QuadMesh { Material = new StandardMaterial3D { AlbedoTexture = albedoBase } },
        };
        host.AddChild(body);
        sceneTree.Root.AddChild(host);
        try
        {
            // The installer's copy step: the template's component lands on the target root.
            var copy = (SurfaceMarkings)templateMarkings.Duplicate();
            host.AddChild(copy);
            Assert.Null(copy.Overlay);

            // The production rebase call (failOnUnresolved: true) that used to abort the
            // whole character install on cross-topology targets.
            TemplateSceneReferenceRebaser.RebaseExportedNodeReferences(
                copy, templateRoot, host, new CharacterRuntimeSubsystemInstaller(), failOnUnresolved: true);

            // Discovery bakes onto the differently-named body mesh through the component's
            // parent subtree.
            copy.Overlay = CreateSingleChannelOverlay(albedoBase);
            copy.Apply();

            Material baked = body.GetSurfaceOverrideMaterial(0);
            Assert.NotNull(baked);
            ImageTexture bakedAlbedo = Assert.IsType<ImageTexture>(baked.Get("albedo_texture").As<Texture2D>(), exactMatch: false);
            Assert.Equal(0, ReadByte(bakedAlbedo.GetImage(), 8, 8, 0));
            Assert.Equal(204, ReadByte(bakedAlbedo.GetImage(), 1, 1, 0));
            Assert.Same(albedoBase, body.Mesh.SurfaceGetMaterial(0).Get("albedo_texture").As<Texture2D>());
        }
        finally
        {
            host.QueueFree();
            templateRoot.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: discovery finds no mesh surface referencing the albedo base — the
    /// explicit bake throws a clear authoring error before any mutation, the editor preview
    /// reports it as a diagnostic, and the same failure inside <c>_Ready</c> never escapes
    /// into the host that receives the component.
    /// </summary>
    [Headless]
    [Fact]
    public void Discovery_WithoutMatches_FailsLoudWithoutMutationOrEscapingIntoTheHost()
    {
        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);
        Node3D host = new()
        {
            Name = "DiscoveryHost"
        };
        MeshInstance3D body = new()
        {
            Name = "UnrelatedBody",
            Mesh = new QuadMesh
            {
                Material = new StandardMaterial3D { AlbedoTexture = CreateSolidTexture(16, 10, 20, 30) },
            },
        };
        host.AddChild(body);
        GetSceneTree().Root.AddChild(host);
        try
        {
            SurfaceMarkings markings = new()
            {
                Name = "SurfaceMarkings"
            };
            host.AddChild(markings);
            markings.Overlay = CreateSingleChannelOverlay(albedoBase);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(markings.Apply);
            Assert.Contains("no mesh surface", ex.Message, StringComparison.Ordinal);
            Assert.Contains("TargetMesh", ex.Message, StringComparison.Ordinal);
            Assert.Null(body.GetSurfaceOverrideMaterial(0));

            // The install path: a component whose authored overlay cannot resolve a target
            // must not throw out of _Ready into the host tree, and the failure is reported
            // as an error diagnostic while every surface stays unmarked.
            List<(string Message, bool IsError)> diagnostics = [];
            SurfaceMarkings failing = new()
            {
                Name = "FailingSurfaceMarkings",
                Overlay = CreateSingleChannelOverlay(albedoBase),
                PreviewDiagnosticSink = (message, isError) => diagnostics.Add((message, isError))
            };
            host.AddChild(failing);

            Assert.True(failing.GetParent() is not null, "The failing component stays in the host tree.");
            Assert.Contains(diagnostics, d => d.IsError && d.Message.Contains("no mesh surface", StringComparison.Ordinal));
            Assert.Null(body.GetSurfaceOverrideMaterial(0));
        }
        finally
        {
            host.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: two mesh surfaces referencing the same albedo base are ambiguous —
    /// a loud authoring error naming every match, with no mutation.
    /// </summary>
    [Headless]
    [Fact]
    public void Discovery_WithMultipleMatches_FailsLoudNamingEveryMatchWithoutMutation()
    {
        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);
        Node3D host = new()
        {
            Name = "AmbiguousDiscoveryHost"
        };
        MeshInstance3D first = CreateMeshReferencing("FirstBody", albedoBase);
        MeshInstance3D second = CreateMeshReferencing("SecondBody", albedoBase);
        host.AddChild(first);
        host.AddChild(second);
        GetSceneTree().Root.AddChild(host);
        try
        {
            SurfaceMarkings markings = new()
            {
                Name = "SurfaceMarkings"
            };
            host.AddChild(markings);
            markings.Overlay = CreateSingleChannelOverlay(albedoBase);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(markings.Apply);
            Assert.Contains("2 mesh surfaces", ex.Message, StringComparison.Ordinal);
            Assert.Contains("FirstBody", ex.Message, StringComparison.Ordinal);
            Assert.Contains("SecondBody", ex.Message, StringComparison.Ordinal);
            Assert.Null(first.GetSurfaceOverrideMaterial(0));
            Assert.Null(second.GetSurfaceOverrideMaterial(0));
        }
        finally
        {
            host.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: the cross-topology containment no-op — a copied component with no
    /// authored overlay installs into the host and stays a strict no-op.
    /// </summary>
    [Headless]
    [Fact]
    public void CrossTopology_WithoutOverlay_IsStrictNoOp()
    {
        SceneTree sceneTree = GetSceneTree();
        Node templateRoot = LoadPackedScene(ReferenceFemaleBaseScenePath).Instantiate();
        sceneTree.Root.AddChild(templateRoot);
        SurfaceMarkings templateMarkings = templateRoot.GetNode<SurfaceMarkings>("SurfaceMarkings");

        Node3D host = new()
        {
            Name = "UnmarkedCrossTopologyHost"
        };
        MeshInstance3D body = new()
        {
            Name = "Ayana_Style_Body",
            Mesh = new QuadMesh
            {
                Material = new StandardMaterial3D { AlbedoTexture = CreateSolidTexture(4, 1, 2, 3) },
            },
        };
        host.AddChild(body);
        sceneTree.Root.AddChild(host);
        try
        {
            var copy = (SurfaceMarkings)templateMarkings.Duplicate();
            host.AddChild(copy);
            copy.Apply();

            Assert.Null(copy.Overlay);
            Assert.Null(body.GetSurfaceOverrideMaterial(0));
            Assert.Null(body.MaterialOverride);
        }
        finally
        {
            host.QueueFree();
            templateRoot.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: on the reference character herself, discovery resolves the body mesh
    /// through the imported diffuse the body material already references — the editor
    /// preview keeps working on the reference templates after the target-mesh de-authoring.
    /// </summary>
    [Headless]
    [Fact]
    public void Discovery_OnReferenceFemaleTemplate_FindsBodyThroughImportedDiffuse()
    {
        SceneTree sceneTree = GetSceneTree();
        CharacterBody3D root = LoadPackedScene(ReferenceFemaleBaseScenePath).Instantiate<CharacterBody3D>();
        MeshInstance3D bodyMesh = root.GetNode<MeshInstance3D>("Female/GeneralSkeleton/Female_body");
        Material sharedImportedBase = bodyMesh.GetActiveMaterial(0);
        Texture2D importedAlbedo = sharedImportedBase.Get("albedo_texture").As<Texture2D>();
        Assert.NotNull(importedAlbedo);
        SurfaceMarkings markings = root.GetNode<SurfaceMarkings>("SurfaceMarkings");
        Assert.Null(markings.TargetMesh);

        sceneTree.Root.AddChild(root);
        try
        {
            markings.Overlay = CreateSingleChannelOverlay(importedAlbedo);
            markings.Apply();

            Material baked = bodyMesh.GetSurfaceOverrideMaterial(0);
            Assert.NotNull(baked);
            Assert.NotSame(sharedImportedBase, baked);
            ImageTexture bakedAlbedo = Assert.IsType<ImageTexture>(baked.Get("albedo_texture").As<Texture2D>(), exactMatch: false);
            Image bakedImage = bakedAlbedo.GetImage();
            // The black multiply stamp centres on the diffuse: sample the texture centre.
            Assert.True(ReadByte(bakedImage, bakedImage.GetWidth() / 2, bakedImage.GetHeight() / 2, 0) < 32);
            Assert.Same(importedAlbedo, sharedImportedBase.Get("albedo_texture").As<Texture2D>());
        }
        finally
        {
            root.QueueFree();
        }
    }

    private static MeshInstance3D CreateMeshReferencing(string name, Texture2D albedoBase) => new()
    {
        Name = name,
        Mesh = new QuadMesh { Material = new StandardMaterial3D { AlbedoTexture = albedoBase } },
    };

    private static MarkingOverlay CreateSingleChannelOverlay(Texture2D albedoBase)
        => new()
        {
            AlbedoBase = albedoBase,
            Layers = [new MarkingLayer { Name = "Fixture", Markings = [CreateBlackBoxMarking()] }],
        };

    private static SurfaceMarking CreateBlackBoxMarking() => new()
    {
        Position = new Vector2(0.5f, 0.5f),
        Scale = new Vector2(0.5f, 0.5f),
        AlbedoTexture = CreateSolidTexture(4, 255, 255, 255),
        Tint = Colors.Black,
        Strength = 1f,
        AlbedoBlend = MarkingBlendMode.Multiply,
    };

    private static Image CreateSolidImage(int size, byte r, byte g, byte b)
    {
        var image = Image.CreateEmpty(size, size, useMipmaps: false, Image.Format.Rgba8);
        image.Fill(new Color(r / 255f, g / 255f, b / 255f));
        return image;
    }

    private static Texture2D CreateSolidTexture(int size, byte r, byte g, byte b)
        => ImageTexture.CreateFromImage(CreateSolidImage(size, r, g, b));

    private static int ReadByte(Image image, int x, int y, int component)
    {
        Color colour = image.GetPixel(x, y);
        return component switch
        {
            0 => colour.R8,
            1 => colour.G8,
            2 => colour.B8,
            _ => colour.A8,
        };
    }
}
