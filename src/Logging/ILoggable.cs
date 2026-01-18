using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlleyCat.Logging;

public interface ILoggable
{
    ILoggerFactory? LoggerFactory { get; }

    ILogger Logger => (LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger(GetType());
}

public static class LoggingExtensions
{
    extension(ILoggerFactory? factory)
    {
        public ILogger GetLogger<T>() =>
            (factory ?? NullLoggerFactory.Instance).CreateLogger<T>();

        public ILogger GetLogger(Type type) =>
            (factory ?? NullLoggerFactory.Instance).CreateLogger(type.FullName!);
    }
}