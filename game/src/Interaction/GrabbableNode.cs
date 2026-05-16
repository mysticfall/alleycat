using AlleyCat.Core;
using Godot;

namespace AlleyCat.Interaction;

/// <summary>
/// Simple scene-authored grabbable holder that delegates to child grab-point components.
/// </summary>
[GlobalClass]
public partial class GrabbableNode : Node3D, IGrabbable, IReleasableGrabbable
{
    private const float FreshnessPositionToleranceMetres = 0.001f;
    private const float FreshnessBasisTolerance = 0.001f;

    private IComponent[] _components = [];
    private GrabPointCandidate? _activeGrabPoint;

    /// <summary>
    /// Whether this object is currently held by a hand.
    /// </summary>
    public bool IsGrabbed => _activeGrabPoint is not null;

    /// <inheritdoc />
    public IReadOnlyList<IComponent> Components => _components;

    /// <inheritdoc />
    [Export]
    public GrabbableMobility Mobility { get; set; } = GrabbableMobility.Movable;

    /// <inheritdoc />
    public override void _Ready()
    {
        AddToGroup("grabbable");
        RefreshComponents();
    }

    /// <summary>
    /// Refreshes the deterministic child component cache.
    /// </summary>
    public void RefreshComponents()
    {
        var components = new List<IComponent>();
        foreach (Node child in GetChildren())
        {
            if (child is IComponent component)
            {
                components.Add(component);
            }
        }

        _components = [.. components];
    }

    /// <inheritdoc />
    public bool Grab(GrabPointCandidate grabPoint)
    {
        RefreshComponents();
        if (_activeGrabPoint is not null
            || !_components.Contains(grabPoint.Source)
            || !IsCandidateFresh(grabPoint))
        {
            return false;
        }

        _activeGrabPoint = grabPoint;
        return true;
    }

    /// <inheritdoc />
    public void Release() => _activeGrabPoint = null;

    private static bool IsCandidateFresh(GrabPointCandidate candidate)
    {
        GrabPointCandidate? refreshedCandidate = candidate.Source.GetGrabPoint(candidate.HandSide, candidate.HandTransform);
        return refreshedCandidate is not null
            && ReferenceEquals(refreshedCandidate.Source, candidate.Source)
            && IsMatchingGrabPointTransform(refreshedCandidate.GrabPointTransform, candidate.GrabPointTransform)
            && ReferenceEquals(refreshedCandidate.Animation, candidate.Animation)
            && refreshedCandidate.HandTarget.Origin.DistanceSquaredTo(candidate.HandTarget.Origin)
                <= FreshnessPositionToleranceMetres * FreshnessPositionToleranceMetres
            && refreshedCandidate.HandTarget.Basis.X.DistanceSquaredTo(candidate.HandTarget.Basis.X)
                <= FreshnessBasisTolerance * FreshnessBasisTolerance
            && refreshedCandidate.HandTarget.Basis.Y.DistanceSquaredTo(candidate.HandTarget.Basis.Y)
                <= FreshnessBasisTolerance * FreshnessBasisTolerance
            && refreshedCandidate.HandTarget.Basis.Z.DistanceSquaredTo(candidate.HandTarget.Basis.Z)
                <= FreshnessBasisTolerance * FreshnessBasisTolerance;
    }

    private static bool IsMatchingGrabPointTransform(Transform3D refreshedTransform, Transform3D candidateTransform)
        => refreshedTransform.Origin.DistanceSquaredTo(candidateTransform.Origin)
                <= FreshnessPositionToleranceMetres * FreshnessPositionToleranceMetres
            && refreshedTransform.Basis.X.DistanceSquaredTo(candidateTransform.Basis.X)
                <= FreshnessBasisTolerance * FreshnessBasisTolerance
            && refreshedTransform.Basis.Y.DistanceSquaredTo(candidateTransform.Basis.Y)
                <= FreshnessBasisTolerance * FreshnessBasisTolerance
            && refreshedTransform.Basis.Z.DistanceSquaredTo(candidateTransform.Basis.Z)
                <= FreshnessBasisTolerance * FreshnessBasisTolerance;
}
