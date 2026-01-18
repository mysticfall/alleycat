using AlleyCat.Env;
using LanguageExt;

namespace AlleyCat.Template;

public interface ITemplate
{
    Eff<IEnv, string> Render(Map<object, object?> context = default);
}