using System.Text;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Core.Logging;

internal static class LoggerMessageFormatter
{
    public static string Format(
        string categoryName,
        LogLevel logLevel,
        EventId eventId,
        string message,
        Exception? exception,
        bool includeException = true)
    {
        var builder = new StringBuilder();
        _ = builder.Append('[').Append(logLevel).Append("] ").Append(categoryName);
        if (eventId.Id != 0 || !string.IsNullOrWhiteSpace(eventId.Name))
        {
            _ = builder.Append(" (").Append(eventId.Id);
            if (!string.IsNullOrWhiteSpace(eventId.Name))
            {
                _ = builder.Append(':').Append(eventId.Name);
            }

            _ = builder.Append(')');
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            _ = builder.Append(": ").Append(message);
        }

        if (includeException && exception is not null)
        {
            _ = builder.AppendLine().Append(exception);
        }

        return builder.ToString();
    }
}
