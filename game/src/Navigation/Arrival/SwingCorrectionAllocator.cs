using Godot;

namespace AlleyCat.Navigation.Arrival;

/// <summary>Per-window correction limits, in metres and radians.</summary>
public sealed record SwingCorrectionCaps
{
    /// <summary>Maximum forward translation per single-swing window.</summary>
    public float ForwardMetres { get; init; } = 0.030f;
    /// <summary>Maximum backward translation per single-swing window.</summary>
    public float BackwardMetres { get; init; } = 0.100f;
    /// <summary>Maximum translation to either side per single-swing window.</summary>
    public float LateralMetres { get; init; } = 0.050f;
    /// <summary>Maximum absolute yaw per eligible window.</summary>
    public float YawRadians { get; init; } = MathF.PI / 60f;
    /// <summary>Maximum flight translation as a fraction of that window's predicted travel.</summary>
    public float FlightTravelShare { get; init; } = 0.15f;
    /// <summary>Whether both-swing windows may receive any correction.</summary>
    public bool AllowFlightCorrection { get; init; } = true;
}

/// <summary>Only an advancing, active locomotion pose may receive correction.</summary>
public enum SwingCorrectionState
{
    /// <summary>Advancing locomotion before the final swing ends.</summary>
    Active,
    /// <summary>Idle playback.</summary>
    Idle,
    /// <summary>A non-advancing pose.</summary>
    Frozen,
    /// <summary>Completed playback.</summary>
    Finished,
    /// <summary>The last permitted swing has ended.</summary>
    AfterFinalSwing,
}

/// <summary>
/// Target minus predicted endpoint, expressed in the current actor frame before this tick's correction.
/// Position is actor-semantic [right, forward], in metres; yaw is positive left, in radians.
/// </summary>
public readonly record struct SwingCorrectionResidual(Vector2 Position, float Yaw);

/// <summary>Estimated geometry at the start of one whole remaining window, in that window's actor frame.</summary>
/// <param name="EndpointOffset">Vector from the estimated actor origin to the predicted endpoint.</param>
/// <param name="PlantedBall">Vector from that origin to the planted ball; ignored in flight.</param>
public readonly record struct SwingCorrectionGeometry(Vector2 EndpointOffset, Vector2 PlantedBall);

/// <summary>The fixed point of the additional yaw rotation.</summary>
public enum SwingCorrectionPivot
{
    /// <summary>The actor origin (flight).</summary>
    Origin,
    /// <summary>The planted foot's ball (single swing).</summary>
    Ball,
}

/// <summary>
/// Additional motion in the pre-correction actor frame. Rotate by Yaw about Pivot, then translate by
/// Planar in the pre-correction frame: o' = o + R_heading(Planar + OriginShift); heading += Yaw.
/// </summary>
/// <param name="Planar">Independent translation, excluding the yaw-induced origin shift.</param>
/// <param name="Yaw">Additional yaw in radians.</param>
/// <param name="Pivot">The yaw pivot, not the translation pivot.</param>
/// <param name="OriginShift">
/// Yaw-induced displacement Δo = (R(ψ) − I)(o − b), where R(ψ) is the actual applied yaw rotation,
/// o is the actor origin and b the planted ball. Zero for an origin pivot. Add this to Planar exactly once.
/// </param>
public readonly record struct SwingCorrection(Vector2 Planar, float Yaw, SwingCorrectionPivot Pivot, Vector2 OriginShift);

/// <summary>
/// Stateless swing-only correction. The caller supplies current endpoint error each tick, including the effects
/// of previous translation and pivot displacement. No Godot objects or engine calls are required.
/// </summary>
public sealed class SwingCorrectionAllocator
{

    /// <summary>Creates an allocator with immutable, finite, non-negative caps.</summary>
    public SwingCorrectionAllocator(SwingCorrectionCaps caps)
    {
        ArgumentNullException.ThrowIfNull(caps);
        ValidateLimit(caps.ForwardMetres);
        ValidateLimit(caps.BackwardMetres);
        ValidateLimit(caps.LateralMetres);
        ValidateLimit(caps.YawRadians);
        ValidateLimit(caps.FlightTravelShare);
        Caps = caps;
    }

    /// <summary>The immutable caps allocations and phase budgets are bounded by.</summary>
    public SwingCorrectionCaps Caps
    {
        get;
    }

    /// <summary>
    /// Allocates one tick. Tick is an absolute prediction tick; the original, untrimmed window is required so
    /// every tick is bounded by cap / original tick count. Future windows exclude the current window and must
    /// contain only advancing locomotion poses before the final swing ends (DS entries are harmless).
    /// PlantedBall and endpointOffset are vectors from the current actor origin in its pre-correction
    /// actor-semantic [right, forward] frame, not raw Godot [X, Z]. PlantedBall is ignored in flight.
    /// LivePhase must be measured this tick using the Planted Combination Rule; a prediction mismatch refuses correction.
    /// </summary>
    public SwingCorrection Step(
        SwingCorrectionResidual residual,
        SupportWindow window,
        int tick,
        ReadOnlySpan<SupportWindow> remainingWindows,
        Vector2 plantedBall,
        Vector2 endpointOffset,
        SupportPhase livePhase,
        SwingCorrectionState state = SwingCorrectionState.Active)
    {
        if (state != SwingCorrectionState.Active || livePhase != window.Phase || !Eligible(window) || tick < window.FirstTick || tick > window.LastTick)
        {
            return default;
        }

        ValidateResidual(residual);
        ValidateGeometry(new(endpointOffset, plantedBall), window.Phase.IsSingleSwing());

        float fraction = (float)(window.LastTick - tick + 1) / window.TickCount;
        int futureCount = 0;
        float single = window.Phase.IsSingleSwing() ? fraction : 0f;
        float flight = window.Phase == SupportPhase.BothSwing ? FlightBudget(window) * fraction : 0f;
        foreach (SupportWindow future in remainingWindows)
        {
            if (!Eligible(future))
            {
                continue;
            }

            futureCount++;
            if (future.Phase.IsSingleSwing())
            {
                single++;
            }
            else
            {
                flight += FlightBudget(future);
            }
        }

        float shares = fraction + futureCount;
        // Fill the last eligible windows first; the current window receives only the remainder.
        float yaw = AssignedYaw(residual.Yaw, futureCount, fraction) / (window.TickCount * fraction);
        bool ballPivot = window.Phase.IsSingleSwing();
        Vector2 shift = ballPivot ? plantedBall - plantedBall.Rotated(yaw) : Vector2.Zero;

        Vector2 lever = endpointOffset - (ballPivot ? plantedBall : Vector2.Zero);
        Vector2 swing = lever.Rotated(yaw) - lever;
        Vector2 position = residual.Position;
        Vector2 singleTotal = ClampSingle(position, single);
        Vector2 overflow = position - singleTotal;
        Vector2 allocation;
        if (ballPivot)
        {
            // Equal shares when possible; reserve enough for SS when the flight budget cannot carry its share.
            Vector2 preferred = position / shares;
            Vector2 flightPart = position - (ClampSingle(preferred, 1f) * single);
            if (flightPart.Length() > flight)
            {
                // A rectangle plus a disc is the exact combined planar capacity. Its closest rectangle
                // point leaves the smallest possible flight demand, including diagonal residuals.
                allocation = singleTotal / single;
            }
            else
            {
                allocation = ClampSingle(preferred, 1f);
            }

            allocation /= window.TickCount;
        }
        else
        {
            float budget = FlightBudget(window);
            Vector2 preferred = position / shares;
            // Keep enough SS capacity for each component. Overflow has priority over equal sharing.
            Vector2 demand = overflow.LengthSquared() > 0f ? overflow : preferred * (fraction + futureCount - single);
            allocation = AllocateFlight(demand, budget, fraction, remainingWindows) / window.TickCount;
        }

        // Endpoint swing belongs to this tick, not to the residual's window-share allocation.
        // Subtract it once after sharing, then enforce the original per-tick translation cap.
        // Any uncompensated swing is observed in the caller's next endpoint residual.
        allocation -= swing;
        if (ballPivot)
        {
            allocation = ClampSingle(allocation, 1f / window.TickCount);
        }
        else
        {
            float length = allocation.Length();
            float budget = FlightBudget(window) / window.TickCount;
            if (length > budget)
            {
                allocation *= budget / length;
            }
        }

        return new SwingCorrection(allocation, yaw, ballPivot ? SwingCorrectionPivot.Ball : SwingCorrectionPivot.Origin, shift);
    }

    /// <summary>
    /// Allocates one tick of an ONGOING physical support phase (CORNER-03): the shared stateless operation of
    /// <see cref="Step(SwingCorrectionResidual, SupportWindow, int, ReadOnlySpan{SupportWindow}, Vector2, Vector2, SupportPhase, SwingCorrectionState)"/>
    /// for this tick, with its yaw demand and independent Planar bounded by <paramref name="phase"/>'s remaining
    /// allowance and by the original per-tick spreading limits (<c>cap / OriginalTicks</c>), so a truncated window
    /// of the same physical phase — a section handoff, a replan, a request replacement — is never a fresh grant and
    /// never raises the per-tick allowance. A fresh phase (no consumption, original basis = this window) keeps the
    /// stateless schedule exactly, including its multi-window late-yaw sharing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CORNER-09/11: the final yaw is selected BEFORE every yaw-dependent quantity, and THIS tick's intended yaw is
    /// determined from the FULL remaining demand and the later eligible windows' own grants (the stateless
    /// late-yaw schedule <c>clamp(|demand| − future·cap, 0, cap·fraction)</c>), not from a global clamp of the
    /// demand to this phase's capacity: only this window's SHARE of that schedule is capped, by the phase's
    /// remaining ABSOLUTE yaw allowance (one cap for both signs, CORNER-03) at the original per-tick rate. The one
    /// shared operation then derives the endpoint-lever compensation, the planar water-fill and the pivot-origin
    /// shift from the APPLIED yaw — never a clamped yaw that still carries the discarded yaw's compensation, and
    /// never a second formula. Future windows keep their full normal grants.
    /// </para>
    /// <para>
    /// CORNER-10: the translation allowance constrains the independent <c>Planar</c> alone; the yaw allowance
    /// constrains <c>Yaw</c>. The pivot-origin shift is the deterministic displacement for that same bounded yaw
    /// about its pivot — applied once and recorded in the total synthetic movement, never a second translation
    /// grant and never deducted from the <c>Planar</c> cap. A flight phase (origin pivot, zero shift) keeps its
    /// scalar travel-derived budget over the Planar increment's length.
    /// </para>
    /// </remarks>
    public SwingCorrection Step(
        SwingCorrectionResidual residual,
        SupportWindow window,
        int tick,
        ReadOnlySpan<SupportWindow> remainingWindows,
        Vector2 plantedBall,
        Vector2 endpointOffset,
        SupportPhase livePhase,
        SwingCorrectionPhaseBudget phase,
        SwingCorrectionState state = SwingCorrectionState.Active)
    {
        ArgumentNullException.ThrowIfNull(phase);
        SwingCorrection stateless = Step(residual, window, tick, remainingWindows, plantedBall, endpointOffset, livePhase, state);
        if (phase.Phase != window.Phase || phase.Phase != livePhase)
        {
            return stateless;
        }

        // CORNER-09/11: select the final yaw before all dependent endpoint/pivot/planar geometry. The stateless
        // assignment for this tick is clamp(|demand| − futureWindows·cap, 0, cap·fraction), spread over the window's
        // remaining ticks: it already uses the FULL demand and the future windows' own grants. Capping the demand at
        // futureWindows·cap + perTickLimit·windowTicks therefore caps only THIS window's share — to at most the
        // phase's remaining ABSOLUTE yaw allowance at the original per-tick rate — while leaving the schedule's
        // use of future capacity and a fresh phase's schedule untouched.
        int futureWindows = 0;
        foreach (SupportWindow future in remainingWindows)
        {
            if (Eligible(future))
            {
                futureWindows++;
            }
        }

        float windowTicks = window.LastTick - tick + 1;
        float perTickYawLimit = (float)Math.Max(0d, Math.Min(phase.RemainingYaw, Caps.YawRadians / phase.OriginalTicks));
        float yawDemandLimit = (futureWindows * Caps.YawRadians) + (perTickYawLimit * windowTicks);
        SwingCorrectionResidual boundedResidual = residual with
        {
            Yaw = Math.Clamp(residual.Yaw, -yawDemandLimit, yawDemandLimit),
        };
        SwingCorrection proposal = boundedResidual == residual
            ? stateless
            : Step(boundedResidual, window, tick, remainingWindows, plantedBall, endpointOffset, livePhase, state);

        // CORNER-10: the translation allowance bounds the independent Planar alone (per-axis directional remaining
        // allowance and the original per-tick rate; a flight phase bounds the Planar increment's length by its
        // scalar budget). The pivot-origin shift stays the bounded yaw's own displacement.
        float perTick = 1f / phase.OriginalTicks;
        Vector2 planar;
        if (window.Phase == SupportPhase.BothSwing)
        {
            float length = proposal.Planar.Length();
            float limit = (float)Math.Max(0d, Math.Min(phase.RemainingFlight, phase.FlightBudgetMetres * perTick));
            planar = length > limit && length > 0f ? proposal.Planar * (limit / length) : proposal.Planar;
        }
        else
        {
            planar = new(
                (float)Math.Clamp(proposal.Planar.X, -Math.Min(phase.RemainingLeft, Caps.LateralMetres * perTick), Math.Min(phase.RemainingRight, Caps.LateralMetres * perTick)),
                (float)Math.Clamp(proposal.Planar.Y, -Math.Min(phase.RemainingBackward, Caps.BackwardMetres * perTick), Math.Min(phase.RemainingForward, Caps.ForwardMetres * perTick)));
        }

        return proposal with
        {
            Planar = planar,
        };
    }

    /// <summary>
    /// Estimates feasibility for whole remaining windows, never a partially consumed current window.
    /// Geometry has one entry per window, in its estimated starting actor-semantic frame. It estimates
    /// origin-to-endpoint and origin-to-ball vectors after planned travel, before that window's corrections.
    /// Simulates Step's late-yaw schedule and capped translation in chronological order; unused capacity
    /// expires with each window. Within single swing the ball stays fixed; planned translation cancels
    /// from the endpoint-to-ball lever. Flight uses its starting endpoint lever throughout as an estimate;
    /// no intervening planned heading changes or intra-flight travel are modelled.
    /// This is not a collision or future support/geometry guarantee; feed realised values back into Step.
    /// </summary>
    public bool CanClose(SwingCorrectionResidual residual, ReadOnlySpan<SupportWindow> windows,
        ReadOnlySpan<SwingCorrectionGeometry> geometry)
    {
        ValidateResidual(residual);
        if (geometry.Length != windows.Length)
        {
            throw new ArgumentException("Supply geometry for every remaining window.", nameof(geometry));
        }
        int count = 0;
        foreach (SupportWindow window in windows)
        {
            if (!Eligible(window))
            {
                continue;
            }

            count++;
        }

        if (Math.Abs(residual.Yaw) > count * Caps.YawRadians)
        {
            return false;
        }

        for (int index = 0; index < windows.Length; index++)
        {
            SupportWindow window = windows[index];
            if (!Eligible(window))
            {
                continue;
            }

            bool ballPivot = window.Phase.IsSingleSwing();
            ValidateGeometry(geometry[index], ballPivot);
            Vector2 endpoint = geometry[index].EndpointOffset;
            Vector2 ball = geometry[index].PlantedBall;
            for (int offset = 0; offset < window.TickCount; offset++)
            {
                SwingCorrection correction = Step(residual, window, window.FirstTick + offset,
                    windows[(index + 1)..], ball, endpoint, window.Phase);
                Vector2 lever = endpoint - (ballPivot ? ball : Vector2.Zero);
                Vector2 swing = lever.Rotated(correction.Yaw) - lever;
                residual = new SwingCorrectionResidual(
                    (residual.Position - correction.Planar - swing).Rotated(-correction.Yaw),
                    residual.Yaw - correction.Yaw);
                if (ballPivot)
                {
                    // Equivalent to keeping the ball fixed in world space while the actor moves.
                    ball = (ball - correction.OriginShift - correction.Planar).Rotated(-correction.Yaw);
                }
            }
        }

        // Numerical round-off only, deliberately much tighter than the arrival acceptance tolerances.
        return residual.Position.Length() <= 1e-6f && Math.Abs(residual.Yaw) <= 1e-6f;
    }

    private float AssignedYaw(float residual, int futureCount, float fraction) =>
        MathF.CopySign(Math.Clamp(Math.Abs(residual) - (futureCount * Caps.YawRadians), 0f, Caps.YawRadians * fraction), residual);

    private static void ValidateGeometry(SwingCorrectionGeometry geometry, bool ballPivot)
    {
        if (!geometry.EndpointOffset.IsFinite() || (ballPivot && !geometry.PlantedBall.IsFinite()))
        {
            throw new ArgumentOutOfRangeException(nameof(geometry));
        }
    }

    private bool Eligible(SupportWindow window) => window.TickCount > 0 &&
        (window.Phase.IsSingleSwing() || (window.Phase == SupportPhase.BothSwing && Caps.AllowFlightCorrection));

    private float FlightBudget(SupportWindow window) =>
        window.StartPose.Position.DistanceTo(window.EndPose.Position) * Caps.FlightTravelShare;

    private Vector2 ClampSingle(Vector2 value, float windows) => new(
        Math.Clamp(value.X, -Caps.LateralMetres * windows, Caps.LateralMetres * windows),
        Math.Clamp(value.Y, -Caps.BackwardMetres * windows, Caps.ForwardMetres * windows));

    private Vector2 AllocateFlight(Vector2 demand, float budget, float fraction, ReadOnlySpan<SupportWindow> future)
    {
        float length = demand.Length();
        if (length == 0f || budget == 0f)
        {
            return Vector2.Zero;
        }

        float count = fraction;
        foreach (SupportWindow window in future)
        {
            if (Eligible(window) && window.Phase == SupportPhase.BothSwing)
            {
                count++;
            }
        }

        // Water-fill equal window shares, redistributing the excess from smaller capped windows. Each pass
        // either finishes or saturates another window; no temporary collection or persistent state is needed.
        float share = length / count;
        while (share < budget)
        {
            float available = fraction;
            float remaining = length;
            foreach (SupportWindow window in future)
            {
                if (!Eligible(window) || window.Phase != SupportPhase.BothSwing)
                {
                    continue;
                }

                float cap = FlightBudget(window);
                if (cap < share)
                {
                    remaining -= cap;
                }
                else
                {
                    available++;
                }
            }

            float next = remaining / available;
            if (next <= share)
            {
                break;
            }

            share = next;
        }

        return demand * (Math.Min(share, budget) / length);
    }

    private static void ValidateLimit(float value)
    {
        if (!float.IsFinite(value) || value < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Correction caps must be finite and non-negative.");
        }
    }

    private static void ValidateResidual(SwingCorrectionResidual residual)
    {
        if (!residual.Position.IsFinite() || !float.IsFinite(residual.Yaw))
        {
            throw new ArgumentOutOfRangeException(nameof(residual));
        }
    }
}
