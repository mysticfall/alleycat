using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Service;
using AlleyCat.Service.Typed;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Rig.Ik;

[GlobalClass]
public abstract partial class IkModifierFactory : NodeFactory<IIkModifier>, IServiceFactory
{
    [Export] public IkModifierNode? Node { get; set; }

    InstantiationOption IServiceFactory.Instantiation => InstantiationOption.Singleton;

    protected override Eff<IEnv, IIkModifier> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from node in Node.Require("IK modifier node is not set.")
        from modifier in CreateService(node.OnIkProcess, loggerFactory)
        select modifier;

    protected abstract Eff<IEnv, IIkModifier> CreateService(
        IObservable<Duration> onIkProcess,
        ILoggerFactory loggerFactory
    );
}