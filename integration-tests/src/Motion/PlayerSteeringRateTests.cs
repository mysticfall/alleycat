using System.Text.Json;
using AlleyCat.Control;
using AlleyCat.IntegrationTests.Support;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>The approved walking default gives full/half rates independently of stationary turn angle.</summary>
    [Fact]
    public async Task VirtualSteering_DefaultRatePreservesPartialControl()
    {
        var samples = new List<object>();
        try
        {
            await using ClosureFixture fixture = await ClosureFixture.CreateAsync();
            fixture.Left.TriggerActionVector2InputChanged("primary", new Vector2(0, 0.5f));
            var rates = new List<double>();
            foreach (float input in new[] { 1f, 0.5f, 0f })
            {
                fixture.Right.TriggerActionVector2InputChanged("primary", new Vector2(input, 0));
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 2);
                WalkingExperimentDriver driver = fixture.Motion.ContinuousDriver!;
                double time = driver.ObservedAt, yaw = driver.SteeringYaw;
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 6);
                double rate = -(driver.SteeringYaw - yaw) / (driver.ObservedAt - time) * 180 / Math.PI;
                rates.Add(rate);
                samples.Add(new
                {
                    input,
                    rate,
                    playerDefault = fixture.Motion.WalkingSteeringDegreesPerSecond,
                    driverDefault = driver.WalkingSteeringDegreesPerSecond,
                    stationaryPolicy = "source-landmark"
                });
                Assert.Equal(new Vector2(0, 0.5f), fixture.Motion.MovementIntent);
            }
            fixture.Motion.ClearInput();
            Assert.Equal(0, fixture.Motion.ContinuousDriver!.SteeringYaw);
            // User-authorised rates, not derived from the configured default under test.
            Assert.InRange(rates[0], 59.999, 60.001);
            Assert.InRange(rates[1], 29.999, 30.001);
            Assert.Equal(0, rates[2]);
            Assert.Equal(60, fixture.Motion.WalkingSteeringDegreesPerSecond);
            Assert.Equal(60, fixture.Motion.ContinuousDriver.WalkingSteeringDegreesPerSecond);
        }
        finally
        {
            string label = System.Environment.GetEnvironmentVariable("PLAYER_RATE_PROBE") ?? $"rate-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
            string path = ProjectSettings.GlobalizePath($"res://temp/motionbricks/player-steering-rate-60-01/{label}.json");
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = new FileStream(path, FileMode.CreateNew);
            JsonSerializer.Serialize(file, samples);
        }
    }
}
