using System.Globalization;

namespace AlleyCat.Core.Time;

/// <summary>
/// Formats game-time deltas as the model-facing relative phrasing shared by every surface that states when a
/// past event happened — observation lines, attended-position evidence, and every other event time a model sees.
/// The composition site supplies its request-moment clock reading, so each rendered age is relative to the moment
/// the model reads it and no arithmetic is left to the model.
/// </summary>
public static class RelativeGameTime
{
    /// <summary>
    /// Formats one past-event age for model-facing text: <c>just now</c> for anything under a second (also the
    /// composition-skew guard for zero and negative deltas), <c>N sec ago</c> under a minute,
    /// <c>N min M sec ago</c> (seconds omitted when zero) under an hour, and <c>N hr M min ago</c> (minutes
    /// omitted when zero) beyond. Ages round to the nearest second.
    /// </summary>
    public static string FormatAgo(double ageSeconds)
    {
        if (!double.IsFinite(ageSeconds) || ageSeconds < 1d)
        {
            return "just now";
        }

        long totalSeconds = (long)Math.Round(ageSeconds, MidpointRounding.AwayFromZero);
        if (totalSeconds < 60)
        {
            return $"{totalSeconds.ToString(CultureInfo.InvariantCulture)} sec ago";
        }

        if (totalSeconds < 3600)
        {
            long minutes = totalSeconds / 60;
            long seconds = totalSeconds % 60;
            return seconds == 0
                ? $"{minutes.ToString(CultureInfo.InvariantCulture)} min ago"
                : $"{minutes.ToString(CultureInfo.InvariantCulture)} min {seconds.ToString(CultureInfo.InvariantCulture)} sec ago";
        }

        long hours = totalSeconds / 3600;
        long remainderMinutes = totalSeconds % 3600 / 60;
        return remainderMinutes == 0
            ? $"{hours.ToString(CultureInfo.InvariantCulture)} hr ago"
            : $"{hours.ToString(CultureInfo.InvariantCulture)} hr {remainderMinutes.ToString(CultureInfo.InvariantCulture)} min ago";
    }
}
