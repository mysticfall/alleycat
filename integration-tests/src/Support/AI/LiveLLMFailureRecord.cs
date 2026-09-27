using System.Net.Sockets;

namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// Safe classification of an execution failure. Classifications never carry raw exception detail; the
/// accompanying <see cref="LiveLLMFailureRecord.SafeMessage" /> is bounded and sanitised.
/// </summary>
public enum LiveLLMFailureClassification
{
    /// <summary>The failure was a network or transport-level provider failure.</summary>
    Transport,

    /// <summary>The scenario exceeded its declared timeout budget.</summary>
    Timeout,

    /// <summary>
    /// A response or request payload had an invalid shape, as decided by scenario-specific checks.
    /// </summary>
    Malformed,

    /// <summary>The scenario attempted to exceed a declared bound.</summary>
    BoundExceeded,

    /// <summary>The caller's cancellation token was cancelled.</summary>
    Cancelled,

    /// <summary>The failure did not match any known classification.</summary>
    Unknown,
}

/// <summary>
/// Immutable, sanitised record of an execution failure: a classification, the exception type name, and a
/// bounded safe message. Raw exception objects, stack traces, and inner exceptions are never captured.
/// </summary>
public sealed record LiveLLMFailureRecord(
    LiveLLMFailureClassification Classification,
    string ExceptionType,
    string SafeMessage)
{
    /// <summary>
    /// Classifies an exception and builds its sanitised failure record without serialising the exception
    /// object itself.
    /// </summary>
    /// <param name="exception">Exception to classify.</param>
    /// <param name="timedOut">Whether the scenario's own timeout deadline fired for this failure.</param>
    /// <returns>The sanitised failure record.</returns>
    public static LiveLLMFailureRecord FromException(Exception exception, bool timedOut)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new LiveLLMFailureRecord(
            Classify(exception, timedOut),
            exception.GetType().Name,
            LiveLLMTraceSanitiser.SanitiseText(exception.Message));
    }

    private static LiveLLMFailureClassification Classify(Exception exception, bool timedOut)
        => exception switch
        {
            LiveLLMScenarioException scenario => scenario.Classification,
            OperationCanceledException when timedOut => LiveLLMFailureClassification.Timeout,
            OperationCanceledException => LiveLLMFailureClassification.Cancelled,
            TimeoutException => LiveLLMFailureClassification.Timeout,
            HttpRequestException or SocketException or IOException => LiveLLMFailureClassification.Transport,
            _ => LiveLLMFailureClassification.Unknown,
        };
}
