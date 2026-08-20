namespace AlleyCat.Core.Logging;

/// <summary>
/// Point-in-time pipeline marker, eligible for notification display alongside trace logging.
/// </summary>
public sealed record PipelineMarkerEntry(string Stage, string? Detail = null) : IUINotificationEntry
{
    /// <inheritdoc />
    public string ToNotificationText()
        => string.IsNullOrWhiteSpace(Detail) ? Stage : $"{Stage} ({Detail})";
}
