using Godot;

namespace AlleyCat.Navigation.Arrival;

/// <summary>
/// The allowance state of ONE physical support phase (CORNER-03): the original grant basis established when the
/// phase was first covered by a prediction window, and the correction actually consumed from it since. It is an
/// immutable value: charging returns a new instance, so a planning snapshot can never be mutated by the runtime, by
/// a speculative candidate assessment, or by another consumer of a copy.
/// </summary>
/// <remarks>
/// <para>
/// Consumption is INCREMENTAL and never refunded (CORNER-03): every enabled applied increment is charged exactly
/// once to the allowance of its own direction — <see cref="SpentRight"/>/<see cref="SpentLeft"/> accumulate the
/// positive/negative lateral <c>Planar</c> increments, <see cref="SpentForward"/>/<see cref="SpentBackward"/> the
/// forward/backward ones, <see cref="SpentYawLeft"/>/<see cref="SpentYawRight"/> the yaw increments per sign, and a
/// flight phase accumulates the LENGTH of each <c>Planar</c> increment against its scalar travel-derived budget
/// (<see cref="FlightSpent"/>). Net displacement is never charged instead of the increments, and no maximum
/// excursion is stored: reversing a correction consumes the other side's allowance and does not restore capacity,
/// and moving back through an already-visited displacement consumes again — so a plan boundary inside the same
/// physical phase (a section handoff, a replan, a request replacement) cannot replenish it, and a repeated reversal
/// cannot extract more motion than the original grant. A genuine phase change grants a fresh window budget through
/// <see cref="ForWindow"/>.
/// </para>
/// <para>
/// Charged quantities preserve the existing operation (CORNER-10): the translation allowance constrains the
/// independent <c>Planar</c> alone; the yaw allowance constrains <c>Yaw</c>. The pivot-origin shift is the
/// deterministic displacement necessary for that same bounded yaw about its pivot — applied once and recorded in
/// the cumulative totals (<see cref="CumulativeRight"/>/<see cref="CumulativeForward"/> carry the whole applied
/// synthetic translation <c>Planar + OriginShift</c> as signed diagnostics), but it is NOT a second independent
/// translation grant and is never deducted from the <c>Planar</c> allowance.
/// </para>
/// <para>
/// Bookkeeping is per-axis in the current actor frame, like the allocator's own caps; heading changes within a phase
/// (bounded by the phase's yaw allowance plus native intra-phase turn) are not rotated out of the axes. This is
/// allowance accounting only: it does not predict collisions, reach or contact, and it does not carry a phase across
/// an observation gap (a gap keeps the last observed phase and its remaining allowance; it cannot create
/// permission).
/// </para>
/// </remarks>
public sealed record SwingCorrectionPhaseBudget
{
    /// <summary>The physical support phase this budget belongs to (single swing or flight).</summary>
    public required SupportPhase Phase
    {
        get; init;
    }

    /// <summary>
    /// Tick count of the prediction window that established the grant: the per-tick spreading limits stay
    /// <c>cap / OriginalTicks</c> even when a later, truncated window of the same phase is planned.
    /// </summary>
    public required int OriginalTicks
    {
        get; init;
    }

    /// <summary>The caps the grant was established with (the production allocator's immutable caps).</summary>
    public required SwingCorrectionCaps Caps
    {
        get; init;
    }

    /// <summary>Flight phases: the original window's travel-derived budget, in metres (zero for single swing).</summary>
    public float FlightBudgetMetres
    {
        get; init;
    }

    /// <summary>Signed cumulative applied synthetic translation (Planar + OriginShift) toward the actor's right since the grant — a diagnostic, not an allowance, in metres.</summary>
    public double CumulativeRight
    {
        get; init;
    }

    /// <summary>Signed cumulative applied synthetic translation (Planar + OriginShift) forward since the grant — a diagnostic, not an allowance, in metres.</summary>
    public double CumulativeForward
    {
        get; init;
    }

    /// <summary>Signed cumulative applied yaw since the grant, in radians (positive turns left; diagnostic).</summary>
    public double CumulativeYaw
    {
        get; init;
    }

    /// <summary>Sum of applied rightward Planar increments (each charged once, never refunded), in metres.</summary>
    public double SpentRight
    {
        get; init;
    }

    /// <summary>Sum of applied leftward Planar increments (each charged once, never refunded), in metres.</summary>
    public double SpentLeft
    {
        get; init;
    }

    /// <summary>Sum of applied forward Planar increments (each charged once, never refunded), in metres.</summary>
    public double SpentForward
    {
        get; init;
    }

    /// <summary>Sum of applied backward Planar increments (each charged once, never refunded), in metres.</summary>
    public double SpentBackward
    {
        get; init;
    }

    /// <summary>Diagnostic sum of applied left-turn yaw increments, in radians (the consumed allowance is the
    /// absolute total <see cref="SpentYawAbsolute"/>, shared by both signs).</summary>
    public double SpentYawLeft
    {
        get; init;
    }

    /// <summary>Diagnostic sum of applied right-turn yaw increments, in radians (the consumed allowance is the
    /// absolute total <see cref="SpentYawAbsolute"/>, shared by both signs).</summary>
    public double SpentYawRight
    {
        get;
        init;
    }

    /// <summary>Sum of applied yaw increment magnitudes — the ONE non-refundable absolute yaw allowance consumed
    /// from the single per-phase yaw cap by both signs, in radians.</summary>
    public double SpentYawAbsolute
    {
        get; init;
    }

    /// <summary>Flight phases: sum of applied Planar increment lengths (each charged once, never refunded), in metres.</summary>
    public double FlightSpent
    {
        get; init;
    }

    /// <summary>Remaining lateral allowance toward the right: original lateral cap less the consumed increments.</summary>
    public double RemainingRight => Caps.LateralMetres - SpentRight;

    /// <summary>Remaining lateral allowance toward the left: original lateral cap less the consumed increments.</summary>
    public double RemainingLeft => Caps.LateralMetres - SpentLeft;

    /// <summary>Remaining forward allowance: original forward cap less the consumed increments.</summary>
    public double RemainingForward => Caps.ForwardMetres - SpentForward;

    /// <summary>Remaining backward allowance: original backward cap less the consumed increments.</summary>
    public double RemainingBackward => Caps.BackwardMetres - SpentBackward;

    /// <summary>Remaining ABSOLUTE yaw allowance of the phase (the one cap less both signs' consumed increments).</summary>
    public double RemainingYaw => Caps.YawRadians - SpentYawAbsolute;

    /// <summary>Flight phases: remaining travel-derived allowance, in metres.</summary>
    public double RemainingFlight => FlightBudgetMetres - FlightSpent;

    /// <summary>
    /// Establishes a fresh grant from the prediction window that first covers a physical phase: the window's own
    /// tick count and (for a flight phase) its travel-derived budget are the original basis. Only an eligible window
    /// (single swing, or flight with correction enabled) establishes a budget.
    /// </summary>
    public static SwingCorrectionPhaseBudget? ForWindow(SupportWindow window, SwingCorrectionCaps caps)
    {
        ArgumentNullException.ThrowIfNull(caps);
        bool flight = window.Phase == SupportPhase.BothSwing;
        return !window.Phase.IsSingleSwing() && !(flight && caps.AllowFlightCorrection)
            ? null
            : new SwingCorrectionPhaseBudget
            {
                Phase = window.Phase,
                OriginalTicks = window.TickCount,
                Caps = caps,
                FlightBudgetMetres = flight
                ? window.StartPose.Position.DistanceTo(window.EndPose.Position) * caps.FlightTravelShare
                : 0f,
            };
    }

    /// <summary>
    /// Charges one applied correction proposal to this budget and returns the committed state. Each ENABLED
    /// increment is charged exactly once to its own direction's allowance (CORNER-03): the positive/negative
    /// <paramref name="applied"/>.<c>Planar</c> components accumulate <see cref="SpentRight"/>/<see cref="SpentLeft"/>
    /// and <see cref="SpentForward"/>/<see cref="SpentBackward"/>, the yaw accumulates the single
    /// absolute allowance <see cref="SpentYawAbsolute"/> (with the per-sign sums kept as diagnostics), and a flight phase accumulates the Planar
    /// increment's length into <see cref="FlightSpent"/>. Net displacement is never charged instead of the
    /// increments and nothing is ever refunded. Yaw consumes ONE absolute allowance per phase
    /// (<see cref="SpentYawAbsolute"/> sums the applied increments' magnitudes against the single yaw cap): switching
    /// sign grants no second allowance — the per-sign sums <see cref="SpentYawLeft"/>/<see cref="SpentYawRight"/> and
    /// the signed <see cref="CumulativeYaw"/> remain diagnostics only, and the original per-tick rate applies to the
    /// absolute magnitude. The pivot-origin shift is recorded only in the cumulative totals
    /// (the whole applied synthetic translation, as signed diagnostics): it is not a second translation grant
    /// (CORNER-10). Charging is conservative: it records the proposal as spent even if a collision later blocks
    /// part of its translation (a blocked correction is not refunded, so it cannot enlarge later capacity).
    /// </summary>
    public SwingCorrectionPhaseBudget Charge(SwingCorrection applied)
    {
        Vector2 translation = applied.Planar + applied.OriginShift;
        return this with
        {
            CumulativeRight = CumulativeRight + translation.X,
            CumulativeForward = CumulativeForward + translation.Y,
            CumulativeYaw = CumulativeYaw + applied.Yaw,
            SpentRight = SpentRight + Math.Max(0d, applied.Planar.X),
            SpentLeft = SpentLeft + Math.Max(0d, -applied.Planar.X),
            SpentForward = SpentForward + Math.Max(0d, applied.Planar.Y),
            SpentBackward = SpentBackward + Math.Max(0d, -applied.Planar.Y),
            SpentYawLeft = SpentYawLeft + Math.Max(0d, applied.Yaw),
            SpentYawRight = SpentYawRight + Math.Max(0d, -applied.Yaw),
            SpentYawAbsolute = SpentYawAbsolute + Math.Abs(applied.Yaw),
            FlightSpent = FlightSpent + applied.Planar.Length(),
        };
    }
}
