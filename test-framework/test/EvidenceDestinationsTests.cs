using Xunit;

namespace AlleyCat.TestFramework.Tests;

/// <summary>
/// Non-destructive evidence destination contracts (CORNER-06): a fresh or empty intended directory is used in
/// place; an occupied one is never overwritten — it redirects to a fresh numbered sibling carrying a
/// REDIRECTED-FROM note — unless the explicit allow-overwrite variable is set, which reuses the directory in
/// place and records an OVERWRITTEN note listing what is being replaced. Every case runs in its own scratch
/// tree so the per-process resolution cache cannot leak between cases.
/// </summary>
public sealed class EvidenceDestinationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"alleycat-evidence-tests-{Guid.NewGuid():N}");

    /// <summary>A non-existent intended directory is used in place, created empty, with no redirect or overwrite notes.</summary>
    [Fact]
    public void FreshDefault_WritesInPlaceWithoutNotes()
    {
        string intended = Path.Combine(_root, "run-01", "corner");

        string resolved = EvidenceDestinations.Resolve(intended);

        Assert.Equal(intended, resolved);
        Assert.True(Directory.Exists(resolved));
        Assert.Empty(Directory.GetFiles(resolved));
        Assert.False(File.Exists(Path.Combine(_root, "run-01-2", "corner", "REDIRECTED-FROM.txt")));
    }

    /// <summary>An existing but empty intended directory is used in place: nothing can be lost.</summary>
    [Fact]
    public void ExistingEmptyDirectory_WritesInPlace()
    {
        string intended = Path.Combine(_root, "run-01", "corner");
        _ = Directory.CreateDirectory(intended);

        string resolved = EvidenceDestinations.Resolve(intended);

        Assert.Equal(intended, resolved);
        Assert.Empty(Directory.GetFiles(resolved));
    }

    /// <summary>An occupied intended directory redirects to a numbered run sibling carrying a REDIRECTED-FROM note; the prior files are untouched.</summary>
    [Fact]
    public void OccupiedDefault_RedirectsToNumberedSiblingWithNote()
    {
        string intended = Path.Combine(_root, "run-01", "corner");
        _ = Directory.CreateDirectory(intended);
        File.WriteAllText(Path.Combine(intended, "corner-route-report.txt"), "prior evidence");

        string resolved = EvidenceDestinations.Resolve(intended);

        Assert.Equal(Path.Combine(_root, "run-01-2", "corner"), resolved);
        Assert.True(Directory.Exists(resolved));
        Assert.Equal("prior evidence", File.ReadAllText(Path.Combine(intended, "corner-route-report.txt")));
        string note = File.ReadAllText(Path.Combine(resolved, "REDIRECTED-FROM.txt"));
        Assert.Contains(intended, note);
        Assert.Contains(EvidenceDestinations.AllowOverwriteEnvironmentVariable, note);
    }

    /// <summary>A path without a run-NN segment numbers its final segment instead.</summary>
    [Fact]
    public void OccupiedDefaultWithoutRunSegment_NumbersTheLeafSegment()
    {
        string intended = Path.Combine(_root, "planner-oracle");
        _ = Directory.CreateDirectory(intended);
        File.WriteAllText(Path.Combine(intended, "Female-plans.txt"), "prior evidence");

        string resolved = EvidenceDestinations.Resolve(intended);

        Assert.Equal(Path.Combine(_root, "planner-oracle-2"), resolved);
        Assert.True(File.Exists(Path.Combine(resolved, "REDIRECTED-FROM.txt")));
    }

    /// <summary>When the first sibling is occupied too, the next number is used.</summary>
    [Fact]
    public void OccupiedSibling_IncrementsFurther()
    {
        string intended = Path.Combine(_root, "run-01", "baseline");
        _ = Directory.CreateDirectory(intended);
        File.WriteAllText(Path.Combine(intended, "terminal-seed-report.txt"), "prior evidence");
        _ = Directory.CreateDirectory(Path.Combine(_root, "run-01-2", "baseline"));
        File.WriteAllText(Path.Combine(_root, "run-01-2", "baseline", "terminal-seed-report.txt"), "other evidence");

        string resolved = EvidenceDestinations.Resolve(intended);

        Assert.Equal(Path.Combine(_root, "run-01-3", "baseline"), resolved);
        Assert.True(File.Exists(Path.Combine(resolved, "REDIRECTED-FROM.txt")));
    }

    /// <summary>An occupied explicitly-selected destination flows through the same redirect rather than overwriting.</summary>
    [Fact]
    public void OccupiedExplicitPath_RedirectsLikeTheDefault()
    {
        // Env-selected paths flow through the same resolution, so an occupied explicit destination redirects
        // rather than overwrites.
        string intended = Path.Combine(_root, "run-01", "env-selected");
        _ = Directory.CreateDirectory(intended);
        File.WriteAllText(Path.Combine(intended, "report.txt"), "prior evidence");

        string resolved = EvidenceDestinations.Resolve(intended, allowOverwrite: null);

        Assert.Equal(Path.Combine(_root, "run-01-2", "env-selected"), resolved);
        Assert.True(File.Exists(Path.Combine(resolved, "REDIRECTED-FROM.txt")));
        Assert.Equal("prior evidence", File.ReadAllText(Path.Combine(intended, "report.txt")));
    }

    /// <summary>With the allow-overwrite value set, the destination is reused in place and an OVERWRITTEN note lists the replaced files.</summary>
    [Fact]
    public void AllowOverwrite_ReusesInPlaceAndRecordsWhatWasReplaced()
    {
        string intended = Path.Combine(_root, "run-01", "corner");
        _ = Directory.CreateDirectory(intended);
        File.WriteAllText(Path.Combine(intended, "corner-route-report.txt"), "prior evidence");

        string resolved = EvidenceDestinations.Resolve(intended, allowOverwrite: "1");

        Assert.Equal(intended, resolved);
        Assert.Equal("prior evidence", File.ReadAllText(Path.Combine(intended, "corner-route-report.txt")));
        string[] notes = Directory.GetFiles(intended, "OVERWRITTEN-*.txt");
        _ = Assert.Single(notes);
        Assert.Contains("corner-route-report.txt", File.ReadAllText(notes[0]));
        Assert.False(File.Exists(Path.Combine(intended, "REDIRECTED-FROM.txt")));
    }

    /// <summary>Resolving the same intended path twice in one process yields one destination, so multi-writer fixtures never redirect behind each other.</summary>
    [Fact]
    public void Resolution_IsIdempotentPerIntendedPath()
    {
        string intended = Path.Combine(_root, "run-01", "corner");
        _ = Directory.CreateDirectory(intended);
        File.WriteAllText(Path.Combine(intended, "corner-route-report.txt"), "prior evidence");

        string first = EvidenceDestinations.Resolve(intended);
        File.WriteAllText(Path.Combine(first, "new-evidence.txt"), "this run");
        string second = EvidenceDestinations.Resolve(intended);

        Assert.Equal(first, second);
        Assert.Equal(2, Directory.GetFiles(first).Length);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone.
        }
    }
}
