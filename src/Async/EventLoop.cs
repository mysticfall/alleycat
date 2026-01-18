using LanguageExt;

namespace AlleyCat.Async;

public interface IFrameAware
{
    IObservable<Duration> OnProcess { get; }
}

public interface IPhysicsFrameAware
{
    IObservable<Duration> OnPhysicsProcess { get; }
}