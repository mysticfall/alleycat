using System.Collections.Immutable;
using Godot;

namespace AlleyCat.Scene.MiniMap;

/// <summary>Shared numeric helpers for minimap capture and composition.</summary>
internal static class MiniMapMath
{
    /// <summary>World polygon area below which a footprint counts as transiently collapsed (TR-16).</summary>
    internal const float DegenerateAreaSquareMetres = 1e-6f;

    /// <summary>The only rejected transform class is non-finiteness (TR-8).</summary>
    internal static bool IsFinite(Transform3D transform) =>
        transform.Origin.IsFinite() && transform.Basis.X.IsFinite() && transform.Basis.Y.IsFinite() && transform.Basis.Z.IsFinite();

    /// <summary>Signed polygon area.</summary>
    internal static float Area(ImmutableArray<Vector2> points)
    {
        float area = 0;
        for (int index = 0; index < points.Length; index++)
        {
            area += points[index].Cross(points[(index + 1) % points.Length]);
        }

        return area / 2;
    }
}
