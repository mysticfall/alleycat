using AlleyCat.Env;
using LanguageExt;

namespace AlleyCat.Common;

public interface IRunnable
{
    Eff<IEnv, IDisposable> Run();
}