using AlleyCat.Scene.MiniMap;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Scene;

/// <summary>
/// SCN-002 TA-1 runtime coverage: marker group self-registration with lifecycle-driven membership, live per-render
/// group discovery, DI-only renderer resolution, and the namespace/coupling contracts of TR-1 and TR-4.
/// </summary>
public sealed class MiniMapMarkerDiscoveryIntegrationTests
{
    /// <summary>
    /// Markers join the dedicated scene group on every tree entry (<c>_EnterTree</c>, so re-adding a removed marker
    /// re-registers it) and leave it in <c>_ExitTree</c>, exposing the shared
    /// group-name constant (TR-2/TR-3).
    /// </summary>
    [Fact]
    public async Task Marker_SelfRegistersIntoDedicatedGroupOnEnterTreeAndDeregistersOnExitTree()
    {
        SceneTree sceneTree = GetSceneTree();
        int baseline = sceneTree.GetNodesInGroup(MiniMapMarker.GroupName).Count;
        Node root = new()
        {
            Name = "MinimapGroupLifecycleRoot"
        };
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker("Solo");

        root.AddChild(marker);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            Assert.True(marker.IsInsideTree());
            Assert.Equal("minimap_markers", MiniMapMarker.GroupName);
            Assert.True(marker.IsInGroup(MiniMapMarker.GroupName));
            Assert.Equal(baseline + 1, sceneTree.GetNodesInGroup(MiniMapMarker.GroupName).Count);

            root.RemoveChild(marker);
            await WaitForFramesAsync(sceneTree, 2);

            Assert.False(marker.IsInGroup(MiniMapMarker.GroupName));
            Assert.Equal(baseline, sceneTree.GetNodesInGroup(MiniMapMarker.GroupName).Count);

            root.AddChild(marker);
            await WaitForFramesAsync(sceneTree, 2);

            Assert.True(marker.IsInGroup(MiniMapMarker.GroupName));
            Assert.Equal(baseline + 1, sceneTree.GetNodesInGroup(MiniMapMarker.GroupName).Count);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Markers added and removed through normal scene-tree lifecycle appear and disappear on subsequent renders with
    /// no renderer registration API (UR-2/TA-1).
    /// </summary>
    [Fact]
    public async Task MarkerMembership_LifecycleChangesAreReflectedInSubsequentRenders()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapLifecycleRenderRoot"
        };
        MiniMapMarker stationary = MiniMapTestHarness.CreateMarker("Stationary", 0, new MiniMapRectangle
        {
            Name = "Keep",
            Width = 2,
            Depth = 2,
            FillColour = new Color("287fa0"),
        });
        MiniMapMarker transient = MiniMapTestHarness.CreateMarker("Transient", 0, new MiniMapRectangle
        {
            Name = "Drop",
            Width = 2,
            Depth = 2,
            FillColour = new Color("d84156"),
        });

        root.AddChild(stationary);
        root.AddChild(transient);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderer renderer = MiniMapTestHarness.CreateScopedRenderer(root);
            MiniMapRenderSettings settings = new(MaximumDistance: float.PositiveInfinity, Width: 256, Height: 256);

            MiniMapRenderResult withBoth = renderer.Render(Vector2.Zero, settings);
            Assert.Equal(MiniMapRenderStatus.Ready, withBoth.Status);
            Assert.Contains("#287fa0", MiniMapTestHarness.Svg(withBoth), StringComparison.Ordinal);
            Assert.Contains("#d84156", MiniMapTestHarness.Svg(withBoth), StringComparison.Ordinal);

            root.RemoveChild(transient);
            await WaitForFramesAsync(sceneTree, 2);

            MiniMapRenderResult withoutTransient = renderer.Render(Vector2.Zero, settings);
            Assert.Equal(MiniMapRenderStatus.Ready, withoutTransient.Status);
            Assert.Contains("#287fa0", MiniMapTestHarness.Svg(withoutTransient), StringComparison.Ordinal);
            Assert.DoesNotContain("#d84156", MiniMapTestHarness.Svg(withoutTransient), StringComparison.Ordinal);

            root.AddChild(transient);
            await WaitForFramesAsync(sceneTree, 2);

            MiniMapRenderResult restored = renderer.Render(Vector2.Zero, settings);
            Assert.Equal(MiniMapRenderStatus.Ready, restored.Status);
            Assert.Contains("#d84156", MiniMapTestHarness.Svg(restored), StringComparison.Ordinal);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// The renderer resolves through CORE-004 global service resolution only: the running game's provider carries the
    /// singleton, and the type exposes no static accessor of its own (TR-9).
    /// </summary>
    [Fact]
    public void Renderer_ResolvesThroughGlobalDependencyInjectionWithoutStaticAccessors()
    {
        MiniMapRenderer resolved = Game.Instance.GetRequiredService<MiniMapRenderer>();

        Assert.NotNull(resolved);
        Assert.Same(resolved, Game.Instance.GetRequiredService<MiniMapRenderer>());

        Assert.DoesNotContain(typeof(MiniMapRenderer)
            .GetMembers()
, member => member is { Name: "Instance", MemberType: System.Reflection.MemberTypes.Property });
    }

    /// <summary>
    /// TR-1 places every minimap type in <c>AlleyCat.Scene.MiniMap</c> with none leaking into the root scene
    /// namespace, and TR-4 forbids scene-context and character coupling in the renderer source.
    /// </summary>
    [Fact]
    public void MiniMapTypes_StayInTheirNamespaceAndAvoidSceneContextAndCharacterCoupling()
    {
        System.Reflection.Assembly gameAssembly = typeof(MiniMapMarker).Assembly;
        Type[] minimapTypes = [.. gameAssembly
            .GetTypes()
            .Where(type => type.Name.StartsWith("MiniMap", StringComparison.Ordinal) && type.Namespace?.StartsWith("AlleyCat.Scene", StringComparison.Ordinal) == true)];

        Assert.Contains(minimapTypes, type => type == typeof(MiniMapMarker));
        Assert.Contains(minimapTypes, type => type == typeof(MiniMapRenderer));
        Assert.DoesNotContain(minimapTypes, type => type.Namespace == "AlleyCat.Scene");

        using DirAccess directory = DirAccess.Open("res://src/Scene/MiniMap")
            ?? throw new InvalidOperationException("The minimap source directory is missing from the project.");

        List<string> offenders = [];
        foreach (string file in directory.GetFiles().Where(file => file.EndsWith(".cs", StringComparison.Ordinal)))
        {
            using var source = Godot.FileAccess.Open($"res://src/Scene/MiniMap/{file}", Godot.FileAccess.ModeFlags.Read);
            string text = source?.GetAsText() ?? throw new InvalidOperationException($"Failed to read minimap source {file}.");
            // Documentation comments may name the forbidden contracts when explaining them; code must not use them.
            string code = string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal)));
            if (code.Contains("ISceneContext", StringComparison.Ordinal) || code.Contains("ICharacter", StringComparison.Ordinal))
            {
                offenders.Add(file);
            }
        }

        Assert.True(offenders.Count == 0, $"Minimap sources must not reference ISceneContext or ICharacter: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// A character hosting a marker receives no special handling: the marker is discovered by group membership
    /// wherever its creator parented it, and the renderer needs no character API to render it (TR-3/TR-4).
    /// </summary>
    [Fact]
    public async Task Marker_UnderAnOrdinaryCharacter_IsDiscoveredByGroupMembershipAlone()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapCharacterHostingRoot"
        };
        CharacterHub character = new()
        {
            Name = "Wandering",
            Id = "wandering"
        };
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker("Carried", 0, new MiniMapCircle
        {
            Name = "Spot",
            Radius = 0.5f,
            FillColour = new Color("64adce"),
        });

        character.AddChild(marker);
        root.AddChild(character);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            Assert.True(marker.IsInGroup(MiniMapMarker.GroupName));
            Assert.True(character.IsAncestorOf(marker));

            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root)
                .Render(new Vector2(character.GlobalPosition.X, character.GlobalPosition.Z), new MiniMapRenderSettings(MaximumDistance: float.PositiveInfinity, Width: 256, Height: 256));

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            Assert.Contains("#64adce", MiniMapTestHarness.Svg(result), StringComparison.Ordinal);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }
}
