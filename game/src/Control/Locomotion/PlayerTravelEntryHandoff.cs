using Godot;

namespace AlleyCat.Control.Locomotion;

/// <summary>
/// Bounded, deterministic playback-rate handoff for the player entry-to-travel boundary (CTRL-001 TR10).
/// </summary>
/// <remarks>
/// The i2* entries play at rate 1.0 and end at their authored terminal root speed, while sustained travel
/// follows the stick-demanded rate. Commanding the demanded rate on the boundary frame steps the body speed
/// by the full gap at once (measured at the packaged responses: i2f ends at 0.698 m/s while wf sustains
/// 0.927 m/s per rate unit, a +33% step even before the former 1.2 full-stick overdrive). Mirroring the NPC
/// entry handoff, this helper first publishes the speed-matched rate under a one-sided slow-departure bound:
/// matched rates in [0.8, 1.0] are preserved exactly, slower matches are lifted to the 0.8 floor (at most a
/// 20% slow-down departure), and faster matches collapse to unit rate rather than overdriving. It holds that
/// rate through the entry-to-travel crossfade, then accelerates toward the demanded rate under the same
/// acceleration/jerk limits the NPC uses (2 1/s², 10 1/s³).
/// </remarks>
public sealed class PlayerTravelEntryHandoff
{
    private const float MaximumSlowDeparture = 0.2f;
    private const float MinimumRate = 0.8f;
    private const float MaximumRate = 1.2f;
    private const float MaximumAcceleration = 2f;
    private const float MaximumJerk = 10f;
    private float _remainingHoldSeconds;
    private float _acceleration;

    /// <summary>Whether a handoff window is still owning the published travel rate.</summary>
    public bool Active
    {
        get; private set;
    }

    /// <summary>The rate the handoff currently publishes.</summary>
    public float Rate { get; private set; } = 1f;

    /// <summary>
    /// Begins one handoff window from the entry's terminal authored root speed onto a travel clip whose
    /// rate-1 sustained speed is <paramref name="sustainedUnitSpeed" />.
    /// </summary>
    public void Begin(float entryTerminalSpeed, float sustainedUnitSpeed, float holdSeconds)
    {
        float matched = sustainedUnitSpeed > 0.0001f && float.IsFinite(entryTerminalSpeed) && entryTerminalSpeed >= 0f
            ? entryTerminalSpeed / sustainedUnitSpeed
            : 1f;
        float bounded = 1f - Mathf.Clamp(1f - matched, 0f, MaximumSlowDeparture);
        Rate = Mathf.Clamp(bounded, MinimumRate, MaximumRate);
        _remainingHoldSeconds = Mathf.Max(0f, holdSeconds);
        _acceleration = 0f;
        Active = true;
    }

    /// <summary>Ends the handoff window, returning rate authority to the demanded value.</summary>
    public void Cancel()
    {
        Active = false;
        _acceleration = 0f;
    }

    /// <summary>
    /// Steps the published rate for one frame: the matched rate during the hold window, then an
    /// acceleration-limited ramp toward <paramref name="demandedRate" /> that ends the window on convergence.
    /// </summary>
    public float Step(float demandedRate, float delta)
    {
        if (!Active)
        {
            return FiniteOr(demandedRate, 1f);
        }

        if (delta <= 0f || !float.IsFinite(delta))
        {
            return Rate;
        }

        if (_remainingHoldSeconds > 0f)
        {
            _remainingHoldSeconds -= delta;
            return Rate;
        }

        float target = FiniteOr(demandedRate, Rate);
        Rate = StepRate(Rate, target, ref _acceleration, delta);
        if (Mathf.IsEqualApprox(Rate, target))
        {
            Rate = target;
            _acceleration = 0f;
            Active = false;
        }

        return Rate;
    }

    private static float StepRate(float current, float target, ref float acceleration, float delta)
    {
        float offset = target - current;
        float desiredAcceleration = Mathf.Clamp(offset / delta, -MaximumAcceleration, MaximumAcceleration);
        acceleration = Mathf.MoveToward(acceleration, desiredAcceleration, MaximumJerk * delta);
        float step = acceleration * delta;
        if (Mathf.Sign(step) == Mathf.Sign(offset) && Mathf.Abs(step) >= Mathf.Abs(offset))
        {
            acceleration = 0f;
            return target;
        }

        return current + step;
    }

    private static float FiniteOr(float value, float fallback) => float.IsFinite(value) ? value : fallback;
}
