using LanguageExt;

namespace AlleyCat.Rig.Ik;

public interface IIkRig : IRig
{
    IObservable<Duration> OnBeforeIk { get; }

    IObservable<Duration> OnAfterIk { get; }
}

public interface IIkRig<in TBone> : IIkRig, IRig<TBone> where TBone : struct, Enum;