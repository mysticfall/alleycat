using System.Text.Json;
using System.Text.Json.Nodes;
using AlleyCat.Control.Locomotion;
using AlleyCat.IntegrationTests.Support;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.AnimationCatalogue;

/// <summary>Validates runtime catalogue consumers against a declared, test-owned embedded-only package.</summary>
public sealed class LocomotionRuntimeCatalogueV4IntegrationTests
{
    /// <inheritdoc/>
    [Fact]
    public void SyntheticRuntimeCatalogue_ResourcesAndDeclaredRootResponsesAgree()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(nameof(LocomotionRuntimeCatalogueV4IntegrationTests));
        using var document = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(fixture.NavigationCataloguePath)));
        SyntheticNavigationManifest manifest = fixture.Declaration.NavigationManifest;

        Assert.Equal(4, document.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(manifest.Package, document.RootElement.GetProperty("package").GetString());
        Assert.Equal(manifest.Variant, document.RootElement.GetProperty("package_variant").GetString());
        Assert.Equal(
            "navigation_library.tres",
            document.RootElement.GetProperty("animation_library").GetString());
        Assert.Equal(
            fixture.NavigationLibraryPath[..^"navigation_library.tres".Length],
            fixture.NavigationCataloguePath[..^"navigation_catalogue.json".Length]);
        Assert.Equal(
            fixture.Declaration.Clips.Count(clip => !clip.IsIdle)
                + fixture.Declaration.NavigationManifest.Clips.Count,
            fixture.LocomotionLibrary.GetAnimationList().Count);
        // The navigation fixture declares its own terminal idle in addition to the catalogue idle.
        Assert.Equal(
            fixture.Declaration.Clips.Count(clip => clip.IsIdle) + 1,
            fixture.IdleLibrary.GetAnimationList().Count);

        foreach (SyntheticClipDeclaration declaration in fixture.Declaration.Clips)
        {
            Animation animation = fixture.AnimationFor(declaration.Name);
            int track = animation.FindTrack(new NodePath("SyntheticSkeleton:Root"), Animation.TrackType.Position3D);
            Assert.True(track >= 0);
            Assert.Equal(declaration.RootResponse, animation.PositionTrackInterpolate(track, declaration.DurationSeconds));
            Assert.Equal(declaration.Loops ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None, animation.LoopMode);
        }
    }

    /// <summary>Player forward arc leaves resolve authored, signed yaw while the centre remains ordinary forward.</summary>
    [Fact]
    public void SyntheticPlayerForwardArcSpaces_ResolveSignedYawTracksAndKeepOrdinaryForward()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(nameof(SyntheticPlayerForwardArcSpaces_ResolveSignedYawTracksAndKeepOrdinaryForward));
        SyntheticPlayerLocomotionRuntime runtime = fixture.CreatePlayerLocomotionRuntime();
        AnimationNodeBlendTree root = Assert.IsType<AnimationNodeBlendTree>(runtime.AnimationTree.TreeRoot, exactMatch: false);
        AnimationNodeStateMachine states = Assert.IsType<AnimationNodeStateMachine>(root.GetNode("States"), exactMatch: false);
        AnimationNodeBlendTree travel = Assert.IsType<AnimationNodeBlendTree>(states.GetNode(SyntheticPlayerLocomotionRuntime.TravelState), exactMatch: false);

        foreach ((string spaceName, string left, string right) in new[]
                 {
                     ("ForwardArcL", "al_l", "ar_l"),
                     ("ForwardArcR", "al_r", "ar_r"),
                 })
        {
            AnimationNodeBlendSpace2D space = Assert.IsType<AnimationNodeBlendSpace2D>(travel.GetNode(spaceName), exactMatch: false);
            AssertPoint(0, "wf", 0f);
            AssertPoint(1, left, 1f);
            AssertPoint(2, right, -1f);

            void AssertPoint(int index, string role, float expectedYawSign)
            {
                SyntheticNavigationClipDeclaration clip = fixture.Declaration.NavigationManifest[role];
                AnimationNodeAnimation leaf = Assert.IsType<AnimationNodeAnimation>(space.GetBlendPointNode(index), exactMatch: false);
                Assert.Equal(new StringName($"locomotion/{clip.Identity}"), leaf.Animation);
                Animation animation = fixture.LocomotionLibrary.GetAnimation(clip.Identity);
                Assert.NotNull(animation);
                int track = animation.FindTrack(new NodePath("SyntheticSkeleton:Root"), Animation.TrackType.Rotation3D);
                Assert.True(track >= 0, $"{spaceName}/{role} has no player Root yaw track");
                Assert.True(animation.Length > 0f);
                float start = animation.RotationTrackInterpolate(track, 0d).GetEuler().Y;
                float midpoint = animation.RotationTrackInterpolate(track, clip.DurationSeconds * 0.5d).GetEuler().Y;
                float end = animation.RotationTrackInterpolate(track, clip.DurationSeconds).GetEuler().Y;
                Assert.InRange(Mathf.Abs(start), 0f, 0.0001f);
                if (expectedYawSign == 0f)
                {
                    Assert.InRange(Mathf.Abs(midpoint) + Mathf.Abs(end), 0f, 0.0001f);
                    Assert.Equal(new Vector3(0f, 0f, 0.72f), clip.SkeletonLocalRootResponse);
                }
                else
                {
                    Assert.True(midpoint * expectedYawSign > 0f, $"{spaceName}/{role} midpoint yaw {midpoint} has wrong sign");
                    Assert.True(end * expectedYawSign > 0f, $"{spaceName}/{role} end yaw {end} has wrong sign");
                    Assert.Equal(clip.YawRadians, end, 5);
                }
            }
        }
    }

    /// <inheritdoc/>
    [Fact]
    public void SyntheticRuntimeCatalogue_ProducerPhaseSchemasAreValidatedFailClosed()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(nameof(LocomotionRuntimeCatalogueV4IntegrationTests));
        string catalogueJson = File.ReadAllText(ProjectSettings.GlobalizePath(fixture.NavigationCataloguePath));

        var catalogue = StandingLocomotionCatalogue.Parse(catalogueJson, fixture.NavigationCataloguePath);
        Assert.Equal(fixture.Declaration.NavigationManifest.Clips.Count, catalogue.Clips.Count);

        _ = Assert.Throws<InvalidDataException>(() => StandingLocomotionCatalogue.Parse(catalogueJson.Replace(
            "\"duration_seconds\":0.8,\"events\"",
            "\"duration_seconds\":0.7,\"events\"",
            StringComparison.Ordinal), fixture.NavigationCataloguePath));
        _ = Assert.Throws<InvalidDataException>(() => StandingLocomotionCatalogue.Parse(catalogueJson.Replace(
            "\"duration_seconds\":0.8,\"events\"",
            "\"duration_seconds\":0,\"events\"",
            StringComparison.Ordinal), fixture.NavigationCataloguePath));
        _ = Assert.Throws<InvalidDataException>(() => StandingLocomotionCatalogue.Parse(catalogueJson.Replace(
            "\"duration_seconds\":0.8,\"events\"",
            "\"duration_seconds\":0.8,\"unsupported\":true,\"events\"",
            StringComparison.Ordinal), fixture.NavigationCataloguePath));
    }

    /// <inheritdoc/>
    [Fact]
    public void SyntheticRuntimeCatalogue_ArbitraryInventoryAndDeclaredGraphAreDerivedWithoutContentOracle()
    {
        SyntheticNavigationManifest manifest = CreateGenericManifest();
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(nameof(SyntheticRuntimeCatalogue_ArbitraryInventoryAndDeclaredGraphAreDerivedWithoutContentOracle), manifest);

        string json = File.ReadAllText(ProjectSettings.GlobalizePath(fixture.NavigationCataloguePath));
        var catalogue = StandingLocomotionCatalogue.Parse(json, fixture.NavigationCataloguePath);

        Assert.Equal(3, catalogue.Clips.Count);
        Assert.Equal(manifest.Clips.Select(clip => clip.Role).Order(), catalogue.Clips.Keys.Order());
        Assert.True(catalogue.HasApprovedMapping("stride_alpha", "release_l", "settle"));
        Assert.Equal(manifest.Variant, catalogue.PackageVariant);
        catalogue.RequireCapabilities(
            "generic test consumer",
            [StandingLocomotionCapability.CyclicForwardPrimary, StandingLocomotionCapability.ExitForwardLeft]);
        _ = Assert.Throws<InvalidDataException>(() => catalogue.RequireCapabilities(
            "semantic consumer",
            [StandingLocomotionCapability.EntryBackwardRight]));
    }

    /// <inheritdoc/>
    [Fact]
    public void SyntheticRuntimeCatalogue_RefusesMissingUnknownDuplicateAndBoundaryIncompatibleCapabilities()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(
            nameof(SyntheticRuntimeCatalogue_RefusesMissingUnknownDuplicateAndBoundaryIncompatibleCapabilities));
        string json = File.ReadAllText(ProjectSettings.GlobalizePath(fixture.NavigationCataloguePath));

        JsonObject missing = JsonNode.Parse(json)!.AsObject();
        _ = missing["clips"]!.AsArray()[0]!.AsObject().Remove("capability");
        _ = Assert.Throws<InvalidDataException>(() => StandingLocomotionCatalogue.Parse(missing.ToJsonString(), fixture.NavigationCataloguePath));

        JsonObject unknown = JsonNode.Parse(json)!.AsObject();
        unknown["clips"]!.AsArray()[0]!["capability"] = "unknown_semantic_slot";
        _ = Assert.Throws<InvalidDataException>(() => StandingLocomotionCatalogue.Parse(unknown.ToJsonString(), fixture.NavigationCataloguePath));

        JsonObject duplicate = JsonNode.Parse(json)!.AsObject();
        JsonArray duplicateClips = duplicate["clips"]!.AsArray();
        duplicateClips[1]!["capability"] = duplicateClips[0]!["capability"]!.GetValue<string>();
        _ = Assert.Throws<InvalidDataException>(() => StandingLocomotionCatalogue.Parse(duplicate.ToJsonString(), fixture.NavigationCataloguePath));

        JsonObject incompatible = JsonNode.Parse(json)!.AsObject();
        JsonObject cyclic = incompatible["clips"]!.AsArray()
            .Select(node => node!.AsObject())
            .First(node => node["boundary_kind"]!.GetValue<string>() == "cyclic_travel");
        cyclic["capability"] = "confined_pivot_left";
        _ = Assert.Throws<InvalidDataException>(() => StandingLocomotionCatalogue.Parse(incompatible.ToJsonString(), fixture.NavigationCataloguePath));
    }

    /// <summary>Stale schema version 2 catalogues declare per-clip resource paths and must fail closed.</summary>
    [Fact]
    public void SyntheticRuntimeCatalogue_RejectsStaleV2ClipPathCataloguesFailClosed()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(
            nameof(SyntheticRuntimeCatalogue_RejectsStaleV2ClipPathCataloguesFailClosed));
        string json = File.ReadAllText(ProjectSettings.GlobalizePath(fixture.NavigationCataloguePath));

        // Downgrade the declared version and reintroduce a representative v2 per-clip resource-path field.
        JsonObject stale = JsonNode.Parse(json)!.AsObject();
        stale["schema_version"] = 2;
        stale["clips"]!.AsArray()[0]!.AsObject()["clip"] = "user://stale/clips/token.res";
        InvalidDataException staleError = Assert.Throws<InvalidDataException>(
            () => StandingLocomotionCatalogue.Parse(stale.ToJsonString(), fixture.NavigationCataloguePath));
        Assert.Contains("stale schema version 2", staleError.Message, StringComparison.Ordinal);
        Assert.Contains(fixture.NavigationCataloguePath, staleError.Message, StringComparison.Ordinal);

        InvalidDataException otherVersionError = Assert.Throws<InvalidDataException>(
            () => StandingLocomotionCatalogue.Parse(json.Replace("\"schema_version\":4", "\"schema_version\":5", StringComparison.Ordinal), fixture.NavigationCataloguePath));
        Assert.Contains("schema version 4", otherVersionError.Message, StringComparison.Ordinal);

        // Even at the current version, per-clip path fields and path-like library declarations fail closed.
        JsonObject perClipPath = JsonNode.Parse(json)!.AsObject();
        perClipPath["clips"]!.AsArray()[0]!.AsObject()["clip"] = "user://somewhere/token.res";
        _ = Assert.Throws<InvalidDataException>(
            () => StandingLocomotionCatalogue.Parse(perClipPath.ToJsonString(), fixture.NavigationCataloguePath));

        InvalidDataException libraryPathError = Assert.Throws<InvalidDataException>(
            () => StandingLocomotionCatalogue.Parse(
                json.Replace("\"animation_library\":\"navigation_library.tres\"", "\"animation_library\":\"user://elsewhere/library.tres\"", StringComparison.Ordinal),
                fixture.NavigationCataloguePath));
        Assert.Contains("co-located", libraryPathError.Message, StringComparison.Ordinal);
    }

    /// <summary>Catalogue identities must form a bijection with the embedded library animation keys.</summary>
    [Fact]
    public void SyntheticRuntimeCatalogue_RejectsCatalogueLibraryIdentityMismatch()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(
            nameof(SyntheticRuntimeCatalogue_RejectsCatalogueLibraryIdentityMismatch));
        string json = File.ReadAllText(ProjectSettings.GlobalizePath(fixture.NavigationCataloguePath));
        string mismatchedIdentity = fixture.Declaration.NavigationManifest.Clips[0].Identity + "_renamed";

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => StandingLocomotionCatalogue.Parse(
                json.Replace($"\"identity\":\"{fixture.Declaration.NavigationManifest.Clips[0].Identity}\"", $"\"identity\":\"{mismatchedIdentity}\"", StringComparison.Ordinal),
                fixture.NavigationCataloguePath));
        Assert.Contains("identity bijection", error.Message, StringComparison.Ordinal);
        Assert.Contains(fixture.NavigationLibraryPath, error.Message, StringComparison.Ordinal);
    }

    /// <summary>A declared duration that drifts from its embedded animation by a single frame fails validation.</summary>
    [Fact]
    public void SyntheticRuntimeCatalogue_RejectsEmbeddedDurationDriftOfOneFrame()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(
            nameof(SyntheticRuntimeCatalogue_RejectsEmbeddedDurationDriftOfOneFrame));
        string json = File.ReadAllText(ProjectSettings.GlobalizePath(fixture.NavigationCataloguePath));
        string driftedIdentity = fixture.Declaration.NavigationManifest.Clips[0].Identity;

        AnimationLibrary library = ResourceLoader.Load<AnimationLibrary>(fixture.NavigationLibraryPath)
            ?? throw new Xunit.Sdk.XunitException("The synthetic navigation library could not be loaded.");
        Animation drifted = library.GetAnimation(driftedIdentity)
            ?? throw new Xunit.Sdk.XunitException($"The synthetic library lacks animation '{driftedIdentity}'.");
        drifted.Length += 1f / 30f;
        Error saveResult = ResourceSaver.Save(library, fixture.NavigationLibraryPath);
        Assert.Equal(Error.Ok, saveResult);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => StandingLocomotionCatalogue.Parse(json, fixture.NavigationCataloguePath));
        Assert.Contains("duration or loop intent", error.Message, StringComparison.Ordinal);
        Assert.Contains(fixture.NavigationLibraryPath, error.Message, StringComparison.Ordinal);
    }

    /// <summary>Native events resolve from declared boundary semantics.</summary>
    [Fact]
    public void SyntheticRuntimeCatalogue_ResolvesNativeBoundaryEventsFromDeclaredSemantics()
        => AssertTransformedBoundaryEventsFromDeclaredSemantics(
            "native", "release_l", StandingLocomotionEventKind.Release, StandingLocomotionAnatomicalSide.Left);

    /// <summary>Mirrored events resolve from declared boundary semantics.</summary>
    [Fact]
    public void SyntheticRuntimeCatalogue_ResolvesMirroredBoundaryEventsFromDeclaredSemantics()
        => AssertTransformedBoundaryEventsFromDeclaredSemantics(
            "mirror", "release_r", StandingLocomotionEventKind.Release, StandingLocomotionAnatomicalSide.Right);

    /// <summary>Reversed events resolve from declared boundary semantics.</summary>
    [Fact]
    public void SyntheticRuntimeCatalogue_ResolvesReversedBoundaryEventsFromDeclaredSemantics()
        => AssertTransformedBoundaryEventsFromDeclaredSemantics(
            "reverse", "acquire_l", StandingLocomotionEventKind.Acquire, StandingLocomotionAnatomicalSide.Left);

    /// <summary>Reversed and mirrored events resolve from declared boundary semantics.</summary>
    [Fact]
    public void SyntheticRuntimeCatalogue_ResolvesReversedMirroredBoundaryEventsFromDeclaredSemantics()
        => AssertTransformedBoundaryEventsFromDeclaredSemantics(
            "reverse_mirror", "acquire_r", StandingLocomotionEventKind.Acquire, StandingLocomotionAnatomicalSide.Right);

    private static void AssertTransformedBoundaryEventsFromDeclaredSemantics(
        string transform,
        string eventID,
        StandingLocomotionEventKind kind,
        StandingLocomotionAnatomicalSide side)
    {
        SyntheticNavigationManifest manifest = CreateSemanticMappingManifest(eventID);
        using var fixture = SyntheticLocomotionRuntimeFixture.Create($"semantic_{transform}", manifest);
        var catalogue = StandingLocomotionCatalogue.Parse(
            File.ReadAllText(ProjectSettings.GlobalizePath(fixture.NavigationCataloguePath)),
            fixture.NavigationCataloguePath);

        StandingLocomotionPhaseEvent cyclic = catalogue.ResolvePhaseEvent(
            "renamed_cycle",
            side,
            StandingLocomotionBoundaryContext.Cyclic);
        StandingLocomotionBoundaryMapping mapping = catalogue.ResolveBoundaryMapping(
            "renamed_cycle",
            "renamed_boundary",
            side);

        Assert.Equal(eventID, cyclic.ID);
        Assert.Equal(kind, cyclic.Kind);
        Assert.Equal(eventID, mapping.Source.ID);
        Assert.Equal(eventID, mapping.Target.ID);
    }

    /// <inheritdoc/>
    [Fact]
    public void SyntheticRuntimeCatalogue_RefusesMissingAmbiguousDanglingAndIncompatibleBoundaryEvents()
    {
        SyntheticNavigationManifest missing = CreateSemanticMappingManifest("acquire_l") with
        {
            DeclaredMappings = []
        };
        using (var fixture = SyntheticLocomotionRuntimeFixture.Create("semantic_missing", missing))
        {
            var catalogue = StandingLocomotionCatalogue.Parse(
                File.ReadAllText(ProjectSettings.GlobalizePath(fixture.NavigationCataloguePath)),
                fixture.NavigationCataloguePath);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => catalogue.ResolveBoundaryMapping(
                "renamed_cycle",
                "renamed_boundary",
                StandingLocomotionAnatomicalSide.Left));
            Assert.Contains("no compatible Left phase-event mapping", error.Message, StringComparison.Ordinal);
        }

        SyntheticNavigationManifest incompatible = CreateSemanticMappingManifest("acquire_l", "release_l");
        using (var fixture = SyntheticLocomotionRuntimeFixture.Create("semantic_incompatible", incompatible))
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => StandingLocomotionCatalogue.Parse(
                File.ReadAllText(ProjectSettings.GlobalizePath(fixture.NavigationCataloguePath)),
                fixture.NavigationCataloguePath));
            Assert.Contains("incompatible phase-event semantics", error.Message, StringComparison.Ordinal);
        }

        SyntheticNavigationManifest dangling = CreateSemanticMappingManifest("acquire_l") with
        {
            DeclaredMappings = [new("renamed_cycle", "release_l", "renamed_boundary")],
        };
        using (var fixture = SyntheticLocomotionRuntimeFixture.Create("semantic_dangling", dangling))
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => StandingLocomotionCatalogue.Parse(
                File.ReadAllText(ProjectSettings.GlobalizePath(fixture.NavigationCataloguePath)),
                fixture.NavigationCataloguePath));
            Assert.Contains("unknown role or event", error.Message, StringComparison.Ordinal);
        }

        SyntheticNavigationManifest ambiguous = CreateSemanticMappingManifest("acquire_l") with
        {
            DeclaredMappings =
            [
                new("renamed_cycle", "acquire_l", "renamed_boundary"),
                new("renamed_cycle", "acquire_l", "renamed_boundary"),
            ],
        };
        using (var fixture = SyntheticLocomotionRuntimeFixture.Create("semantic_ambiguous", ambiguous))
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => StandingLocomotionCatalogue.Parse(
                File.ReadAllText(ProjectSettings.GlobalizePath(fixture.NavigationCataloguePath)),
                fixture.NavigationCataloguePath));
            Assert.Contains("declared exactly once", error.Message, StringComparison.Ordinal);
        }
    }

    internal static SyntheticNavigationManifest CreateGenericManifest() => new(
        [
            new(
                "stride_alpha",
                "quartz_token_a",
                1f,
                true,
                new Vector3(0.12f, 0f, -0.31f),
                0.07f,
                "cyclic_travel",
                [new("release_l", 0.25f)],
                Capability: StandingLocomotionCapability.CyclicForwardPrimary),
            new(
                "settle",
                "quartz_token_b",
                0.5f,
                false,
                new Vector3(0f, 0f, -0.08f),
                0f,
                "transition",
                [new("release_l", 0f, "start")],
                Capability: StandingLocomotionCapability.ExitForwardLeft),
            new("turn", "opaque_turn_token", 0.4f, true, Vector3.Zero, 0.3f, "confined_manoeuvre",
                Capability: StandingLocomotionCapability.ConfinedPivotLeft),
        ],
        [new("stride_alpha", "release_l", "settle")],
        Variant: "quartz");

    private static SyntheticNavigationManifest CreateSemanticMappingManifest(
        string sourceEvent,
        string? targetEvent = null)
    {
        string resolvedTarget = targetEvent ?? sourceEvent;
        bool rightLane = sourceEvent.EndsWith("_r", StringComparison.Ordinal);
        return new(
            [
                new(
                    "renamed_boundary",
                    "ember_boundary_token",
                    0.5f,
                    false,
                    Vector3.Zero,
                    0f,
                    "transition",
                    DeclaredPhaseEvents: [new(resolvedTarget, 0f, "start")],
                    Capability: rightLane
                        ? StandingLocomotionCapability.ExitForwardRight
                        : StandingLocomotionCapability.ExitForwardLeft),
                new(
                    "renamed_cycle",
                    "ember_cycle_token",
                    1f,
                    true,
                    new Vector3(0f, 0f, -0.2f),
                    0f,
                    "cyclic_travel",
                    DeclaredPhaseEvents: [new(sourceEvent, 0.35f)],
                    Capability: StandingLocomotionCapability.CyclicForwardPrimary),
            ],
            [new("renamed_cycle", sourceEvent, "renamed_boundary", resolvedTarget)]);
    }
}
