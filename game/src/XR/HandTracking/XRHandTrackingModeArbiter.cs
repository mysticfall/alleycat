namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Pure bilateral arbiter for the committed global hand-pose mode (XR-002 TR1-TR5).
/// </summary>
/// <remarks>
/// The committed mode switches only when the left and right observations agree on the same non-ambiguous proposal;
/// disagreement, ambiguity, or missing observations retain the prior committed mode. Optical sample loss is an
/// observation-level concern that feeds ambiguity and therefore never exits
/// <see cref="XRHandTrackingMode.Optical" /> on its own.
/// </remarks>
public sealed class XRHandTrackingModeArbiter
{
    /// <summary>
    /// Gets the committed global hand-pose mode. The initial committed mode is
    /// <see cref="XRHandTrackingMode.Controller" /> (XR-002 TR1).
    /// </summary>
    public XRHandTrackingMode CommittedMode
    {
        get;
        private set;
    } = XRHandTrackingMode.Controller;

    /// <summary>
    /// Raised exactly once for each evaluation that changes the committed mode, and never raised while the committed
    /// mode is retained.
    /// </summary>
    public event Action? ModeChanged;

    /// <summary>
    /// Re-evaluates the committed mode from one bilateral observation pair in a single atomic step (XR-002 TR3-TR4).
    /// </summary>
    /// <param name="left">Left-hand observation derived from the runtime.</param>
    /// <param name="right">Right-hand observation derived from the runtime.</param>
    /// <returns>
    /// <see langword="true" /> when the committed mode changed during this evaluation; otherwise <see langword="false" />.
    /// </returns>
    public bool Evaluate(XRHandSourceObservation left, XRHandSourceObservation right)
    {
        XRHandTrackingMode? proposal = ResolveAgreedProposal(left, right);

        if (proposal is null || proposal == CommittedMode)
        {
            return false;
        }

        CommittedMode = proposal.Value;
        ModeChanged?.Invoke();

        return true;
    }

    private static XRHandTrackingMode? ResolveAgreedProposal(XRHandSourceObservation left, XRHandSourceObservation right)
        => (left, right) switch
        {
            (XRHandSourceObservation.Controller, XRHandSourceObservation.Controller) => XRHandTrackingMode.Controller,
            (XRHandSourceObservation.Optical, XRHandSourceObservation.Optical) => XRHandTrackingMode.Optical,
            _ => null,
        };
}
