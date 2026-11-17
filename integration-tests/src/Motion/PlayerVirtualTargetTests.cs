using System.Diagnostics;
using AlleyCat.IntegrationTests.Support;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>A sharp curve supersedes incompatible native work, without losing the bout frame.</summary>
    [Fact]
    public async Task VirtualSteering_RejectsIncompatiblePendingCurve()
    {
        {
            await using ClosureFixture fixture = await ClosureFixture.CreateAsync();
            using var gate = new NativePublicationGate();
            fixture.Motion.SessionDecorator = session => new GatedNativeSession(session, gate);
            var submitted = new List<long>();
            var applied = new HashSet<long>();
            fixture.Motion.WindowSubmitted += (ticket, _, _) => submitted.Add(ticket);
            void Observe()
            {
                _ = applied.Add(fixture.Motion.LastAppliedRequest);
            }
            fixture.Skeleton.SkeletonUpdated += Observe;
            try
            {
                fixture.Left.TriggerActionVector2InputChanged("primary", new Vector2(0, 0.5f));
                var clock = Stopwatch.StartNew();
                while (!gate.Generated.IsSet && clock.Elapsed < TimeSpan.FromSeconds(25))
                {
                    await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
                }
                Assert.True(gate.Generated.IsSet);
                long stale = submitted[^1];
                double before = fixture.Motion.ContinuousDriver!.SteeringYaw;
                fixture.Right.TriggerActionVector2InputChanged("primary", new Vector2(-1, 0));
                Assert.Equal("intent-changed", fixture.Motion.Phase);
                Assert.Equal(before, fixture.Motion.ContinuousDriver.SteeringYaw);
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 2);
                Assert.NotEqual(stale, submitted[^1]);
                gate.Release.Set();
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 120);
                Assert.DoesNotContain(stale, applied);
                Assert.True(fixture.Motion.DiscardedResults >= 1);
                fixture.Left.TriggerActionVector2InputChanged("primary", Vector2.Zero);
                fixture.Right.TriggerActionVector2InputChanged("primary", Vector2.Zero);
                Assert.Equal(0, fixture.Motion.ContinuousDriver.SteeringYaw);
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 240);
                Assert.Equal("settled-idle", fixture.Motion.Phase);
            }
            finally
            {
                gate.Release.Set();
                fixture.Skeleton.SkeletonUpdated -= Observe;
                await fixture.DisposeAsync();
            }
        }
    }

    /// <summary>Useful in-flight walking survives tiny noise, but an incompatible direction cannot publish stale motion.</summary>
    [Fact]
    public async Task VirtualTarget_RetainsNoisyPendingWorkAndRejectsSharpChange()
    {
        {
            await using ClosureFixture fixture = await ClosureFixture.CreateAsync();
            using var gate = new NativePublicationGate();
            fixture.Motion.SessionDecorator = session => new GatedNativeSession(session, gate);
            var applied = new HashSet<long>();
            var submitted = new List<long>();
            fixture.Motion.WindowSubmitted += (ticket, _, _) => submitted.Add(ticket);
            void Observe()
            {
                _ = applied.Add(fixture.Motion.LastAppliedRequest);
            }
            fixture.Skeleton.SkeletonUpdated += Observe;
            try
            {
                fixture.Left.TriggerActionVector2InputChanged("primary", new Vector2(0, 0.475f));
                var clock = Stopwatch.StartNew();
                while (!gate.Generated.IsSet && clock.Elapsed < TimeSpan.FromSeconds(25))
                {
                    await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
                }
                Assert.True(gate.Generated.IsSet, "Real native walking must reach the publication boundary.");
                long stale = Assert.Single(submitted);
                for (int tick = 0; tick < 60; tick++)
                {
                    fixture.Left.TriggerActionVector2InputChanged("primary", new Vector2(0, tick % 2 == 0 ? 0.474f : 0.476f));
                    await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
                }
                Assert.Equal(stale, Assert.Single(submitted));
                // Mild steering and steering-only release preserve compatible native work and the bout.
                fixture.Right.TriggerActionVector2InputChanged("primary", new Vector2(0.2f, 0));
                Assert.Equal(0.2f, fixture.Motion.TurnIntent);
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 30);
                Assert.Equal(stale, Assert.Single(submitted));
                Assert.True(fixture.Motion.ContinuousDriver!.SteeringYaw < 0);
                double retainedYaw = fixture.Motion.ContinuousDriver.SteeringYaw;
                fixture.Right.TriggerActionVector2InputChanged("primary", Vector2.Zero);
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 2);
                Assert.Equal(retainedYaw, fixture.Motion.ContinuousDriver.SteeringYaw);
                Assert.Equal(stale, Assert.Single(submitted));
                fixture.Left.TriggerActionVector2InputChanged("primary", new Vector2(-0.5f, 0));
                Assert.Equal("intent-changed", fixture.Motion.Phase);
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 2);
                Assert.Equal(2, submitted.Count);
                long current = submitted[^1];
                gate.Release.Set();
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 120);
                Assert.DoesNotContain(stale, applied);
                Assert.Contains(current, applied);
                Assert.True(fixture.Motion.DiscardedResults >= 1);
                fixture.Left.TriggerActionVector2InputChanged("primary", Vector2.Zero);
                Assert.Equal(Vector2.Zero, fixture.Motion.MovementIntent);
                Assert.Equal("settling-requested", fixture.Motion.Phase);
                Assert.Null(fixture.Motion.ContinuousDriver!.VirtualTarget);
                Assert.Equal(0, fixture.Motion.ContinuousDriver.SteeringYaw);
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 240);
                Assert.Equal("settled-idle", fixture.Motion.Phase);
                Assert.Equal("", fixture.Motion.Failure);
            }
            finally
            {
                gate.Release.Set();
                fixture.Skeleton.SkeletonUpdated -= Observe;
                await fixture.DisposeAsync();
            }
        }
    }
}
