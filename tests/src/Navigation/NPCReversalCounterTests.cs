using AlleyCat.Navigation;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCReversalCounterTests
{
    [Fact]
    public void NoiseBelowThreshold_IsIgnored()
    {
        NPCReversalCounter counter = new(0.06f, 4);
        foreach (float sample in new[] { 0.2f, 0.01f, -0.02f, 0.03f, -0.04f, 0.2f })
        {
            Assert.False(counter.Observe(sample));
        }
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public void TransientBoundarySignChange_IsNotSustained()
    {
        NPCReversalCounter counter = new(0.06f, 4);
        foreach (float sample in new[] { 0.3f, 0.3f, -0.1f, -0.12f, 0.2f, 0.2f })
        {
            _ = counter.Observe(sample);
        }
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public void SustainedDirectionChange_CountsExactlyOnce()
    {
        NPCReversalCounter counter = new(0.06f, 4);
        bool[] events = [.. new float[] { 0.3f, 0.3f, -0.1f, -0.12f, -0.11f, -0.09f, -0.08f }
            .Select(counter.Observe)
            .ToArray()];
        _ = Assert.Single(events, value => value);
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public void Reset_ClearsCountAndEstablishedDirection()
    {
        NPCReversalCounter counter = new(0.06f, 2);
        foreach (float sample in new[] { 0.3f, -0.1f, -0.1f })
        {
            _ = counter.Observe(sample);
        }
        Assert.Equal(1, counter.Count);

        counter.Reset();

        Assert.Equal(0, counter.Count);
        Assert.False(counter.Observe(-0.1f));
        Assert.False(counter.Observe(-0.1f));
    }
}
