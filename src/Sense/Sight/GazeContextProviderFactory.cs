using AlleyCat.Env;
using AlleyCat.Template;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Sense.Sight;

[GlobalClass]
public partial class GazeContextProviderFactory : TemplateContextProviderFactory
{
    [Export(PropertyHint.Range, "0.01,1.0")]
    public float MaxHeadOffset { get; set; } = 0.20f;

    protected override Eff<IEnv, ITemplateContextProvider> CreateService(
        ILoggerFactory loggerFactory
    ) => SuccessEff<ITemplateContextProvider>(
        new GazeContextProvider(MaxHeadOffset.Metres())
    );
}