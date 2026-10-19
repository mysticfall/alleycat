namespace AlleyCat.IntegrationTests.Mind.Attention;

/// <summary>
/// One post-draw freeze observation for the full-freeze detector: face pose, face-to-anchor residual
/// and commanded influence at one sample time.
/// </summary>
internal readonly record struct FreezeSample(
    double TimeSeconds,
    double Yaw,
    double Pitch,
    double Roll,
    double Residual,
    double Influence);

/// <summary>
/// The assignment-anchored designed-dwell exclusion context (TEST-6 corrected semantics). The
/// pre-assignment pose is the pose captured at or before the actual assignment event — never a pose
/// read from the freeze window's vicinity.
/// </summary>
/// <param name="AssignmentTimeSeconds">The exact time of the actual target assignment event.</param>
/// <param name="PreAssignmentPose">The face pose at/before the assignment event.</param>
/// <param name="ClearedOrReplacedTimeSeconds">The time the target was cleared or replaced, if it was;
/// a sustained transit target passes <see langword="null"/> and never qualifies for the exclusion.</param>
internal sealed record DwellExclusion(
    double AssignmentTimeSeconds,
    FreezeSample PreAssignmentPose,
    double? ClearedOrReplacedTimeSeconds);

/// <summary>
/// The user-authorised saturation-park dwell exclusion (E1 resolution, 2026-10-13). A contiguous freeze
/// window qualifies as a designed saturation park ONLY IF all three conditions hold: the head is within
/// freeze tolerance of the COMMANDED saturation pose (the stillness is the commanded park, not
/// incidental motionlessness); the window's face-to-anchor residual equals the geometric saturation
/// residual within the stated tolerance (the cone boundary the park holds); and the window ends no later
/// than centring eligibility (assignment + the authored centring delay) plus the stated time tolerance —
/// a dwell continuing past eligibility into genuine freeze is never excluded. Sustained transit is never
/// excluded. All limits (150 ms, 0.05°, >2°, influence >0.5) are unchanged.
/// </summary>
/// <param name="CommandedYawDegrees">The commanded saturation target face yaw, in the freeze samples'
/// measurement frame.</param>
/// <param name="CommandedPitchDegrees">The commanded saturation target face pitch, same frame.</param>
/// <param name="GeometricResidualDegrees">The geometric saturation residual the park holds (the cone
/// boundary the saturation command places the anchor on).</param>
/// <param name="CentringEligibilityTimeSeconds">Assignment time plus the authored centring delay, as the
/// policy defines eligibility.</param>
/// <param name="ResidualToleranceDegrees">Stated tolerance for the residual condition (default 0.5°).</param>
/// <param name="EligibilityToleranceSeconds">Stated tolerance for the eligibility condition
/// (default 0.05 s, three frames at 60 fps).</param>
internal sealed record SaturationParkExclusion(
    double CommandedYawDegrees,
    double CommandedPitchDegrees,
    double GeometricResidualDegrees,
    double CentringEligibilityTimeSeconds,
    double ResidualToleranceDegrees = 0.5,
    double EligibilityToleranceSeconds = 0.05);

/// <summary>
/// The approved full-freeze detector (2026-10-12 limits, frozen): the longest contiguous window, within
/// one active-engagement run (influence &gt;0.5), where the face yaw/pitch/roll ranges are each at most
/// 0.05° and the face-to-anchor residual stays above 2° throughout.
/// </summary>
/// <remarks>
/// <para>
/// TEST-6 corrections to the designed-dwell exclusion (limits unchanged): the clear/replacement time is
/// measured from the ACTUAL ASSIGNMENT event, never the freeze-window start; the head-hold reference is
/// the PRE-ASSIGNMENT pose from the exclusion context, never the sample preceding the window; and a
/// candidate window must hold residual &gt;2° from its FIRST sample — a window whose first sample already
/// has residual ≤2° is not a violation candidate.
/// </para>
/// <para>
/// A window counts as a designed glance dwell only when the target was cleared/replaced within 0.6 s of
/// the assignment AND the head held its pre-assignment pose within the 0.05° freeze tolerance for the
/// whole interval. A sustained transit target never qualifies.
/// </para>
/// </remarks>
internal static class FullFreezeDetector
{
    public const double FreezeToleranceDegrees = 0.05;
    public const double ActiveResidualDegrees = 2.0;
    public const double ActiveInfluence = 0.5;
    public const double DesignedDwellDeadlineSeconds = 0.6;

    /// <summary>Returns the longest full-freeze duration in seconds after designed-dwell exclusions.</summary>
    public static double FindLongestFullFreezeSeconds(
        IReadOnlyList<FreezeSample> series,
        DwellExclusion? exclusion = null,
        SaturationParkExclusion? parkExclusion = null,
        IReadOnlyList<RestAtLimitExclusion.Context?>? restContexts = null)
    {
        // Distinct TR23 rest-at-limit exception, evaluated independently per sample. A missing or
        // mis-sized context list fails closed. Segment at eligibility changes: never bridge across an
        // excluded interval or discard a non-eligible approach/invalidation part of the raw freeze.
        bool[] resting = new bool[series.Count];
        if (restContexts?.Count == series.Count)
        {
            for (int i = 0; i < series.Count; i++)
            {
                resting[i] = RestAtLimitExclusion.Evaluate(series[i], restContexts[i]).Eligible;
            }
        }

        double longest = 0;
        int start = 0;
        while (start < series.Count)
        {
            if (series[start].Influence <= ActiveInfluence || resting[start])
            {
                start++;
                continue;
            }

            int end = start;
            while (end + 1 < series.Count && series[end + 1].Influence > ActiveInfluence && !resting[end + 1])
            {
                end++;
            }

            for (int i = start; i <= end; i++)
            {
                // TEST-6 correction: the residual >2° condition holds THROUGHOUT the window, so a
                // window whose first sample already has residual ≤2° is not a violation candidate.
                if (series[i].Residual <= ActiveResidualDegrees)
                {
                    continue;
                }

                double yawMin = series[i].Yaw, yawMax = series[i].Yaw;
                double pitchMin = series[i].Pitch, pitchMax = series[i].Pitch;
                double rollMin = series[i].Roll, rollMax = series[i].Roll;
                for (int j = i + 1; j <= end; j++)
                {
                    FreezeSample current = series[j];
                    if (current.Residual <= ActiveResidualDegrees)
                    {
                        break;
                    }

                    yawMin = Math.Min(yawMin, current.Yaw);
                    yawMax = Math.Max(yawMax, current.Yaw);
                    pitchMin = Math.Min(pitchMin, current.Pitch);
                    pitchMax = Math.Max(pitchMax, current.Pitch);
                    rollMin = Math.Min(rollMin, current.Roll);
                    rollMax = Math.Max(rollMax, current.Roll);
                    if (yawMax - yawMin > FreezeToleranceDegrees
                        || pitchMax - pitchMin > FreezeToleranceDegrees
                        || rollMax - rollMin > FreezeToleranceDegrees)
                    {
                        break;
                    }

                    double duration = current.TimeSeconds - series[i].TimeSeconds;
                    if (duration > longest
                        && !IsDesignedGlanceDwell(series, i, j, exclusion)
                        && !IsDesignedSaturationPark(series, i, j, parkExclusion))
                    {
                        longest = duration;
                    }
                }
            }

            start = end + 1;
        }

        return longest;
    }

    /// <summary>
    /// Designed glance dwell exclusion with the TEST-6 corrected semantics: the clear/replacement
    /// deadline runs from the actual assignment event, and the head-hold reference is the
    /// pre-assignment pose from the exclusion context.
    /// </summary>
    private static bool IsDesignedGlanceDwell(
        IReadOnlyList<FreezeSample> series,
        int from,
        int to,
        DwellExclusion? exclusion)
    {
        if (exclusion is not { ClearedOrReplacedTimeSeconds: { } replacedAt }
            // Measured from the ACTUAL ASSIGNMENT, never the freeze-window start.
            || replacedAt - exclusion.AssignmentTimeSeconds > DesignedDwellDeadlineSeconds)
        {
            return false;
        }

        FreezeSample preAssignment = exclusion.PreAssignmentPose;
        for (int i = from; i <= to; i++)
        {
            if (Math.Abs(series[i].Yaw - preAssignment.Yaw) > FreezeToleranceDegrees
                || Math.Abs(series[i].Pitch - preAssignment.Pitch) > FreezeToleranceDegrees
                || Math.Abs(series[i].Roll - preAssignment.Roll) > FreezeToleranceDegrees)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The saturation-park exclusion (E1 resolution): all three user-authorised conditions must hold for
    /// the whole window — within freeze tolerance of the commanded saturation pose; the residual equals
    /// the geometric saturation residual within the stated tolerance; and the window ends no later than
    /// centring eligibility plus the stated time tolerance (a dwell over-running eligibility into genuine
    /// freeze is never excluded; windows entirely before eligibility are sub-windows of the park).
    /// </summary>
    private static bool IsDesignedSaturationPark(
        IReadOnlyList<FreezeSample> series,
        int from,
        int to,
        SaturationParkExclusion? park)
    {
        if (park is null)
        {
            return false;
        }

        for (int i = from; i <= to; i++)
        {
            if (Math.Abs(series[i].Yaw - park.CommandedYawDegrees) > FreezeToleranceDegrees
                || Math.Abs(series[i].Pitch - park.CommandedPitchDegrees) > FreezeToleranceDegrees
                || Math.Abs(series[i].Residual - park.GeometricResidualDegrees) > park.ResidualToleranceDegrees)
            {
                return false;
            }
        }

        return series[to].TimeSeconds <= park.CentringEligibilityTimeSeconds + park.EligibilityToleranceSeconds;
    }
}
