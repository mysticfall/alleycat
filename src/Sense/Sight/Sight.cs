using AlleyCat.Transform;
using LanguageExt;

namespace AlleyCat.Sense.Sight;

public interface IVision : IPercept;

public interface ISight : IActiveSense<IVision>, ILocatable3d
{
    IO<Option<ILocatable3d>> LookAt { get; }

    IO<Unit> SetLookAt(ILocatable3d target);

    IO<Unit> ClearLookAt();
}

public interface IWatcher : ISensing
{
    ISight Sight => Senses.OfType<ISight>().First();
}