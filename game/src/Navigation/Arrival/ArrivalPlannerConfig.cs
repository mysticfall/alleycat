using Godot;

namespace AlleyCat.Navigation.Arrival;

/// <summary>
/// Tunables of the <see cref="ArrivalPlanner"/> (NAV-001 Technical Requirements 5–7, 13 and 14): the A5 movement-choice
/// thresholds, the candidate families' bounds, the search's work budget and the ranking. They are configuration, not
/// spec constants; the defaults follow the NAV-001 P4 design (Q5).
/// </summary>
public sealed record ArrivalPlannerConfig
{
    /// <summary>The design defaults.</summary>
    public static ArrivalPlannerConfig Default { get; } = new();

    /// <summary>Planar distance within which a request needs no travel, in metres.</summary>
    public float ArrivedDistanceMetres { get; init; } = 0.01f;

    /// <summary>Heading change within which a request needs no turn, in radians.</summary>
    public float ArrivedYawRadians { get; init; } = Mathf.DegToRad(1f);

    /// <summary>Planar distance up to which a request is a turn only (pivots, no travel), in metres.</summary>
    public float TurnOnlyDistanceMetres { get; init; } = 0.05f;

    /// <summary>
    /// Side-step when the target is lateral-dominant: its lateral offset exceeds this multiple of its forward offset
    /// (<c>|x| &gt; 2|y|</c>).
    /// </summary>
    public float SideStepLateralRatio { get; init; } = 2f;

    /// <summary>Largest side-step distance, in metres (about two measured side-step quanta).</summary>
    public float SideStepMaxDistanceMetres { get; init; } = 0.71f;

    /// <summary>Largest heading change a side-step request may carry (the final pivot is appended), in radians.</summary>
    public float SideStepMaxYawChangeRadians { get; init; } = Mathf.DegToRad(45f);

    /// <summary>Bearing beyond which the NPC always turns first, in radians.</summary>
    public float TurnFirstBearingRadians { get; init; } = Mathf.DegToRad(100f);

    /// <summary>Bearing beyond which a nearby target (closer than <see cref="ArcMinDistanceMetres"/>) is turned to first, in radians.</summary>
    public float NearTurnFirstBearingRadians { get; init; } = Mathf.DegToRad(45f);

    /// <summary>
    /// Shortest distance of an arc into walking, in metres; between <see cref="NearTurnFirstBearingRadians"/> and
    /// <see cref="TurnFirstBearingRadians"/> of bearing a nearer target turns first instead.
    /// </summary>
    public float ArcMinDistanceMetres { get; init; } = 1.5f;

    /// <summary>Largest alignment pivot before a straight walk, in radians.</summary>
    public float StraightMaxAlignPivotRadians { get; init; } = Mathf.DegToRad(50f);

    /// <summary>Largest lead pivot (towards the target side) before an arc, in radians.</summary>
    public float ArcMaxLeadPivotRadians { get; init; } = Mathf.DegToRad(60f);

    /// <summary>Most extra loops of a looping pivot in one block (each about 45°).</summary>
    public int MaxPivotLoops { get; init; } = 4;

    /// <summary>Most extra loops of a looping side-step in one block (each one whole step).</summary>
    public int MaxSideStepLoops { get; init; } = 2;

    /// <summary>Most extra whole cycles of the forward walk cycle in one walk.</summary>
    public int MaxWalkCycles { get; init; } = 12;

    /// <summary>Most extra whole cycles of an arc in one walk.</summary>
    public int MaxArcCycles { get; init; } = 2;

    /// <summary>Longest Idle dwell before an Idle seam into the next block, in seconds.</summary>
    public double MaxIdleDwellSeconds { get; init; } = 1.5d;

    /// <summary>
    /// Seams kept per forward-walk pair (entry → cycle and cycle → exit joins): the lowest-separation aligned
    /// seam at each distinct outgoing time, lowest separation first; zero or less keeps every distinct outgoing time,
    /// which gives the walk its full distance coverage. This limit does not apply to direct Idle → entry → exit → Idle
    /// blocks, which keep every legal entry seek, incoming cut and exit cut and are ranked by complete motion.
    /// </summary>
    public int SeamOptionsPerPair
    {
        get; init;
    }

    /// <summary>Seams kept per arc pair (entry → arc, arc → exit and arc → forward cycle), as <see cref="SeamOptionsPerPair"/>.</summary>
    public int ArcSeamOptionsPerPair { get; init; } = 8;

    /// <summary>Smallest bearing of a straight request at which gentle arcs into the walk are also searched, in radians.</summary>
    public float StraightArcMinBearingRadians { get; init; } = Mathf.DegToRad(3f);

    /// <summary>
    /// Exit → Idle seams kept per exit segment (lowest separation first), except in direct Idle → entry → exit → Idle
    /// blocks, which keep every legal entry seek, incoming cut and exit cut and are ranked by complete motion.
    /// </summary>
    public int ExitToIdleOptions { get; init; } = 2;

    /// <summary>
    /// Idle → entry seams kept per entry lane (distinct entry positions, lowest separation first), except in direct
    /// Idle → entry → exit → Idle blocks, which keep every legal entry seek, incoming cut and exit cut and are ranked
    /// by complete motion.
    /// </summary>
    public int WalkEntryOptions { get; init; } = 2;

    /// <summary>Walk tails evaluated on each side of the distance a walk prefix needs.</summary>
    public int TailNeighbours { get; init; } = 2;

    /// <summary>Pivot blocks evaluated on each side of the yaw an alignment or final pivot needs.</summary>
    public int PivotNeighbours { get; init; } = 1;

    /// <summary>A travel candidate whose distance misses the target's by more than this is skipped, in metres.</summary>
    public float DistanceWindowMetres { get; init; } = 0.3f;

    /// <summary>
    /// Metres of residual cost per radian of heading residual; the default equates the A1 tolerances (1 cm ≙ 1°).
    /// </summary>
    public float YawWeightMetresPerRadian { get; init; } = 0.01f / Mathf.DegToRad(1f);

    /// <summary>
    /// Residual costs within the same multiple of this quantum tie, in metres. Standalone Idle-start stationary turns
    /// break a residual-bucket tie by fewer interior Idle bridges, shorter playback, fewer pieces (Steps), then lower
    /// seam separation. Mixed searches retain the legacy steps, separation, duration order.
    /// </summary>
    public float ResidualTieMetres { get; init; } = 0.005f;

    /// <summary>Work budget: most candidate compositions one plan evaluates.</summary>
    public int MaxCompositions { get; init; } = 60_000;

    /// <summary>Finalists predicted exactly (summary mode) per plan.</summary>
    public int MaxFinalists { get; init; } = 8;

    /// <summary>Ways to reach Idle from a mid-clip start that are planned onwards from.</summary>
    public int StopPrefixOptions { get; init; } = 2;
}
