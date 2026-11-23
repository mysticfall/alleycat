using System.Globalization;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// The one shared own-position formatter of the coordinate navigation tools (AI-015 TR-15): every navigation
/// outcome ends with "You are at (x, z), facing N°." — world metres rounded to 0.1 m and a whole-degree bearing
/// from <see cref="CompassBearing" />, in invariant culture. Own position appears only in outcome texts, never in
/// the minimap result (AI-015 TR-16).
/// </summary>
internal static class SelfPositionText
{
    /// <summary>Formats the own-position sentence for world X/Z metres and a compass bearing in degrees.</summary>
    public static string Format(double x, double z, double bearing)
        => $"You are at ({FormatMetres(x)}, {FormatMetres(z)}), facing {CompassBearing.FormatWholeDegrees(bearing)}°.";

    /// <summary>Formats world metres rounded half away from zero to 0.1 m, never as negative zero.</summary>
    internal static string FormatMetres(double metres)
    {
        double rounded = Math.Round(metres, 1, MidpointRounding.AwayFromZero);
        return (rounded == 0d ? 0d : rounded).ToString("0.0", CultureInfo.InvariantCulture);
    }
}
