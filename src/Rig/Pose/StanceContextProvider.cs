using AlleyCat.Entity;
using AlleyCat.Env;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Pose;

public class StanceContextProvider : RigDescriptorContextProvider<HumanBone>
{
    protected override Eff<IEnv, Map<object, object?>> CreateRigContext(
        IRigged<HumanBone> subject,
        IEntity observer
    ) =>
        from standing in subject.IsStanding()
        from kneeling in standing ? SuccessEff(false) : subject.IsKneeling()
        select Map<object, object?>(
            ("stance", Map<object, object?>(
                ("standing", standing),
                ("kneeling", kneeling)
            ))
        );
}