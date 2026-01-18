using AlleyCat.Env;
using AlleyCat.Common;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Sense.Hearing;

[GlobalClass]
public partial class PhysicalHearingFactory : HearingFactory
{
    [Export] public Area3D? Area { get; set; }

    protected override Eff<IEnv, IHearing> CreateService(ILoggerFactory loggerFactory) =>
        from area in Area.Require("Area is not set.")
        select (IHearing)new PhysicalHearing(area);
}