using AlleyCat.Async;
using AlleyCat.Common;
using AlleyCat.Env;
using LanguageExt;

namespace AlleyCat.Control;

public interface ICharacterControl : IRunnable, IPhysicsFrameAware
{
    Eff<IEnv, IDisposable> IRunnable.Run()
    {
        throw new NotImplementedException();
    }
}