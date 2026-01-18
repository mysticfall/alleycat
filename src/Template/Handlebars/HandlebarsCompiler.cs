using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Template.Handlebars.Helper;
using AlleyCat.Io;
using AlleyCat.Logging;
using HandlebarsDotNet;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Template.Handlebars;

public class HandlebarsCompiler(
    IHandlebars instance,
    ILoggerFactory? loggerFactory = null
) : ITemplateCompiler
{
    private readonly ILogger _logger = loggerFactory.GetLogger<HandlebarsCompiler>();

    public Eff<IEnv, ITemplate> Compile(string source) =>
        from template in liftEff(() =>
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace(
                    "Compiling template from source:\n {source}", source);
            }

            return instance.Compile(source);
        })
        from arg in template.Require(
            "The compiled template is null."
        )
        select (ITemplate)new HandlebarsTemplate(arg, loggerFactory);

    public static Eff<IEnv, ITemplateCompiler> Create(
        ResourcePath partialsPath,
        Seq<IHandlebarsHelper> helpers = default,
        HandlebarsConfiguration? configuration = null,
        ILoggerFactory? loggerFactory = null
    ) =>
        from instance in liftEff(() =>
            HandlebarsDotNet.Handlebars.Create(configuration)
        )
        from env in runtime<IEnv>()
        let io = env.FileProvider
        let logger = loggerFactory.GetLogger<HandlebarsCompiler>()
        from partials in liftEff(() => io
            .GetDirectoryContents(partialsPath)
            .AsIterable()
            .Map(x => x.Name)
        )
        from _1 in partials.Traverse(p =>
            from _1 in liftEff(() =>
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("Registering a partial template: {path}", p);
                }
            })
            from text in io.ReadAllText(partialsPath + p)
            from _2 in liftEff(() => instance.RegisterTemplate(p.Split(".")[0], text))
            select unit
        )
        from _2 in helpers.Traverse(h => h.Register(instance))
        select (ITemplateCompiler)new HandlebarsCompiler(instance, loggerFactory);
}