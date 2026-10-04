using Xunit;

namespace AlleyCat.TestFramework.Tests;

/// <summary>
/// Fail-closed movement-diagnostics pairing contracts for the corner route's A4 criterion (CORNER-06): the
/// structural failure modes that used to print "A4: UNPROVED" and continue — no diagnostics at all, no
/// double-support ticks, a double-support tick with no paired sample, duplicate samples for one tick — must
/// surface as problems the fixture turns into test failures, while a complete pairing measures violations
/// exactly. Synthetic diagnostic sets only; no engine run.
/// </summary>
public sealed class MovementDiagnosticsPairingTests
{
    /// <summary>A complete, unique pairing reports no problems and measures the non-zero synthetic correction as a violation with its worst tick.</summary>
    [Fact]
    public void CompletePairing_HasNoProblemsAndMeasuresViolations()
    {
        MovementDiagnosticsPairing.DiagnosticSample[] samples =
        [
            new(10, 0f, 0f, 0f),
            new(11, 0.002f, -0.001f, 0.0005f),
            new(12, 0f, 0f, 0f),
        ];

        MovementDiagnosticsPairing.Evaluation evaluation =
            MovementDiagnosticsPairing.Evaluate(samples, [10UL, 11UL, 12UL]);

        Assert.Empty(evaluation.Problems);
        Assert.Equal(3, evaluation.DoubleSupportTicks);
        Assert.Equal(3, evaluation.PairedTicks);
        Assert.Equal(1, evaluation.Violations);
        Assert.Equal(11UL, evaluation.WorstTick);
        Assert.Equal(0.002f, evaluation.WorstSyntheticX);
        Assert.Equal(-0.001f, evaluation.WorstSyntheticY);
        Assert.Equal(0.0005f, evaluation.WorstSyntheticYaw);
    }

    /// <summary>A complete pairing of zero synthetic corrections reports zero violations: the guard passes non-vacuously.</summary>
    [Fact]
    public void CompleteZeroSyntheticPairing_PassesWithZeroViolations()
    {
        MovementDiagnosticsPairing.DiagnosticSample[] samples =
        [
            new(10, 0f, 0f, 0f),
            new(11, 0f, 0f, 0f),
        ];

        MovementDiagnosticsPairing.Evaluation evaluation =
            MovementDiagnosticsPairing.Evaluate(samples, [10UL, 11UL]);

        Assert.Empty(evaluation.Problems);
        Assert.Equal(2, evaluation.PairedTicks);
        Assert.Equal(0, evaluation.Violations);
    }

    /// <summary>An empty diagnostic set is a problem: A4 cannot be measured and must fail closed.</summary>
    [Fact]
    public void NoSamplesAtAll_IsAProblem()
    {
        MovementDiagnosticsPairing.Evaluation evaluation =
            MovementDiagnosticsPairing.Evaluate([], [10UL, 11UL]);

        Assert.Contains("the movement diagnostics exposed no samples", evaluation.Problems);
        Assert.Contains("double-support tick 10 has no paired movement sample", evaluation.Problems);
        Assert.Contains("zero double-support ticks were paired with movement diagnostics", evaluation.Problems);
        Assert.Equal(0, evaluation.PairedTicks);
    }

    /// <summary>A window with no independently measured double-support ticks is a problem.</summary>
    [Fact]
    public void NoDoubleSupportTicks_IsAProblem()
    {
        MovementDiagnosticsPairing.DiagnosticSample[] samples = [new(10, 0f, 0f, 0f)];

        MovementDiagnosticsPairing.Evaluation evaluation = MovementDiagnosticsPairing.Evaluate(samples, []);

        Assert.Contains("the observation window contained no independently measured double-support ticks", evaluation.Problems);
    }

    /// <summary>A double-support tick without a paired movement sample is a problem.</summary>
    [Fact]
    public void MissingSampleForADoubleSupportTick_IsAProblem()
    {
        MovementDiagnosticsPairing.DiagnosticSample[] samples =
        [
            new(10, 0f, 0f, 0f),
            new(12, 0f, 0f, 0f),
        ];

        MovementDiagnosticsPairing.Evaluation evaluation =
            MovementDiagnosticsPairing.Evaluate(samples, [10UL, 11UL, 12UL]);

        Assert.Contains("double-support tick 11 has no paired movement sample", evaluation.Problems);
        Assert.Equal(2, evaluation.PairedTicks);
        Assert.Equal(1, evaluation.DoubleSupportTicks - evaluation.PairedTicks);
    }

    /// <summary>Duplicate samples for one tick are a problem; the retained pairing still measures.</summary>
    [Fact]
    public void DuplicateSampleTicks_AreAProblem()
    {
        MovementDiagnosticsPairing.DiagnosticSample[] samples =
        [
            new(10, 0f, 0f, 0f),
            new(10, 0.001f, 0f, 0f),
            new(11, 0f, 0f, 0f),
        ];

        MovementDiagnosticsPairing.Evaluation evaluation =
            MovementDiagnosticsPairing.Evaluate(samples, [10UL, 11UL]);

        Assert.Contains("duplicate movement diagnostic samples for tick 10 (2 entries)", evaluation.Problems);
        // The pairing itself still measures over the retained samples.
        Assert.Equal(2, evaluation.PairedTicks);
    }

    /// <summary>Unpaired ticks beyond the listing bound are aggregated into one problem entry.</summary>
    [Fact]
    public void ManyUnpairedTicks_AreAggregatedBeyondTheListingBound()
    {
        MovementDiagnosticsPairing.DiagnosticSample[] samples = [new(1, 0f, 0f, 0f)];
        ulong[] doubleSupportTicks = [.. Enumerable.Range(1, MovementDiagnosticsPairing.MaximumListedProblems + 5).Select(tick => (ulong)tick)];

        MovementDiagnosticsPairing.Evaluation evaluation = MovementDiagnosticsPairing.Evaluate(samples, doubleSupportTicks);

        Assert.Equal(MovementDiagnosticsPairing.MaximumListedProblems,
            evaluation.Problems.Count(problem => problem.Contains("has no paired movement sample", StringComparison.Ordinal)));
        Assert.Contains("4 further double-support ticks have no paired movement sample", evaluation.Problems);
    }
}
