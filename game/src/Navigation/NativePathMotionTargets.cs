using Godot;

namespace AlleyCat.Navigation;

/// <summary>Distance-ahead sampling of the native ordered polyline. Does not construct or validate a route.</summary>
internal static class NativePathMotionTargets
{
    internal readonly record struct Sample(Vector3 Position, Vector3 Direction, bool AtEnd);

    internal static Sample At(IReadOnlyList<Vector3> path, int activeIndex, Vector3 actual, float distance)
    {
        if (path.Count == 0 || !actual.IsFinite() || !float.IsFinite(distance) || distance < 0)
        {
            throw new InvalidDataException("Native path sample is unavailable or non-finite.");
        }
        int index = Math.Clamp(activeIndex, 0, path.Count - 1);
        Vector3 from = path[index];
        if (index > 0)
        {
            Vector3 edge = path[index] - path[index - 1];
            float lengthSquared = edge.LengthSquared();
            from = lengthSquared > 0 ? path[index - 1] + (edge * Math.Clamp((actual - path[index - 1]).Dot(edge) / lengthSquared, 0, 1)) : path[index];
        }
        Vector3 direction = Vector3.Zero;
        for (; index < path.Count; index++)
        {
            Vector3 to = path[index], delta = to - from;
            if (!from.IsFinite() || !to.IsFinite())
            {
                throw new InvalidDataException("Native agent returned a non-finite path point.");
            }
            float length = delta.Length();
            if (length > 0)
            {
                direction = delta / length;
                if (distance < length)
                {
                    return new(from + (direction * distance), direction, false);
                }
                distance -= length;
            }
            from = to;
        }
        if (direction == Vector3.Zero && path.Count > 1)
        {
            direction = (path[^1] - path[^2]).Normalized();
        }
        return new(from, direction, true);
    }

    /// <summary>
    /// End of the straight native corridor ahead of the agent's active point: that point, extended through following
    /// points at which the ordered polyline continues straight on (navigation-mesh subdivisions), up to the first real
    /// corner. Every point passed lies in order within <paramref name="lateralTolerance" /> planar of the line from the
    /// corridor's start to its end, so the corridor never leaves the native path or cuts a corner. The corridor starts
    /// at the previous route point, or at <paramref name="origin" /> before the first.
    /// </summary>
    internal static Vector3 StraightCorridorEnd(IReadOnlyList<Vector3> path, int activeIndex, Vector3 origin, float lateralTolerance)
    {
        if (path.Count == 0 || !origin.IsFinite() || !float.IsFinite(lateralTolerance) || lateralTolerance < 0)
        {
            throw new InvalidDataException("Native corridor is unavailable or non-finite.");
        }
        int index = Math.Clamp(activeIndex, 0, path.Count - 1);
        Vector3 from = index > 0 ? path[index - 1] : origin;
        int end = index;
        for (int candidate = index + 1; candidate < path.Count && PassesStraightThrough(path, from, index, candidate, lateralTolerance); candidate++)
        {
            end = candidate;
        }
        return path[end];
    }

    // Points first..candidate-1 must lie strictly ahead of one another on the planar line from 'from' to the candidate.
    private static bool PassesStraightThrough(IReadOnlyList<Vector3> path, Vector3 from, int first, int candidate, float tolerance)
    {
        Vector2 start = new(from.X, from.Z), line = new Vector2(path[candidate].X, path[candidate].Z) - start;
        float length = line.Length();
        if (!float.IsFinite(length) || length <= tolerance)
        {
            return false;
        }
        Vector2 axis = line / length;
        float previous = 0;
        for (int index = first; index < candidate; index++)
        {
            Vector2 offset = new Vector2(path[index].X, path[index].Z) - start;
            float along = offset.Dot(axis);
            if (!float.IsFinite(along) || along <= previous || along >= length || Math.Abs(offset.Cross(axis)) > tolerance)
            {
                return false;
            }
            previous = along;
        }
        return true;
    }
}
