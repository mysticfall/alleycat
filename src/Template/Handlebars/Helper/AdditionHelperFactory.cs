using AlleyCat.Env;
using AlleyCat.Logging;
using Godot;
using HandlebarsDotNet;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Template.Handlebars.Helper;

public class AdditionHelper(ILoggerFactory? loggerFactory = null) : IHandlebarsHelper
{
    private readonly ILogger _logger = loggerFactory.GetLogger<AdditionHelper>();

    public Eff<IEnv, Unit> Register(IHandlebars handlebars) => liftEff(() =>
    {
        _logger.LogDebug("Registering Handlebars helper 'add'.");

        handlebars.RegisterHelper("add", (writer, _, parameters) =>
        {
            if (parameters.Length < 2)
            {
                _logger.LogWarning("Handlebars helper 'add' called with less than two parameters.");
                return;
            }

            if (parameters[0] is null || !int.TryParse(parameters[0].ToString(), out var left))
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(
                        "Handlebars helper 'add' called with invalid parameter: {param}.",
                        parameters[0]
                    );
                }

                return;
            }

            if (parameters[1] is null || !int.TryParse(parameters[1].ToString(), out var right))
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(
                        "Handlebars helper 'add' called with invalid parameter: {param}.",
                        parameters[1]
                    );
                }

                return;
            }

            writer.WriteSafeString(left + right);
        });
    });
}

[GlobalClass]
public partial class AdditionHelperFactory : HandlebarsHelperFactory
{
    protected override Eff<IEnv, IHandlebarsHelper> CreateService(
        ILoggerFactory loggerFactory
    ) => SuccessEff<IHandlebarsHelper>(new AdditionHelper(loggerFactory));
}