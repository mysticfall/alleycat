using AlleyCat.Env;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Ik;

public readonly record struct SimpleIkContext(
    Transform3D ToSkeleton,
    Transform3D FromSkeleton
) : IIkContext;

public abstract class SimpleIkModifier<TBone>(
    IRig<TBone> rig,
    IObservable<Duration> onIkProcess,
    ILoggerFactory? loggerFactory = null
) : IkModifier<TBone, SimpleIkContext>(rig, onIkProcess, loggerFactory)
    where TBone : struct, Enum
{
    protected override Eff<IEnv, SimpleIkContext> CreateContext(
        Transform3D toSkeleton,
        Transform3D fromSkeleton
    ) => SuccessEff(new SimpleIkContext(toSkeleton, fromSkeleton));
}