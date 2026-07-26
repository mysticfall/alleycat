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

    /// <summary>Eyes expose synchronous visual scanning and scan results expose immutable read-only contracts.</summary>
    [Fact]
    public void VisualScanning_ExposesRequiredContracts()
    {
        Assert.Equal(typeof(IReadOnlyList<VisualScanResult>), typeof(IEyes).GetMethod(nameof(IEyes.Scan))?.ReturnType);
        Assert.NotNull(typeof(VisualScanResult).GetProperty(nameof(VisualScanResult.Subject))?.GetMethod);
        Assert.Null(typeof(VisualScanResult).GetProperty(nameof(VisualScanResult.Subject))?.SetMethod);
        Assert.Null(typeof(VisualScanResult).GetProperty(nameof(VisualScanResult.VisibleCues))?.SetMethod);
    }

    /// <summary>Scan results require an actual visible cue and snapshot their supplied cue membership.</summary>
    [Fact]
    public void VisualScanResult_RequiresNonEmptyCueListAndSnapshotsIt()
    {
        var subject = new TestVisualSubject();
        VisualCue firstCue = null!;
        var suppliedCues = new List<VisualCue> { firstCue };

        VisualScanResult result = new(subject, suppliedCues);
        suppliedCues.Clear();

        Assert.Same(firstCue, Assert.Single(result.VisibleCues));
        _ = Assert.Throws<ArgumentException>(() => new VisualScanResult(subject, []));
        _ = Assert.Throws<NotSupportedException>(() => ((IList<VisualCue>)result.VisibleCues).Add(null!));
    }

    private sealed class TestVisualSubject : IVisualSubject
    {
        public IReadOnlyList<VisualCue> VisualCues => [];

        public IReadOnlyDictionary<string, object?> GetContext(Scene.ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();
    }
}
