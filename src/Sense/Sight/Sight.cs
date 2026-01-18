using AlleyCat.Transform;
using LanguageExt;

namespace AlleyCat.Sense.Sight;

public interface IVision : IPercept;

public interface ISight : IActiveSense<IVision>, IObject3d
{
    IO<Option<IObject3d>> LookAt { get; }

    IO<Unit> SetLookAt(IObject3d target);

    IO<Unit> ClearLookAt();
}

public interface IWatcher : ISensing
{
    ISight Sight => Senses.OfType<ISight>().First();
}