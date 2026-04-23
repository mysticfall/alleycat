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
        UIOverlay overlay = await CreateRuntimeUIOverlayAsync(sceneTree, includeDebugWidget: true, includeNotificationWidget: true);

        try
        {
            IDebugWidget? foundDebugWidget = overlay.FindWidget<IDebugWidget>();
            IDebugWidget requiredDebugWidget = overlay.GetWidget<IDebugWidget>();
            INotificationWidget? foundNotificationWidget = overlay.FindWidget<INotificationWidget>();
            INotificationWidget requiredNotificationWidget = overlay.GetWidget<INotificationWidget>();

            Assert.NotNull(foundDebugWidget);
            Assert.Same(foundDebugWidget, requiredDebugWidget);
            Assert.NotNull(foundNotificationWidget);
            Assert.Same(foundNotificationWidget, requiredNotificationWidget);

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
        UIOverlay overlay = await CreateRuntimeUIOverlayAsync(sceneTree, includeDebugWidget: true, includeNotificationWidget: false);

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

    /// <summary>
    /// Verifies notification posting order, timeout expiry, and queue capacity trimming.
    /// </summary>
    [Fact]
    public async Task UIOverlay_TryPostNotification_QueuesNewestFirst_Expires_AndTrimsOldest()
    {
        SceneTree sceneTree = GetSceneTree();
        UIOverlay overlay = await CreateRuntimeUIOverlayAsync(sceneTree, includeDebugWidget: false, includeNotificationWidget: true);

        try
        {
            NotificationWidget notificationWidget = Assert.IsType<NotificationWidget>(overlay.GetWidget<INotificationWidget>(), exactMatch: false);
            VBoxContainer messages = notificationWidget.GetNode<VBoxContainer>("Messages");

            notificationWidget.MaximumQueueSize = 3;

            Assert.True(overlay.TryPostNotification("One", timeoutSeconds: 5.0));
            Assert.True(overlay.TryPostNotification("Two", timeoutSeconds: 5.0));
            Assert.True(overlay.TryPostNotification("Three", timeoutSeconds: 5.0));
            Assert.True(overlay.TryPostNotification("Four", timeoutSeconds: 5.0));

            await WaitForNextFrameAsync(sceneTree);

            Assert.True(notificationWidget.Visible);
            Assert.Equal(3, messages.GetChildCount());
            Assert.Equal("Four", GetNotificationTextAt(messages, 0));
            Assert.Equal("Three", GetNotificationTextAt(messages, 1));
            Assert.Equal("Two", GetNotificationTextAt(messages, 2));

            notificationWidget.MaximumQueueSize = 4;
            Assert.True(overlay.TryPostNotification("Short", timeoutSeconds: 0.0));
            await WaitForNextFrameAsync(sceneTree);

            Assert.Equal(3, messages.GetChildCount());
            Assert.Equal("Four", GetNotificationTextAt(messages, 0));
            Assert.Equal("Three", GetNotificationTextAt(messages, 1));
            Assert.Equal("Two", GetNotificationTextAt(messages, 2));

            Assert.True(overlay.TryClearNotifications());
            await WaitForNextFrameAsync(sceneTree);

            Assert.False(notificationWidget.Visible);
            Assert.Equal(0, messages.GetChildCount());
        }
        finally
        {
            overlay.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies blank notification posts are no-ops and do not clear queued messages.
    /// </summary>
    [Fact]
    public async Task UIOverlay_TryPostNotification_BlankMessage_DoesNotClearExistingQueue()
    {
        SceneTree sceneTree = GetSceneTree();
        UIOverlay overlay = await CreateRuntimeUIOverlayAsync(sceneTree, includeDebugWidget: false, includeNotificationWidget: true);

        try
        {
            NotificationWidget notificationWidget = Assert.IsType<NotificationWidget>(overlay.GetWidget<INotificationWidget>(), exactMatch: false);
            VBoxContainer messages = notificationWidget.GetNode<VBoxContainer>("Messages");

            Assert.True(overlay.TryPostNotification("First", timeoutSeconds: 5.0));
            Assert.True(overlay.TryPostNotification("Second", timeoutSeconds: 5.0));
            await WaitForNextFrameAsync(sceneTree);

            Assert.True(notificationWidget.Visible);
            Assert.Equal(2, messages.GetChildCount());
            Assert.Equal("Second", GetNotificationTextAt(messages, 0));
            Assert.Equal("First", GetNotificationTextAt(messages, 1));

            Assert.True(overlay.TryPostNotification(null));
            Assert.True(overlay.TryPostNotification(string.Empty));
            Assert.True(overlay.TryPostNotification("   \t\n  "));
            await WaitForNextFrameAsync(sceneTree);

            Assert.True(notificationWidget.Visible);
            Assert.Equal(2, messages.GetChildCount());
            Assert.Equal("Second", GetNotificationTextAt(messages, 0));
            Assert.Equal("First", GetNotificationTextAt(messages, 1));
        }
        finally
        {
            overlay.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies node-level notification extension methods route through the global overlay safely.
    /// </summary>
    [Fact]
    public async Task NotificationUIConvenience_PostsAndClears_WhenWidgetPresent_AndWarnsWhenMissing()
    {
        SceneTree sceneTree = GetSceneTree();
        Node? existingGlobal = sceneTree.Root.GetNodeOrNull<Node>("Global");
        bool globalNodeRenamed = false;

        if (existingGlobal is not null)
        {
            existingGlobal.Name = "Global_PreNotificationTest";
            globalNodeRenamed = true;
            await WaitForNextFrameAsync(sceneTree);
        }

        try
        {
            (Node globalRootWithNotification, UIOverlay overlay) = CreateGlobalHierarchy(includeDebugWidget: false, includeNotificationWidget: true);
            sceneTree.Root.AddChild(globalRootWithNotification);
            await WaitForFramesAsync(sceneTree, 2);

            try
            {
                NotificationWidget notificationWidget = Assert.IsType<NotificationWidget>(overlay.GetWidget<INotificationWidget>(), exactMatch: false);
                VBoxContainer messages = notificationWidget.GetNode<VBoxContainer>("Messages");

                bool postResult = sceneTree.Root.PostNotification("Hello Notification", timeoutSeconds: 5.0);
                await WaitForNextFrameAsync(sceneTree);

                Assert.True(postResult);
                Assert.True(notificationWidget.Visible);
                Assert.Equal("Hello Notification", GetNotificationTextAt(messages, 0));

                bool clearResult = sceneTree.Root.TryClearNotifications();
                await WaitForNextFrameAsync(sceneTree);

                Assert.True(clearResult);
                Assert.False(notificationWidget.Visible);
                Assert.Equal(0, messages.GetChildCount());
            }
            finally
            {
                globalRootWithNotification.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }

            (Node globalRootWithoutNotification, _) = CreateGlobalHierarchy(includeDebugWidget: false, includeNotificationWidget: false);
            sceneTree.Root.AddChild(globalRootWithoutNotification);
            await WaitForNextFrameAsync(sceneTree);

            try
            {
                bool postResult = sceneTree.Root.PostNotification("Missing widget");
                bool clearResult = sceneTree.Root.TryClearNotifications();

                Assert.False(postResult);
                Assert.False(clearResult);
            }
            finally
            {
                globalRootWithoutNotification.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }
        finally
        {
            if (globalNodeRenamed)
            {
                existingGlobal!.Name = "Global";
                await WaitForNextFrameAsync(sceneTree);
            }
        }
    }

    private static async Task<UIOverlay> CreateRuntimeUIOverlayAsync(SceneTree sceneTree, bool includeDebugWidget, bool includeNotificationWidget)
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

        if (includeNotificationWidget)
        {
            NotificationWidget notificationWidget = new()
            {
                Name = "NotificationOverlay",
            };

            VBoxContainer messages = new()
            {
                Name = "Messages",
            };

            notificationWidget.AddChild(messages);
            overlay.AddChild(notificationWidget);
        }

        sceneTree.Root.AddChild(overlay);
        await WaitForFramesAsync(sceneTree, 2);

        return overlay;
    }

    private static Node CreateGlobalHierarchyWithoutDebugWidget()
        => CreateGlobalHierarchy(includeDebugWidget: false, includeNotificationWidget: false).global;

    private static (Node global, UIOverlay overlay) CreateGlobalHierarchy(bool includeDebugWidget, bool includeNotificationWidget)
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

        if (includeNotificationWidget)
        {
            NotificationWidget notificationWidget = new()
            {
                Name = "NotificationOverlay",
            };

            VBoxContainer messages = new()
            {
                Name = "Messages",
            };

            notificationWidget.AddChild(messages);
            overlay.AddChild(notificationWidget);
        }

        subViewport.AddChild(overlay);
        xr.AddChild(subViewport);
        global.AddChild(xr);

        return (global, overlay);
    }

    private static string GetNotificationTextAt(VBoxContainer messages, int index)
        => Assert.IsType<Label>(messages.GetChild(index)).Text;

    private interface IMissingWidget : IUIWidget
    {
    }
}
