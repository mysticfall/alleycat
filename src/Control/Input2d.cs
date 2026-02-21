using System.Reactive.Linq;
using Godot;
using LanguageExt;

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