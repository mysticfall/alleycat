using AlleyCat.Entity;
using AlleyCat.Env;
using AlleyCat.Template;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Tests.Rig.Pose;

public class MockStanceContextProvider : ITemplateContextProvider
{
    public bool IsStanding { get; set; }

    public bool IsKneeling { get; set; }

    public void StandUp()
    {
        IsStanding = true;
        IsKneeling = false;
    }

    public void Kneel()
    {
        IsStanding = false;
        IsKneeling = true;
    }

    public Eff<IEnv, Map<object, object?>> CreateContext(
        ITemplateRenderable subject,
        IEntity observer
    ) => SuccessEff(
        Map<object, object?>(
            ("stance", Map<object, object?>(
                ("standing", IsStanding),
                ("kneeling", IsKneeling)
            ))
        )
    );
}