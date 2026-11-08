using AlleyCat.Scene.MiniMap;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Scene;

/// <summary>
/// SCN-002 TA-4/TA-6/TA-7 runtime coverage: byte-identical determinism, paint ordering independent of group
/// iteration order, exact output dimensions, the editor-driver composition equivalence, and every TR-16 error and
/// edge contract — including scope-relative diagnostic paths and the TR-7 editor-tool identity notice exercised
/// through the renderer's editor-context seam.
/// </summary>
public sealed class MiniMapDeterminismAndEdgeIntegrationTests
{
    private static readonly MiniMapRenderSettings _smallSquare = new(MaximumDistance: float.PositiveInfinity, Width: 512, Height: 512);

    private static string Describe(MiniMapRenderResult result) => result.Diagnostics.Length == 0
        ? $"{result.Status} (no diagnostics)"
        : $"{result.Status}: {string.Join("; ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code} [{diagnostic.Reference}] {diagnostic.Message}"))}";

    private static void AssertSingleFailureNamesMarker(MiniMapRenderResult result, string markerPath, string primitivePath, string code)
    {
        Assert.Equal(MiniMapRenderStatus.Failed, result.Status);
        Assert.Null(result.Svg);
        Assert.Null(result.Image);
        MiniMapDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(markerPath, diagnostic.Reference, StringComparer.Ordinal);
        Assert.Contains(primitivePath, diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Repeated requests over a stable scene produce byte-identical SVG, including after the markers are removed and
    /// re-added in reverse order, so composition never depends on scene-group iteration order (TR-12/TR-13).
    /// </summary>
    [Fact]
    public async Task IdenticalRequests_ProduceByteIdenticalSvgRegardlessOfGroupIterationOrder()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapDeterminismRoot"
        };
        MiniMapMarker CreateAlpha()
        {
            return MiniMapTestHarness.CreateMarker("Alpha", 3, new MiniMapRectangle
            {
                Name = "Box",
                Width = 2,
                Depth = 2,
                FillColour = new Color("287fa0"),
            });
        }

        MiniMapMarker CreateBeta()
        {
            return MiniMapTestHarness.CreateMarker("Beta", 3, new MiniMapRectangle
            {
                Name = "Box",
                Width = 2,
                Depth = 2,
                FillColour = new Color("d84156"),
                Position = new Vector3(5, 0, 0),
            });
        }

        MiniMapMarker alpha = CreateAlpha();
        MiniMapMarker beta = CreateBeta();
        root.AddChild(beta);
        root.AddChild(alpha);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderer renderer = MiniMapTestHarness.CreateScopedRenderer(root);
            string first = MiniMapTestHarness.Svg(renderer.Render(Vector2.Zero, _smallSquare));
            Assert.Equal(first, MiniMapTestHarness.Svg(renderer.Render(Vector2.Zero, _smallSquare)));

            root.QueueFree();
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);

            root = new()
            {
                Name = "MinimapDeterminismRoot"
            };
            root.AddChild(CreateAlpha());
            root.AddChild(CreateBeta());
            await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

            string reordered = MiniMapTestHarness.Svg(MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, _smallSquare));
            Assert.Equal(first, reordered);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Markers paint in ascending authored draw-order ordinal, tie-broken by scene node path (TR-12).
    /// </summary>
    [Fact]
    public async Task PaintOrder_FollowsDrawOrderOrdinalThenNodePath()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapPaintOrderRoot"
        };
        MiniMapMarker alpha = MiniMapTestHarness.CreateMarker("Alpha", 5, new MiniMapRectangle
        {
            Name = "Box",
            Width = 2,
            Depth = 2,
            FillColour = new Color("287fa0"),
        });
        MiniMapMarker beta = MiniMapTestHarness.CreateMarker("Beta", 1, new MiniMapRectangle
        {
            Name = "Box",
            Width = 2,
            Depth = 2,
            FillColour = new Color("d84156"),
            Position = new Vector3(5, 0, 0),
        });

        root.AddChild(alpha);
        root.AddChild(beta);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderer renderer = MiniMapTestHarness.CreateScopedRenderer(root);

            string byOrdinal = MiniMapTestHarness.Svg(renderer.Render(Vector2.Zero, _smallSquare));
            Assert.True(byOrdinal.IndexOf("#d84156", StringComparison.Ordinal) < byOrdinal.IndexOf("#287fa0", StringComparison.Ordinal),
                "Lower draw-order ordinal must paint first.");

            alpha.DrawOrder = 0;
            await WaitForFramesAsync(sceneTree, 2);

            string byNodePath = MiniMapTestHarness.Svg(renderer.Render(Vector2.Zero, _smallSquare));
            Assert.True(byNodePath.IndexOf("#287fa0", StringComparison.Ordinal) < byNodePath.IndexOf("#d84156", StringComparison.Ordinal),
                "Equal ordinals must fall back to ascending node path (Alpha before Beta).");
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// The rasterised image is exactly the requested dimensions for a non-default request, and capture off the Godot
    /// main thread fails loudly instead of racing scene state (TR-11/TR-13).
    /// </summary>
    [Fact]
    public async Task OutputDimensions_MatchRequestExactlyAndCaptureIsMainThreadOnly()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapDimensionsRoot"
        };
        root.AddChild(MiniMapTestHarness.CreateMarker("Solo", 0, new MiniMapRectangle
        {
            Name = "Box",
            Width = 2,
            Depth = 2,
            FillColour = new Color("287fa0"),
        }));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root)
                .Render(Vector2.Zero, new MiniMapRenderSettings(MaximumDistance: float.PositiveInfinity, Width: 700, Height: 520));

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            Assert.Equal(700, MiniMapTestHarness.Image(result).GetWidth());
            Assert.Equal(520, MiniMapTestHarness.Image(result).GetHeight());

            MiniMapRenderer renderer = MiniMapTestHarness.CreateScopedRenderer(root);
            MiniMapRenderResult offThread = await Task.Run(() => renderer.Render(Vector2.Zero, _smallSquare));
            Assert.Equal(MiniMapRenderStatus.Failed, offThread.Status);
            Assert.Equal("OffMainThread", Assert.Single(offThread.Diagnostics).Code);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// The editor preview's composition path — a renderer scoped exactly like <c>MiniMapPreviewDriver</c> with
    /// coverage and explicit-viewport driver inputs — renders fragments identical to an unscoped runtime renderer
    /// over the same scene state (TR-17, amended TA-7: equality holds whenever the same ancestry instantiates, which
    /// is always true inside one runtime process).
    /// </summary>
    [Fact]
    public async Task ScopedPreviewComposition_MatchesUnscopedRendererFragments()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapPreviewParityRoot"
        };
        root.AddChild(MiniMapTestHarness.CreateMarker("West", 0, new MiniMapRectangle
        {
            Name = "Box",
            Width = 2,
            Depth = 2,
            FillColour = new Color("287fa0"),
        }));
        root.AddChild(MiniMapTestHarness.CreateMarker("East", 1, new MiniMapCircle
        {
            Name = "Pool",
            Radius = 1,
            FillColour = new Color("64adce"),
            Position = new Vector3(8, 0, 3),
        }));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderer scoped = MiniMapTestHarness.CreateScopedRenderer(root);
            MiniMapRenderer unscoped = new(root);

            Assert.Equal(
                MiniMapTestHarness.Svg(unscoped.RenderCoverage(_smallSquare)),
                MiniMapTestHarness.Svg(scoped.RenderCoverage(_smallSquare)));

            Rect2 viewport = new(new Vector2(-2, -2), new Vector2(14, 10));
            MiniMapViewportRequest request = new(viewport.GetCenter(), viewport, new MiniMapRenderSettings(Width: 480, Height: 320));
            Assert.Equal(
                MiniMapTestHarness.Svg(unscoped.Render(request)),
                MiniMapTestHarness.Svg(scoped.Render(request)));
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Zero markers in range is a documented success over the default extent, not a failure (TR-16).
    /// </summary>
    [Fact]
    public void ZeroMarkersInRange_SucceedsWithBackgroundGridAndDiagnostic()
    {
        SceneTree sceneTree = GetSceneTree();
        Node scope = new()
        {
            Name = "MinimapZeroMarkersScope"
        };
        MiniMapRenderer renderer = new(treeOwner: sceneTree.Root, discoveryScope: scope);

        MiniMapRenderResult result = renderer.Render(new Vector2(3, 4), new MiniMapRenderSettings(MaximumDistance: 10, Width: 256, Height: 256));

        Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
        _ = MiniMapTestHarness.Svg(result);
        _ = MiniMapTestHarness.Image(result);
        (int vertical, int horizontal) = MiniMapTestHarness.GridLineCounts(MiniMapTestHarness.Svg(result));
        Assert.True(vertical > 0 && horizontal > 0, "The grid must still draw with zero markers.");
        Assert.True(result.Viewport.HasPoint(new Vector2(3, 4)));
        Assert.Equal(MiniMapRenderer.MinimumWorldExtentMetres, result.Viewport.Size.X, 1);
        Assert.Contains("ZeroMarkers", Assert.Single(result.Diagnostics).Code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Empty or whitespace-only configured label text renders no glyphs and is legal: no placeholder is invented and
    /// no diagnostic is raised (TR-16).
    /// </summary>
    [Fact]
    public async Task EmptyConfiguredLabelText_RendersNoGlyphsLegally()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapEmptyLabelRoot"
        };
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker("Silent", 0, new MiniMapLabel
        {
            Name = "Caption",
            Text = "   ",
            TextSize = 16,
            FillColour = new Color("8a2be2"),
        });

        root.AddChild(marker);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, _smallSquare);

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            // Grid labels legitimately use aria-label; the empty label's own glyph fill must be entirely absent.
            Assert.DoesNotContain("#8a2be2", MiniMapTestHarness.Svg(result), StringComparison.Ordinal);
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Reference == "Silent");
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// An identity label with no instantiated identifiable ancestor is omitted for that render with a diagnostic
    /// naming the marker, and the render continues (TR-7/TR-16).
    /// </summary>
    [Fact]
    public async Task IdentityLabelWithoutIdentifiableAncestor_IsOmittedWithDiagnosticNamingMarker()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapMissingIdentityRoot"
        };
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker("Anonymous", 0,
            new MiniMapLabel { Name = "Identity", Flavour = MiniMapLabelFlavour.Identity, TextSize = 16 },
            new MiniMapRectangle { Name = "Box", Width = 2, Depth = 2, FillColour = new Color("287fa0") });

        root.AddChild(marker);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, _smallSquare);

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            Assert.Contains("#287fa0", MiniMapTestHarness.Svg(result), StringComparison.Ordinal);
            MiniMapDiagnostic diagnostic = Assert.Single(result.Diagnostics);
            // Runtime semantics are unchanged (TR-16): the MissingIdentity error names the marker by scope-relative
            // node path, never by an absolute tree path.
            Assert.Equal("MissingIdentity", diagnostic.Code);
            Assert.Equal("Anonymous", diagnostic.Reference, StringComparer.Ordinal);
            Assert.Contains("Anonymous/Identity", diagnostic.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("/root", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// In an editor-tool context — exercised through the renderer's editor-context seam because the harness cannot
    /// launch the Godot editor (TR-7/TA-6) — an unresolved identity label reports the informational
    /// <c>IdentityResolvesAtRuntime</c> notice naming the label, its marker and the nearest scripted ancestor by
    /// scope-relative node path; the label stays omitted, no <c>MissingIdentity</c> is reported, and the render
    /// continues Ready.
    /// </summary>
    [Fact]
    public async Task EditorToolContext_IdentityLabelReportsInformationalNoticeNamingScriptedAncestor()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapEditorNoticeRoot"
        };
        Node hosted = new Node3D
        {
            Name = "Hosted"
        };
        Node scriptedHost = new Node3D
        {
            Name = "Alice"
        };
        // Script presence without an IIdentifiable instance mirrors an authored character in the edited scene: the
        // script resource is attached, yet identity genuinely resolves to nothing here. A GDScript stand-in is used
        // because attaching a C# script to a live C#-created node replaces its wrapper.
        GDScript characterScript = new()
        {
            SourceCode = "extends Node3D\n"
        };
        _ = characterScript.Reload();
        scriptedHost.Set("script", characterScript);
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker("AliceMarker", 0,
            new MiniMapLabel { Name = "Identity", Flavour = MiniMapLabelFlavour.Identity, TextSize = 16, FillColour = new Color("8a2be2") },
            new MiniMapRectangle { Name = "Box", Width = 2, Depth = 2, FillColour = new Color("287fa0") });

        hosted.AddChild(scriptedHost);
        scriptedHost.AddChild(marker);
        root.AddChild(hosted);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateEditorPreviewRenderer(root).Render(Vector2.Zero, _smallSquare);

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            Assert.Contains("#287fa0", MiniMapTestHarness.Svg(result), StringComparison.Ordinal);
            Assert.DoesNotContain("#8a2be2", MiniMapTestHarness.Svg(result), StringComparison.Ordinal);
            MiniMapDiagnostic diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("IdentityResolvesAtRuntime", diagnostic.Code);
            Assert.Equal("Hosted/Alice/AliceMarker", diagnostic.Reference, StringComparer.Ordinal);
            Assert.Contains("Hosted/Alice/AliceMarker/Identity", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("'Alice'", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("Hosted/Alice)", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("does not run in the editor", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("play mode", diagnostic.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("MissingIdentity", Describe(result), StringComparison.Ordinal);
            Assert.All(result.Diagnostics, item => Assert.False(item.Reference.StartsWith('/'),
                $"Diagnostics must use scope-relative paths, got '{item.Reference}'."));
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// The editor-context notice degrades gracefully when no ancestor node carries a script: the ancestor clause is
    /// omitted — no ancestor is invented — while the label is still omitted, no <c>MissingIdentity</c> is reported,
    /// and the render continues Ready (TR-7/TA-6).
    /// </summary>
    [Fact]
    public async Task EditorToolContextWithoutScriptedAncestor_NoticeOmitsTheAncestorClause()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapEditorNoticeNoScriptRoot"
        };
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker("Lone", 0,
            new MiniMapLabel { Name = "Identity", Flavour = MiniMapLabelFlavour.Identity, TextSize = 16, FillColour = new Color("8a2be2") },
            new MiniMapRectangle { Name = "Box", Width = 2, Depth = 2, FillColour = new Color("287fa0") });

        root.AddChild(marker);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateEditorPreviewRenderer(root).Render(Vector2.Zero, _smallSquare);

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            Assert.Contains("#287fa0", MiniMapTestHarness.Svg(result), StringComparison.Ordinal);
            Assert.DoesNotContain("#8a2be2", MiniMapTestHarness.Svg(result), StringComparison.Ordinal);
            MiniMapDiagnostic diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("IdentityResolvesAtRuntime", diagnostic.Code);
            Assert.Equal("Lone", diagnostic.Reference, StringComparer.Ordinal);
            Assert.Contains("Lone/Identity", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("play mode", diagnostic.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("does not run in the editor", diagnostic.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("MissingIdentity", Describe(result), StringComparison.Ordinal);
            Assert.All(result.Diagnostics, item => Assert.False(item.Reference.StartsWith('/'),
                $"Diagnostics must use scope-relative paths, got '{item.Reference}'."));
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Every diagnostic kind names markers and primitives by node path relative to the renderer's scope root — a
    /// nested marker's authored failure carries the multi-segment relative form, never an absolute tree path that
    /// traverses host UI hierarchies (TR-16/TA-6).
    /// </summary>
    [Fact]
    public async Task AuthoredFailureOnNestedMarker_NamesScopeRelativePathsOnly()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapNestedScopeRoot"
        };
        Node nested = new Node3D
        {
            Name = "Nested"
        };
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker("Broken", 0, new MiniMapRectangle
        {
            Name = "ZeroWidth",
            Width = 0,
            Depth = 1,
        });

        nested.AddChild(marker);
        root.AddChild(nested);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root)
                .Render(Vector2.Zero, new MiniMapRenderSettings(MaximumDistance: float.PositiveInfinity, Width: 256, Height: 256));

            AssertSingleFailureNamesMarker(result, "Nested/Broken", "Nested/Broken/ZeroWidth", "InvalidSize");
            MiniMapDiagnostic diagnostic = Assert.Single(result.Diagnostics);
            Assert.False(diagnostic.Reference.StartsWith('/'), $"Reference must be scope-relative, got '{diagnostic.Reference}'.");
            Assert.DoesNotContain("/root", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Authored degenerate or non-finite primitive sizes fail the whole render loudly, naming the marker node path
    /// and the offending primitive (TR-16).
    /// </summary>
    [Fact]
    public async Task AuthoredDegenerateSizes_FailLoudlyNamingMarkerAndPrimitive()
    {
        SceneTree sceneTree = GetSceneTree();
        MiniMapPrimitive[] cases =
        [
            new MiniMapRectangle { Name = "ZeroWidth", Width = 0, Depth = 1 },
            new MiniMapCircle { Name = "NegativeRadius", Radius = -1 },
            new MiniMapTriangle { Name = "NanLength", Width = 1, Length = float.NaN },
            new MiniMapLabel { Name = "ZeroTextSize", Text = "x", TextSize = 0 },
        ];

        foreach (MiniMapPrimitive primitive in cases)
        {
            Node caseRoot = new()
            {
                Name = $"MinimapDegenerate_{primitive.Name}"
            };
            MiniMapMarker marker = MiniMapTestHarness.CreateMarker("Broken", 0, primitive);
            caseRoot.AddChild(marker);
            await MiniMapTestHarness.AddToTreeAsync(sceneTree, caseRoot);

            string primitiveName = primitive.Name;
            MiniMapRenderResult result;
            try
            {
                result = MiniMapTestHarness.CreateScopedRenderer(caseRoot)
                    .Render(Vector2.Zero, new MiniMapRenderSettings(MaximumDistance: float.PositiveInfinity, Width: 256, Height: 256));
            }
            finally
            {
                await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, caseRoot);
            }

            Assert.True(result.Diagnostics.Length == 1, $"Expected exactly one diagnostic for {primitiveName}, got: {Describe(result)}.");
            AssertSingleFailureNamesMarker(result, "Broken", $"Broken/{primitiveName}", "InvalidSize");
        }
    }

    /// <summary>
    /// Non-finite marker transforms are the only rejected transform class and fail loudly naming the marker
    /// (TR-8/TR-16).
    /// </summary>
    [Fact]
    public async Task NonFiniteTransform_FailsLoudly()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapNonFiniteRoot"
        };
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker("Lost", 0, new MiniMapRectangle
        {
            Name = "Box",
            Width = 2,
            Depth = 2,
            FillColour = new Color("287fa0"),
        });

        root.AddChild(marker);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            marker.Position = new Vector3(float.NaN, 0, 0);
            await WaitForFramesAsync(sceneTree, 2);

            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, _smallSquare);
            Assert.True(result.Diagnostics.Length == 1, $"Expected exactly one diagnostic, got {result.Diagnostics.Length}.");
            AssertSingleFailureNamesMarker(result, "Lost", "Lost", "InvalidTransform");
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Duplicate primitive references and empty primitive slots are authoring errors and fail the render loudly
    /// (TR-2/TR-16).
    /// </summary>
    [Fact]
    public async Task DuplicateAndEmptyPrimitiveSlots_FailLoudly()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapAuthoringRoot"
        };
        Node duplicateScope = new()
        {
            Name = "DuplicateScope"
        };
        Node emptyScope = new()
        {
            Name = "EmptyScope"
        };
        MiniMapRectangle shared = new()
        {
            Name = "Shared",
            Width = 2,
            Depth = 2,
            FillColour = new Color("287fa0")
        };
        MiniMapMarker duplicated = new()
        {
            Name = "Duplicated",
            Primitives = [shared, shared],
        };
        MiniMapMarker emptySlot = new()
        {
            Name = "EmptySlot",
            Primitives = [null!]
        };

        duplicated.AddChild(shared);
        duplicateScope.AddChild(duplicated);
        root.AddChild(duplicateScope);
        emptyScope.AddChild(emptySlot);
        root.AddChild(emptyScope);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderSettings settings = new(MaximumDistance: float.PositiveInfinity, Width: 256, Height: 256);

            MiniMapRenderResult duplicateResult = MiniMapTestHarness.CreateScopedRenderer(duplicateScope).Render(Vector2.Zero, settings);
            Assert.True(duplicateResult.Status == MiniMapRenderStatus.Failed, $"Duplicate render was {Describe(duplicateResult)}.");
            Assert.True(duplicateResult.Diagnostics.Length == 1,
                $"Expected one diagnostic, got: {Describe(duplicateResult)}.");
            AssertSingleFailureNamesMarker(duplicateResult, "Duplicated", "Duplicated/Shared", "DuplicatePrimitive");

            MiniMapRenderResult emptyResult = MiniMapTestHarness.CreateScopedRenderer(emptyScope).Render(Vector2.Zero, settings);
            Assert.True(emptyResult.Diagnostics.Length == 1,
                $"Expected one diagnostic, got: {Describe(emptyResult)}.");
            // A null slot has no node path to name; the diagnostic names the marker and the slot index.
            AssertSingleFailureNamesMarker(emptyResult, "EmptySlot", "Primitive slot 0", "MissingPrimitive");
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// A transiently degenerate projection — a footprint collapsed to zero area by a live pose — skips that
    /// primitive with a diagnostic while the render continues (TR-16).
    /// </summary>
    [Fact]
    public async Task TransientDegenerateProjection_SkipsPrimitiveAndContinues()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapDegenerateRoot"
        };
        MiniMapMarker pitched = MiniMapTestHarness.CreateMarker("Pitched", 0, new MiniMapRectangle
        {
            Name = "Vanishing",
            Width = 2,
            Depth = 2,
            FillColour = new Color("287fa0"),
        });
        MiniMapMarker upright = MiniMapTestHarness.CreateMarker("Upright", 0, new MiniMapRectangle
        {
            Name = "Stays",
            Width = 2,
            Depth = 2,
            FillColour = new Color("d84156"),
            Position = new Vector3(5, 0, 0),
        });

        root.AddChild(pitched);
        root.AddChild(upright);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            pitched.RotationDegrees = new Vector3(90, 0, 0);
            await WaitForFramesAsync(sceneTree, 2);

            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, _smallSquare);

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            (string Fill, Vector2[] Points) = Assert.Single(MiniMapTestHarness.Polygons(MiniMapTestHarness.Svg(result)));
            Assert.Equal("#d84156", Fill);
            MiniMapDiagnostic diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("DegenerateProjection", diagnostic.Code);
            Assert.Equal("Pitched", diagnostic.Reference, StringComparer.Ordinal);
            Assert.Contains("Pitched/Vanishing", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Text the bundled Inter font does not cover — non-Latin identity is legitimate — produces an unsupported-glyph
    /// diagnostic and omits the glyphs without failing the render (TR-15/TR-16).
    /// </summary>
    [Fact]
    public async Task UnsupportedGlyph_DiagnosesWithoutFailingTheRender()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapGlyphRoot"
        };
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker("Korean", 0,
            new MiniMapLabel { Name = "Caption", Text = "민수", TextSize = 16 },
            new MiniMapRectangle { Name = "Box", Width = 2, Depth = 2, FillColour = new Color("287fa0") });

        root.AddChild(marker);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, _smallSquare);

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            Assert.Contains("#287fa0", MiniMapTestHarness.Svg(result), StringComparison.Ordinal);
            Assert.DoesNotContain("민수", MiniMapTestHarness.Svg(result), StringComparison.Ordinal);
            MiniMapDiagnostic diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("UnsupportedGlyph", diagnostic.Code);
            Assert.Equal("Korean", diagnostic.Reference, StringComparer.Ordinal);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }
}
