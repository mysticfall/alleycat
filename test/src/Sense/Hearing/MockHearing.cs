using System.Reactive.Linq;
using System.Reactive.Subjects;
using AlleyCat.Sense.Hearing;

namespace AlleyCat.Tests.Sense.Hearing;

public class MockHearing : IHearing
{
    public IObservable<ISound> OnPerceive => _subject.AsObservable();

    public void ListenTo(ISound sound) => _subject.OnNext(sound);

    private readonly Subject<ISound> _subject = new();
}