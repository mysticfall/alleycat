using AlleyCat.Env;
using AlleyCat.Logging;
using Godot;
using HandlebarsDotNet;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Template.Handlebars.Helper;

public class EqHelper(ILoggerFactory? loggerFactory = null) : IHandlebarsHelper
{
    private readonly ILogger _logger = loggerFactory.GetLogger<EqHelper>();

    public Eff<IEnv, Unit> Register(IHandlebars handlebars) => liftEff(() =>
    {
        _logger.LogDebug("Registering Handlebars helper 'eq'.");

        handlebars.RegisterHelper("eq", (writer, _, parameters) =>
        {
            if (parameters.Length < 2)
            {
                _logger.LogWarning("Handlebars helper 'eq' called with less than two parameters.");
                return;
            }

            var left = parameters[0];
            var right = parameters[1];

            var areEqual = left?.ToString()?.Equals(
                right?.ToString(),
                StringComparison.OrdinalIgnoreCase
            ) == true;

            if (areEqual)
            {
                writer.WriteSafeString("true");
            }
        });
    });
}

[GlobalClass]
public partial class EqHelperFactory : HandlebarsHelperFactory
{
    protected override Eff<IEnv, IHandlebarsHelper> CreateService(
        ILoggerFactory loggerFactory
    ) => SuccessEff<IHandlebarsHelper>(new EqHelper(loggerFactory));
}