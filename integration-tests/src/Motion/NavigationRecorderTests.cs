using System.Collections.Concurrent;
using System.Text.Json;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Recording;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    private sealed class RecordingSession : SomaInferenceWorker.ISession
    {
        private readonly TargetEndpointSession _inner = new(true);
        internal readonly ConcurrentQueue<Transform3D[]> Outputs = new();
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            Transform3D[][] output = _inner.Generate(context, target);
            Outputs.Enqueue([.. output.SelectMany(p => p)]);
            return output;
        }
        public void Dispose() => _inner.Dispose();
    }

    /// <summary>Automatic normal-scene capture matches actual inputs/provider/skin/camera across save and continuation.</summary>
    [Fact]
    public async Task AutomaticNavigationRecorderPreservesOneNativeFreeFeedbackSession()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame { Name = "AutomaticRecorder" };
        Node scene = GD.Load<PackedScene>(NavigationPlaytestRecorder.PlaytestScene).Instantiate();
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        actor.Position = new(-2, 0, -2); // Normal-composition test placement, before Ready only.
        Skeleton3D skeleton = actor.GetNode<Skeleton3D>("Female/GeneralSkeleton");
        Camera3D camera = scene.GetNode<Camera3D>("Camera3D");
        NavigationPlaytestRecorder recorder = scene.GetNode<NavigationPlaytestRecorder>("SessionRecorder");
        MotionBricksNavigation? nav = null;
        var provider = new RecordingSession();
        var actual = new Dictionary<ulong, Transform3D[]>();
        var windows = new List<(long Ticket, Transform3D[] Values)>();
        void Observe()
        {
            if (nav is null)
            {
                return;
            }
            Transform3D[] globals = [.. Enumerable.Range(0, skeleton.GetBoneCount()).Select(skeleton.GetBoneGlobalPose)];
            actual[Engine.GetPhysicsFrames()] = [actor.GlobalTransform, skeleton.GlobalTransform, camera.GlobalTransform,
                .. nav.GeneratedInputPose.ToArray(), .. nav.RealisedFeedback.ToArray(), .. globals];
        }
        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation value)
            {
                return;
            }
            nav = value;
            value.SessionFactory = () => provider;
            value.PoseObserved += Observe;
            value.WindowSubmitted += (ticket, context, targets) => windows.Add((ticket, [.. context.SelectMany(p => p), .. targets.SelectMany(p => p)]));
        }
        tree.NodeAdded += Configure;
        root.AddChild(scene);
        tree.Root.AddChild(root);
        string path = "";
        try
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 40);
            Assert.NotNull(nav);
            path = recorder.SessionPath;
            Assert.NotEmpty(path);
            Assert.Equal("not-started", nav.NativePhase); // No real native session constructed.
            Assert.Same(nav, actor.Navigation);
            Assert.True(nav.SamplesRecorded > 0);
            Label statusLabel = scene.FindChildren("RecordingStatus", "Label", true, false).OfType<Label>().Single();
            Assert.True(statusLabel.IsVisibleInTree());
            Assert.DoesNotContain(path, statusLabel.Text);
            Assert.Contains(Path.GetFileName(path)[^8..], statusLabel.Text);
            Assert.True(scene.GetViewport().GetVisibleRect().Encloses(statusLabel.GetGlobalRect()));
            Button bookmark = scene.FindChildren("MarkIssue", "Button", true, false).OfType<Button>().Single();
            Button save = scene.FindChildren("SaveFeedback", "Button", true, false).OfType<Button>().Single();
            Assert.True(bookmark.IsVisibleInTree() && save.IsVisibleInTree());
            Assert.Null(bookmark.Shortcut);
            Assert.Null(save.Shortcut);
            Assert.Equal("Mark issue", bookmark.Text);
            Assert.Equal("Save feedback", save.Text);
            Transform3D first = new(new Basis(Vector3.Up, .21f), actor.GlobalPosition + new Vector3(-.6f, 0, 0));
            Vector3[] vias = [actor.GlobalPosition + new Vector3(-.1f, 0, 0), actor.GlobalPosition + new Vector3(-.2f, 0, 0)];
            Assert.Equal(0, scene.Call("_submit_destination", first, vias).AsInt32());
            NavigationRequestIdentity? initial = nav.CurrentStatus.ActiveRequest;
            Transform3D second = new(new Basis(Vector3.Up, -.13f), actor.GlobalPosition + new Vector3(-.4f, 0, 0));
            Assert.Equal(0, scene.Call("_submit_destination", second, Array.Empty<Vector3>()).AsInt32());
            Assert.NotEqual(initial, nav.CurrentStatus.ActiveRequest);
            // Use normal UI signal and normal Escape input, not a replaced controller.
            HSlider slider = scene.FindChildren("Speed", "HSlider", true, false).OfType<HSlider>().Single();
            slider.Value = .65;
            scene.Set("_origin", new Vector3(-1.7f, .15f, -1.9f));
            scene.Set("_yaw", .45);
            scene.Set("_distance", 5.5);
            _ = scene.Call("_update_camera");
            _ = scene.Call("_record_view");
            _ = scene.Call("_input", new InputEventKey { Keycode = Key.Escape, Pressed = true });
            _ = bookmark.EmitSignal(BaseButton.SignalName.Pressed);
            _ = save.EmitSignal(BaseButton.SignalName.Pressed);
            await WaitSaved(tree, path, completed: false);
            NavigationSessionReader.Entry[] checkpoint = [.. NavigationSessionReader.ReadCheckpoint(path)];
            Assert.Contains(checkpoint, e => Kind(e) == "bookmark");
            Assert.Contains(checkpoint, e => Kind(e) == "cancel");
            Assert.Contains(checkpoint, e => Kind(e) == "speed");
            Assert.Equal(.65f, nav.NormalisedSpeed);
            // Later successful outcome under the same provider/production arrival lifecycle.
            Transform3D third = nav.RecordingMeasuredTransform;
            third.Origin = actor.GlobalPosition;
            Assert.Equal(0, scene.Call("_submit_destination", third, Array.Empty<Vector3>()).AsInt32());
            for (int i = 0; i < 480 && !nav.TerminalOutcomes.Any(o => o.Outcome == NavigationTerminalOutcome.Completed); i++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
            Assert.Contains(nav.TerminalOutcomes, o => o.Outcome == NavigationTerminalOutcome.Completed);
            _ = bookmark.EmitSignal(BaseButton.SignalName.Pressed); // Completed-but-visually-bad feedback remains recordable.
            Transform3D rejected = third;
            rejected.Origin = new(float.NaN, 0, 0);
            Assert.Equal((int)NavigationDestinationResult.Invalid, scene.Call("_submit_destination", rejected, Array.Empty<Vector3>()).AsInt32());
            // Exercise RequestExit's drain without quitting the reusable test process.
            await root.DrainMotionAndDiagnosticsAsync();
            Task? worker = nav.WorkerCompletion;
            root.RemoveChild(scene);
            scene.QueueFree();
            if (worker is not null)
            {
                await worker;
            }
            await WaitSaved(tree, path, completed: true);
            NavigationSessionReader.Entry[] entries = [.. NavigationSessionReader.ReadCheckpoint(path)];
            Assert.True(entries.Length > checkpoint.Length);
            NavigationSessionReader.Entry[] commands = [.. entries.Where(e => Kind(e) == "command-input")];
            Assert.Equal(4, commands.Length);
            JsonElement firstData = commands[0].Event.GetProperty("data").GetProperty("data");
            Assert.Equal(first, Decode(firstData.GetProperty("requested").Deserialize<float[]>()!, 0));
            Assert.Equal(vias.SelectMany(v => new[] { v.X, v.Y, v.Z }),
                firstData.GetProperty("vias").EnumerateArray().SelectMany(v => v.Deserialize<float[]>()!));
            Assert.Equal([1, 2, 3, 4], commands.Select(c => c.Event.GetProperty("data").GetProperty("attempt").GetInt64()));
            Assert.Contains(entries, e => Kind(e) == "command-result" && e.Event.GetProperty("data").GetProperty("data").GetProperty("result").GetString() == "Invalid");
            Assert.Equal(provider.Outputs.Count, entries.Count(e => Kind(e) == "native-returned"));
            foreach ((Transform3D[] output, NavigationSessionReader.Entry recorded) in provider.Outputs.Zip(entries.Where(e => Kind(e) == "native-returned")))
            {
                Assert.Equal(output, Enumerable.Range(0, output.Length).Select(i => Decode(recorded.Values, i * 12)));
            }
            foreach ((long ticket, Transform3D[] values) in windows)
            {
                NavigationSessionReader.Entry recorded = entries.Single(e => Kind(e) == "submission" && e.Event.GetProperty("data").GetProperty("ticket").GetInt64() == ticket);
                Assert.Equal(values, Enumerable.Range(0, values.Length).Select(i => Decode(recorded.Values, i * 12)));
            }
            NavigationSessionReader.Entry[] poses = [.. entries.Where(e => Kind(e) == "pose")];
            Assert.NotEmpty(poses);
            foreach (NavigationSessionReader.Entry pose in poses)
            {
                Transform3D[] expected = actual[pose.Event.GetProperty("data").GetProperty("tick").GetUInt64()];
                Assert.Equal(expected, Enumerable.Range(0, expected.Length).Select(i => Decode(pose.Values, i * 12))); // Actual final bones/camera.
            }
            Assert.Contains(entries, e => Kind(e) == "outcome" && e.Event.GetProperty("data").GetProperty("data").GetProperty("Outcome").GetInt32() == (int)NavigationTerminalOutcome.Completed);
            using var final = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(path, "manifest.json")));
            Assert.Equal(0, final.RootElement.GetProperty("droppedRecords").GetInt64());
            Assert.Equal("complete-through-checkpoint", final.RootElement.GetProperty("integrity").GetString());
            string report = ProjectSettings.GlobalizePath("res://temp/motionbricks/navigation-recorder-01/e2e.json");
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                path,
                commands = commands.Length,
                poses = poses.Length,
                windows = windows.Count,
                returnedChunks = provider.Outputs.Count,
                nativeCalls = 0,
                status = "PASS: actual inputs/provider/skin/camera/checkpoint/continuation/finalisation"
            }));
            var outside = new NavigationPlaytestRecorder();
            root.AddChild(outside);
            Assert.Empty(outside.SessionPath);
            outside.QueueFree();
        }
        finally
        {
            tree.NodeAdded -= Configure;
            if (nav is not null && GodotObject.IsInstanceValid(nav))
            {
                nav.PoseObserved -= Observe;
            }
            root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
        }
    }

    private static string Kind(NavigationSessionReader.Entry entry) => entry.Event.GetProperty("kind").GetString()!;

    // Decode the declared column-major contract, independent of the recorder's packing helper.
    private static Transform3D Decode(float[] values, int at) => new(new Basis(
        new(values[at], values[at + 1], values[at + 2]),
        new(values[at + 3], values[at + 4], values[at + 5]),
        new(values[at + 6], values[at + 7], values[at + 8])),
        new(values[at + 9], values[at + 10], values[at + 11]));

    private static async Task WaitSaved(SceneTree tree, string path, bool completed)
    {
        for (int i = 0; i < 600; i++)
        {
            string file = Path.Combine(path, "manifest.json");
            if (File.Exists(file))
            {
                using var manifest = JsonDocument.Parse(File.ReadAllBytes(file));
                Assert.NotEqual("failed", manifest.RootElement.GetProperty("state").GetString());
                if (completed ? manifest.RootElement.GetProperty("state").GetString() == "completed" : manifest.RootElement.GetProperty("segments").GetArrayLength() > 0)
                {
                    return;
                }
            }
            await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
        }
        Assert.Fail("Recorder did not durably seal its checkpoint within ten seconds of physics observations.");
    }
}
