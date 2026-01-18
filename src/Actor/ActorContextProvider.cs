using AlleyCat.Entity;
using AlleyCat.Env;
using AlleyCat.Template;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Actor;

public class ActorContextProvider : EntityContextProvider
{
    public override Eff<IEnv, Map<object, object?>> CreateContext(
        ITemplateRenderable subject,
        IEntity observer
    ) =>
        from parent in base.CreateContext(subject, observer)
        from context in subject switch
        {
            IActor actor => SuccessEff(
                Map<object, object?>(
                    ("gender", actor.Gender.ToString().ToLowerInvariant()),
                    ("pronouns", actor.Pronouns)
                )
            ),
            _ => SuccessEff(
                Map<object, object?>()
            )
        }
        select parent + context;
}