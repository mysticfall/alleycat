using AlleyCat.Body.Eyes;
using AlleyCat.Context;
using Xunit;

namespace AlleyCat.Tests.Body.Eyes;

/// <summary>
/// Unit coverage for visual-cue role contracts.
/// </summary>
public sealed class VisualCueContractTests
{
    /// <summary>Visual observer and subject roles aggregate their specified base contracts.</summary>
    [Fact]
    public void VisualRoles_AggregateRequiredContracts()
    {
        Assert.True(typeof(IEyesHolder).IsAssignableFrom(typeof(IVisualObserver)));
        Assert.True(typeof(IContextual).IsAssignableFrom(typeof(IVisualObserver)));
        Assert.True(typeof(IProvidesVisualCues).IsAssignableFrom(typeof(IVisualSubject)));
        Assert.True(typeof(IContextual).IsAssignableFrom(typeof(IVisualSubject)));
    }

}
