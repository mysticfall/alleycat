using System.Reactive.Linq;
using LanguageExt;

namespace AlleyCat.Sense;

public interface IPercept
{
    public DateTime Timestamp { get; }
}

public interface ISense;

public interface IActiveSense : ISense
{
    SourceT<IO, IPercept> Perceptions { get; }
}

public interface IActiveSense<TPercept> : IActiveSense where TPercept : IPercept
{
    SourceT<IO, IPercept> IActiveSense.Perceptions => Perceptions.Map(x => (IPercept)x);

    new SourceT<IO, TPercept> Perceptions { get; }
}

public interface IPassiveSense : ISense
{
    IObservable<IPercept> OnPerceive { get; }
}

public interface IPassiveSense<out TPercept> : IPassiveSense where TPercept : IPercept
{
    IObservable<IPercept> IPassiveSense.OnPerceive => OnPerceive.Select(x => (IPercept)x);

    new IObservable<TPercept> OnPerceive { get; }
}

public interface ISensing
{
    Seq<ISense> Senses { get; }
}