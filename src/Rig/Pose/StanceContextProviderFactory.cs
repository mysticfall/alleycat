using AlleyCat.Env;
using AlleyCat.Template;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Pose;

[GlobalClass]
public partial class StanceContextProviderFactory : TemplateContextProviderFactory
{
    protected override Eff<IEnv, ITemplateContextProvider> CreateService(
        ILoggerFactory loggerFactory
    ) => SuccessEff<ITemplateContextProvider>(new StanceContextProvider());
}