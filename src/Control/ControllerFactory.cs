using AlleyCat.Env;
using AlleyCat.Service.Typed;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Control;

public partial class ControllerFactory<T> : NodeFactory<IController<T>>
    where T : IControllable
{
    protected override Eff<IEnv, IController<T>> CreateService(
        ILoggerFactory loggerFactory
    ) => SuccessEff<IController<T>>(new Controller<T>(loggerFactory));
}