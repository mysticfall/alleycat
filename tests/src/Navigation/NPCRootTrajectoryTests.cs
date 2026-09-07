using AlleyCat.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCRootTrajectoryTests
{
    [Fact]
    public void Resample_PreservesEndpointsAndProducesBoundedAdjacentJerk()
    {
        float[] times = [0f, 0.5f, 1f];
        Vector2[] positions = [Vector2.Zero, new(0f, 0.4f), new(0f, 0.8f)];
        Vector2[] velocities = [new(0f, 0.8f), new(0f, 0.8f), new(0f, 0.8f)];

        NPCRootTrajectoryKey[] samples = NPCRootTrajectory.Resample(times, positions, velocities, 1f, 60f, true);

        Assert.Equal(Vector2.Zero, samples[0].Position);
        Assert.Equal(new Vector2(0f, 0.8f), samples[^1].Position);
        float maximumJerk = 0f;
        Vector2 previousVelocity = Vector2.Zero;
        Vector2 previousAcceleration = Vector2.Zero;
        for (int index = 1; index < samples.Length; index++)
        {
            Vector2 velocity = (samples[index].Position - samples[index - 1].Position) * 60f;
            Vector2 acceleration = (velocity - previousVelocity) * 60f;
            if (index > 2)
            {
                maximumJerk = Math.Max(maximumJerk, (acceleration - previousAcceleration).Length() * 60f);
            }
            previousVelocity = velocity;
            previousAcceleration = acceleration;
        }
        Assert.InRange(maximumJerk, 0f, 0.1f);
    }
}
