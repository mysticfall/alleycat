using AlleyCat.Animation.BlendShape;
using AlleyCat.Env;
using AlleyCat.Service.Typed;
using AlleyCat.Common;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using static AlleyCat.Env.Prelude;

namespace AlleyCat.Speech.LipSync;

[GlobalClass]
public abstract partial class LipSyncGeneratorFactory : NodeFactory<ILipSyncGenerator>
{
    [Export] public BlendShapeSet? BlendShapes { get; set; }

    [Export] public string? ConfigSection { get; set; } = "LipSync";

    protected override Eff<IEnv, ILipSyncGenerator> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from blendShapes in BlendShapes.Require("BlendShapes is not set.")
        from sectionName in ConfigSection
            .Require("Config section is not set.")
        from config in service<IConfiguration>()
        from section in config.RequireSection(sectionName)
        from service in CreateService(section, blendShapes, loggerFactory)
        select service;

    protected abstract Eff<IEnv, ILipSyncGenerator> CreateService(
        IConfiguration config,
        BlendShapeSet blendShapes,
        ILoggerFactory loggerFactory
    );
}