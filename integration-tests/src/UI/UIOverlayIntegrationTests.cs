using AlleyCat.UI;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.UI;

/// <summary>
/// Integration coverage for UI overlay widget resolution and debug widget convenience behaviour.
/// </summary>
public sealed class UIOverlayIntegrationTests
{
    /// <summary>
    /// Verifies optional and required typed widget resolution contracts.
    /// </summary>
    [Fact]
    public async Task UIOverlay_FindAndGetWidget_UsesOptionalAndRequiredResolutionSemantics()
    {
        SceneTree sceneTree = GetSceneTree();
        UIOverlay overlay = await CreateRuntimeUIOverlayAsync(sceneTree, includeDebugWidget: true);

        try
        {
            IDebugWidget? foundDebugWidget = overlay.FindWidget<IDebugWidget>();
            IDebugWidget requiredDebugWidget = overlay.GetWidget<IDebugWidget>();

            Assert.NotNull(foundDebugWidget);
            Assert.Same(foundDebugWidget, requiredDebugWidget);

            Assert.Null(overlay.FindWidget<IMissingWidget>());
            _ = Assert.Throws<InvalidOperationException>(overlay.GetWidget<IMissingWidget>);
        }
        finally
        {
            overlay.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies debug message set/clear behaviour through the overlay convenience path.
    /// </summary>
    [Fact]
    public async Task UIOverlay_TrySetDebugMessage_SetsAndClearsDebugWidgetText()
    {
        SceneTree sceneTree = GetSceneTree();
        UIOverlay overlay = await CreateRuntimeUIOverlayAsync(sceneTree, includeDebugWidget: true);

        try
        {
            bool setResult = overlay.TrySetDebugMessage("Overlay Test Message");
            DebugWidget debugWidget = Assert.IsType<DebugWidget>(overlay.GetWidget<IDebugWidget>(), exactMatch: false);
            Label label = debugWidget.GetNode<Label>("Label");

            Assert.True(setResult);
            Assert.True(debugWidget.Visible);
            Assert.Equal("Overlay Test Message", label.Text);

            bool clearResult = overlay.TrySetDebugMessage(null);

            Assert.True(clearResult);
            Assert.False(debugWidget.Visible);
            Assert.Equal(string.Empty, label.Text);
        }
        finally
        {
            overlay.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies debug convenience calls warn and return without throwing when the widget is unavailable.
    /// </summary>
    [Fact]
    public async Task DebugUIConvenience_WhenDebugWidgetMissing_WarnsAndDoesNotThrow()
    {
        SceneTree sceneTree = GetSceneTree();
        Node globalRoot = CreateGlobalHierarchyWithoutDebugWidget();
        sceneTree.Root.AddChild(globalRoot);
        await WaitForNextFrameAsync(sceneTree);

        try
        {
            bool setResult = sceneTree.Root.SetDebugMessage("Missing debug widget message");
            bool clearResult = sceneTree.Root.SetDebugMessage(null);

            Assert.False(setResult);
            Assert.False(clearResult);
        }
        finally
        {
            globalRoot.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private static async Task<UIOverlay> CreateRuntimeUIOverlayAsync(SceneTree sceneTree, bool includeDebugWidget)
    {
        UIOverlay overlay = new()
        {
            Name = "UIOverlay",
        };

        if (includeDebugWidget)
        {
            DebugWidget debugWidget = new()
            {
                Name = "DebugOverlay",
            };

            Label debugLabel = new()
            {
                Name = "Label",
            };

            debugWidget.AddChild(debugLabel);
            overlay.AddChild(debugWidget);
        }

        sceneTree.Root.AddChild(overlay);
        await WaitForFramesAsync(sceneTree, 2);

        return overlay;
    }

    private static Node CreateGlobalHierarchyWithoutDebugWidget()
    {
        Node global = new()
        {
            Name = "Global",
        };

        Node xr = new()
        {
            Name = "XR",
        };

        SubViewport subViewport = new()
        {
            Name = "SubViewport",
        };

        UIOverlay overlay = new()
        {
            Name = "UIOverlay",
        };

        subViewport.AddChild(overlay);
        xr.AddChild(subViewport);
        global.AddChild(xr);

        return global;
    }

    private interface IMissingWidget : IUIWidget
    {
    }
}
