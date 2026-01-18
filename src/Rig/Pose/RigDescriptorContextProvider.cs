using AlleyCat.Entity;
using AlleyCat.Env;
using AlleyCat.Template;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Pose;

public abstract class RigDescriptorContextProvider<TBone> : ITemplateContextProvider
    where TBone : struct, Enum
{
    public Eff<IEnv, Map<object, object?>> CreateContext(
        ITemplateRenderable subject,
        IEntity observer
    ) => subject switch
    {
        // ReSharper disable once SuspiciousTypeConversion.Global
        IRigged<TBone> riggedSubject => CreateRigContext(riggedSubject, observer),
        _ => SuccessEff(Map<object, object?>())
    };

    protected abstract Eff<IEnv, Map<object, object?>> CreateRigContext(
        IRigged<TBone> subject,
        IEntity observer
    );
}