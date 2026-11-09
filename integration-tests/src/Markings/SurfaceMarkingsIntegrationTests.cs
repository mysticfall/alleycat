using AlleyCat.Markings;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Markings;

/// <summary>
/// Godot-runtime coverage for image-level surface marking baking: identity-based slot
/// replacement against <see cref="StandardMaterial3D"/> and the HumanShaders skin
/// <see cref="ShaderMaterial"/>, the authored surface-override and whole-instance override
/// paths, isolation of shared imported resources, the unmarked no-op on both reference
/// templates, dynamic layer mutation re-bakes, and fail-fast validation.
/// </summary>
public sealed class SurfaceMarkingsIntegrationTests
{
    private const string ReferenceFemaleBaseScenePath =
        "res://assets/characters/templates/reference_female/reference_female_base.tscn";
    private const string ReferenceMaleBaseScenePath =
        "res://assets/characters/templates/reference_male/reference_male_base.tscn";
    private const string SkinShaderPath = "res://addons/HumanShaders/skin_shader.gdshader";
    private const string FemaleBodyMeshPath = "Female/GeneralSkeleton/Female_body";
    private const string MaleBodyMeshPath = "Male/GeneralSkeleton/Male_body";

    /// <summary>
    /// Headless-safe: identity replacement and baked-pixel assertions only read resources.
    /// </summary>
    [Headless]
    [Fact]
    public void Apply_OnStandardMaterial_ReplacesEveryChannelSlotByIdentityAndBakesPixels()
    {
        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);
        Texture2D normalBase = CreateSolidTexture(16, 128, 128, 200);
        Texture2D roughnessBase = CreateSolidTexture(16, 0, 153, 0);
        StandardMaterial3D baseMaterial = new()
        {
            AlbedoTexture = albedoBase,
            NormalTexture = normalBase,
            RoughnessTexture = roughnessBase,
        };
        (MeshInstance3D mesh, SurfaceMarkings markings) = CreateMarkedQuad(baseMaterial, albedoBase, normalBase, roughnessBase);
        try
        {
            markings.Apply();

            Material baked = mesh.GetSurfaceOverrideMaterial(0);
            Assert.NotNull(baked);
            Assert.NotSame(baseMaterial, baked);

            // Identity replacement: every slot that held the exact base now holds a baked
            // ImageTexture, without any slot-name assumptions.
            ImageTexture bakedAlbedo = Assert.IsType<ImageTexture>(baked.Get("albedo_texture").As<Texture2D>(), exactMatch: false);
            ImageTexture bakedNormal = Assert.IsType<ImageTexture>(baked.Get("normal_texture").As<Texture2D>(), exactMatch: false);
            ImageTexture bakedRoughness = Assert.IsType<ImageTexture>(baked.Get("roughness_texture").As<Texture2D>(), exactMatch: false);
            Assert.NotSame(albedoBase, bakedAlbedo);
            Assert.NotSame(normalBase, bakedNormal);
            Assert.NotSame(roughnessBase, bakedRoughness);

            // The baked maps keep the base resolution and gain mipmaps.
            Image albedoImage = bakedAlbedo.GetImage();
            Assert.Equal(new Vector2I(16, 16), new Vector2I(albedoImage.GetWidth(), albedoImage.GetHeight()));
            Assert.True(albedoImage.HasMipmaps());

            // Black multiply ink darkens the stamped albedo to exactly zero; untouched pixels
            // keep the authored bytes.
            Assert.Equal(0, ReadByte(albedoImage, 8, 8, 0));
            Assert.Equal(204, ReadByte(albedoImage, 1, 1, 0));

            // Normals whiteout on rg and preserve the packed AO in blue exactly. Byte-aligned
            // base+ink sums land on half-byte results, so rg are asserted within one byte of
            // the exact whiteout value while blue must match the base byte-for-byte.
            Image normalImage = bakedNormal.GetImage();
            Assert.InRange(ReadByte(normalImage, 8, 8, 0), 200, 201);
            Assert.InRange(ReadByte(normalImage, 8, 8, 1), 128, 129);
            Assert.Equal(200, ReadByte(normalImage, 8, 8, 2));
            Assert.InRange(ReadByte(normalImage, 1, 1, 0), 128, 129);

            // Roughness blends the green scalar only: 0.6 * 0.2 = 0.12 -> byte 31.
            Image roughnessImage = bakedRoughness.GetImage();
            Assert.Equal(31, ReadByte(roughnessImage, 8, 8, 1));
            Assert.Equal(0, ReadByte(roughnessImage, 8, 8, 0));
            Assert.Equal(153, ReadByte(roughnessImage, 1, 1, 1));

            // The shared base material and its textures are never mutated.
            Assert.Same(albedoBase, baseMaterial.Get("albedo_texture").As<Texture2D>());
            AssertBytesEqual(albedoBase.GetImage(), CreateSolidImage(16, 204, 178, 153));
        }
        finally
        {
            mesh.GetParent()?.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: shader uniform slots are discovered through the same property
    /// enumeration, so the assertions only read resources.
    /// </summary>
    [Headless]
    [Fact]
    public void Apply_OnSkinShaderMaterial_ReplacesUniformSlotsByIdentity()
    {
        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);
        Texture2D normalBase = CreateSolidTexture(16, 128, 128, 200);
        Texture2D roughnessBase = CreateSolidTexture(16, 0, 153, 0);
        Shader shader = GD.Load<Shader>(SkinShaderPath)
            ?? throw new InvalidOperationException($"Expected the skin shader at '{SkinShaderPath}' to load.");
        ShaderMaterial baseMaterial = new()
        {
            Shader = shader
        };
        baseMaterial.SetShaderParameter("texture_albedo", albedoBase);
        baseMaterial.SetShaderParameter("texture_normal", normalBase);
        baseMaterial.SetShaderParameter("texture_roughness", roughnessBase);

        (MeshInstance3D mesh, SurfaceMarkings markings) = CreateMarkedQuad(baseMaterial, albedoBase, normalBase, roughnessBase);
        try
        {
            markings.Apply();

            Material baked = mesh.GetSurfaceOverrideMaterial(0);
            Assert.NotNull(baked);
            ShaderMaterial bakedShader = Assert.IsType<ShaderMaterial>(baked, exactMatch: false);
            Assert.NotSame(baseMaterial, bakedShader);

            // The uniform slots are found by texture identity, not by name mapping.
            _ = Assert.IsType<ImageTexture>(bakedShader.GetShaderParameter("texture_albedo").As<Texture2D>(), exactMatch: false);
            _ = Assert.IsType<ImageTexture>(bakedShader.GetShaderParameter("texture_normal").As<Texture2D>(), exactMatch: false);
            _ = Assert.IsType<ImageTexture>(bakedShader.GetShaderParameter("texture_roughness").As<Texture2D>(), exactMatch: false);

            // The skin-shader packing conventions survive the bake: AO packed in normal blue is
            // preserved and roughness lands on green.
            ImageTexture bakedNormal = bakedShader.GetShaderParameter("texture_normal").As<ImageTexture>();
            Assert.Equal(200, ReadByte(bakedNormal.GetImage(), 8, 8, 2));
            ImageTexture bakedRoughness = bakedShader.GetShaderParameter("texture_roughness").As<ImageTexture>();
            Assert.Equal(31, ReadByte(bakedRoughness.GetImage(), 8, 8, 1));

            // The shared authored material still references the exact base textures.
            Assert.Same(albedoBase, baseMaterial.GetShaderParameter("texture_albedo").As<Texture2D>());
        }
        finally
        {
            mesh.GetParent()?.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: the content-pack reality of an authored surface override composing with
    /// the baked duplicate, read back through material references.
    /// </summary>
    [Headless]
    [Fact]
    public void Apply_ThroughAuthoredSurfaceOverride_BakesOntoTheOverrideMaterial()
    {
        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);
        StandardMaterial3D authoredOverride = new()
        {
            AlbedoTexture = albedoBase
        };
        MeshInstance3D mesh = CreateQuad();
        mesh.SetSurfaceOverrideMaterial(0, authoredOverride);
        SurfaceMarkings markings = AttachMarkings(mesh, CreateSingleChannelOverlay(albedoBase));

        try
        {
            markings.Apply();

            Material baked = mesh.GetSurfaceOverrideMaterial(0);
            Assert.NotNull(baked);
            Assert.NotSame(authoredOverride, baked);
            _ = Assert.IsType<ImageTexture>(baked.Get("albedo_texture").As<Texture2D>(), exactMatch: false);
            Assert.Same(albedoBase, authoredOverride.Get("albedo_texture").As<Texture2D>());
        }
        finally
        {
            mesh.GetParent()?.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: a whole-instance material override wins the active-material precedence,
    /// so the bake composes onto it instead of the surface slots.
    /// </summary>
    [Headless]
    [Fact]
    public void Apply_ThroughWholeInstanceOverride_BakesOntoTheInstanceMaterial()
    {
        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);
        StandardMaterial3D instanceOverride = new()
        {
            AlbedoTexture = albedoBase
        };
        MeshInstance3D mesh = CreateQuad();
        mesh.MaterialOverride = instanceOverride;
        SurfaceMarkings markings = AttachMarkings(mesh, CreateSingleChannelOverlay(albedoBase));

        try
        {
            markings.Apply();

            Material baked = mesh.MaterialOverride;
            Assert.NotNull(baked);
            Assert.NotSame(instanceOverride, baked);
            _ = Assert.IsType<ImageTexture>(baked.Get("albedo_texture").As<Texture2D>(), exactMatch: false);
            Assert.Null(mesh.GetSurfaceOverrideMaterial(0));
            Assert.Same(albedoBase, instanceOverride.Get("albedo_texture").As<Texture2D>());
        }
        finally
        {
            mesh.GetParent()?.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: marking one character instance must leave the shared imported material
    /// and every other instance unmarked.
    /// </summary>
    [Headless]
    [Fact]
    public void Apply_OnOneCharacter_LeavesCharactersSharingImportedMaterialUnmarked()
    {
        (CharacterBody3D markedRoot, MeshInstance3D markedMesh, SurfaceMarkings markedMarkings) = InstantiateReferenceFemale();
        (CharacterBody3D plainRoot, MeshInstance3D plainMesh, _) = InstantiateReferenceFemale();
        try
        {
            Material sharedImportedBase = markedMesh.GetActiveMaterial(0);

            // Premise: both character instances really do share one imported material resource.
            Assert.Same(sharedImportedBase, plainMesh.GetActiveMaterial(0));

            Texture2D importedAlbedo = sharedImportedBase.Get("albedo_texture").As<Texture2D>();
            Assert.NotNull(importedAlbedo);
            byte[] baseBytes = importedAlbedo.GetImage().GetData();

            markedMarkings.Overlay = CreateSingleChannelOverlay(importedAlbedo);
            markedMarkings.Apply();

            Assert.NotSame(sharedImportedBase, markedMesh.GetSurfaceOverrideMaterial(0));
            Assert.Null(plainMesh.GetSurfaceOverrideMaterial(0));
            Assert.Same(sharedImportedBase, plainMesh.GetActiveMaterial(0));
            Assert.Same(importedAlbedo, sharedImportedBase.Get("albedo_texture").As<Texture2D>());
            AssertBytesEqual(importedAlbedo.GetImage(), baseBytes);
        }
        finally
        {
            markedRoot.QueueFree();
            plainRoot.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: the templates ship authored-but-unmarked components with no authored
    /// target mesh (discovery resolves it at bake time), so exercising <c>_Ready</c> in the
    /// tree only reads material state afterwards.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Ready_WithoutOverlay_LeavesFemaleBodyMaterialsUntouched()
        => await Ready_WithoutOverlay_LeavesBodyMaterialsUntouched(
            ReferenceFemaleBaseScenePath, FemaleBodyMeshPath);

    /// <summary>
    /// Headless-safe: the male template's authored wiring locks the same unmarked contract.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Ready_WithoutOverlay_LeavesMaleBodyMaterialsUntouched()
        => await Ready_WithoutOverlay_LeavesBodyMaterialsUntouched(
            ReferenceMaleBaseScenePath, MaleBodyMeshPath);

    private static async Task Ready_WithoutOverlay_LeavesBodyMaterialsUntouched(string scenePath, string bodyMeshPath)
    {
        SceneTree sceneTree = GetSceneTree();
        PackedScene scene = LoadPackedScene(scenePath);
        CharacterBody3D root = scene.Instantiate<CharacterBody3D>();
        MeshInstance3D bodyMesh = root.GetNode<MeshInstance3D>(bodyMeshPath);
        SurfaceMarkings markings = root.GetNode<SurfaceMarkings>("SurfaceMarkings");
        Assert.Null(markings.Overlay);
        Assert.Null(markings.TargetMesh);
        Material sharedBase = bodyMesh.GetActiveMaterial(0);

        sceneTree.Root.AddChild(root);
        try
        {
            await WaitForNextFrameAsync(sceneTree);

            Assert.Null(bodyMesh.GetSurfaceOverrideMaterial(0));
            Assert.Same(sharedBase, bodyMesh.GetActiveMaterial(0));
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Headless-safe: dynamic layer mutation re-bakes from the pristine base maps while other
    /// instances that share the authored overlay resource stay untouched.
    /// </summary>
    [Headless]
    [Fact]
    public void LayerMutation_RebakesFromPristineBasesAndStaysInstanceLocal()
    {
        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);
        MarkingOverlay sharedOverlay = new()
        {
            AlbedoBase = albedoBase,
            Layers =
            [
                new MarkingLayer { Name = "Tattoos" },
                new MarkingLayer { Name = "Dirt" },
            ],
        };

        (MeshInstance3D firstMesh, SurfaceMarkings first) = CreateMarkedQuad(
            new StandardMaterial3D { AlbedoTexture = albedoBase }, sharedOverlay);
        (MeshInstance3D secondMesh, SurfaceMarkings second) = CreateMarkedQuad(
            new StandardMaterial3D { AlbedoTexture = albedoBase }, sharedOverlay);
        try
        {
            // Nothing enabled is authored yet, so both components stay strict no-ops.
            first.Apply();
            second.Apply();
            Assert.Null(firstMesh.GetSurfaceOverrideMaterial(0));
            Assert.Null(secondMesh.GetSurfaceOverrideMaterial(0));

            // Adding the first marking installs the bake.
            SurfaceMarking tattoo = CreateBlackBoxMarking(positionU: 0.5f, positionV: 0.5f);
            first.AddToLayer("Tattoos", tattoo);
            ImageTexture firstBaked = firstMesh.GetSurfaceOverrideMaterial(0)!.Get("albedo_texture").As<ImageTexture>();
            Assert.Equal(0, ReadByte(firstBaked.GetImage(), 8, 8, 0));
            Assert.Null(secondMesh.GetSurfaceOverrideMaterial(0));

            // Adding dirt re-bakes with both layers; clearing dirt restores the tattoo-only
            // bake exactly; disabling the last layer restores base pixels.
            first.AddToLayer("Dirt", CreateBlackBoxMarking(positionU: 0.8f, positionV: 0.8f));
            ImageTexture twoLayers = firstMesh.GetSurfaceOverrideMaterial(0)!.Get("albedo_texture").As<ImageTexture>();
            Assert.Equal(0, ReadByte(twoLayers.GetImage(), 8, 8, 0));
            Assert.Equal(0, ReadByte(twoLayers.GetImage(), 12, 12, 0));

            first.ClearLayer("Dirt");
            ImageTexture tattooOnly = firstMesh.GetSurfaceOverrideMaterial(0)!.Get("albedo_texture").As<ImageTexture>();
            Assert.Equal(0, ReadByte(tattooOnly.GetImage(), 8, 8, 0));
            Assert.Equal(204, ReadByte(tattooOnly.GetImage(), 12, 12, 0));

            first.SetLayerEnabled("Tattoos", false);
            ImageTexture unmarked = firstMesh.GetSurfaceOverrideMaterial(0)!.Get("albedo_texture").As<ImageTexture>();
            Assert.Equal(204, ReadByte(unmarked.GetImage(), 8, 8, 0));

            // The other instance's authored overlay was never touched by the mutations.
            Assert.Empty(sharedOverlay.Layers[0].Markings);
            Assert.Null(secondMesh.GetSurfaceOverrideMaterial(0));
        }
        finally
        {
            firstMesh.GetParent()?.QueueFree();
            secondMesh.GetParent()?.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: with the template wiring no longer authoring a target mesh, an unset
    /// <see cref="SurfaceMarkings.TargetMesh"/> on a detached component is a clear authoring
    /// error — discovery needs the scene tree.
    /// </summary>
    [Headless]
    [Fact]
    public void Apply_WithoutTargetMeshOutsideTree_ThrowsClearAuthoringError()
    {
        SurfaceMarkings markings = new()
        {
            Name = "SurfaceMarkings",
            Overlay = CreateSingleChannelOverlay(CreateSolidTexture(16, 204, 178, 153)),
        };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(markings.Apply);

        Assert.Contains("TargetMesh", ex.Message, StringComparison.Ordinal);
        Assert.Contains("scene tree", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Headless-safe: a marking supplying a channel the overlay has no base for fails before
    /// any material mutation.
    /// </summary>
    [Headless]
    [Fact]
    public void Apply_WithMarkingChannelMissingOverlayBase_ThrowsBeforeMutatingMaterials()
    {
        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);
        MarkingOverlay overlay = new()
        {
            AlbedoBase = albedoBase,
            Layers = [new MarkingLayer { Name = "L" }]
        };
        (MeshInstance3D mesh, SurfaceMarkings markings) = CreateMarkedQuad(
            new StandardMaterial3D { AlbedoTexture = albedoBase }, overlay);
        try
        {
            SurfaceMarking overreaching = CreateBlackBoxMarking(positionU: 0.5f, positionV: 0.5f);
            overreaching.NormalTexture = CreateSolidTexture(4, 128, 128, 200);
            markings.Overlay!.Layers[0].Markings = [overreaching];

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(markings.Apply);

            Assert.Contains("NormalBase", ex.Message, StringComparison.Ordinal);
            Assert.Null(mesh.GetSurfaceOverrideMaterial(0));
            Assert.Same(albedoBase, mesh.GetActiveMaterial(0).Get("albedo_texture").As<Texture2D>());
        }
        finally
        {
            mesh.GetParent()?.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: a channel base that no material slot references is an authoring mistake
    /// and fails before mutation.
    /// </summary>
    [Headless]
    [Fact]
    public void Apply_WithBaseNotReferencedByAnySlot_ThrowsClearAuthoringError()
    {
        Texture2D unreferencedBase = CreateSolidTexture(16, 204, 178, 153);
        (MeshInstance3D mesh, SurfaceMarkings markings) = CreateMarkedQuad(
            new StandardMaterial3D { AlbedoTexture = CreateSolidTexture(16, 10, 20, 30) },
            CreateSingleChannelOverlay(unreferencedBase));
        try
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(markings.Apply);

            Assert.Contains("no slot", ex.Message, StringComparison.Ordinal);
            Assert.Null(mesh.GetSurfaceOverrideMaterial(0));
        }
        finally
        {
            mesh.GetParent()?.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: the surface index is validated against the target mesh before mutation.
    /// </summary>
    [Headless]
    [Fact]
    public void Apply_WithSurfaceIndexOutOfRange_ThrowsClearAuthoringError()
    {
        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);
        MeshInstance3D mesh = CreateQuad();
        ((QuadMesh)mesh.Mesh).Material = new StandardMaterial3D { AlbedoTexture = albedoBase };
        SurfaceMarkings markings = AttachMarkings(mesh);
        markings.SurfaceIndex = 3;
        markings.Overlay = CreateSingleChannelOverlay(albedoBase);

        try
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(markings.Apply);

            Assert.Contains("SurfaceIndex", ex.Message, StringComparison.Ordinal);
            Assert.Null(mesh.GetSurfaceOverrideMaterial(0));
        }
        finally
        {
            mesh.GetParent()?.QueueFree();
        }
    }

    /// <summary>
    /// Headless-safe: texture-less markings and invalid authored values fail loudly.
    /// </summary>
    [Headless]
    [Fact]
    public void Apply_WithInvalidMarkingValues_ThrowsClearAuthoringErrors()
    {
        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);

        SurfaceMarking textureless = CreateBlackBoxMarking(0.5f, 0.5f);
        textureless.AlbedoTexture = null;
        _ = Assert.ThrowsAny<InvalidOperationException>(
            () => BakeDisposable(albedoBase, textureless));

        SurfaceMarking zeroScale = CreateBlackBoxMarking(0.5f, 0.5f);
        zeroScale.Scale = Vector2.Zero;
        _ = Assert.ThrowsAny<InvalidOperationException>(
            () => BakeDisposable(albedoBase, zeroScale));

        SurfaceMarking overStrength = CreateBlackBoxMarking(0.5f, 0.5f);
        overStrength.Strength = 1.5f;
        _ = Assert.ThrowsAny<InvalidOperationException>(
            () => BakeDisposable(albedoBase, overStrength));

        MarkingOverlay duplicateNames = new()
        {
            AlbedoBase = albedoBase,
            Layers =
            [
                new MarkingLayer { Name = "Same" },
                new MarkingLayer { Name = "Same" },
            ],
        };
        duplicateNames.Layers[1].Markings = [CreateBlackBoxMarking(0.5f, 0.5f)];
        _ = Assert.ThrowsAny<InvalidOperationException>(
            () => BakeDisposable(albedoBase, duplicateNames));
    }

    /// <summary>
    /// Headless-safe: runtime mutation validates its arguments without mutating materials.
    /// </summary>
    [Headless]
    [Fact]
    public void LayerMutation_WithUnknownLayerOrMissingOverlay_ThrowsClearAuthoringErrors()
    {
        Texture2D albedoBase = CreateSolidTexture(16, 204, 178, 153);

        SurfaceMarkings withoutOverlay = new();
        Assert.Contains(
            "Overlay",
            Assert.Throws<InvalidOperationException>(
                () => withoutOverlay.AddToLayer("Dirt", CreateBlackBoxMarking(0.5f, 0.5f))).Message,
            StringComparison.Ordinal);

        (MeshInstance3D mesh, SurfaceMarkings markings) = CreateMarkedQuad(
            new StandardMaterial3D { AlbedoTexture = albedoBase },
            new MarkingOverlay { AlbedoBase = albedoBase, Layers = [new MarkingLayer { Name = "Tattoos" }] });
        try
        {
            Assert.Contains(
                "Dirt",
                Assert.Throws<InvalidOperationException>(
                    () => markings.AddToLayer("Dirt", CreateBlackBoxMarking(0.5f, 0.5f))).Message,
                StringComparison.Ordinal);
            Assert.Null(mesh.GetSurfaceOverrideMaterial(0));
        }
        finally
        {
            mesh.GetParent()?.QueueFree();
        }
    }

    private static void BakeDisposable(Texture2D albedoBase, SurfaceMarking marking)
    {
        MeshInstance3D mesh = CreateQuad();
        ((QuadMesh)mesh.Mesh).Material = new StandardMaterial3D { AlbedoTexture = albedoBase };
        SurfaceMarkings markings = AttachMarkings(
            mesh,
            new MarkingOverlay { AlbedoBase = albedoBase, Layers = [new MarkingLayer { Name = "L", Markings = [marking] }] });
        try
        {
            markings.Apply();
        }
        finally
        {
            mesh.GetParent()?.QueueFree();
        }
    }

    private static void BakeDisposable(Texture2D albedoBase, MarkingOverlay overlay)
    {
        MeshInstance3D mesh = CreateQuad();
        ((QuadMesh)mesh.Mesh).Material = new StandardMaterial3D { AlbedoTexture = albedoBase };
        SurfaceMarkings markings = AttachMarkings(mesh, overlay);
        try
        {
            markings.Apply();
        }
        finally
        {
            mesh.GetParent()?.QueueFree();
        }
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

    private static (MeshInstance3D Mesh, SurfaceMarkings Markings) CreateMarkedQuad(
        Material material,
        Texture2D albedoBase,
        Texture2D? normalBase = null,
        Texture2D? roughnessBase = null)
    {
        MarkingOverlay overlay = new()
        {
            AlbedoBase = albedoBase,
            NormalBase = normalBase,
            RoughnessBase = roughnessBase,
        };
        SurfaceMarking marking = CreateBlackBoxMarking(0.5f, 0.5f);
        if (normalBase is not null)
        {
            marking.NormalTexture = CreateSolidTexture(4, 200, 128, 128);
        }

        if (roughnessBase is not null)
        {
            marking.RoughnessTexture = CreateSolidTexture(4, 255, 51, 255);
        }

        overlay.Layers = [new MarkingLayer { Name = "Fixture", Markings = [marking] }];

        MeshInstance3D mesh = CreateQuad();
        ((QuadMesh)mesh.Mesh).Material = material;
        return (mesh, AttachMarkings(mesh, overlay));
    }

    private static (MeshInstance3D Mesh, SurfaceMarkings Markings) CreateMarkedQuad(Material material, MarkingOverlay overlay)
    {
        MeshInstance3D mesh = CreateQuad();
        ((QuadMesh)mesh.Mesh).Material = material;
        return (mesh, AttachMarkings(mesh, overlay));
    }

    private static MeshInstance3D CreateQuad()
    {
        MeshInstance3D mesh = new()
        {
            Mesh = new QuadMesh()
        };
        Node3D holder = new()
        {
            Name = "MarkingsFixture"
        };
        holder.AddChild(mesh);
        GetSceneTree().Root.AddChild(holder);
        return mesh;
    }

    private static SurfaceMarkings AttachMarkings(MeshInstance3D mesh, MarkingOverlay? overlay = null)
    {
        // The overlay is assigned after entry so _Ready stays a strict no-op and each fact
        // drives Apply (or mutation) explicitly.
        SurfaceMarkings markings = new()
        {
            Name = "SurfaceMarkings",
            TargetMesh = mesh,
        };
        mesh.AddChild(markings);
        markings.Overlay = overlay;
        return markings;
    }

    private static (CharacterBody3D Root, MeshInstance3D BodyMesh, SurfaceMarkings Markings) InstantiateReferenceFemale()
        => InstantiateTemplate(ReferenceFemaleBaseScenePath, FemaleBodyMeshPath);

    private static (CharacterBody3D Root, MeshInstance3D BodyMesh, SurfaceMarkings Markings) InstantiateTemplate(
        string scenePath,
        string bodyMeshPath)
    {
        CharacterBody3D root = LoadPackedScene(scenePath).Instantiate<CharacterBody3D>();
        MeshInstance3D bodyMesh = root.GetNode<MeshInstance3D>(bodyMeshPath);
        SurfaceMarkings markings = root.GetNode<SurfaceMarkings>("SurfaceMarkings");
        return (root, bodyMesh, markings);
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

    private static void AssertBytesEqual(Image actual, Image expected)
    {
        Assert.Equal(expected.GetWidth(), actual.GetWidth());
        Assert.Equal(expected.GetHeight(), actual.GetHeight());
        Assert.Equal(expected.GetData(), actual.GetData());
    }

    private static void AssertBytesEqual(Image actual, byte[] expected)
        => Assert.Equal(expected, actual.GetData());
}
