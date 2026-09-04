using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.XR.HandTracking;

/// <summary>Authorable neutral-normalisation values for one finger destination, thumb or non-thumb (XR-002 TR44).</summary>
[Tool]
[GlobalClass]
public partial class OpticalFingerTrackingCalibrationEntry : Resource
{
    /// <summary>Tracked hand side to which this record applies.</summary>
    [Export]
    public LimbSide Side
    {
        get; set;
    }

    /// <summary>Destination joint to which this record applies.</summary>
    [Export]
    public XRHandJoint Joint { get; set; } = XRHandJoint.IndexProximal;

    /// <summary>Immutable straight-neutral source relation, S0.</summary>
    [Export]
    public Quaternion SourceNeutral { get; set; } = Quaternion.Identity;

    /// <summary>
    /// Destination-neutral provenance metadata; never applied at runtime. Production derives the effective
    /// non-thumb N from the bound skeleton's rest geometry and the thumb N from the sampled Reset key
    /// (XR-002 TR29), preventing profile and rig neutral correction being composed twice. Thumb records mirror
    /// the Reset-sampled thumb neutrals here as provenance (XR-002 TR44, TR29).
    /// </summary>
    [Export]
    public Quaternion DestinationNeutral { get; set; } = Quaternion.Identity;

    /// <summary>Source-to-destination local-basis correspondence, K.</summary>
    [Export]
    public Quaternion BasisCorrespondence { get; set; } = Quaternion.Identity;

    /// <summary>Reserved generic gain. The current profile contract accepts unit gain only.</summary>
    [Export]
    public float Gain { get; set; } = 1.0f;
}
