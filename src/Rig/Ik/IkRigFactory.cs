using AlleyCat.Env;
using AlleyCat.Service;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Rig.Ik;

[GlobalClass]
public abstract partial class IkRigFactory : RigFactory, IServiceFactory<IIkRig>
{
    public new Eff<IEnv, IIkRig> TypedService => base.TypedService.Map(x => (IIkRig)x);

    protected override Eff<IEnv, IRig> CreateService(
        Skeleton3D skeleton,
        ILoggerFactory loggerFactory
    ) =>
        from onBeforeIk in IO.lift(() =>
        {
            var node = new IkModifierNode();

            skeleton.AddChild(node, false, InternalMode.Front);

            node.Owner = skeleton;

            return node.OnIkProcess;
        })
        from onAfterIk in IO.lift(() =>
        {
            var node = new IkModifierNode();

            skeleton.AddChild(node, false, InternalMode.Back);

            node.Owner = skeleton;

            return node.OnIkProcess;
        })
        from service in CreateService(
            skeleton,
            onBeforeIk,
            onAfterIk,
            loggerFactory
        ).Map(IRig (x) => x)
        select service;

    protected abstract Eff<IEnv, IIkRig> CreateService(
        Skeleton3D skeleton,
        IObservable<Duration> onBeforeIk,
        IObservable<Duration> onAfterIk,
        ILoggerFactory loggerFactory
    );
}

public abstract partial class TypedIkRigFactory<TBone> : IkRigFactory, IServiceFactory<IIkRig<TBone>>
    where TBone : struct, Enum
{
    public new Eff<IEnv, IIkRig<TBone>> TypedService => base.TypedService.Map(x => (IIkRig<TBone>)x);

    protected override Eff<IEnv, IIkRig> CreateService(
        Skeleton3D skeleton,
        IObservable<Duration> onBeforeIk,
        IObservable<Duration> onAfterIk,
        ILoggerFactory loggerFactory
    ) => CreateTypedService(
        skeleton,
        onBeforeIk,
        onAfterIk,
        loggerFactory
    ).Map(IIkRig (x) => x);

    protected abstract Eff<IEnv, IIkRig<TBone>> CreateTypedService(
        Skeleton3D skeleton,
        IObservable<Duration> onBeforeIk,
        IObservable<Duration> onAfterIk,
        ILoggerFactory loggerFactory
    );
}