using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Navigation;

/// <summary>Immutable authored interpolation support for the NPC sustained blend space.</summary>
public sealed class NPCBlendDomain
{
    private readonly Vector2[] _points;
    private readonly int[] _triangles;

    public NPCBlendDomain(Vector2[] points, int[] triangles)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(triangles);
        if (points.Length == 0 || triangles.Length == 0 || triangles.Length % 3 != 0)
        {
            throw new ArgumentException("An NPC blend domain requires points and complete authored triangles.");
        }
        if (triangles.Any(index => index < 0 || index >= points.Length))
        {
            throw new ArgumentOutOfRangeException(nameof(triangles), "NPC blend-domain triangle indices must reference authored points.");
        }

        _points = [.. points];
        _triangles = [.. triangles];
    }

    public bool Contains(Vector2 point) => TryFindTriangle(point, out _);

    public Vector2 Project(Vector2 point)
    {
        if (!point.IsFinite())
        {
            return _points[0];
        }
        if (TryFindTriangle(point, out _))
        {
            return point;
        }

        Vector2 closest = _points[0];
        float closestDistance = float.PositiveInfinity;
        for (int triangle = 0; triangle < _triangles.Length; triangle += 3)
        {
            Vector2 a = _points[_triangles[triangle]];
            Vector2 b = _points[_triangles[triangle + 1]];
            Vector2 c = _points[_triangles[triangle + 2]];
            ProjectEdge(point, a, b, ref closest, ref closestDistance);
            ProjectEdge(point, b, c, ref closest, ref closestDistance);
            ProjectEdge(point, c, a, ref closest, ref closestDistance);
        }
        return closest;
    }

    public Vector2 StepInside(Vector2 current, Vector2 target, float maximumDistance)
    {
        Vector2 supportedCurrent = Project(current);
        Vector2 supportedTarget = Project(target);
        float safeDistance = float.IsFinite(maximumDistance) ? Math.Max(maximumDistance, 0f) : 0f;
        Vector2 stepped = supportedCurrent.MoveToward(supportedTarget, safeDistance);
        return Project(stepped);
    }

    private bool TryFindTriangle(Vector2 point, out int triangleIndex)
    {
        triangleIndex = -1;
        if (!point.IsFinite())
        {
            return false;
        }
        for (int triangle = 0; triangle < _triangles.Length; triangle += 3)
        {
            Vector3 barycentric = Barycentric(
                point,
                _points[_triangles[triangle]],
                _points[_triangles[triangle + 1]],
                _points[_triangles[triangle + 2]]);
            if (barycentric.X >= -0.0001f && barycentric.Y >= -0.0001f && barycentric.Z >= -0.0001f)
            {
                triangleIndex = triangle / 3;
                return true;
            }
        }
        return false;
    }

    private static void ProjectEdge(
        Vector2 point,
        Vector2 start,
        Vector2 end,
        ref Vector2 closest,
        ref float closestDistance)
    {
        Vector2 edge = end - start;
        float lengthSquared = edge.LengthSquared();
        float amount = lengthSquared > 0f
            ? Mathf.Clamp((point - start).Dot(edge) / lengthSquared, 0f, 1f)
            : 0f;
        Vector2 projected = start + (edge * amount);
        float distance = point.DistanceSquaredTo(projected);
        if (distance < closestDistance)
        {
            closest = projected;
            closestDistance = distance;
        }
    }

    private static Vector3 Barycentric(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
    {
        float denominator = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));
        if (Mathf.IsZeroApprox(denominator))
        {
            return new(-1f, -1f, -1f);
        }
        float aWeight = (((b.Y - c.Y) * (point.X - c.X)) + ((c.X - b.X) * (point.Y - c.Y))) / denominator;
        float bWeight = (((c.Y - a.Y) * (point.X - c.X)) + ((a.X - c.X) * (point.Y - c.Y))) / denominator;
        return new(aWeight, bWeight, 1f - aWeight - bWeight);
    }
}
