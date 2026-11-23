using AlleyCat.Models;
using Xunit;

namespace AlleyCat.Tests.Models;

/// <summary>CORE-011-TAC-004 (TR13): the v1 policy in isolation.</summary>
public sealed class RoundRobinSchedulingPolicyTests
{
    /// <summary>Selections rotate across consumers and keep FIFO order within each consumer.</summary>
    [Fact]
    public void SelectNext_RotatesConsumers_FIFOWithinConsumer()
    {
        var policy = new RoundRobinSchedulingPolicy();
        var a = new ModelConsumerId("a");
        var b = new ModelConsumerId("b");
        var pending = new List<ModelWorkItem> { new(a), new(a), new(b), new(a), new(b) };
        var served = new List<ModelWorkItem>();
        List<ModelWorkItem> expected = [pending[0], pending[2], pending[1], pending[4], pending[3]];

        while (pending.Count > 0)
        {
            int index = policy.SelectNext(pending);
            served.Add(pending[index]);
            pending.RemoveAt(index);
        }

        Assert.Equal(expected, served);
    }

    /// <summary>A newly arriving consumer is served before a consumer that already had a turn.</summary>
    [Fact]
    public void SelectNext_PrefersConsumersNotYetServed()
    {
        var policy = new RoundRobinSchedulingPolicy();
        var veteran = new ModelConsumerId("veteran");
        var newcomer = new ModelConsumerId("newcomer");
        _ = policy.SelectNext([new ModelWorkItem(veteran)]);

        int index = policy.SelectNext([new ModelWorkItem(veteran), new ModelWorkItem(newcomer)]);

        Assert.Equal(1, index);
    }

    /// <summary>Fairness still holds after many released consumers exceed the pruning threshold.</summary>
    [Fact]
    public void SelectNext_RemainsFairAfterPruningReleasedConsumers()
    {
        var policy = new RoundRobinSchedulingPolicy();
        for (int i = 0; i < 100; i++)
        {
            _ = policy.SelectNext([new ModelWorkItem(new ModelConsumerId($"released-{i}"))]);
        }

        var a = new ModelConsumerId("a");
        var b = new ModelConsumerId("b");
        _ = policy.SelectNext([new ModelWorkItem(a)]);

        Assert.Equal(1, policy.SelectNext([new ModelWorkItem(a), new ModelWorkItem(b)]));
        Assert.Equal(0, policy.SelectNext([new ModelWorkItem(a), new ModelWorkItem(b)]));
    }
}
