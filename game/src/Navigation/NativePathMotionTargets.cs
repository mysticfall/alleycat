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
}
