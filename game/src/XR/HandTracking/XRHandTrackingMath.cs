using Godot;

namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Shared transform validation helpers for hand-tracking sample acceptance (XR-002 TR20).
/// </summary>
internal static class XRHandTrackingMath
{
    /// <summary>
    /// Returns whether the transform basis rows and origin are all finite.
    /// </summary>
    /// <param name="transform">Transform to validate.</param>
    /// <returns><see langword="true" /> when every component is finite; otherwise <see langword="false" />.</returns>
    internal static bool IsFinite(Transform3D transform)
        => transform.Origin.IsFinite()
           && transform.Basis.Row0.IsFinite()
           && transform.Basis.Row1.IsFinite()
           && transform.Basis.Row2.IsFinite();
}
