using AlleyCat.Env;
using AlleyCat.Template;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Transform;

[GlobalClass]
public partial class PositionContextProviderFactory : TemplateContextProviderFactory
{
    [Export(PropertyHint.Range, "0.1,10.0")]
    public float NearThreshold { get; set; } = 1.0f;

    protected override Eff<IEnv, ITemplateContextProvider> CreateService(
        ILoggerFactory loggerFactory
    ) => SuccessEff<ITemplateContextProvider>(
        new PositionContextProvider(NearThreshold.Metres())
    );
}