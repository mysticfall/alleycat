using AlleyCat.Entity;
using AlleyCat.Env;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Template;

public interface ITemplateRenderable
{
    Seq<ITemplateContextProvider> TemplateContextProviders { get; }
}

public static class TemplateRenderableExtensions
{
    public static Eff<IEnv, Map<object, object?>> CreateTemplateContext(
        this ITemplateRenderable subject,
        IEntity observer
    ) =>
        from context in subject.TemplateContextProviders
            .Traverse(p => p.CreateContext(subject, observer))
            .Map(x => x.Fold(
                Map<object, object?>(),
                (c1, c2) => c1 + c2
            ))
            .As()
        let baseContext = Map<object, object?>(
            ("subject", subject),
            ("observer", observer)
        )
        select baseContext + context;
}