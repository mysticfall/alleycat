using AlleyCat.Entity;
using AlleyCat.Env;
using LanguageExt;

namespace AlleyCat.Template;

public interface ITemplateContextProvider
{
    Eff<IEnv, Map<object, object?>> CreateContext(
        ITemplateRenderable subject,
        IEntity observer
    );
}