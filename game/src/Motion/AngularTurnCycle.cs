namespace AlleyCat.Motion;

/// <summary>One service cycle over the measured angular source, with the original two foot-lift phases.</summary>
internal readonly record struct AngularTurnCycle(StationaryTurnReference Reference, double StartYaw, double Allocation)
{
    internal const double Maximum = Math.Tau / 3;
    internal double Scale => Allocation / Reference.SignedSpan;

    internal static AngularTurnCycle Allocate(StationaryTurnReference reference, double startYaw, double requestedArc) =>
        new(reference, startYaw, reference.Sign * Math.Min(requestedArc, Maximum));

    internal double YawAt(double localPhase) => MapYaw(Reference, StartYaw, Scale, localPhase);

    internal static double MapYaw(StationaryTurnReference reference, double startYaw, double scale, double localPhase) =>
        startYaw + (reference.Sign * scale * reference.Angle(localPhase));
}

/// <summary>Immutable source interval between played cues; a join may cross between two differently scaled cycles.</summary>
internal readonly record struct AngularTurnInterval(AngularTurnCycle Previous, AngularTurnCycle Current,
    int FromPhase, int ToPhase)
{
    internal double AtFrame(double frame, int lastFrame)
    {
        double progress = lastFrame == 0 ? 1 : Math.Clamp(frame / lastFrame, 0, 1);
        double phase = FromPhase + ((ToPhase - FromPhase) * progress);
        return phase < 0 ? Previous.YawAt(Previous.Reference.Period + phase) : Current.YawAt(phase);
    }
}
