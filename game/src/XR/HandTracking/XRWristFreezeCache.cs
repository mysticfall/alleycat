using Godot;

namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Per-side cache of the last valid world-space optical wrist transform with freeze-on-loss semantics
/// (XR-002 TR5, TR25).
/// </summary>
/// <remarks>
/// A captured transform stays available while tracking is lost, so consumers keep receiving the last valid
/// world-space pose. Capturing a non-finite sample is rejected and treated as a loss (XR-002 TR20).
/// <see cref="Reset" /> ends the current optical session.
/// </remarks>
public sealed class XRWristFreezeCache
{
    private Transform3D _lastValid = Transform3D.Identity;

    /// <summary>
    /// Gets whether at least one valid wrist sample has been captured during the current session.
    /// </summary>
    public bool EverCaptured
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets whether tracking is currently lost while a previously captured sample is retained.
    /// </summary>
    public bool IsFrozen
    {
        get;
        private set;
    }

    /// <summary>
    /// Tries to get the latest valid world-space wrist transform, including the frozen sample while tracking is lost.
    /// </summary>
    /// <param name="transform">Retained world-space wrist transform.</param>
    /// <returns>
    /// <see langword="true" /> when a valid sample has been captured during this session; otherwise
    /// <see langword="false" />.
    /// </returns>
    public bool TryGetTransform(out Transform3D transform)
    {
        transform = _lastValid;

        return EverCaptured;
    }

    /// <summary>
    /// Captures a valid world-space wrist sample. Non-finite samples are rejected and treated as a loss.
    /// </summary>
    /// <param name="worldTransform">World-space wrist transform reported by the runtime.</param>
    public void Capture(Transform3D worldTransform)
    {
        if (!XRHandTrackingMath.IsFinite(worldTransform))
        {
            MarkLost();

            return;
        }

        _lastValid = worldTransform;
        EverCaptured = true;
        IsFrozen = false;
    }

    /// <summary>
    /// Marks tracking as lost without mutating the retained world-space transform.
    /// </summary>
    public void MarkLost()
    {
        if (EverCaptured)
        {
            IsFrozen = true;
        }
    }

    /// <summary>
    /// Clears the cached sample and lifecycle flags, ending the current optical session.
    /// </summary>
    public void Reset()
    {
        _lastValid = Transform3D.Identity;
        EverCaptured = false;
        IsFrozen = false;
    }
}
