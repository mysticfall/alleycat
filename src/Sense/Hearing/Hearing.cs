namespace AlleyCat.Sense.Hearing;

public interface ISound : IPercept;

public interface IHearing : IPassiveSense<ISound>;

public interface IListener : ISensing
{
    IHearing Hearing => Senses.OfType<IHearing>().First();
}
