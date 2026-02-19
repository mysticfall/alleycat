using System.Reactive.Linq;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Service.Typed;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Control;

public interface IInput2d
{
    IObservable<Vector2> OnInput { get; }
}

public abstract class Input2d(IObservable<Duration> onProcess) : IInput2d
{
    public IObservable<Vector2> OnInput => onProcess.Select(Process);

    protected abstract Vector2 Process(Duration duration);
}

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