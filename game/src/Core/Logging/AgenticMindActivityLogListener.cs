using System.Collections;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Core.Logging;

/// <summary>
/// Temporary in-process Activity listener for the Agent Framework OpenTelemetry trial.
/// </summary>
internal static class AgenticMindActivityLogListener
{
    internal const string DefaultActivitySourceName = "AlleyCat.Mind.AI.AgenticMind";

    private static readonly Lock _listenerLock = new();
    private static readonly HashSet<string> _startedSourceNames = [];

    /// <summary>
    /// Starts the temporary listener for AgenticMind Agent Framework spans.
    /// </summary>
    /// <remarks>
    /// Sensitive diagnostic trial only: with Agent Framework sensitive data enabled this logs prompts, model responses,
    /// tool payloads, tags, events, and baggage to the AlleyCat runtime log. Do not ship permanently without explicit
    /// debug/config gating and a privacy review.
    /// </remarks>
    /// <param name="loggerFactory">Active AlleyCat logger factory.</param>
    /// <param name="activitySourceName">ActivitySource name to subscribe to.</param>
    public static void Start(ILoggerFactory loggerFactory, string activitySourceName = DefaultActivitySourceName)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(activitySourceName);

        lock (_listenerLock)
        {
            if (!_startedSourceNames.Add(activitySourceName))
            {
                return;
            }
        }

        ILogger logger = loggerFactory.CreateLogger("AlleyCat.Diagnostics.AgenticMindOpenTelemetryTrial");
        ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == activitySourceName,
            Sample = SampleAllDataAndRecorded,
            SampleUsingParentId = SampleAllDataAndRecorded,
            ActivityStopped = activity => LogStoppedActivity(logger, activity),
        };

        ActivitySource.AddActivityListener(listener);
    }

    private static ActivitySamplingResult SampleAllDataAndRecorded(ref ActivityCreationOptions<ActivityContext> options)
        => ActivitySamplingResult.AllDataAndRecorded;

    private static ActivitySamplingResult SampleAllDataAndRecorded(ref ActivityCreationOptions<string> options)
        => ActivitySamplingResult.AllDataAndRecorded;

    private static void LogStoppedActivity(ILogger logger, Activity activity)
    {
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        string tags = FormatKeyValuePairs(activity.TagObjects);
        string events = FormatEvents(activity.Events);
        string baggage = FormatKeyValuePairs(activity.Baggage);

        // Sensitive diagnostic trial only: these fields may include full prompts, responses, and tool payloads.
        logger.LogInformation(
            "Agent Framework OpenTelemetry trial activity stopped. Name: {ActivityName}; Source: {ActivitySource}; TraceId: {TraceId}; SpanId: {SpanId}; ParentSpanId: {ParentSpanId}; Kind: {Kind}; Status: {Status}; DurationMs: {DurationMs}; Tags: {Tags}; Events: {Events}; Baggage: {Baggage}",
            activity.DisplayName,
            activity.Source.Name,
            activity.TraceId.ToString(),
            activity.SpanId.ToString(),
            activity.ParentSpanId.ToString(),
            activity.Kind,
            activity.Status,
            activity.Duration.TotalMilliseconds,
            tags,
            events,
            baggage);
    }

    private static string FormatEvents(IEnumerable<ActivityEvent> events)
    {
        List<string> formattedEvents = [];

        foreach (ActivityEvent activityEvent in events)
        {
            formattedEvents.Add(
                $"{activityEvent.Name} @ {activityEvent.Timestamp:O} [{FormatKeyValuePairs(activityEvent.Tags)}]");
        }

        return formattedEvents.Count == 0 ? "<none>" : string.Join(" | ", formattedEvents);
    }

    private static string FormatKeyValuePairs(IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        List<string> formattedPairs = [];

        foreach ((string key, object? value) in pairs)
        {
            formattedPairs.Add($"{key}={FormatValue(value)}");
        }

        return formattedPairs.Count == 0 ? "<none>" : string.Join("; ", formattedPairs);
    }

    private static string FormatKeyValuePairs(IEnumerable<KeyValuePair<string, string?>> pairs)
    {
        List<string> formattedPairs = [];

        foreach ((string key, string? value) in pairs)
        {
            formattedPairs.Add($"{key}={FormatValue(value)}");
        }

        return formattedPairs.Count == 0 ? "<none>" : string.Join("; ", formattedPairs);
    }

    private static string FormatValue(object? value)
    {
        if (value is null)
        {
            return "<null>";
        }

        if (value is string stringValue)
        {
            return stringValue;
        }

        if (value is IFormattable formattable)
        {
            return formattable.ToString(null, CultureInfo.InvariantCulture);
        }

        if (value is IEnumerable enumerable)
        {
            List<string> values = [];

            foreach (object? item in enumerable)
            {
                values.Add(FormatValue(item));
            }

            return $"[{string.Join(", ", values)}]";
        }

        return value.ToString() ?? string.Empty;
    }
}
