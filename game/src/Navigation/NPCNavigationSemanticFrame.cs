using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Navigation;

/// <summary>
/// Explicit conversion boundary between NAV world/actor semantics and ANIM-002's reference-rig frame.
/// </summary>
public readonly record struct NPCNavigationSemanticFrame(Basis ActorWorldBasis, Basis RootReferenceWorldBasis)
{
    public static readonly Vector3 ActorLocalForward = Vector3.Forward;
    public static readonly Vector3 SkeletonLocalAvatarForward = Vector3.Back;
    public static readonly Vector3 SkeletonLocalAvatarRight = Vector3.Left;

    public Basis ActorRotation => ActorWorldBasis.Orthonormalized();
    public Basis RootReferenceRotation => RootReferenceWorldBasis.Orthonormalized();

    /// <summary>Converts a world route vector to conventional actor-semantic X-right/Y-forward coordinates.</summary>
    public Vector2 WorldRouteToActorSemantic(Vector3 worldVector)
    {
        Vector3 actorLocal = ActorRotation.Transposed() * worldVector;
        return new Vector2(actorLocal.X, -actorLocal.Z);
    }

    /// <summary>Converts conventional actor-semantic X-right/Y-forward coordinates to world space.</summary>
    public Vector3 ActorSemanticToWorld(Vector2 semanticVector)
        => ActorRotation * new Vector3(semanticVector.X, 0f, -semanticVector.Y);

    /// <summary>
    /// Converts an evaluated Skeleton-local Root delta to world space. The authored 180-degree reference-rig basis is
    /// applied exactly once here; callers must not pre-rotate or post-rotate the result.
    /// </summary>
    public Vector3 EvaluatedRootDeltaToWorld(Vector3 skeletonLocalDelta)
        => RootReferenceRotation * skeletonLocalDelta;

    /// <summary>Converts evaluated Skeleton-local Root rotation into the actor motor's local rotation frame.</summary>
    public Quaternion EvaluatedRootRotationToActorLocal(Quaternion skeletonLocalRotation)
    {
        Basis rootRelativeToActor = ActorRotation.Transposed() * RootReferenceRotation;
        Basis converted = rootRelativeToActor * new Basis(skeletonLocalRotation) * rootRelativeToActor.Transposed();
        return converted.GetRotationQuaternion();
    }

    public Vector3 ActorWorldForward => ActorRotation * ActorLocalForward;
    public Vector3 VisualWorldForward => RootReferenceRotation * SkeletonLocalAvatarForward;
}
