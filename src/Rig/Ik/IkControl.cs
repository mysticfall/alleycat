using LanguageExt;

namespace AlleyCat.Rig.Ik;

public interface IIkControl
{
    IO<bool> IsActive { get; }

    IO<Unit> SetActive(bool active);

    IO<float> Influence { get; }

    IO<Unit> SetInfluence(float influence);
}