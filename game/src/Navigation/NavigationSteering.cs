using Godot;

namespace AlleyCat.Navigation;

/// <summary>
/// Deterministic horizontal steering calculations shared by navigation consumers and tests.
/// </summary>
public static class NavigationSteering
{
    private const float DirectionEpsilonSquared = 0.000001f;

    /// <summary>
    /// Calculates one coherent intent from an already sampled path.
    /// </summary>
    public static NavigationMotionIntent Calculate(
        Transform3D actorTransform,
        Transform3D destination,
        Vector3 nextPathPosition,
        ReadOnlySpan<Vector3> path,
        int pathIndex,
        Vector3 initialFacing,
        float initialPathLength,
        float travelledPathDistance,
        float initialFacingRampDistance,
        float facingRampDistance,
        float shortMoveDistance,
        float destinationTolerance,
        float pathTolerance,
        float facingToleranceRadians)
    {
        bool hasValidSample = actorTransform.IsFinite() && nextPathPosition.IsFinite();
        Vector3 actorPosition = FiniteOr(actorTransform.Origin, Vector3.Zero);
        Vector3 destinationPosition = FiniteOr(destination.Origin, actorPosition);
        Vector3 actorFacing = HorizontalFacing(actorTransform.Basis, FiniteDirection(initialFacing, Vector3.Forward));
        Vector3 stableInitialFacing = FiniteDirection(initialFacing, actorFacing);
        Vector3 terminalFacing = HorizontalFacing(destination.Basis, stableInitialFacing);
        Vector3 stableNextPosition = FiniteOr(nextPathPosition, actorPosition);
        int firstPathIndex = Math.Clamp(pathIndex, 0, path.Length);
        float remainingDistance = CalculateRemainingDistance(actorPosition, stableNextPosition, path, firstPathIndex);
        float stableInitialLength = NonNegativeFinite(initialPathLength);
        float stableTravelledDistance = NonNegativeFinite(travelledPathDistance);
        float positionDistance = Distance(actorPosition, destinationPosition);
        bool destinationReached = positionDistance <= NonNegativeFinite(destinationTolerance);
        bool terminalPathReached = path.Length == 0
            ? destinationReached
            : remainingDistance <= NonNegativeFinite(pathTolerance);
        bool positionReached = destinationReached && terminalPathReached;

        Vector3 travelDirection = positionReached
            ? Vector3.Zero
            : Direction(actorPosition, stableNextPosition);
        Vector3 initialBearing = travelDirection.LengthSquared() > DirectionEpsilonSquared
            ? travelDirection
            : FirstValidBearing(stableNextPosition, path, firstPathIndex, stableInitialFacing);

        Vector3 desiredFacing;
        if (stableInitialLength <= NonNegativeFinite(shortMoveDistance))
        {
            desiredFacing = terminalFacing;
        }
        else
        {
            float initialWeight = SmoothstepRatio(stableTravelledDistance, initialFacingRampDistance);
            desiredFacing = SlerpHorizontal(stableInitialFacing, initialBearing, initialWeight);
            desiredFacing = ApplyDownstreamRamps(
                desiredFacing,
                actorPosition,
                stableNextPosition,
                path,
                firstPathIndex,
                terminalFacing,
                facingRampDistance);
        }

        if (positionReached)
        {
            desiredFacing = terminalFacing;
        }

        desiredFacing = FiniteDirection(desiredFacing, stableInitialFacing);
        float yawError = SignedYaw(actorFacing, desiredFacing);
        bool facingReached = Mathf.Abs(yawError) <= NonNegativeFinite(facingToleranceRadians);

        return new NavigationMotionIntent(
            stableNextPosition,
            FiniteDirectionOrZero(travelDirection),
            desiredFacing,
            float.IsFinite(yawError) ? yawError : 0.0f,
            NonNegativeFinite(remainingDistance),
            stableTravelledDistance,
            positionReached,
            facingReached,
            hasValidSample && positionReached && facingReached,
            hasValidSample);
    }

    /// <summary>
    /// Limits a requested travel distance to the current three-dimensional sampled path segment.
    /// </summary>
    public static float LimitTravelDistanceToNextPathPosition(
        float requestedDistance,
        Vector3 currentPosition,
        Vector3 nextPathPosition)
        => Mathf.Min(NonNegativeFinite(requestedDistance), Distance(currentPosition, nextPathPosition));

    /// <summary>
    /// Returns a smoothstep value for a travelled distance and ramp length.
    /// </summary>
    public static float SmoothstepRatio(float travelledDistance, float rampDistance)
    {
        float distance = NonNegativeFinite(travelledDistance);
        float ramp = NonNegativeFinite(rampDistance);
        if (ramp <= 0.0f)
        {
            return 1.0f;
        }

        float ratio = Mathf.Clamp(distance / ramp, 0.0f, 1.0f);
        return ratio * ratio * (3.0f - (2.0f * ratio));
    }

    /// <summary>
    /// Returns the shortest signed world-up yaw from one horizontal direction to another.
    /// </summary>
    public static float SignedYaw(Vector3 from, Vector3 to)
    {
        Vector3 stableFrom = FiniteDirection(from, Vector3.Forward);
        Vector3 stableTo = FiniteDirection(to, stableFrom);
        return Mathf.Atan2(stableFrom.Cross(stableTo).Dot(Vector3.Up), stableFrom.Dot(stableTo));
    }

    private static Vector3 ApplyDownstreamRamps(
        Vector3 currentFacing,
        Vector3 actorPosition,
        Vector3 nextPathPosition,
        ReadOnlySpan<Vector3> path,
        int pathIndex,
        Vector3 terminalFacing,
        float rampDistance)
    {
        float stableRampDistance = NonNegativeFinite(rampDistance);
        float distanceToPoint = Distance(actorPosition, nextPathPosition);
        Vector3 previousPoint = nextPathPosition;
        Vector3 incomingBearing = HorizontalDirection(actorPosition, nextPathPosition, currentFacing);
        Vector3 result = currentFacing;

        // Iterate from upstream to downstream. Every active later ramp overwrites the earlier result,
        // which gives deterministic downstream precedence when ramp windows overlap.
        for (int index = pathIndex; index < path.Length; index++)
        {
            Vector3 point = FiniteOr(path[index], previousPoint);
            bool duplicatesNextPosition = index == pathIndex && Distance(point, nextPathPosition) <= 0.0001f;
            if (!duplicatesNextPosition)
            {
                distanceToPoint += Distance(previousPoint, point);
            }

            Vector3 outgoingBearing = index + 1 < path.Length
                ? HorizontalDirection(point, FiniteOr(path[index + 1], point), incomingBearing)
                : terminalFacing;

            if (distanceToPoint <= stableRampDistance)
            {
                float weight = SmoothstepRatio(stableRampDistance - distanceToPoint, stableRampDistance);
                result = SlerpHorizontal(incomingBearing, outgoingBearing, weight);
            }

            incomingBearing = outgoingBearing;
            previousPoint = point;
        }

        return result;
    }

    private static float CalculateRemainingDistance(
        Vector3 actorPosition,
        Vector3 nextPathPosition,
        ReadOnlySpan<Vector3> path,
        int pathIndex)
    {
        float distance = Distance(actorPosition, nextPathPosition);
        Vector3 previous = nextPathPosition;
        for (int index = pathIndex; index < path.Length; index++)
        {
            Vector3 point = FiniteOr(path[index], previous);
            if (index == pathIndex && Distance(point, nextPathPosition) <= 0.0001f)
            {
                previous = point;
                continue;
            }

            distance += Distance(previous, point);
            previous = point;
        }

        return NonNegativeFinite(distance);
    }

    private static Vector3 FirstValidBearing(
        Vector3 nextPathPosition,
        ReadOnlySpan<Vector3> path,
        int pathIndex,
        Vector3 fallback)
    {
        Vector3 previous = nextPathPosition;
        for (int index = pathIndex; index < path.Length; index++)
        {
            Vector3 point = FiniteOr(path[index], previous);
            Vector3 direction = HorizontalDirection(previous, point, Vector3.Zero);
            if (direction.LengthSquared() > DirectionEpsilonSquared)
            {
                return direction;
            }

            previous = point;
        }

        return fallback;
    }

    private static Vector3 SlerpHorizontal(Vector3 from, Vector3 to, float weight)
    {
        Vector3 stableFrom = FiniteDirection(from, Vector3.Forward);
        float yaw = SignedYaw(stableFrom, to);
        return FiniteDirection(stableFrom.Rotated(Vector3.Up, yaw * Mathf.Clamp(weight, 0.0f, 1.0f)), stableFrom);
    }

    private static Vector3 HorizontalFacing(Basis basis, Vector3 fallback)
        => basis.IsFinite() ? FiniteDirection(new Vector3(-basis.Z.X, 0.0f, -basis.Z.Z), fallback) : fallback;

    private static Vector3 HorizontalDirection(Vector3 from, Vector3 to, Vector3 fallback)
        => FiniteDirection(new Vector3(to.X - from.X, 0.0f, to.Z - from.Z), fallback);

    private static Vector3 Direction(Vector3 from, Vector3 to)
    {
        Vector3 direction = to - from;
        return direction.IsFinite() && direction.LengthSquared() > DirectionEpsilonSquared
            ? direction.Normalized()
            : Vector3.Zero;
    }

    private static float Distance(Vector3 from, Vector3 to)
    {
        float distance = from.DistanceTo(to);
        return NonNegativeFinite(distance);
    }

    private static Vector3 FiniteDirection(Vector3 value, Vector3 fallback)
    {
        Vector3 horizontal = new(value.X, 0.0f, value.Z);
        return !horizontal.IsFinite() || horizontal.LengthSquared() <= DirectionEpsilonSquared
            ? fallback.IsFinite() && fallback.LengthSquared() > DirectionEpsilonSquared
                ? new Vector3(fallback.X, 0.0f, fallback.Z).Normalized()
                : Vector3.Forward
            : horizontal.Normalized();
    }

    private static Vector3 FiniteDirectionOrZero(Vector3 value)
        => value.IsFinite() && value.LengthSquared() > DirectionEpsilonSquared ? value.Normalized() : Vector3.Zero;

    private static Vector3 FiniteOr(Vector3 value, Vector3 fallback) => value.IsFinite() ? value : fallback;

    private static float NonNegativeFinite(float value) => float.IsFinite(value) ? Mathf.Max(value, 0.0f) : 0.0f;
}
