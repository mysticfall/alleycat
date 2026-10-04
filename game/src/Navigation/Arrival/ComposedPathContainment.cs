using Godot;

namespace AlleyCat.Navigation.Arrival;

/// <summary>
/// Depth of one world planar position below the navigable surface, in metres: zero or negative when the position
/// is on the surface, positive when it is off it. Implementations come from the live navigation map's own closest
/// point query, so the oracle is the actual authored surface the route was queried from — never a proxy shape.
/// </summary>
public delegate float NavigationSurfaceDepthMetres(Vector2 worldXz);

/// <summary>
/// Containment report for one composed trajectory: whether every probe was on the surface, the worst off-surface
/// depth found, and where the first off-surface probe was (CORNER-07 diagnostics; the decision uses
/// <see cref="IsContained"/> alone).
/// </summary>
/// <param name="IsContained">Whether every pose and consecutive-pose midpoint probe was on the surface.</param>
/// <param name="WorstDepthMetres">Largest off-surface depth over all probes, in metres (zero when contained).</param>
/// <param name="ProbeCount">Number of probes evaluated (poses plus consecutive midpoints).</param>
/// <param name="FirstOffSurfaceTick">Trajectory index of the first off-surface probe, or -1 when contained.</param>
/// <param name="FirstOffSurfaceWasMidpoint">Whether that first offending probe was a consecutive-pose midpoint.</param>
internal readonly record struct ComposedPathContainmentReport(
    bool IsContained,
    float WorstDepthMetres,
    int ProbeCount,
    int FirstOffSurfaceTick,
    bool FirstOffSurfaceWasMidpoint);

/// <summary>
/// Containment of one composed trajectory — native playback plus the runtime's capped swing correction, exactly as
/// <see cref="TerminalCorrectionSchedule.Simulate(SequencePrediction, ArrivalTarget, SwingCorrectionAllocator, SwingCorrectionPhaseBudget, List{Vector2}?)"/>
/// realises it — against a caller-supplied navigable-surface oracle. Every realised pose and every
/// consecutive-pose midpoint must lie on the surface: endpoint-only checks cannot see a segment that crosses off
/// the surface and returns, and the realised (corrected) samples, not the native prediction alone, are what leaves
/// the authored navmesh. The check is pure geometry over supplied samples; it never queries the engine itself.
/// </summary>
internal static class ComposedPathContainment
{
    /// <summary>
    /// Slack for positions exactly on a surface boundary: float-representation tolerance only, never a behavioural
    /// margin — no corridor is widened by it.
    /// </summary>
    public const float SurfaceEpsilonMetres = 0.0001f;

    /// <summary>Checks one composed trajectory's poses and consecutive midpoints against the surface.</summary>
    /// <param name="composedPositions">
    /// The composed per-tick world planar positions (the start pose followed by each tick's realised pose), as the
    /// correction schedule records them.
    /// </param>
    /// <param name="surfaceDepth">The surface-depth oracle (the live navigation map's closest-point query).</param>
    public static ComposedPathContainmentReport Check(
        IReadOnlyList<Vector2> composedPositions,
        NavigationSurfaceDepthMetres surfaceDepth)
    {
        ArgumentNullException.ThrowIfNull(composedPositions);
        ArgumentNullException.ThrowIfNull(surfaceDepth);

        bool contained = true;
        float worst = 0f;
        int firstTick = -1;
        bool firstWasMidpoint = false;
        int probes = 0;
        for (int index = 0; index < composedPositions.Count; index++)
        {
            Vector2 position = composedPositions[index];
            if (index > 0)
            {
                probes++;
                TrackProbe(surfaceDepth((composedPositions[index - 1] + position) * 0.5f), index, true);
            }

            probes++;
            TrackProbe(surfaceDepth(position), index, false);
            if (firstTick >= 0)
            {
                contained = false;
            }
        }

        return new ComposedPathContainmentReport(contained, worst, probes, firstTick, firstWasMidpoint);

        void TrackProbe(float depth, int index, bool midpoint)
        {
            if (depth <= SurfaceEpsilonMetres)
            {
                return;
            }

            worst = MathF.Max(worst, depth);
            if (firstTick < 0)
            {
                firstTick = index;
                firstWasMidpoint = midpoint;
            }
        }
    }
}
