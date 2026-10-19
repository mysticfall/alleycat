using AlleyCat.TestFramework;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.Attention;

/// <summary>
/// TEST-6 synthetic boundary replays for the corrected full-freeze/designed-dwell detector
/// (<see cref="FullFreezeDetector"/>): a genuine designed dwell is excluded, while a sustained transit
/// dwell, a late clear measured from the actual assignment, a head not at its pre-assignment pose, and
/// a window whose first sample already has residual ≤2° are handled with the corrected semantics. The
/// pre-correction (audited) exclusion decision is reproduced evidence-only to demonstrate where the old
/// semantics were wrong. No rendering is involved.
/// </summary>
[Headless]
public sealed class DwellExclusionBoundaryReplaysTests
{
    private const double ViolationBoundSeconds = 0.15;
    private const double PreAssignmentYaw = 10.0;
    private static readonly FreezeSample _preAssignmentPose = new(0d, PreAssignmentYaw, 0d, 0d, 5d, 0.3d);

    /// <summary>
    /// A genuine designed dwell — cleared within 0.6 s of the actual assignment while the head held the
    /// pre-assignment pose — is excluded; the same window without the exclusion is a ≥150 ms violation,
    /// proving the accept happens for the intended reason.
    /// </summary>
    [Fact]
    public void GenuineDesignedDwell_IsExcluded()
    {
        List<FreezeSample> series = [];
        AppendRun(series, 0, 0, PreAssignmentYaw, residual: 5, influence: 0.3);
        AppendRun(series, 1, 30, PreAssignmentYaw, residual: 5, influence: 1.0);

        double excluded = FullFreezeDetector.FindLongestFullFreezeSeconds(
            series,
            new DwellExclusion(AssignmentTimeSeconds: 0d, _preAssignmentPose, ClearedOrReplacedTimeSeconds: 0.3d));
        double withoutExclusion = FullFreezeDetector.FindLongestFullFreezeSeconds(series, exclusion: null);

        Assert.True(excluded < ViolationBoundSeconds, $"A designed dwell must be excluded, found {excluded:F3} s.");
        Assert.True(withoutExclusion >= ViolationBoundSeconds,
            $"The same window without the exclusion must be a violation, found {withoutExclusion:F3} s.");
        Assert.Equal(0.29d, withoutExclusion, 3);
    }

    /// <summary>
    /// A sustained transit target (never cleared or replaced) never qualifies for the exclusion, exactly
    /// as the acceptance path wires it — the dwell is reported.
    /// </summary>
    [Fact]
    public void SustainedTransitDwell_IsReported()
    {
        List<FreezeSample> series = [];
        AppendRun(series, 0, 0, PreAssignmentYaw, residual: 5, influence: 0.3);
        AppendRun(series, 1, 30, PreAssignmentYaw, residual: 5, influence: 1.0);

        double reported = FullFreezeDetector.FindLongestFullFreezeSeconds(
            series,
            new DwellExclusion(AssignmentTimeSeconds: 0d, _preAssignmentPose, ClearedOrReplacedTimeSeconds: null));

        Assert.True(reported >= ViolationBoundSeconds,
            $"A sustained transit dwell must be reported, found {reported:F3} s.");
        Assert.Equal(0.29d, reported, 3);
    }

    /// <summary>
    /// A clear 0.7 s after the assignment is beyond the 0.6 s deadline MEASURED FROM THE ACTUAL
    /// ASSIGNMENT, so the dwell is reported. The pre-correction semantics measured from the
    /// freeze-window start (0.7 − 0.1 = 0.6 ≤ 0.6) and wrongly excluded exactly this case.
    /// </summary>
    [Fact]
    public void LateClear_BeyondDeadlineFromAssignment_IsReported()
    {
        List<FreezeSample> series = [];
        AppendRun(series, 0, 9, PreAssignmentYaw, residual: 1.5, influence: 1.0);
        AppendRun(series, 10, 30, PreAssignmentYaw, residual: 5, influence: 1.0);

        double reported = FullFreezeDetector.FindLongestFullFreezeSeconds(
            series,
            new DwellExclusion(AssignmentTimeSeconds: 0d, _preAssignmentPose, ClearedOrReplacedTimeSeconds: 0.7d));

        Assert.True(reported >= ViolationBoundSeconds,
            $"A clear beyond 0.6 s from the assignment must be reported, found {reported:F3} s.");
        Assert.Equal(0.2d, reported, 3);

        // Evidence-only: the audited pre-correction exclusion (window-start timing, prior-sample
        // reference) wrongly accepts exactly this case.
        Assert.True(
            LegacyDesignedDwellExclusion(series, from: 10, to: 30, replacedAt: 0.7d),
            "The pre-correction semantics must demonstrably exclude (wrongly accept) this late clear.");
    }

    /// <summary>
    /// A head that left its pre-assignment pose (0.2° &gt; 0.05° tolerance) does not hold the
    /// pre-assignment pose, so the dwell is reported. The pre-correction semantics compared against the
    /// sample preceding the window — which sat at the moved pose — and wrongly excluded exactly this case.
    /// </summary>
    [Fact]
    public void MovedHead_NotAtPreAssignmentPose_IsReported()
    {
        List<FreezeSample> series = [];
        AppendRun(series, 0, 4, PreAssignmentYaw, residual: 1.5, influence: 1.0);
        AppendRun(series, 5, 8, PreAssignmentYaw + 0.2, residual: 1.5, influence: 1.0);
        AppendRun(series, 9, 29, PreAssignmentYaw + 0.2, residual: 5, influence: 1.0);

        double reported = FullFreezeDetector.FindLongestFullFreezeSeconds(
            series,
            new DwellExclusion(AssignmentTimeSeconds: 0d, _preAssignmentPose, ClearedOrReplacedTimeSeconds: 0.3d));

        Assert.True(reported >= ViolationBoundSeconds,
            $"A dwell with the head away from its pre-assignment pose must be reported, found {reported:F3} s.");
        Assert.Equal(0.2d, reported, 3);

        Assert.True(
            LegacyDesignedDwellExclusion(series, from: 9, to: 29, replacedAt: 0.3d),
            "The pre-correction semantics must demonstrably exclude (wrongly accept) this moved head.");
    }

    /// <summary>
    /// A window whose FIRST sample already has residual ≤2° is not a violation candidate: the corrected
    /// detector measures only the qualifying sub-window (0.14 s &lt; 0.15 s), where the pre-correction
    /// scan — which never checked the first sample's residual — measured 0.15 s and would have flagged.
    /// </summary>
    [Fact]
    public void FirstSampleResidualAtOrBelowTwo_IsNotAViolationCandidate()
    {
        List<FreezeSample> series = [];
        series.Add(new FreezeSample(0d, 12d, 0d, 0d, Residual: 1.5, Influence: 1.0));
        AppendRun(series, 1, 15, yaw: 12, residual: 5, influence: 1.0);

        double reported = FullFreezeDetector.FindLongestFullFreezeSeconds(series, exclusion: null);

        Assert.True(reported < ViolationBoundSeconds,
            $"A window starting at residual <=2° must not reach the 0.15 s bound, found {reported:F3} s.");
        Assert.Equal(0.14d, reported, 3);
    }

    /// <summary>
    /// Boundary values: a clear exactly 0.6 s after the assignment is within the deadline (excluded),
    /// and a head held exactly at the 0.05° freeze tolerance still counts as holding the pre-assignment
    /// pose — while 0.06° does not.
    /// </summary>
    [Fact]
    public void Boundaries_ExactlyAtDeadlineAndTolerance_AreExcluded()
    {
        List<FreezeSample> lateSeries = [];
        AppendRun(lateSeries, 0, 9, PreAssignmentYaw, residual: 1.5, influence: 1.0);
        AppendRun(lateSeries, 10, 30, PreAssignmentYaw, residual: 5, influence: 1.0);

        double exactlyAtDeadline = FullFreezeDetector.FindLongestFullFreezeSeconds(
            lateSeries,
            new DwellExclusion(AssignmentTimeSeconds: 0d, _preAssignmentPose, ClearedOrReplacedTimeSeconds: 0.6d));
        Assert.True(exactlyAtDeadline < ViolationBoundSeconds,
            $"A clear exactly 0.6 s from the assignment is within the deadline, found {exactlyAtDeadline:F3} s.");

        List<FreezeSample> heldSeries = [];
        // The pre-assignment pose anchors at yaw 0 so the 0.05/0.06 offsets compare bit-exactly
        // against the tolerance constant (10.05 − 10.0 is not exactly 0.05 in binary).
        FreezeSample preAssignmentAtZero = new(0d, 0d, 0d, 0d, 5d, 0.3d);
        AppendRun(heldSeries, 0, 0, 0.05, residual: 5, influence: 0.3);
        AppendRun(heldSeries, 1, 25, 0.05, residual: 5, influence: 1.0);
        DwellExclusion heldExclusion =
            new(AssignmentTimeSeconds: 0d, preAssignmentAtZero, ClearedOrReplacedTimeSeconds: 0.3d);

        double exactlyAtTolerance = FullFreezeDetector.FindLongestFullFreezeSeconds(heldSeries, heldExclusion);
        Assert.True(exactlyAtTolerance < ViolationBoundSeconds,
            $"A head exactly at the 0.05° tolerance still holds the pre-assignment pose, found "
            + $"{exactlyAtTolerance:F3} s.");

        List<FreezeSample> beyondTolerance = [];
        AppendRun(beyondTolerance, 0, 0, 0.06, residual: 5, influence: 0.3);
        AppendRun(beyondTolerance, 1, 25, 0.06, residual: 5, influence: 1.0);

        double reported = FullFreezeDetector.FindLongestFullFreezeSeconds(beyondTolerance, heldExclusion);
        Assert.True(reported >= ViolationBoundSeconds,
            $"A head 0.06° off the pre-assignment pose does not hold it, found {reported:F3} s.");
    }

    /// <summary>
    /// Appends samples at a 10 ms cadence from <paramref name="fromTenths"/> to
    /// <paramref name="toTenths"/> inclusive (tenths of a second × 10 ms units).
    /// </summary>
    private static void AppendRun(
        List<FreezeSample> series,
        int fromTenths,
        int toTenths,
        double yaw,
        double residual,
        double influence)
    {
        for (int t = fromTenths; t <= toTenths; t++)
        {
            series.Add(new FreezeSample(t * 0.01, yaw, 0d, 0d, residual, influence));
        }
    }

    /// <summary>
    /// Builds a designed saturation park around the sealed-run geometry: the head converges to the
    /// commanded saturation pose (yaw ≈22.69°), parks holding the anchor on the 15° cone boundary, and
    /// centring engages at assignment + 0.6 s.
    /// </summary>
    private static (List<FreezeSample> Series, SaturationParkExclusion Park) BuildParkSeries(
        double commandedYaw = 22.69,
        double parkResidual = 15.02,
        double eligibilityTenths = 60)
    {
        List<FreezeSample> series = [];
        // Approach: residual sweeps down towards the cone boundary while the head moves (no freeze).
        for (int t = 0; t <= 20; t++)
        {
            double yaw = commandedYaw * t / 20d;
            series.Add(new FreezeSample(t * 0.01, yaw, 0d, 0d, 15.02 + ((20 - t) * 0.25), 1.0));
        }

        // The park: commanded pose held, residual on the cone boundary, until eligibility.
        for (int t = 21; t <= eligibilityTenths; t++)
        {
            series.Add(new FreezeSample(t * 0.01, commandedYaw, 0d, 0d, parkResidual, 1.0));
        }

        // Centring engages: the head moves and the residual falls (no freeze).
        for (int t = (int)(eligibilityTenths + 1); t <= eligibilityTenths + 15; t++)
        {
            series.Add(new FreezeSample(
                t * 0.01,
                commandedYaw + ((t - eligibilityTenths) * 1.2d),
                0d,
                0d,
                parkResidual - ((t - eligibilityTenths) * 0.8d),
                1.0));
        }

        SaturationParkExclusion park = new(
            CommandedYawDegrees: commandedYaw,
            CommandedPitchDegrees: 0d,
            GeometricResidualDegrees: 15d,
            CentringEligibilityTimeSeconds: eligibilityTenths * 0.01);
        return (series, park);
    }

    /// <summary>
    /// A genuine saturation park is excluded while every other criterion is green: the exclusion must be
    /// what accepts it (the same window without the park context is a ≥150 ms violation).
    /// </summary>
    [Fact]
    public void SaturationPark_IsExcludedForTheRightReason()
    {
        (List<FreezeSample> series, SaturationParkExclusion park) = BuildParkSeries();

        double excluded = FullFreezeDetector.FindLongestFullFreezeSeconds(series, parkExclusion: park);
        double withoutExclusion = FullFreezeDetector.FindLongestFullFreezeSeconds(series);

        Assert.True(
            excluded < ViolationBoundSeconds,
            $"A designed saturation park must be excluded, found {excluded:F3} s.");
        Assert.True(
            withoutExclusion >= ViolationBoundSeconds,
            $"The same window without the park context must be a violation, found {withoutExclusion:F3} s.");
        Assert.Equal(0.40d, withoutExclusion, 3);
    }

    /// <summary>
    /// Dropping each authorised condition individually must report the window: a mid-transit freeze
    /// (park pose absent), a residual off the cone boundary, or a dwell over-running centring eligibility.
    /// </summary>
    [Fact]
    public void SaturationPark_EachConditionIsLoadBearing()
    {
        // Condition 1 — the commanded pose: freeze at a pose the park never commanded.
        (_, SaturationParkExclusion park) = BuildParkSeries();
        List<FreezeSample> wrongPose = [];
        AppendRun(wrongPose, 0, 30, yaw: 5d, residual: 15.02, influence: 1.0);
        Assert.True(
            FullFreezeDetector.FindLongestFullFreezeSeconds(wrongPose, parkExclusion: park) >= ViolationBoundSeconds,
            "A freeze at a pose the park never commanded must be reported (condition 1).");

        // Condition 2 — the geometric residual: the same commanded pose but the residual far off the cone
        // boundary is not the park's held geometry.
        List<FreezeSample> wrongResidual = [];
        AppendRun(wrongResidual, 0, 30, yaw: 22.69, residual: 5d, influence: 1.0);
        Assert.True(
            FullFreezeDetector.FindLongestFullFreezeSeconds(wrongResidual, parkExclusion: park) >= ViolationBoundSeconds,
            "A freeze holding a residual far off the cone boundary must be reported (condition 2).");

        // Condition 3 — centring eligibility: the dwell continues past eligibility into genuine freeze.
        List<FreezeSample> overRun = [];
        AppendRun(overRun, 0, 20, yaw: 22.69, residual: 15.02, influence: 1.0);
        SaturationParkExclusion earlyEligibility = park with
        {
            CentringEligibilityTimeSeconds = 0.1d
        };
        Assert.True(
            FullFreezeDetector.FindLongestFullFreezeSeconds(overRun, parkExclusion: earlyEligibility)
                >= ViolationBoundSeconds,
            "A dwell over-running centring eligibility into genuine freeze must be reported (condition 3).");
    }

    /// <summary>
    /// Sustained transit never qualifies: a long freeze with the park context present but the transit
    /// conditions absent is reported in full.
    /// </summary>
    [Fact]
    public void SaturationPark_SustainedTransitNeverExcluded()
    {
        (_, SaturationParkExclusion park) = BuildParkSeries();
        List<FreezeSample> sustained = [];
        AppendRun(sustained, 0, 50, yaw: 10d, residual: 20d, influence: 1.0);
        double reported = FullFreezeDetector.FindLongestFullFreezeSeconds(sustained, parkExclusion: park);
        Assert.True(
            reported >= ViolationBoundSeconds,
            $"Sustained transit must never be excluded, found {reported:F3} s.");
        Assert.Equal(0.50d, reported, 3);
    }

    /// <summary>
    /// Boundary values: a park just within every tolerance is excluded; a residual just outside the
    /// stated 0.5° tolerance and a pose just beyond the 0.05° freeze tolerance are reported.
    /// </summary>
    [Fact]
    public void SaturationPark_Boundaries()
    {
        (List<FreezeSample> withinSeries, SaturationParkExclusion park) = BuildParkSeries(parkResidual: 15.49);
        Assert.True(
            FullFreezeDetector.FindLongestFullFreezeSeconds(withinSeries, parkExclusion: park) < ViolationBoundSeconds,
            "A park holding the residual just inside the stated tolerance is excluded.");

        (List<FreezeSample> outsideSeries, _) = BuildParkSeries(parkResidual: 15.51);
        Assert.True(
            FullFreezeDetector.FindLongestFullFreezeSeconds(outsideSeries, parkExclusion: park)
                >= ViolationBoundSeconds,
            "A residual just outside the stated tolerance must be reported.");

        (List<FreezeSample> poseSeries, SaturationParkExclusion posePark) = BuildParkSeries();
        SaturationParkExclusion offsetCommand = posePark with
        {
            CommandedYawDegrees = posePark.CommandedYawDegrees - 0.06
        };
        Assert.True(
            FullFreezeDetector.FindLongestFullFreezeSeconds(poseSeries, parkExclusion: offsetCommand)
                >= ViolationBoundSeconds,
            "A park pose just beyond the freeze tolerance of the commanded target must be reported.");
    }

    /// <summary>
    /// EVIDENCE ONLY — reproduces the audited pre-correction dwell-exclusion decision (deadline measured
    /// from the freeze-window start; head-hold reference = the sample preceding the window) so the
    /// replays can demonstrate empirically that the old semantics wrongly accepted the late-clear and
    /// moved-head cases. Never used by acceptance paths.
    /// </summary>
    private static bool LegacyDesignedDwellExclusion(
        IReadOnlyList<FreezeSample> series,
        int from,
        int to,
        double replacedAt)
    {
        if (replacedAt - series[from].TimeSeconds > FullFreezeDetector.DesignedDwellDeadlineSeconds)
        {
            return false;
        }

        FreezeSample priorWindowSample = series[from - 1];
        for (int i = from; i <= to; i++)
        {
            if (Math.Abs(series[i].Yaw - priorWindowSample.Yaw) > FullFreezeDetector.FreezeToleranceDegrees
                || Math.Abs(series[i].Pitch - priorWindowSample.Pitch) > FullFreezeDetector.FreezeToleranceDegrees
                || Math.Abs(series[i].Roll - priorWindowSample.Roll) > FullFreezeDetector.FreezeToleranceDegrees)
            {
                return false;
            }
        }

        return true;
    }
}
