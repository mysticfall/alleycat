using Godot;

namespace AlleyCat.Navigation;

/// <summary>
/// Character-specific standing locomotion profile identifiers.
/// </summary>
public enum StandingLocomotionCharacter
{
    /// <summary>The reference female Walking graph clip map.</summary>
    ReferenceFemale,

    /// <summary>The reference male Walking graph clip map.</summary>
    ReferenceMale,
}

/// <summary>
/// Normalised-cycle response measured from one selected standing catalogue clip.
/// </summary>
/// <remarks>
/// Planar coordinates use metres in actor-local Godot space: +X is right and +Y is forwards. Yaw uses radians around
/// Godot world up: positive turns left and negative turns right. Rates are per metric second, not per imported Godot
/// timeline second; both durations are retained so consumers cannot silently interchange them.
/// </remarks>
public readonly struct LocomotionCycleResponse
{
    /// <summary>
    /// Creates a validated normalised-cycle response.
    /// </summary>
    public LocomotionCycleResponse(
        Vector2 planarDisplacement,
        float averagePlanarSpeed,
        float yaw,
        float metricDurationSeconds,
        float importedTimelineDurationSeconds)
    {
        if (!planarDisplacement.IsFinite()
            || !float.IsFinite(averagePlanarSpeed)
            || averagePlanarSpeed < 0.0f
            || !float.IsFinite(yaw)
            || !float.IsFinite(metricDurationSeconds)
            || metricDurationSeconds <= 0.0f
            || !float.IsFinite(importedTimelineDurationSeconds)
            || importedTimelineDurationSeconds <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(planarDisplacement), "Locomotion response values must be finite and durations must be positive.");
        }

        PlanarDisplacement = planarDisplacement;
        AveragePlanarSpeed = averagePlanarSpeed;
        Yaw = yaw;
        MetricDurationSeconds = metricDurationSeconds;
        ImportedTimelineDurationSeconds = importedTimelineDurationSeconds;
    }

    /// <summary>
    /// Gets average actor-local planar displacement over one normalised cycle, in metres.
    /// </summary>
    public Vector2 PlanarDisplacement
    {
        get;
    }

    /// <summary>
    /// Gets average planar path speed over the metric cycle, in metres per second.
    /// </summary>
    public float AveragePlanarSpeed
    {
        get;
    }

    /// <summary>
    /// Gets signed yaw over one normalised cycle, in radians.
    /// </summary>
    public float Yaw
    {
        get;
    }

    /// <summary>
    /// Gets the metrics interval used to calculate response rates, in seconds.
    /// </summary>
    public float MetricDurationSeconds
    {
        get;
    }

    /// <summary>
    /// Gets the corresponding imported Godot animation timeline duration, in seconds.
    /// </summary>
    public float ImportedTimelineDurationSeconds
    {
        get;
    }

    /// <summary>
    /// Gets average signed planar translation rate in metres per metric second.
    /// </summary>
    public Vector2 PlanarVelocity => PlanarDisplacement / MetricDurationSeconds;

    /// <summary>
    /// Gets average signed angular rate in radians per metric second.
    /// </summary>
    public float AngularVelocity => Yaw / MetricDurationSeconds;
}

/// <summary>
/// Compact immutable response model for the eight moving and bilateral stationary-turn roles used by a character's
/// standing Walking graph.
/// </summary>
public sealed class LocomotionResponseProfile
{
    internal LocomotionResponseProfile(
        StandingLocomotionCharacter character,
        LocomotionCycleResponse forwards,
        LocomotionCycleResponse backwards,
        LocomotionCycleResponse sideStepLeft,
        LocomotionCycleResponse sideStepRight,
        LocomotionCycleResponse walkArcLeft,
        LocomotionCycleResponse walkArcRight,
        LocomotionCycleResponse turnInPlaceLeft90,
        LocomotionCycleResponse turnInPlaceRight90)
    {
        ValidateDirections(
            forwards,
            backwards,
            sideStepLeft,
            sideStepRight,
            walkArcLeft,
            walkArcRight,
            turnInPlaceLeft90,
            turnInPlaceRight90);
        Character = character;
        Forwards = forwards;
        Backwards = backwards;
        SideStepLeft = sideStepLeft;
        SideStepRight = sideStepRight;
        WalkArcLeft = walkArcLeft;
        WalkArcRight = walkArcRight;
        TurnInPlaceLeft90 = turnInPlaceLeft90;
        TurnInPlaceRight90 = turnInPlaceRight90;
    }

    /// <summary>Gets the character clip map represented by this profile.</summary>
    public StandingLocomotionCharacter Character
    {
        get;
    }

    /// <summary>Gets forwards-cycle response.</summary>
    public LocomotionCycleResponse Forwards
    {
        get;
    }

    /// <summary>Gets backwards-cycle response.</summary>
    public LocomotionCycleResponse Backwards
    {
        get;
    }

    /// <summary>Gets left side-step-cycle response.</summary>
    public LocomotionCycleResponse SideStepLeft
    {
        get;
    }

    /// <summary>Gets right side-step-cycle response.</summary>
    public LocomotionCycleResponse SideStepRight
    {
        get;
    }

    /// <summary>Gets left walking-arc-cycle response.</summary>
    public LocomotionCycleResponse WalkArcLeft
    {
        get;
    }

    /// <summary>Gets right walking-arc-cycle response.</summary>
    public LocomotionCycleResponse WalkArcRight
    {
        get;
    }

    /// <summary>Gets the finite left 90-degree turn-in-place-cycle response.</summary>
    public LocomotionCycleResponse TurnInPlaceLeft90
    {
        get;
    }

    /// <summary>Gets the finite right 90-degree turn-in-place-cycle response.</summary>
    public LocomotionCycleResponse TurnInPlaceRight90
    {
        get;
    }

    private static void ValidateDirections(
        LocomotionCycleResponse forwards,
        LocomotionCycleResponse backwards,
        LocomotionCycleResponse sideStepLeft,
        LocomotionCycleResponse sideStepRight,
        LocomotionCycleResponse walkArcLeft,
        LocomotionCycleResponse walkArcRight,
        LocomotionCycleResponse turnInPlaceLeft90,
        LocomotionCycleResponse turnInPlaceRight90)
    {
        if (forwards.PlanarDisplacement.Y <= 0.0f
            || backwards.PlanarDisplacement.Y >= 0.0f
            || sideStepLeft.PlanarDisplacement.X >= 0.0f
            || sideStepRight.PlanarDisplacement.X <= 0.0f
            || walkArcLeft.Yaw <= 0.0f
            || walkArcRight.Yaw >= 0.0f
            || turnInPlaceLeft90.Yaw <= 0.0f
            || turnInPlaceRight90.Yaw >= 0.0f
            || !Mathf.IsEqualApprox(Mathf.Abs(turnInPlaceLeft90.Yaw), Mathf.Pi / 2.0f)
            || !Mathf.IsEqualApprox(Mathf.Abs(turnInPlaceRight90.Yaw), Mathf.Pi / 2.0f))
        {
            throw new ArgumentException("Locomotion response directions do not match the documented actor-local sign convention.");
        }
    }
}

/// <summary>
/// Authored runtime profiles derived from the currently selected ANIM-003 standing catalogue metrics.
/// </summary>
public static class StandingLocomotionResponseProfiles
{
    private static readonly LocomotionResponseProfile _referenceFemale = new(
        StandingLocomotionCharacter.ReferenceFemale,
        Response(0.0f, 1.5152825f, 1.5152825f, 0.0f, 1.0f, 1.2916666f),
        Response(0.0f, -1.2085974f, 1.250273f, 0.0f, 0.9666667f, 1.25f),
        Response(-1.4102161f, 0.0f, 1.2087567f, 0.0f, 1.1666667f, 1.5f),
        Response(1.5068623f, 0.0f, 1.2557186f, 0.0f, 1.2f, 1.5416666f),
        Response(-0.28185284f, 1.4387138f, 1.4309370f, 0.4779944f, 1.0333333f, 1.3333334f),
        Response(0.35882136f, 0.9370873f, 0.7173050f, -0.52306855f, 1.4666667f, 1.875f),
        Response(0.0f, 0.0f, 0.0f, Mathf.Pi / 2.0f, 1.5f, 1.9166666f),
        Response(0.0f, 0.0f, 0.0f, -Mathf.Pi / 2.0f, 1.5f, 1.9166666f));

    private static readonly LocomotionResponseProfile _referenceMale = new(
        StandingLocomotionCharacter.ReferenceMale,
        Response(0.0f, 1.5152825f, 1.5152825f, 0.0f, 1.0f, 1.2916666f),
        Response(0.0f, -1.2085974f, 1.250273f, 0.0f, 0.9666667f, 1.25f),
        Response(-1.4102161f, 0.0f, 1.2087567f, 0.0f, 1.1666667f, 1.5f),
        Response(1.5068623f, 0.0f, 1.2557186f, 0.0f, 1.2f, 1.5416666f),
        Response(-0.28185284f, 1.4387138f, 1.4309370f, 0.4779944f, 1.0333333f, 1.3333334f),
        Response(0.35882136f, 0.9370873f, 0.7173050f, -0.52306855f, 1.4666667f, 1.875f),
        Response(0.0f, 0.0f, 0.0f, Mathf.Pi / 2.0f, 1.5f, 1.9166666f),
        Response(0.0f, 0.0f, 0.0f, -Mathf.Pi / 2.0f, 1.5f, 1.9166666f));

    /// <summary>
    /// Gets the immutable profile matching the character's authored Walking graph clip map.
    /// </summary>
    public static LocomotionResponseProfile Get(StandingLocomotionCharacter character) => character switch
    {
        StandingLocomotionCharacter.ReferenceFemale => _referenceFemale,
        StandingLocomotionCharacter.ReferenceMale => _referenceMale,
        _ => throw new ArgumentOutOfRangeException(nameof(character), character, "Unknown standing locomotion character."),
    };

    private static LocomotionCycleResponse Response(
        float right,
        float forwards,
        float averagePlanarSpeed,
        float yaw,
        float metricDurationSeconds,
        float importedTimelineDurationSeconds)
        => new(
            new Vector2(right, forwards),
            averagePlanarSpeed,
            yaw,
            metricDurationSeconds,
            importedTimelineDurationSeconds);
}
