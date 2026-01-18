using AlleyCat.Entity;
using AlleyCat.Env;
using AlleyCat.Template;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Metadata;

public class DescriptionContextProvider(ITemplate template) : ITemplateContextProvider
{
    public Eff<IEnv, Map<object, object?>> CreateContext(
        ITemplateRenderable subject,
        IEntity observer
    ) =>
        from description in template.Render(
            Map<object, object?>(
                ("subject", subject),
                ("observer", observer)
            )
        )
        select Map<object, object?>(
            ("description", description)
        );
}