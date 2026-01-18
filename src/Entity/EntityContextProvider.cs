using AlleyCat.Env;
using AlleyCat.Template;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Entity;

public class EntityContextProvider : ITemplateContextProvider
{
    public virtual Eff<IEnv, Map<object, object?>> CreateContext(
        ITemplateRenderable subject,
        IEntity observer
    ) => subject switch
    {
        IEntity entity => SuccessEff(Map<object, object?>(
            ("id", entity.Id)
        )),
        _ => SuccessEff(Map<object, object?>())
    };
}