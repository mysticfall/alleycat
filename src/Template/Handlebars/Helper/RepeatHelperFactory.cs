using AlleyCat.Env;
using AlleyCat.Logging;
using Godot;
using HandlebarsDotNet;
using LanguageExt;
using Microsoft.Extensions.Logging;
using Error = LanguageExt.Common.Error;
using static LanguageExt.Prelude;

namespace AlleyCat.Template.Handlebars.Helper;

public class RepeatHelper(ILoggerFactory? loggerFactory) : IHandlebarsHelper
{
    private readonly ILogger _logger = loggerFactory.GetLogger<RepeatHelper>();

    public Eff<IEnv, Unit> Register(IHandlebars handlebars) => liftEff(() =>
    {
        _logger.LogDebug("Registering Handlebars helper 'repeat'.");

        handlebars.RegisterHelper("repeat", (writer, _, parameters) =>
        {
            if (parameters.Length < 2)
            {
                _logger.LogWarning("Handlebars helper 'eq' called with less than two parameters.");
                return;
            }

            if (parameters[1] is null || !int.TryParse(parameters[1].ToString(), out var count))
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(
                        "Handlebars helper 'repeat' called with invalid parameter: {param}.",
                        parameters[1]
                    );
                }

                return;
            }

            var value = parameters[0];

            for (var i = 0; i < count; i++)
            {
                writer.Write(value);
            }
        });
    }).MapFail(e =>
        Error.New("Failed to register Handlebars helper 'repeat'.", e)
    );
}

[GlobalClass]
public partial class RepeatHelperFactory : HandlebarsHelperFactory
{
    protected override Eff<IEnv, IHandlebarsHelper> CreateService(
        ILoggerFactory loggerFactory
    ) => SuccessEff<IHandlebarsHelper>(new RepeatHelper(loggerFactory));
}