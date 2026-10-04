using Godot;

namespace AlleyCat.Control.Locomotion;

/// <summary>
/// Pure, allocation-free models of how the Godot 4.7 AnimationTree weights and times the clips that drive a pose, so the
/// ANIM-002 <see cref="PlantedCombinationRule" /> can be evaluated from blend parameters, transition progress, and clip
/// playback positions. Values are read after the tree has evaluated the tick; the models reproduce the weights and
/// source times that evaluation used.
/// </summary>
public static class AnimationGraphWeightModel
{
    /// <summary>The engine's <c>CMP_EPSILON</c>: the floor applied to a crossfade weight so edge keys still process.</summary>
    public const double EngineEpsilon = 0.00001d;

    /// <summary>
    /// State-machine crossfade weights after an evaluation: the current state weighs
    /// <c>min(1, fading_position / fading_length)</c> and the fading-from state the remainder, each floored at
    /// <see cref="EngineEpsilon" />. Without a fading-from state the current state weighs one.
    /// </summary>
    public static (double Current, double FadingFrom) CrossfadeWeights(bool fading, double fadingPosition, double fadingLength)
    {
        if (!fading)
        {
            return (1d, 0d);
        }

        double current = fadingLength > 0d ? Math.Min(1d, fadingPosition / fadingLength) : 1d;
        current = Math.Abs(current) < EngineEpsilon ? EngineEpsilon : current;
        double from = 1d - current;
        from = Math.Abs(from) < EngineEpsilon ? EngineEpsilon : from;
        return (current, from);
    }

    /// <summary>
    /// Whether the evaluation just read interrupted a crossfade: the previous read showed a crossfade that could not
    /// have completed within this tick, and now the current state has changed and fades from the previous current
    /// state. Such an evaluation blended the dropped fading-from state, which the read state no longer shows.
    /// </summary>
    /// <param name="previousFading">Whether the previous read showed a fading-from state.</param>
    /// <param name="previousFadingPosition">Crossfade position of the previous read.</param>
    /// <param name="previousFadingLength">Crossfade length of the previous read.</param>
    /// <param name="delta">Time advanced by this evaluation.</param>
    /// <param name="currentChanged">Whether the current state differs from the previous read.</param>
    /// <param name="fading">Whether this read shows a fading-from state.</param>
    /// <param name="fadingFromIsPreviousCurrent">Whether this read fades from the previous current state.</param>
    public static bool InterruptsCrossfade(
        bool previousFading,
        double previousFadingPosition,
        double previousFadingLength,
        double delta,
        bool currentChanged,
        bool fading,
        bool fadingFromIsPreviousCurrent)
        => previousFading
           && previousFadingPosition + delta < previousFadingLength - EngineEpsilon
           && currentChanged
           && fading
           && fadingFromIsPreviousCurrent;
}

/// <summary>
/// Interpolated-mode <c>AnimationNodeBlendSpace2D</c> weights: barycentric weights of the first triangle containing the
/// blend position, otherwise the linear weights of the closest point on the closest triangle edge.
/// </summary>
public sealed class BlendSpace2DWeightModel
{
    private readonly Vector2[] _points;
    private readonly int[] _triangles;

    /// <param name="points">Blend point positions, indexed like the blend space's points.</param>
    /// <param name="triangles">Triangle point indices, three per triangle, in the blend space's triangle order.</param>
    public BlendSpace2DWeightModel(Vector2[] points, int[] triangles)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(triangles);
        if (triangles.Length == 0 || triangles.Length % 3 != 0)
        {
            throw new ArgumentException("A blend space needs at least one triangle of three point indices.", nameof(triangles));
        }

        foreach (int index in triangles)
        {
            if (index < 0 || index >= points.Length)
            {
                throw new ArgumentException($"Triangle point index {index} is outside the {points.Length} blend points.", nameof(triangles));
            }
        }

        _points = points;
        _triangles = triangles;
    }

    /// <summary>Number of blend points.</summary>
    public int PointCount => _points.Length;

    /// <summary>Writes one weight per blend point for <paramref name="position" />.</summary>
    public void Evaluate(Vector2 position, Span<float> weights)
    {
        if (weights.Length < _points.Length)
        {
            throw new ArgumentException("The weight buffer is smaller than the blend point count.", nameof(weights));
        }

        weights[.._points.Length].Clear();
        Span<float> corner = stackalloc float[3];
        int chosen = -1;
        float bestDistance = float.PositiveInfinity;
        for (int triangle = 0; triangle < _triangles.Length / 3; triangle++)
        {
            Vector2 a = _points[_triangles[triangle * 3]];
            Vector2 b = _points[_triangles[(triangle * 3) + 1]];
            Vector2 c = _points[_triangles[(triangle * 3) + 2]];
            if (Contains(position, a, b, c))
            {
                chosen = triangle;
                Barycentric(position, a, b, c, corner);
                break;
            }

            // Closest-edge fallback, keeping the first strictly closer edge like the engine.
            for (int edge = 0; edge < 3; edge++)
            {
                Vector2 from = edge == 0 ? a : edge == 1 ? b : c;
                Vector2 to = edge == 0 ? b : edge == 1 ? c : a;
                Vector2 closest = ClosestOnSegment(position, from, to);
                float distance = closest.DistanceTo(position);
                if (chosen < 0 || distance < bestDistance)
                {
                    chosen = triangle;
                    bestDistance = distance;
                    float length = from.DistanceTo(to);
                    float along = length == 0f ? 0f : from.DistanceTo(closest) / length;
                    corner[edge] = 1f - along;
                    corner[(edge + 1) % 3] = along;
                    corner[(edge + 2) % 3] = 0f;
                }
            }
        }

        for (int index = 0; index < 3; index++)
        {
            weights[_triangles[(chosen * 3) + index]] = corner[index];
        }
    }

    private static bool Contains(Vector2 s, Vector2 a, Vector2 b, Vector2 c)
    {
        // Geometry2D::is_point_in_triangle: all edge cross products share the orientation of (a - s) × (b - s).
        Vector2 an = a - s;
        Vector2 bn = b - s;
        Vector2 cn = c - s;
        bool orientation = an.Cross(bn) > 0f;
        return (bn.Cross(cn) > 0f) == orientation && (cn.Cross(an) > 0f) == orientation;
    }

    private static Vector2 ClosestOnSegment(Vector2 point, Vector2 from, Vector2 to)
    {
        Vector2 segment = to - from;
        float lengthSquared = segment.LengthSquared();
        if (lengthSquared < 1e-20f)
        {
            return from;
        }

        float t = segment.Dot(point - from) / lengthSquared;
        return t <= 0f ? from : t >= 1f ? to : from + (segment * t);
    }

    private static void Barycentric(Vector2 position, Vector2 a, Vector2 b, Vector2 c, Span<float> weights)
    {
        if (position.IsEqualApprox(a))
        {
            weights[0] = 1f;
            weights[1] = weights[2] = 0f;
            return;
        }

        if (position.IsEqualApprox(b))
        {
            weights[1] = 1f;
            weights[0] = weights[2] = 0f;
            return;
        }

        if (position.IsEqualApprox(c))
        {
            weights[2] = 1f;
            weights[0] = weights[1] = 0f;
            return;
        }

        Vector2 v0 = b - a;
        Vector2 v1 = c - a;
        Vector2 v2 = position - a;
        float d00 = v0.Dot(v0);
        float d01 = v0.Dot(v1);
        float d11 = v1.Dot(v1);
        float d20 = v2.Dot(v0);
        float d21 = v2.Dot(v1);
        float denominator = (d00 * d11) - (d01 * d01);
        if (denominator == 0f)
        {
            weights[0] = 1f;
            weights[1] = weights[2] = 0f;
            return;
        }

        float v = ((d11 * d20) - (d01 * d21)) / denominator;
        float w = ((d00 * d21) - (d01 * d20)) / denominator;
        weights[0] = 1f - v - w;
        weights[1] = v;
        weights[2] = w;
    }
}

/// <summary>
/// How an <c>AnimationNodeAnimation</c> maps its node playback position (its <c>current_position</c> parameter) to the
/// time sampled in its animation: custom timelines add their start offset and, when stretched, scale by
/// animation length over timeline length; the result wraps, ping-pongs, or clamps into the animation, and backward
/// play mirrors it.
/// </summary>
/// <param name="AnimationLength">Length of the sampled animation in seconds.</param>
/// <param name="LoopMode">Effective loop mode: the node's custom-timeline mode, otherwise the animation's own.</param>
/// <param name="UseCustomTimeline">Whether the node uses a custom timeline.</param>
/// <param name="TimelineLength">Custom timeline length in seconds.</param>
/// <param name="StretchTimeScale">Whether the custom timeline stretches the animation to its length.</param>
/// <param name="StartOffset">Custom timeline start offset in node seconds.</param>
/// <param name="Backward">Whether the node plays backward.</param>
public readonly record struct ClipTimelineMapping(
    double AnimationLength,
    Animation.LoopModeEnum LoopMode,
    bool UseCustomTimeline,
    double TimelineLength,
    bool StretchTimeScale,
    double StartOffset,
    bool Backward)
{
    /// <summary>The animation (source) time sampled at the given node playback position.</summary>
    public double SourceTime(double nodePosition)
    {
        double time = nodePosition;
        if (UseCustomTimeline)
        {
            time += StartOffset;
            if (StretchTimeScale && TimelineLength > 0d)
            {
                time *= AnimationLength / TimelineLength;
            }
        }

        if (AnimationLength > 0d)
        {
            time = LoopMode switch
            {
                Animation.LoopModeEnum.Linear => PositiveModulo(time, AnimationLength),
                Animation.LoopModeEnum.Pingpong => PingPong(time, AnimationLength),
                Animation.LoopModeEnum.None => Math.Clamp(time, 0d, AnimationLength),
                _ => Math.Clamp(time, 0d, AnimationLength),
            };
        }

        return Backward ? AnimationLength - time : time;
    }

    private static double PositiveModulo(double value, double length)
    {
        double wrapped = value % length;
        return wrapped < 0d ? wrapped + length : wrapped;
    }

    private static double PingPong(double value, double length)
    {
        double wrapped = PositiveModulo(value, length * 2d);
        return wrapped > length ? (length * 2d) - wrapped : wrapped;
    }
}
