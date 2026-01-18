using AlleyCat.Env;
using AlleyCat.Logging;
using HandlebarsDotNet;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Template.Handlebars;

public class HandlebarsTemplate(
    HandlebarsTemplate<object, object> template,
    ILoggerFactory? loggerFactory = null
) : ITemplate
{
    private readonly ILogger _logger = loggerFactory.GetLogger<HandlebarsTemplate>();

    public Eff<IEnv, string> Render(Map<object, object?> context) => liftEff(() =>
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("Rendering template context: {context}.", context);
        }

        var result = template(ToDictionary(context));

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("Rendered template: {result}.", result);
        }

        return result;
    });

    private static IDictionary<object, object?> ToDictionary(
        Map<object, object?> context
    ) => context.ToDictionary(
        e => e.Key,
        e => e.Value switch
        {
            Map<object, object?> map => ToDictionary(map),
            _ => e.Value
        }
    );
}