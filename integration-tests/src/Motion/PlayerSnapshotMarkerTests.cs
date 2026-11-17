using System.Diagnostics;
using System.Text.Json;
using AlleyCat.IntegrationTests.Support;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>Unchanged held steering retires playback at snapshots and must retire its visible marker too.</summary>
    [Fact]
    public async Task VirtualSteering_SnapshotRetirementClearsOwnedMarkers()
    {
        string label = System.Environment.GetEnvironmentVariable("PLAYER_MARKER_RETIREMENT_PROBE") ?? $"test-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        string directory = ProjectSettings.GlobalizePath($"res://temp/motionbricks/player-walking-steering-01/{label}");
        Assert.False(Directory.Exists(directory), "Preserve previous marker regression evidence.");
        _ = Directory.CreateDirectory(directory);
        var records = new List<object>();
        {
            await using ClosureFixture fixture = await ClosureFixture.CreateAsync();
            using var gate = new NativePublicationGate { HoldGeneration = 2 };
            fixture.Motion.SessionDecorator = session => new GatedNativeSession(session, gate);
            // Keep this snapshot-retirement reproducer's original rate: half input must start compatible.
            // The shipping default is covered separately by the rate configuration test.
            fixture.Motion.WalkingSteeringDegreesPerSecond = 30;
            fixture.Motion.ShowSubmittedTarget = true;
            fixture.Root.AddChild(GD.Load<PackedScene>("res://assets/testing/test_environment.tscn").Instantiate());
            var submitted = new List<long>();
            var applied = new HashSet<long>();
            var clock = Stopwatch.StartNew();
            double? settledAt = null;
            string phase = "framing";
            fixture.Motion.WindowSubmitted += (ticket, _, _) => submitted.Add(ticket);
            void Observe()
            {
                _ = applied.Add(fixture.Motion.LastAppliedRequest);
                if (phase == "released" && fixture.Motion.Phase == "settled-idle")
                {
                    settledAt ??= clock.Elapsed.TotalSeconds;
                }
                records.Add(new
                {
                    phase,
                    seconds = clock.Elapsed.TotalSeconds,
                    physicsFrame = Engine.GetPhysicsFrames(),
                    fixture.Motion.LastAppliedRequest,
                    fixture.Motion.Requests,
                    state = fixture.Motion.Phase,
                    fixture.Motion.TurnIntent,
                    fixture.Motion.ContinuousDriver!.IsPlaying,
                    fixture.Motion.ContinuousDriver.SteeringYaw,
                    fixture.Motion.DiscardedResults,
                    markers = fixture.Motion.GetChildren().OfType<Node3D>().Where(n => n.Name.ToString().EndsWith("MotionTarget", StringComparison.Ordinal)).Select(n => new
                    {
                        name = n.Name.ToString(),
                        n.Visible,
                        caption = n.GetNode<Label3D>("Label3D").Text
                    }).ToArray()
                });
            }
            // Former capture points keep their presented-frame wait so the observation timing is unchanged.
            async Task WaitPresentedFrameAsync()
            {
                _ = await fixture.Tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            }
            fixture.Skeleton.SkeletonUpdated += Observe;
            try
            {
                await WaitPresentedFrameAsync();
                phase = "first-request";
                fixture.Left.TriggerActionVector2InputChanged("primary", new Vector2(0, 0.5f));
                double deadline = clock.Elapsed.TotalSeconds + 25;
                while (!fixture.Motion.ContinuousDriver!.IsPlaying && clock.Elapsed.TotalSeconds < deadline)
                {
                    await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
                }
                Assert.True(fixture.Motion.ContinuousDriver!.IsPlaying, "First native playback must exist before testing retirement.");
                long active = fixture.Motion.LastAppliedRequest;
                Node3D marker = fixture.Motion.GetNode<Node3D>("ActiveMotionTarget");
                Assert.True(marker.Visible);
                Assert.Contains($"playing #{active}\n", marker.GetNode<Label3D>("Label3D").Text);
                phase = "active";
                await WaitPresentedFrameAsync();
                // Only this right callback: later retirement must come from elapsed steering at snapshots.
                fixture.Right.TriggerActionVector2InputChanged("primary", new Vector2(0.5f, 0));
                phase = "pending-hold";
                deadline = clock.Elapsed.TotalSeconds + 10;
                while (!gate.Generated.IsSet && clock.Elapsed.TotalSeconds < deadline)
                {
                    await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
                }
                Assert.True(gate.Generated.IsSet, "Second genuine native completion must be held after inference.");
                Assert.Equal(2, submitted.Count);
                long held = submitted[1];
                await WaitPresentedFrameAsync();
                deadline = clock.Elapsed.TotalSeconds + 6;
                while (submitted.Count == 2 && clock.Elapsed.TotalSeconds < deadline)
                {
                    await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
                }
                Assert.True(submitted.Count > 2, "Unchanged steering must reach snapshot incompatibility and submit a replacement.");
                Assert.False(fixture.Motion.ContinuousDriver.IsPlaying);
                Assert.Equal(active, fixture.Motion.LastAppliedRequest);
                Assert.Equal(0.5f, fixture.Motion.TurnIntent);
                phase = "snapshot-retired";
                bool retiredActiveHidden = !marker.Visible;
                string retiredCaption = marker.GetNode<Label3D>("Label3D").Text;
                await WaitPresentedFrameAsync();
                gate.Release.Set();
                fixture.Left.TriggerActionVector2InputChanged("primary", Vector2.Zero);
                fixture.Right.TriggerActionVector2InputChanged("primary", Vector2.Zero);
                phase = "released";
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 480);
                await WaitPresentedFrameAsync();
                Assert.DoesNotContain(held, applied);
                Assert.True(fixture.Motion.DiscardedResults >= 1);
                Assert.Equal("settled-idle", fixture.Motion.Phase);
                Assert.True(settledAt.HasValue && clock.Elapsed.TotalSeconds - settledAt.Value >= 3);
                File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
                {
                    active,
                    held,
                    submitted,
                    retiredActiveHidden,
                    retiredCaption,
                    settledSeconds = clock.Elapsed.TotalSeconds - settledAt!.Value,
                    applied,
                    fixture.Motion.DiscardedResults
                }));
                Assert.True(retiredActiveHidden, $"Retired ACTIVE #{active} survives snapshot discard: {retiredCaption}");
            }
            finally
            {
                gate.Release.Set();
                fixture.Skeleton.SkeletonUpdated -= Observe;
                File.WriteAllText(Path.Combine(directory, "trace.json"), JsonSerializer.Serialize(records));
                await fixture.DisposeAsync();
            }
        }
    }
}
