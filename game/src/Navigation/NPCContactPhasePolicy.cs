using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Navigation;

/// <summary>Shared contact qualification and yaw-assistance policy for the mapped sustained release lane.</summary>
public static class NPCContactPhasePolicy
{
    public const float SupportPhaseStart = 0.06f;
    public const float SupportPhaseEnd = 0.12f;
    public const float SupportHeightToleranceMetres = 0.015f;
    public const float SupportVerticalSpeedToleranceMetresPerSecond = 0.10f;
    public const float VisibleSlipPerFrameMetres = 0.002f;

    public static NPCAnimationPhaseLane DeclaredSupportLane(NPCAnimationPhaseLane releaseLane)
        => releaseLane == NPCAnimationPhaseLane.Left
            ? NPCAnimationPhaseLane.Right
            : NPCAnimationPhaseLane.Left;

    public static float CircularReleaseDistance(float phase)
    {
        float wrapped = Mathf.PosMod(phase, 1f);
        return Math.Min(wrapped, 1f - wrapped);
    }

    public static bool IsDeclaredSupportWindow(float phase)
    {
        float wrapped = Mathf.PosMod(phase, 1f);
        return wrapped is >= SupportPhaseStart and <= SupportPhaseEnd;
    }

    public static bool QualifiesContact(float heightAboveLowerFoot, float verticalSpeed)
        => float.IsFinite(heightAboveLowerFoot)
            && float.IsFinite(verticalSpeed)
            && heightAboveLowerFoot >= 0f
            && heightAboveLowerFoot <= SupportHeightToleranceMetres
            && Math.Abs(verticalSpeed) <= SupportVerticalSpeedToleranceMetresPerSecond;

    public static float YawInducedTangentialDisplacement(float supportRadius, float yawDelta)
        => float.IsFinite(supportRadius) && float.IsFinite(yawDelta)
            ? Math.Abs(supportRadius) * Math.Abs(yawDelta)
            : float.PositiveInfinity;

    public static float PlanarSlipSpeed(Vector3 previousWorldPosition, Vector3 currentWorldPosition, float delta)
    {
        if (!previousWorldPosition.IsFinite() || !currentWorldPosition.IsFinite() || !float.IsFinite(delta) || delta <= 0f)
        {
            return float.PositiveInfinity;
        }

        Vector3 displacement = currentWorldPosition - previousWorldPosition;
        return new Vector2(displacement.X, displacement.Z).Length() / delta;
    }

    public static bool ExceedsVisibleSlip(float planarDisplacement, float delta)
        => float.IsFinite(planarDisplacement)
            && float.IsFinite(delta)
            && delta > 0f
            && planarDisplacement > VisibleSlipPerFrameMetres;
}
