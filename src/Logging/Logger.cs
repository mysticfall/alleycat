using AlleyCat.Env;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static LanguageExt.Prelude;

namespace AlleyCat.Logging;

public abstract class Logger : ILogger
{
    private const string IdDelimiter = ":";

    protected Option<string> Id { get; }

    protected string Label { get; }

    private IEnv Env { get; }

    private readonly IOptionsMonitor<LoggerOptions> _options;

    protected Logger(
        string category,
        IEnv env,
        IOptionsMonitor<LoggerOptions> options
    )
    {
        Env = env;

        _options = options;

        var segments = category
            .Split(".")
            .AsIterable()
            .ToSeq()
            .Reverse()
            .Take(options.CurrentValue.CategorySegments);

        var (label, id) = segments.Match(
            () => ("", None),
            head =>
            {
                var values = head.Split(IdDelimiter);

                return values.Length == 2 ? (values[0], Some(values[1])) : (head, None);
            },
            More: (head, tail) =>
            {
                var values = head.Split(IdDelimiter).AsIterable().ToSeq();
                var list = tail.Prepend(values.Length == 2 ? values[0] : head);

                return (string.Join(".", list.Reverse()), values.Skip(1).Head);
            });

        Id = id;
        Label = label;
    }

    protected LoggerOptions Options => _options.CurrentValue;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);

        if (string.IsNullOrEmpty(message) && exception == null) return;

        if (GodotThread.IsMainThread())
        {
            Log(
                logLevel,
                message,
                eventId,
                exception
            );
        }
        else
        {
            Env.TaskQueue
                .Enqueue(liftEff(() => Log(
                    logLevel,
                    message,
                    eventId,
                    exception
                )))
                .Run()
                .IfFail(e => Console.Write($"Failed to log message: {e}"));
        }
    }

    protected abstract void Log(
        LogLevel logLevel,
        string message,
        EventId? eventId = null,
        Exception? exception = null
    );

    public virtual bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    protected virtual string FormatLogLevel(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "FATAL",
            LogLevel.None => "NONE",
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
        };
    }

    public virtual IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
}