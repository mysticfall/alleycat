using System.Collections.Immutable;
using System.Globalization;

namespace AlleyCat.TestFramework;

/// <summary>
/// Fail-closed evaluation of the navigation movement-diagnostics pairing behind the corner route's A4 criterion
/// (CORNER-06). A4 is asserted from the navigation component's value-only per-tick movement samples paired with
/// independently measured double-support ticks; the pairing must be complete and unique before the zero-synthetic
/// assertion means anything. Every structural defect — no diagnostics at all, no double-support ticks, duplicate
/// samples for one tick, or a double-support tick with no paired sample — is reported as a problem the caller
/// must turn into a test failure (only an explicit pre-diagnostics baseline exemption may downgrade them to
/// report-only). Pure arithmetic: no engine types, so the failure modes are unit-testable without an engine run.
/// </summary>
public static class MovementDiagnosticsPairing
{
    /// <summary>One navigation movement diagnostic sample as the A4 pairing consumes it: the tick it was
    /// recorded on and the synthetic (applied correction) planar vector and yaw, in metres and radians.</summary>
    /// <param name="Tick">The physics tick the sample was recorded on.</param>
    /// <param name="SyntheticX">The applied synthetic planar X, in metres.</param>
    /// <param name="SyntheticY">The applied synthetic planar Y, in metres.</param>
    /// <param name="SyntheticYaw">The applied synthetic yaw, in radians.</param>
    public readonly record struct DiagnosticSample(ulong Tick, float SyntheticX, float SyntheticY, float SyntheticYaw);

    /// <summary>The fail-closed pairing evaluation of one observation window.</summary>
    /// <param name="DoubleSupportTicks">How many independently measured double-support ticks the window contains.</param>
    /// <param name="PairedTicks">How many of those paired with a movement diagnostic sample.</param>
    /// <param name="Violations">Paired double-support ticks whose synthetic correction was non-zero.</param>
    /// <param name="WorstTick">The tick of the worst violation by planar magnitude (then yaw magnitude).</param>
    /// <param name="WorstSyntheticX">The worst violation's synthetic planar X.</param>
    /// <param name="WorstSyntheticY">The worst violation's synthetic planar Y.</param>
    /// <param name="WorstSyntheticYaw">The worst violation's synthetic yaw.</param>
    /// <param name="Problems">Structural defects that make the measurement incomplete; non-empty fails A4
    /// unless an explicit pre-diagnostics baseline exemption is in force.</param>
    public sealed record Evaluation(
        int DoubleSupportTicks,
        int PairedTicks,
        int Violations,
        ulong WorstTick,
        float WorstSyntheticX,
        float WorstSyntheticY,
        float WorstSyntheticYaw,
        ImmutableArray<string> Problems);

    /// <summary>How many individual problem entries are listed before the remainder is aggregated; the message
    /// must stay readable when a whole window fails to pair.</summary>
    public const int MaximumListedProblems = 8;

    /// <summary>
    /// Evaluates the pairing of movement diagnostics against the independently measured double-support ticks of
    /// one observation window. Violations are measured (never hidden) whenever any pair exists; structural
    /// problems are collected for the caller to fail on.
    /// </summary>
    /// <param name="samples">Every movement diagnostic sample exposed by the production build.</param>
    /// <param name="doubleSupportTicks">The window's independently measured double-support ticks, in order.</param>
    public static Evaluation Evaluate(IEnumerable<DiagnosticSample> samples, IEnumerable<ulong> doubleSupportTicks)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(doubleSupportTicks);

        List<string> problems = [];
        Dictionary<ulong, (float X, float Y, float Yaw)> byTick = [];
        Dictionary<ulong, int> duplicates = [];
        foreach (DiagnosticSample sample in samples)
        {
            if (byTick.ContainsKey(sample.Tick))
            {
                duplicates[sample.Tick] = duplicates.GetValueOrDefault(sample.Tick, 1) + 1;
                continue;
            }

            byTick[sample.Tick] = (sample.SyntheticX, sample.SyntheticY, sample.SyntheticYaw);
        }

        foreach ((ulong tick, int count) in duplicates.OrderBy(pair => pair.Key))
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"duplicate movement diagnostic samples for tick {tick} ({count} entries)"));
        }

        if (byTick.Count == 0)
        {
            problems.Add("the movement diagnostics exposed no samples");
        }

        List<ulong> unpaired = [];
        int doubleSupport = 0;
        int paired = 0;
        int violations = 0;
        ulong worstTick = 0;
        float worstX = 0f;
        float worstY = 0f;
        float worstYaw = 0f;
        foreach (ulong tick in doubleSupportTicks)
        {
            doubleSupport++;
            if (!byTick.TryGetValue(tick, out (float X, float Y, float Yaw) synthetic))
            {
                unpaired.Add(tick);
                continue;
            }

            paired++;
            if (synthetic.X == 0f && synthetic.Y == 0f && synthetic.Yaw == 0f)
            {
                continue;
            }

            violations++;
            float magnitudeSquared = (synthetic.X * synthetic.X) + (synthetic.Y * synthetic.Y);
            float worstMagnitudeSquared = (worstX * worstX) + (worstY * worstY);
            if (magnitudeSquared > worstMagnitudeSquared
                || (magnitudeSquared == worstMagnitudeSquared && MathF.Abs(synthetic.Yaw) > MathF.Abs(worstYaw)))
            {
                worstTick = tick;
                worstX = synthetic.X;
                worstY = synthetic.Y;
                worstYaw = synthetic.Yaw;
            }
        }

        if (doubleSupport == 0)
        {
            problems.Add("the observation window contained no independently measured double-support ticks");
        }

        foreach (ulong tick in unpaired.Take(MaximumListedProblems))
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"double-support tick {tick} has no paired movement sample"));
        }

        if (unpaired.Count > MaximumListedProblems)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"{unpaired.Count - MaximumListedProblems} further double-support ticks have no paired movement sample"));
        }

        if (doubleSupport > 0 && paired == 0)
        {
            problems.Add("zero double-support ticks were paired with movement diagnostics");
        }

        return new Evaluation(
            doubleSupport,
            paired,
            violations,
            worstTick,
            worstX,
            worstY,
            worstYaw,
            [.. problems]);
    }
}
