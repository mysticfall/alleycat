using AlleyCat.Env;
using AlleyCat.Logging;
using AlleyCat.Transform;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Sense.Hearing;

public interface ISoundSource : IObject3d, ILoggable
{
    Eff<IEnv, Unit> EmitSound(ISound data, Length range)
    {
        return
            from env in runtime<IEnv>()
            let scene = env.Scene
            from _1 in liftEff(() =>
                {
                    if (Logger.IsEnabled(LogLevel.Debug))
                    {
                        Logger.LogDebug("Emitting sound: {sound}", data);
                    }
                }
            )
            from origin in this.Origin
            from sound in scene.AddNode(
                new SoundBubble
                {
                    Data = data,
                    Source = this,
                    Range = range
                }
            )
            from _2 in liftEff(() => { sound.GlobalPosition = origin; })
            select unit;
    }
}