using Godot;

namespace AlleyCat.Mind.Attention;

/// <summary>
/// One immutable upstream snapshot captured for a head evaluation (AI-009 TR 14): everything the
/// aimed-origin construction consumes, frozen at one evaluation. The upstream geometry comes from
/// native skeleton bone poses plus authored marker offsets — never solved attachment globals — so it
/// describes the animation-owned input the engine restores between skeleton updates, not the solve.
/// </summary>
/// <param name="Generation">Binding generation; changes on rebind/teardown invalidate the packet.</param>
/// <param name="EvaluationSequence">Monotonic evaluation counter identifying this capture.</param>
/// <param name="DeltaSeconds">The controller evaluation delta supplied with this capture.</param>
/// <param name="EvaluationMode">The owning callback: "begin" for installed skeleton evaluations.</param>
/// <param name="SkeletonIdentity">Instance identity of the captured skeleton.</param>
/// <param name="CharacterGlobalTransform">The owning character's global transform at capture.</param>
/// <param name="SkeletonGlobalTransform">The skeleton's global transform at capture.</param>
/// <param name="HeadOriginWorld">Upstream Head bone-frame origin in world space (h).</param>
/// <param name="PivotPositionWorld">Upstream pivot position in world space (p); equals
/// <paramref name="HeadOriginWorld"/> semantics of the no-pivot fallback when no valid pivot exists.</param>
/// <param name="UpstreamFaceBasisWorld">Upstream face rotation in world space (U): the upstream Head bone
/// rotation with the authored face/Viewpoint offset rotation applied.</param>
/// <param name="HasPivot">Whether a valid authored pivot contributed <paramref name="PivotPositionWorld"/>.</param>
internal sealed record OrientingUpstreamSnapshot(
    ulong Generation,
    ulong EvaluationSequence,
    double DeltaSeconds,
    string EvaluationMode,
    ulong SkeletonIdentity,
    Transform3D CharacterGlobalTransform,
    Transform3D SkeletonGlobalTransform,
    Vector3 HeadOriginWorld,
    Vector3 PivotPositionWorld,
    Basis UpstreamFaceBasisWorld,
    bool HasPivot);

/// <summary>
/// Non-recursive aimed-origin construction (AI-009 TR 21). For upstream head origin <c>h</c>, upstream
/// pivot position <c>p</c>, upstream face rotation <c>U</c> and commanded face rotation <c>D</c>, all in
/// one declared world frame and all from the same upstream snapshot:
/// <c>R = D * inverse(U)</c> and <c>target = p + R * (h − p)</c>. Solved output is not an input, so for
/// constant upstream geometry and command the solve cannot advance the target; <c>D = U</c> yields
/// <c>target = h</c> and infinitesimal rotation approaches zero displacement. Without a valid pivot the
/// target is the upstream head origin itself (TR 22).
/// </summary>
internal static class OrientingAimGeometry
{
    /// <summary>
    /// Constructs the aimed-origin target from one upstream snapshot and the commanded face basis.
    /// Rotations are proper orthonormal bases; positions retain their captured scale.
    /// </summary>
    public static Vector3 ConstructAimedTarget(
        Basis commandFaceBasis,
        Basis upstreamFaceBasis,
        Vector3 upstreamHeadOrigin,
        Vector3 upstreamPivotPosition)
    {
        Basis rotation = commandFaceBasis.Orthonormalized()
            * upstreamFaceBasis.Orthonormalized().Inverse();
        return upstreamPivotPosition + (rotation * (upstreamHeadOrigin - upstreamPivotPosition));
    }

    /// <summary>
    /// Constructs the aimed-origin target from a captured snapshot, applying the TR 22 no-pivot
    /// fallback (the upstream head origin) when the snapshot carries no valid pivot.
    /// </summary>
    public static Vector3 ConstructAimedTarget(OrientingUpstreamSnapshot snapshot, Basis commandFaceBasis)
        => snapshot.HasPivot
            ? ConstructAimedTarget(
                commandFaceBasis,
                snapshot.UpstreamFaceBasisWorld,
                snapshot.HeadOriginWorld,
                snapshot.PivotPositionWorld)
            : snapshot.HeadOriginWorld;
}
