using Godot;
using LanguageExt;
using static LanguageExt.Prelude;
using Error = LanguageExt.Common.Error;

namespace AlleyCat.Audio;

public static class AudioServerApi
{
    public static Eff<T> GetEffectBus<T>(string name) where T : AudioEffect => liftEff<T>(() =>
    {
        var busIndex = AudioServer.GetBusIndex(name);

        if (busIndex == -1)
        {
            return Error.New($"No such audio bus found: {name}");
        }

        var bus = AudioServer.GetBusEffect(busIndex, 0);

        if (bus is not T effect)
        {
            return Error.New($"Audio bus {name} has an unexpected type: {bus.GetType().Name}");
        }

        return effect;
    });

    public static Eff<AudioEffectRecord> GetRecordBus(string name) => GetEffectBus<AudioEffectRecord>(name);
}