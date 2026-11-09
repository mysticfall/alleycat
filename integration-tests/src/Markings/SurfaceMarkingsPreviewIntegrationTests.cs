using AlleyCat.Markings;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Markings;

/// <summary>
/// Godot-runtime coverage for the editor preview trigger of <see cref="SurfaceMarkings"/>:
/// authored resources emit <c>Changed</c> on inspector edits (the engine does not emit it
/// for script resources automatically), the per-frame authored-graph poll re-wires and
/// re-bakes after a fresh <see cref="SurfaceMarkings.Overlay"/> assignment, in-place
/// marking edits re-bake through the wired <c>Changed</c> path, invalid authored states
/// are reported as editor-visible diagnostics instead of a silent no-op, a successful
/// bake reports its per-channel replaced-slot counts, and re-pointed install targets
/// (<see cref="SurfaceMarkings.TargetMesh"/>/<see cref="SurfaceMarkings.SurfaceIndex"/>)
/// strip the recorded target properly before any re-bake — so a baked preview material can
/// never be left on or saved into a target the author moved away from.
/// </summary>
public sealed class SurfaceMarkingsPreviewIntegrationTests
{
    /// <summary>
    /// Headless-safe: signals and resource state only. This is the root contract the
    /// preview wiring depends on — engine documentation is explicit that script resources
    /// must emit <c>Changed</c> themselves, so auto-property resources never fire it.
    /// </summary>
    [Headless]
    [Fact]
    public void AuthoredResources_EmitChangedOnPropertyEdits()
    {
        MarkingOverlay overlay = new();
        int overlayChanges = 0;
        overlay.Changed += () => overlayChanges++;
        overlay.AlbedoBase = CreateSolidTexture(4, 1, 2, 3);
        overlay.NormalBase = CreateSolidTexture(4, 4, 5, 6);
        overlay.Layers = [new MarkingLayer { Name = "L" }];
        Assert.Equal(3, overlayChanges);

        MarkingLayer layer = new();
        int layerChanges = 0;
        layer.Changed += () => layerChanges++;
        layer.Name = "Tattoos";
        layer.Enabled = false;
        layer.Markings = [];
        Assert.Equal(3, layerChanges);

        SurfaceMarking marking = new();
        int markingChanges = 0;
        marking.Changed += () => markingChanges++;
        marking.Position = new Vector2(0.25f, 0.75f);
        marking.RotationDegrees = 15f;
        marking.Scale = new Vector2(0.2f, 0.2f);
        marking.AlbedoTexture = CreateSolidTexture(4, 255, 255, 255);
        marking.NormalTexture = CreateSolidTexture(4, 128, 128, 200);
        marking.RoughnessTexture = CreateSolidTexture(4, 0, 153, 0);
        marking.Tint = Colors.Black;
        marking.Strength = 0.5f;
        marking.AlbedoBlend = MarkingBlendMode.Overlay;
        marking.RoughnessBlend = MarkingBlendMode.Darken;
        Assert.Equal(10, markingChanges);
    }

    /// <summary>
    /// Headless-safe: drives the two <c>_Process</c> steps of the editor preview path
    /// (<see cref="SurfaceMarkings.PollPreviewGraph"/> then
    /// <see cref="SurfaceMarkings.RebuildPreview"/> after the debounce) against the user's
    /// authoring flow — the scene opens unmarked and a fresh overlay is assigned on the
    /// node afterwards.
    /// </summary>
    [Headless]
    [Fact]
    public void EditorPreview_OverlayAssignedAfterReady_InstallsPreviewThroughGraphPoll()
    {
        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);
        MeshInstance3D mesh = CreateQuad();
        ((QuadMesh)mesh.Mesh).Material = new StandardMaterial3D { AlbedoTexture = albedoBase };
        SurfaceMarkings markings = AttachMarkings(mesh);
        try
        {
            // The scene opened unmarked: _Ready stayed a strict no-op.
            Assert.Null(mesh.GetSurfaceOverrideMaterial(0));

            // The editor flow: assign a NEW overlay resource on the node, then the poll
            // (every editor frame) and the debounced rebuild pick it up.
            markings.Overlay = CreateSingleChannelOverlay(albedoBase);
            markings.PollPreviewGraph();
            markings.RebuildPreview();

            ImageTexture baked = Assert.IsType<ImageTexture>(
                mesh.GetSurfaceOverrideMaterial(0)?.Get("albedo_texture").As<Texture2D>(), exactMatch: false);
            Assert.Equal(0, ReadByte(baked.GetImage(), 8, 8, 0));
            Assert.Equal(204, ReadByte(baked.GetImage(), 1, 1, 0));
            Assert.Same(albedoBase, mesh.Mesh.SurfaceGetMaterial(0).Get("albedo_texture").As<Texture2D>());
        }
        finally
        {
            mesh.GetParent()?.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: an in-place marking edit fires <c>Changed</c> on the wired resource
    /// and the next debounced rebuild bakes the new placement.
    /// </summary>
    [Headless]
    [Fact]
    public void EditorPreview_MarkingEditThroughChangedSignal_ReBakesNewPlacement()
    {
        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);
        MeshInstance3D mesh = CreateQuad();
        ((QuadMesh)mesh.Mesh).Material = new StandardMaterial3D { AlbedoTexture = albedoBase };
        SurfaceMarkings markings = AttachMarkings(mesh);
        try
        {
            markings.Overlay = CreateSingleChannelOverlay(albedoBase);
            markings.PollPreviewGraph();
            markings.RebuildPreview();

            // The inspector edit path: the wired marking emits Changed, marking the preview
            // dirty; the next rebuild (post-debounce in the editor) bakes the new placement.
            markings.Overlay!.Layers[0].Markings[0].Position = new Vector2(0.8f, 0.8f);
            markings.RebuildPreview();

            ImageTexture baked = Assert.IsType<ImageTexture>(
                mesh.GetSurfaceOverrideMaterial(0)?.Get("albedo_texture").As<Texture2D>(), exactMatch: false);
            Assert.Equal(204, ReadByte(baked.GetImage(), 8, 8, 0));
            Assert.Equal(0, ReadByte(baked.GetImage(), 12, 12, 0));
        }
        finally
        {
            mesh.GetParent()?.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: a channel base no material slot references is reported as an
    /// editor-visible error diagnostic and the surface stays unmarked — the identity
    /// mismatch can never be a silent no-op.
    /// </summary>
    [Headless]
    [Fact]
    public void EditorPreview_BaseNotReferencedByAnySlot_ReportsErrorAndStaysUnmarked()
    {
        Texture2D unreferencedBase = CreateSolidTexture(16, 204, 178, 153);
        MeshInstance3D mesh = CreateQuad();
        ((QuadMesh)mesh.Mesh).Material = new StandardMaterial3D
        {
            AlbedoTexture = CreateSolidTexture(16, 10, 20, 30),
        };
        SurfaceMarkings markings = AttachMarkings(mesh);
        List<(string Message, bool IsError)> diagnostics = [];
        markings.PreviewDiagnosticSink = (message, isError) => diagnostics.Add((message, isError));
        try
        {
            markings.Overlay = CreateSingleChannelOverlay(unreferencedBase);
            markings.PollPreviewGraph();
            markings.RebuildPreview();

            Assert.Null(mesh.GetSurfaceOverrideMaterial(0));
            Assert.Contains(diagnostics, d => d.IsError && d.Message.Contains("no slot", StringComparison.Ordinal));
        }
        finally
        {
            mesh.GetParent()?.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: a successful preview bake reports how many texture slots each
    /// stamp-supplying channel replaced, so identity replacement is observable by the
    /// author.
    /// </summary>
    [Headless]
    [Fact]
    public void EditorPreview_SuccessfulBake_ReportsPerChannelReplacedSlotCounts()
    {
        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);
        MeshInstance3D mesh = CreateQuad();
        ((QuadMesh)mesh.Mesh).Material = new StandardMaterial3D { AlbedoTexture = albedoBase };
        SurfaceMarkings markings = AttachMarkings(mesh);
        List<(string Message, bool IsError)> diagnostics = [];
        markings.PreviewDiagnosticSink = (message, isError) => diagnostics.Add((message, isError));
        try
        {
            markings.Overlay = CreateSingleChannelOverlay(albedoBase);
            markings.PollPreviewGraph();
            markings.RebuildPreview();

            Assert.NotNull(mesh.GetSurfaceOverrideMaterial(0));
            Assert.Contains(
                diagnostics,
                d => !d.IsError
                    && d.Message.Contains("albedo", StringComparison.Ordinal)
                    && d.Message.Contains("1 slot", StringComparison.Ordinal));
        }
        finally
        {
            mesh.GetParent()?.QueueFree();
        }
    }

    /// <summary>
    /// Windowed (renderer required): the static-skinned-mesh visibility contract — a
    /// skin-weighted mesh under a skeleton that never animates (the editor viewport's
    /// state) shows an installed marking preview immediately, with no flush workaround.
    /// Locks the engine behaviour measured by <c>tests/markings/static_flush_probe.gd</c>.
    /// </summary>
    [Fact]
    public async Task StaticSkinnedMesh_InstalledPreview_IsVisibleWithoutSkeletonAnimation()
    {
        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);
        Node3D holder = new()
        {
            Name = "MarkingsStaticSkinFixture"
        };
        GetSceneTree().Root.AddChild(holder);
        try
        {
            (Skeleton3D skeleton, MeshInstance3D mesh) = CreateSkinnedQuad(holder, albedoBase);
            SurfaceMarkings markings = AttachMarkings(mesh);

            // The mesh instance is genuinely skinned (a skin reference is attached), and
            // the skeleton is fully static across the settling window — no animation, like
            // the editor viewport.
            Assert.NotNull(mesh.GetSkinReference());
            Transform3D bonePose = skeleton.GetBonePose(0);

            SubViewport viewport = CreateFacingViewport(holder);

            await WaitForFramesAsync(GetSceneTree(), 10);
            Assert.Equal(bonePose, skeleton.GetBonePose(0));
            Image unmarked = viewport.GetTexture().GetImage();

            markings.Overlay = CreateSingleChannelOverlay(albedoBase);
            markings.PollPreviewGraph();
            markings.RebuildPreview();
            Assert.NotNull(mesh.GetSurfaceOverrideMaterial(0));

            await WaitForFramesAsync(GetSceneTree(), 10);
            Image marked = viewport.GetTexture().GetImage();

            Assert.True(
                CountDarkenedPixels(unmarked, marked) > 500,
                "A marking installed on a static skinned mesh must become visible in the rendered frame.");
        }
        finally
        {
            holder.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: the recorded-install-target contract on the surface-override path with
    /// a null original override. After a preview is installed on mesh A, re-pointing
    /// <see cref="SurfaceMarkings.TargetMesh"/> to mesh B and polling (the editor
    /// <c>_Process</c> step; the save/exit strip paths share the same recorded-target code)
    /// must restore mesh A exactly — null-original case — and leave mesh B untouched, with
    /// no baked material left installed on either.
    /// </summary>
    [Headless]
    [Fact]
    public void RepointedTargetMesh_PollStripsRecordedTargetAndLeavesNewTargetUntouched()
    {
        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);
        MeshInstance3D meshA = CreateQuad();
        Material materialA = new StandardMaterial3D { AlbedoTexture = albedoBase };
        ((QuadMesh)meshA.Mesh).Material = materialA;
        MeshInstance3D meshB = CreateQuad();
        Material materialB = new StandardMaterial3D { AlbedoTexture = albedoBase };
        ((QuadMesh)meshB.Mesh).Material = materialB;
        SurfaceMarkings markings = AttachMarkings(meshA);
        try
        {
            markings.Overlay = CreateSingleChannelOverlay(albedoBase);
            markings.PollPreviewGraph();
            markings.RebuildPreview();
            Assert.NotSame(materialA, meshA.GetSurfaceOverrideMaterial(0));

            // The inspector re-point: TargetMesh now references mesh B.
            markings.TargetMesh = meshB;
            markings.PollPreviewGraph();

            // Mesh A is restored exactly (null-original surface override case) and keeps its
            // own material with the shared base texture still installed.
            Assert.Null(meshA.GetSurfaceOverrideMaterial(0));
            Assert.Same(materialA, meshA.GetActiveMaterial(0));
            Assert.Same(albedoBase, materialA.Get("albedo_texture").As<Texture2D>());

            // Mesh B is untouched and nothing baked is installed anywhere.
            Assert.Null(meshB.GetSurfaceOverrideMaterial(0));
            Assert.Same(materialB, meshB.GetActiveMaterial(0));
            Assert.Same(albedoBase, materialB.Get("albedo_texture").As<Texture2D>());
        }
        finally
        {
            meshA.GetParent()?.QueueFree();
            meshB.GetParent()?.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: after a target re-point, the debounced rebuild bakes onto the new
    /// target only — the old target stays restored.
    /// </summary>
    [Headless]
    [Fact]
    public void RepointedTargetMesh_RebuildAfterPoll_BakesOntoNewTargetOnly()
    {
        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);
        MeshInstance3D meshA = CreateQuad();
        Material materialA = new StandardMaterial3D { AlbedoTexture = albedoBase };
        ((QuadMesh)meshA.Mesh).Material = materialA;
        MeshInstance3D meshB = CreateQuad();
        Material materialB = new StandardMaterial3D { AlbedoTexture = albedoBase };
        ((QuadMesh)meshB.Mesh).Material = materialB;
        SurfaceMarkings markings = AttachMarkings(meshA);
        try
        {
            markings.Overlay = CreateSingleChannelOverlay(albedoBase);
            markings.PollPreviewGraph();
            markings.RebuildPreview();
            Assert.NotNull(meshA.GetSurfaceOverrideMaterial(0));

            markings.TargetMesh = meshB;
            markings.PollPreviewGraph();
            markings.RebuildPreview();

            ImageTexture baked = Assert.IsType<ImageTexture>(
                meshB.GetSurfaceOverrideMaterial(0)?.Get("albedo_texture").As<Texture2D>(), exactMatch: false);
            Assert.Equal(0, ReadByte(baked.GetImage(), 8, 8, 0));

            Assert.Null(meshA.GetSurfaceOverrideMaterial(0));
            Assert.Same(materialA, meshA.GetActiveMaterial(0));
            Assert.Same(albedoBase, materialA.Get("albedo_texture").As<Texture2D>());
        }
        finally
        {
            meshA.GetParent()?.QueueFree();
            meshB.GetParent()?.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: the recorded-install-target contract on the whole-instance override
    /// path. A re-point must restore mesh A's original <c>MaterialOverride</c> and must not
    /// clobber mesh B's own override with the old target's recorded material.
    /// </summary>
    [Headless]
    [Fact]
    public void RepointedTargetMesh_OnInstanceOverride_PollRestoresOriginalInstanceOverride()
    {
        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);
        MeshInstance3D meshA = CreateQuad();
        Material overrideA = new StandardMaterial3D { AlbedoTexture = albedoBase };
        meshA.MaterialOverride = overrideA;
        MeshInstance3D meshB = CreateQuad();
        Texture2D otherBase = CreateSolidTexture(16, 10, 20, 30);
        Material overrideB = new StandardMaterial3D { AlbedoTexture = otherBase };
        meshB.MaterialOverride = overrideB;
        SurfaceMarkings markings = AttachMarkings(meshA);
        try
        {
            markings.Overlay = CreateSingleChannelOverlay(albedoBase);
            markings.PollPreviewGraph();
            markings.RebuildPreview();
            Material installed = meshA.MaterialOverride;
            Assert.NotNull(installed);
            Assert.NotSame(overrideA, installed);

            markings.TargetMesh = meshB;
            markings.PollPreviewGraph();

            // Mesh A's whole-instance override is restored exactly.
            Assert.Same(overrideA, meshA.MaterialOverride);
            Assert.Same(albedoBase, overrideA.Get("albedo_texture").As<Texture2D>());
            Assert.Null(meshA.GetSurfaceOverrideMaterial(0));

            // Mesh B's own override is untouched — not replaced by the old target's recorded
            // material, and nothing baked is installed on it.
            Assert.Same(overrideB, meshB.MaterialOverride);
            Assert.Same(otherBase, overrideB.Get("albedo_texture").As<Texture2D>());
        }
        finally
        {
            meshA.GetParent()?.QueueFree();
            meshB.GetParent()?.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: the recorded-install-target contract for a <see cref="SurfaceMarkings.SurfaceIndex"/>
    /// re-point on a multi-surface mesh. A preview installed over an authored surface
    /// override on surface 0 must restore that authored override when the index moves to
    /// surface 1, leaving surface 1 untouched.
    /// </summary>
    [Headless]
    [Fact]
    public void RepointedSurfaceIndex_PollRestoresAuthoredOverrideAndLeavesOtherSurfaceUntouched()
    {
        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);
        Texture2D secondSurfaceBase = CreateSolidTexture(16, 10, 20, 30);
        MeshInstance3D mesh = CreateTwoSurfaceMesh(albedoBase, secondSurfaceBase);
        Material authoredOverride = new StandardMaterial3D { AlbedoTexture = albedoBase };
        mesh.SetSurfaceOverrideMaterial(0, authoredOverride);
        SurfaceMarkings markings = AttachMarkings(mesh);
        try
        {
            markings.Overlay = CreateSingleChannelOverlay(albedoBase);
            markings.PollPreviewGraph();
            markings.RebuildPreview();
            Material installed = mesh.GetSurfaceOverrideMaterial(0);
            Assert.NotNull(installed);
            Assert.NotSame(authoredOverride, installed);

            // The inspector re-point: the component now targets surface 1.
            markings.SurfaceIndex = 1;
            markings.PollPreviewGraph();

            // Surface 0 is restored to its authored override; surface 1 keeps its own
            // material and gains nothing.
            Assert.Same(authoredOverride, mesh.GetSurfaceOverrideMaterial(0));
            Assert.Same(albedoBase, authoredOverride.Get("albedo_texture").As<Texture2D>());
            Assert.Null(mesh.GetSurfaceOverrideMaterial(1));
            Assert.Same(secondSurfaceBase, mesh.Mesh.SurfaceGetMaterial(1).Get("albedo_texture").As<Texture2D>());
        }
        finally
        {
            mesh.GetParent()?.QueueFree();
        }
    }

    private static async Task WaitForFramesAsync(SceneTree sceneTree, int frameCount)
    {
        for (int i = 0; i < frameCount; i++)
        {
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private static (Skeleton3D Skeleton, MeshInstance3D Mesh) CreateSkinnedQuad(Node parent, Texture2D albedoBase)
    {
        Skeleton3D skeleton = new()
        {
            Name = "Skeleton"
        };
        _ = skeleton.AddBone("Root");

        // A quad in the XY plane with every vertex fully weighted to the single bone:
        // the renderer takes the skinned path while the bone never moves.
        Vector3[] vertices =
        [
            new(-0.5f, -0.5f, 0f), new(0.5f, -0.5f, 0f), new(-0.5f, 0.5f, 0f), new(0.5f, 0.5f, 0f),
        ];
        Vector3[] normals = [Vector3.Forward, Vector3.Forward, Vector3.Forward, Vector3.Forward];
        Vector2[] uvs = [new(0, 1), new(1, 1), new(0, 0), new(1, 0)];
        int[] bones = new int[16];
        float[] weights =
        [
            1f, 0f, 0f, 0f,
            1f, 0f, 0f, 0f,
            1f, 0f, 0f, 0f,
            1f, 0f, 0f, 0f,
        ];
        int[] indices = [0, 3, 1, 0, 2, 3];

        Godot.Collections.Array arrays = [];
        _ = arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Bones] = bones;
        arrays[(int)Mesh.ArrayType.Weights] = weights;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        BaseMaterial3D material = new StandardMaterial3D
        {
            AlbedoTexture = albedoBase,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        ArrayMesh mesh = new();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);

        MeshInstance3D meshInstance = new()
        {
            Name = "SkinnedQuad",
            Mesh = mesh,
            Skeleton = new NodePath(".."),
        };
        skeleton.AddChild(meshInstance);
        parent.AddChild(skeleton);
        return (skeleton, meshInstance);
    }

    private static SubViewport CreateFacingViewport(Node parent)
    {
        // No own world: the sub-viewport renders the parent viewport's world, where the
        // skinned fixture lives. Unshaded material means no environment is needed.
        SubViewport viewport = new()
        {
            Size = new Vector2I(128, 128),
            OwnWorld3D = false,
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        parent.AddChild(viewport);

        Camera3D camera = new()
        {
            // Facing +Z at the quad; LookAt needs the node inside the tree, so aim after
            // entering.
            Transform = new Transform3D(Basis.LookingAt(Vector3.Back, Vector3.Up), new Vector3(0, 0, -1.5f)),
        };
        viewport.AddChild(camera);
        return viewport;
    }

    private static int CountDarkenedPixels(Image unmarked, Image marked)
    {
        int darkened = 0;
        for (int y = 0; y < marked.GetHeight(); y++)
        {
            for (int x = 0; x < marked.GetWidth(); x++)
            {
                if (unmarked.GetPixel(x, y).V - marked.GetPixel(x, y).V > 0.3f)
                {
                    darkened++;
                }
            }
        }

        return darkened;
    }

    private static MeshInstance3D CreateTwoSurfaceMesh(Texture2D firstAlbedo, Texture2D secondAlbedo)
    {
        Godot.Collections.Array arrays = [];
        _ = arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
        };
        arrays[(int)Mesh.ArrayType.TexUV] = new[] { new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 0), new Vector2(1, 0) };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 3, 1, 0, 2, 3 };

        ArrayMesh mesh = new();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D { AlbedoTexture = firstAlbedo });
        mesh.SurfaceSetMaterial(1, new StandardMaterial3D { AlbedoTexture = secondAlbedo });

        MeshInstance3D meshInstance = new()
        {
            Name = "TwoSurfaceMesh",
            Mesh = mesh,
        };
        Node3D holder = new()
        {
            Name = "MarkingsIndexFixture",
        };
        holder.AddChild(meshInstance);
        GetSceneTree().Root.AddChild(holder);
        return meshInstance;
    }

    private static MarkingOverlay CreateSingleChannelOverlay(Texture2D albedoBase)
        => new()
        {
            AlbedoBase = albedoBase,
            Layers = [new MarkingLayer { Name = "Fixture", Markings = [CreateBlackBoxMarking(0.5f, 0.5f)] }],
        };

    private static SurfaceMarking CreateBlackBoxMarking(float positionU, float positionV) => new()
    {
        Position = new Vector2(positionU, positionV),
        Scale = new Vector2(0.5f, 0.5f),
        AlbedoTexture = CreateSolidTexture(4, 255, 255, 255),
        Tint = Colors.Black,
        Strength = 1f,
        AlbedoBlend = MarkingBlendMode.Multiply,
    };

    private static MeshInstance3D CreateQuad()
    {
        MeshInstance3D mesh = new()
        {
            Mesh = new QuadMesh(),
        };
        Node3D holder = new()
        {
            Name = "MarkingsPreviewFixture",
        };
        holder.AddChild(mesh);
        GetSceneTree().Root.AddChild(holder);
        return mesh;
    }

    private static SurfaceMarkings AttachMarkings(MeshInstance3D mesh)
    {
        // The overlay is assigned after entry so _Ready stays a strict no-op and each fact
        // drives the editor preview path explicitly.
        SurfaceMarkings markings = new()
        {
            Name = "SurfaceMarkings",
            TargetMesh = mesh,
        };
        mesh.AddChild(markings);
        return markings;
    }

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
