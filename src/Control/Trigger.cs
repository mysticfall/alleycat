using System.Reactive.Linq;
using LanguageExt;

namespace AlleyCat.Control;

public interface ITrigger
{
    IObservable<Unit> OnPress { get; }

    IObservable<Unit> OnRelease { get; }
}

public static class TriggerExtensions
{
    extension(ITrigger trigger)
    {
        public IObservable<bool> OnChange =>
            trigger.OnPress.Select(_ => true).Merge(trigger.OnRelease.Select(_ => false));
    }
}