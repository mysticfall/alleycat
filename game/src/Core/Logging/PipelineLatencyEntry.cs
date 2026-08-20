using System.Globalization;

namespace AlleyCat.Core.Logging;

/// <summary>
/// Latency measurement for a pipeline stage, eligible for notification display alongside trace logging.
/// </summary>
/// <remarks>
/// The console formatter renders <see cref="Detail" /> in full, while toasts render
/// <see cref="NotificationDetail" /> — which falls back to <see cref="Detail" /> when null so most entries share one
/// detail string. Stages whose console detail is too verbose for a toast pass an empty value to omit the suffix
/// entirely, or a shortened detail instead.
/// </remarks>
public sealed record PipelineLatencyEntry(
    string Stage,
    TimeSpan Elapsed,
    string? Detail = null,
    string? NotificationDetail = null) : IUINotificationEntry
{
    /// <inheritdoc />
    public string ToNotificationText()
    {
        string seconds = Elapsed.TotalSeconds.ToString("0.0#", CultureInfo.InvariantCulture);
        return $"{Stage} {seconds} seconds{FormatDetailSuffix(NotificationDetail ?? Detail)}";
    }

    private static string FormatDetailSuffix(string? detail)
        => string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({detail})";
}
