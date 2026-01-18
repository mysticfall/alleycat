using AlleyCat.Animation.BlendShape;
using AlleyCat.Env;
using AlleyCat.Logging;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Speech.LipSync.NeuroSync;

[GlobalClass]
public partial class NeuroSyncGenFactory : LipSyncGeneratorFactory
{
    protected override Eff<IEnv, ILipSyncGenerator> CreateService(
        IConfiguration config,
        BlendShapeSet blendShapes,
        ILoggerFactory loggerFactory
    ) =>
        from env in runtime<IEnv>()
        from uri in IO
            .pure(config.GetValue<string>("Endpoint", "http://localhost:8000"))
            .Map(x => new Uri(x, UriKind.Absolute))
        from service in liftEff(ILipSyncGenerator () =>
        {
            var logger = loggerFactory.GetLogger<NeuroSyncGenFactory>();

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Using endpoint: {endpoint}", uri.AbsoluteUri);
            }

            return new NeuroSyncGen(uri, blendShapes, loggerFactory);
        })
        select service;
}