namespace AlleyCat.Navigation;

/// <summary>NAV semantic policy, independent of mesh subdivision, source phase and CTRL stick classification.</summary>
internal static class NavigationRolePolicy
{
    internal sealed record Settings(double NearEnter = 1.5, double NearExit = 1.75,
        double FacingDegrees = 35, double LateralBandDegrees = 20, double HysteresisDegrees = 10,
        double CrossingDistance = .75, double ShortMoveDistance = .5);

    internal sealed record Input(double Forward, double Left, double RemainingJourney, double DestinationDistance,
        double FacingError, double ActualForwardDelta, double ActualLeftDelta, bool PositionReached,
        bool FacingReached, long Generation, long RouteRevision);

    internal sealed record Decision(string Role, bool Positioning, string Reason, Input Inputs);

    internal static Decision Select(Input input, Settings settings, Decision? previous)
    {
        if (!double.IsFinite(input.Forward + input.Left + input.RemainingJourney + input.DestinationDistance +
            input.FacingError + input.ActualForwardDelta + input.ActualLeftDelta) || input.RemainingJourney < 0 || input.DestinationDistance < 0 ||
            !double.IsFinite(settings.NearEnter + settings.NearExit + settings.FacingDegrees + settings.LateralBandDegrees +
                settings.HysteresisDegrees + settings.CrossingDistance + settings.ShortMoveDistance) || settings.NearEnter <= 0 || settings.NearExit < settings.NearEnter ||
            settings.FacingDegrees < 0 || settings.LateralBandDegrees is < 0 or >= 45 || settings.HysteresisDegrees is < 0 or >= 20 ||
            settings.CrossingDistance < 0 || settings.CrossingDistance > settings.NearEnter || settings.ShortMoveDistance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Invalid NAV role policy input or settings.");
        }

        bool continuous = previous?.Inputs.Generation == input.Generation && previous.Inputs.RouteRevision == input.RouteRevision;
        if (!continuous)
        {
            previous = null;
        }

        double facing = Math.Abs(input.FacingError) * 180 / Math.PI;
        if (input.PositionReached)
        {
            return new(input.FacingReached ? "" : input.FacingError > 0 ? "left_turn" : "right_turn", true, "terminal-facing", input);
        }

        double limit = previous?.Positioning == true ? settings.NearExit : settings.NearEnter;
        bool nearby = Math.Max(input.RemainingJourney, input.DestinationDistance) <= limit;
        double facingLimit = settings.FacingDegrees + (previous?.Positioning == true ? settings.HysteresisDegrees : 0);
        // Final facing must not turn a tiny complete adjustment into a route-scale forward loop.
        // Both distances matter: a short next mesh edge does not shorten a detour or long journey.
        bool shortJourney = Math.Max(input.RemainingJourney, input.DestinationDistance) <= settings.ShortMoveDistance;
        if (!nearby || (!shortJourney && facing > facingLimit))
        {
            return new("forward", false, !nearby ? "remaining-journey" : "route-facing-travel", input);
        }

        // A vanishing displacement uses actual realised movement, never source phase, as the directional tie.
        double left = input.Left, forward = input.Forward;
        if (Math.Abs(left) + Math.Abs(forward) == 0)
        {
            left = input.ActualLeftDelta;
            forward = input.ActualForwardDelta;
        }
        double angle = Math.Abs(Math.Atan2(left, forward)) * 180 / Math.PI;
        string side = left >= 0 ? "left" : "right";
        double band = settings.LateralBandDegrees + (previous?.Role == side + "_noncross" ? settings.HysteresisDegrees : 0);
        string role;
        string reason;
        if (previous?.Role == side + "_cross" && Math.Max(input.RemainingJourney, input.DestinationDistance) <= settings.CrossingDistance &&
            facing <= 5 && angle >= 55 - (settings.HysteresisDegrees / 2) && angle <= 70 + (settings.HysteresisDegrees / 2))
        {
            return new(side + "_cross", true, "bounded-oblique-hysteresis", input);
        }

        if (Math.Abs(angle - 90) <= band)
        {
            role = side + "_noncross";
            reason = "nearby-lateral";
        }
        else if (Math.Max(input.RemainingJourney, input.DestinationDistance) <= settings.CrossingDistance && facing <= 5 && angle is >= 55 and <= 70)
        {
            role = side + "_cross";
            reason = "bounded-facing-preserving-oblique";
        }
        else
        {
            role = angle < 90 ? "forward" : "backward";
            reason = "nearby-direct-positioning";
        }
        return new(role, true, reason, input);
    }
}
