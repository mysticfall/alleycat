using AlleyCat.Env;
using AlleyCat.Template.Handlebars.Helper;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;
using Path = AlleyCat.Common.ResourcePath;

namespace AlleyCat.Template.Handlebars;

[GlobalClass]
public partial class HandlebarsCompilerFactory : TemplateCompilerFactory
{
    [Export] public string PartialsPath { get; set; } = "res://data/prompt/partial";

    [Export] public HandlebarsHelperFactory[] Helpers { get; set; } = [];

    protected override Eff<IEnv, ITemplateCompiler> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from path in Path.Create(PartialsPath).ToEff(identity)
        from helpers in Helpers
            .AsIterable()
            .ToSeq()
            .Traverse(x => x.TypedService)
        from service in HandlebarsCompiler.Create(path, helpers)
        select service;
}