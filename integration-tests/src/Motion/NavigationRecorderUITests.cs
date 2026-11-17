using System.Text.Json;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Recording;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>Real viewport routing must leave passive recorder text transparent to destination clicks.</summary>
    [Fact]
    public async Task RecorderOverlayDoesNotObstructScenePointerInput()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        Window window = tree.Root;
        Vector2I oldSize = window.Size, oldScale = window.ContentScaleSize;
        var root = new ProbeGame { Name = "CompactRecorderUI" };
        Node scene = GD.Load<PackedScene>(NavigationPlaytestRecorder.PlaytestScene).Instantiate();
        NavigationPlaytestRecorder recorder = scene.GetNode<NavigationPlaytestRecorder>("SessionRecorder");
        MotionBricksNavigation? navigation = null;
        void Configure(Node node)
        {
            if (node is MotionBricksNavigation value)
            {
                navigation = value;
                value.SessionFactory = () => new RecordingSession();
            }
        }
        tree.NodeAdded += Configure;
        window.Size = new(960, 640);
        window.ContentScaleSize = new(960, 640);
        root.AddChild(scene);
        window.AddChild(root);
        try
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 40);
            Assert.NotNull(navigation);
            Assert.Equal("not-started", navigation.NativePhase);
            Label status = scene.FindChildren("RecordingStatus", "Label", true, false).OfType<Label>().Single();
            Button mark = scene.FindChildren("MarkIssue", "Button", true, false).OfType<Button>().Single();
            Button save = scene.FindChildren("SaveFeedback", "Button", true, false).OfType<Button>().Single();
            HSlider speed = scene.FindChildren("Speed", "HSlider", true, false).OfType<HSlider>().Single();
            // Keep the real scene camera, ground ray and command handler; only frame the ground for this pointer probe.
            scene.Set("_pitch", .8);
            _ = scene.Call("_update_camera");
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
            var point = new Vector2(430, 170); // Passive status/empty overlay area in the reported long-path layout.
            long before = recorder.Attempt;
            await PointerClick(point);
            long after = recorder.Attempt;
            await PointerClick(status.GetGlobalRect().Position + new Vector2(100, 40));
            await PointerClick(new Vector2(330, mark.GetGlobalRect().GetCenter().Y)); // Empty actions-container area.
            long afterPassive = recorder.Attempt;
            await PointerClick(mark.GetGlobalRect().GetCenter());
            await PointerClick(speed.GetGlobalRect().Position + new Vector2(speed.Size.X * .35f, speed.Size.Y / 2));
            await PointerClick(save.GetGlobalRect().GetCenter());
            await WaitSaved(tree, recorder.SessionPath, completed: false);
            NavigationSessionReader.Entry[] entries = [.. NavigationSessionReader.ReadCheckpoint(recorder.SessionPath)];
            string directory = ProjectSettings.GlobalizePath("res://temp/motionbricks/navigation-recorder-compact-ui-01/") +
                DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + (status.Text.Contains(recorder.SessionPath) ? "-baseline" : "-candidate");
            _ = Directory.CreateDirectory(directory);
            _ = await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            File.WriteAllText(Path.Combine(directory, "routing.json"), JsonSerializer.Serialize(new
            {
                viewport = window.GetVisibleRect().ToString(),
                point = point.ToString(),
                status = status.Text,
                statusRect = status.GetGlobalRect().ToString(),
                before,
                after,
                afterPassive,
                finalAttempt = recorder.Attempt,
                speed = speed.Value,
                bookmark = entries.Any(e => Kind(e) == "bookmark"),
                saved = entries.Length,
                sessionPath = recorder.SessionPath,
                nativeCalls = 0
            }));
            Assert.Equal(before + 1, after); // Decisive symptom oracle: normal world destination handling ran.
            Assert.Equal(after + 2, afterPassive);
            Assert.Equal(afterPassive, recorder.Attempt); // Controls did not leak destination clicks.
            Assert.Contains(entries, e => Kind(e) == "bookmark");
            Assert.Contains(entries, e => Kind(e) == "speed");
            Assert.InRange(speed.Value, .3, .5);
            Assert.Equal((float)speed.Value, navigation.NormalisedSpeed);
            Assert.DoesNotContain(recorder.SessionPath, status.Text);
            Assert.InRange(status.Size.X, 1, 360);
            Assert.InRange(status.Size.Y, 80, 160); // Text must be visible, not merely bounded by clipping it away.
            Assert.Null(mark.Shortcut);
            Assert.Null(save.Shortcut);
            scene.SetProcess(false); // Freeze only the status refresh, not pointer/physics handling.
            status.Text = "Recording: FAILED · dropped 42\nError: " + new string('x', 2000);
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
            Assert.InRange(status.Size.X, 1, 360);
            Assert.InRange(status.Size.Y, 80, 160);

            async Task PointerClick(Vector2 position)
            {
                window.PushInput(new InputEventMouseMotion { Position = position, GlobalPosition = position }, true);
                window.PushInput(new InputEventMouseButton { Position = position, GlobalPosition = position, ButtonIndex = MouseButton.Left, Pressed = true }, true);
                await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
                window.PushInput(new InputEventMouseButton { Position = position, GlobalPosition = position, ButtonIndex = MouseButton.Left, Pressed = false }, true);
                await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
            }
        }
        finally
        {
            tree.NodeAdded -= Configure;
            await root.DrainMotionAndDiagnosticsAsync();
            root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
            window.Size = oldSize;
            window.ContentScaleSize = oldScale;
        }
    }
}
