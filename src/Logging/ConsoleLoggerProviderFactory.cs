using System.Text;
using AlleyCat.Env;
using Godot;
using LanguageExt;
using LanguageExt.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Options;
using static LanguageExt.Prelude;
using Error = LanguageExt.Common.Error;

namespace AlleyCat.Logging;

[GlobalClass]
public partial class ConsoleLoggerProviderFactory : LoggerProviderFactory
{
    public override Eff<IEnv, Unit> Configure(ILoggingBuilder builder) =>
        from env in runtime<IEnv>()
        from _ in liftEff(() =>
        {
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<ILoggerProvider, ConsoleLoggerProvider>(sp =>
                    new ConsoleLoggerProvider(
                        env,
                        sp.GetRequiredService<IOptionsMonitor<LoggerOptions>>()
                    )
                )
            );

            LoggerProviderOptions.RegisterProviderOptions<LoggerOptions, ConsoleLoggerProvider>(
                builder.Services);

            return builder;
        })
        select unit;
}

public class ConsoleLoggerProvider(
    IEnv env,
    IOptionsMonitor<LoggerOptions> options
) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName, env, options);

    public void Dispose()
    {
    }
}

public class ConsoleLogger(
    string category,
    IEnv env,
    IOptionsMonitor<LoggerOptions> options
) : Logger(category, env, options)
{
    private readonly StringBuilder _builder = new();

    protected override void Log(
        LogLevel logLevel,
        string message,
        EventId? eventId = null,
        Exception? exception = null
    )
    {
        var level = FormatLogLevel(logLevel);

        _builder.Append(level);
        _builder.Append(" - [");
        _builder.Append(Label);

        Id.Iter(id =>
        {
            _builder.Append(':');
            _builder.Append(id);
        });

        if (eventId != null)
        {
            _builder.Append(" (");
            _builder.Append(eventId);
            _builder.Append(')');
        }

        _builder.Append("] ");
        _builder.Append(message);

        var baseException = exception?.GetBaseException();

        switch (baseException)
        {
            case ErrorException error:
                WriteError(error.ToError());
                break;

                void WriteError(Error e)
                {
                    _builder.AppendLine();
                    _builder.Append(e);

                    var stackTrace = e.ToException().StackTrace;

                    if (stackTrace != null)
                    {
                        _builder.AppendLine();
                        _builder.Append(stackTrace);
                    }

                    e.Inner.IfSome(WriteError);
                }
            case null:
                break;
            default:
                _builder.AppendLine();
                _builder.Append(baseException);

                var stackTrace = baseException.StackTrace;

                if (stackTrace != null)
                {
                    _builder.AppendLine();
                    _builder.Append(stackTrace);
                }

                break;
        }

        var text = _builder.ToString();

        switch (logLevel)
        {
            case LogLevel.Error:
            case LogLevel.Critical:
                GD.PushError(text);
                break;
            case LogLevel.Warning:
                GD.PushWarning(text);
                break;
            case LogLevel.Trace:
            case LogLevel.Debug:
            case LogLevel.Information:
            case LogLevel.None:
            default:
                GD.Print(text);
                break;
        }

        _builder.Clear();
    }
}