namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Committed global hand-pose mode shared by both hands (XR-002 TR1).
/// </summary>
public enum XRHandTrackingMode
{
    /// <summary>
    /// Hand poses follow the XR controllers.
    /// </summary>
    Controller = 0,

    /// <summary>
    /// Hand poses follow optical hand tracking.
    /// </summary>
    Optical = 1,
}
