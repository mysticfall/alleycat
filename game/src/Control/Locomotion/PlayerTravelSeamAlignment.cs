namespace AlleyCat.Control.Locomotion;

/// <summary>
/// Pure lane-seam arithmetic for the player sustained-travel blend spaces (ANIM-002 §7.7).
/// </summary>
/// <remarks>
/// The four player travel blend spaces share one cyclic clock (SYNC_MODE_CYCLIC_CONSTANT over the common
/// 1.0667 s cycle), but a shared clock alone leaves every member starting at its own source phase zero. This
/// helper resolves the per-member custom-timeline start offset that places the retained lane's declared phase
/// event exactly at shared phase zero, so an i2* entry ending on its mapped event hands off
/// to a travel cycle whose feet continue the same swing. The engine mapping is playback = (clock + offset) ×
/// source_duration / timeline_length, so scaling the packaged event into timeline units —
/// offset = event × common_cycle / source_duration — evaluates shared phase zero exactly at the event.
/// </remarks>
public static class PlayerTravelSeamAlignment
{
    /// <summary>
    /// Resolves the custom-timeline start offset, in timeline seconds, that aligns the given clip's lane phase
    /// event with the shared cycle seam.
    /// </summary>
    /// <param name="clip">Catalogued cyclic-travel clip played by one blend point.</param>
    /// <param name="rightLane">Whether the serving blend space belongs to the retained right lane.</param>
    /// <param name="commonCycleSeconds">Common cycle duration shared by the blend space (its cyclic length).</param>
    public static float ResolveStartOffset(StandingLocomotionClip clip, bool rightLane, float commonCycleSeconds)
    {
        StandingLocomotionPhaseEvent phaseEvent = ResolveCyclicEvent(clip, rightLane);
        double eventSeconds = phaseEvent.TimeSeconds;
        double duration = Math.Max(clip.DurationSeconds, double.Epsilon);
        double cycle = float.IsFinite(commonCycleSeconds) && commonCycleSeconds > 0f ? commonCycleSeconds : duration;
        return (float)Math.Clamp(eventSeconds * cycle / duration, 0d, cycle);
    }

    private static StandingLocomotionPhaseEvent ResolveCyclicEvent(StandingLocomotionClip clip, bool rightLane)
    {
        StandingLocomotionAnatomicalSide side = rightLane
            ? StandingLocomotionAnatomicalSide.Right
            : StandingLocomotionAnatomicalSide.Left;
        int count = 0;
        StandingLocomotionPhaseEvent result = default;
        foreach (StandingLocomotionPhaseEvent candidate in clip.PhaseEvents.Values)
        {
            if (candidate.Side == side && candidate.BoundaryContext == StandingLocomotionBoundaryContext.Cyclic)
            {
                result = candidate;
                count++;
            }
        }

        return count switch
        {
            1 => result,
            0 => throw new InvalidOperationException(
                $"ANIM-002 cyclic role '{clip.Role}' declares no {side} phase event."),
            _ => throw new InvalidOperationException(
                $"ANIM-002 cyclic role '{clip.Role}' ambiguously declares {count} {side} phase events."),
        };
    }
}
