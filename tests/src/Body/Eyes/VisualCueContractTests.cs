using AlleyCat.Body.Eyes;
using AlleyCat.Core;
using Xunit;

namespace AlleyCat.Tests.Body.Eyes;

/// <summary>
/// Unit coverage for visual-cue role contracts.
/// </summary>
public sealed class VisualCueContractTests
{
    /// <summary>Visual subjects aggregate their specified ownership and identity contracts.</summary>
    [Fact]
    public void VisualSubject_AggregatesRequiredContracts()
    {
        Assert.True(typeof(IProvidesVisualCues).IsAssignableFrom(typeof(IVisualSubject)));
        Assert.True(typeof(IIdentifiable).IsAssignableFrom(typeof(IVisualSubject)));
    }

    /// <summary>Eyes expose synchronous visual scanning and scan results expose immutable read-only contracts.</summary>
    [Fact]
    public void VisualScanning_ExposesRequiredContracts()
    {
        Assert.Null(typeof(IEyes).GetMethod("Scan"));
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
        public string Id { get; set; } = "test_subject";

        public string Type => "test";

        public IReadOnlyList<VisualCue> VisualCues => [];
    }
}
