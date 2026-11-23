using System.Globalization;
using Godot;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// The one shared conversion between model-facing compass bearings and world facing (AI-015 TR-5), in SCN-002's
/// minimap axis convention: bearing 0 = map up (−Z), 90 = right (+X), 180 = down (+Z), 270 = left (−X), measured
/// clockwise. Bearing <c>b</c> faces world direction (sin b, 0, −cos b); a world facing converts back by the
/// inverse.
/// </summary>
internal static class CompassBearing
{
    private const double FullTurnDegrees = 360d;

    /// <summary>
    /// Normalises a finite bearing into [0, 360) (for example −90 → 270, 360 → 0); a non-finite bearing is refused.
    /// </summary>
    /// <returns><see langword="true" /> when <paramref name="bearing" /> is finite.</returns>
    public static bool TryNormalise(double bearing, out double normalised)
    {
        if (!double.IsFinite(bearing))
        {
            normalised = double.NaN;
            return false;
        }

        double remainder = bearing % FullTurnDegrees;
        if (remainder < 0d)
        {
            remainder += FullTurnDegrees;
        }

        // A tiny negative remainder can round up to exactly 360; adding zero also turns −0 into +0.
        normalised = (remainder >= FullTurnDegrees ? 0d : remainder) + 0d;
        return true;
    }

    /// <summary>Normalises a finite bearing into [0, 360).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The bearing is not finite.</exception>
    public static double Normalise(double bearing)
        => TryNormalise(bearing, out double normalised)
            ? normalised
            : throw new ArgumentOutOfRangeException(nameof(bearing), bearing, "A compass bearing must be finite.");

    /// <summary>Gets the horizontal unit world direction (sin b, 0, −cos b) bearing <paramref name="bearing" /> faces.</summary>
    public static Vector3 ToWorldFacing(double bearing)
    {
        double radians = Normalise(bearing) * Math.PI / 180d;
        return new Vector3((float)Math.Sin(radians), 0f, (float)-Math.Cos(radians));
    }

    /// <summary>
    /// Gets a yaw-only basis whose forward axis (−Z, the navigation facing convention) points along
    /// <see cref="ToWorldFacing" /> of <paramref name="bearing" />.
    /// </summary>
    public static Basis ToBasis(double bearing)
        => new(Vector3.Up, (float)(-Normalise(bearing) * Math.PI / 180d));

    /// <summary>
    /// Converts a world facing back to its bearing by the inverse of <see cref="ToWorldFacing" />; only the
    /// horizontal components count.
    /// </summary>
    /// <returns><see langword="false" /> when the facing has no finite horizontal direction.</returns>
    public static bool TryFromWorldFacing(Vector3 facing, out double bearing)
    {
        double x = facing.X;
        double z = facing.Z;
        if (!double.IsFinite(x) || !double.IsFinite(z) || (x * x) + (z * z) < 1e-12)
        {
            bearing = double.NaN;
            return false;
        }

        bearing = Normalise(Math.Atan2(x, -z) * 180d / Math.PI);
        return true;
    }

    /// <summary>
    /// Converts a navigation yaw (Godot rotation about +Y, where yaw 0 faces −Z) to its bearing: a yaw ψ faces
    /// (−sin ψ, 0, −cos ψ), so the bearing is −ψ.
    /// </summary>
    public static double FromYawRadians(double yawRadians) => Normalise(-yawRadians * 180d / Math.PI);

    /// <summary>
    /// Formats a bearing as a whole-degree value in [0, 360) with invariant culture (359.6 → "0").
    /// </summary>
    public static string FormatWholeDegrees(double bearing)
    {
        double rounded = Math.Round(Normalise(bearing), MidpointRounding.AwayFromZero);
        return (rounded >= FullTurnDegrees ? 0d : rounded).ToString("0", CultureInfo.InvariantCulture);
    }
}
