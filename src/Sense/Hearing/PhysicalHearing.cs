using System.Reactive.Linq;
using Godot;

namespace AlleyCat.Sense.Hearing;

public interface IPhysicalHearing : IHearing
{
    protected Area3D Area { get; }

    IObservable<ISound> IPassiveSense<ISound>.OnPerceive =>
        Observable
            .FromEvent<Area3D.AreaEnteredEventHandler, Area3D>(
                handler => new Area3D.AreaEnteredEventHandler(handler),
                add => Area.AreaEntered += add,
                remove => Area.AreaEntered -= remove)
            .Select(x => x is SoundBubble s ? Observable.Return(s.Data) : Observable.Empty<ISound>())
            .Switch();
}

public readonly record struct PhysicalHearing(Area3D Area) : IPhysicalHearing;