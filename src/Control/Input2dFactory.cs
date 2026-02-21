using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Service.Typed;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Control;

[GlobalClass]
public abstract partial class Input2dFactory : NodeFactory<IInput2d>
{
    [Export] public FrameType FrameType { get; set; } = FrameType.Process;

    protected override Eff<IEnv, IInput2d> CreateService(ILoggerFactory loggerFactory) =>
        CreateService(
            FrameType == FrameType.Process ? OnProcess : OnPhysicsProcess,
            loggerFactory
        );

    protected abstract Eff<IEnv, IInput2d> CreateService(
        IObservable<Duration> onProcess,
        ILoggerFactory loggerFactory
    );
}