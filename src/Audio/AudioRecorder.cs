using AlleyCat.Env;
using AlleyCat.Logging;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;
using Error = LanguageExt.Common.Error;

namespace AlleyCat.Audio;

public interface IAudioRecorder : ILoggable
{
    protected AudioEffectRecord Recorder { get; }

    IO<bool> IsRecording => IO.lift(() => Recorder.IsRecordingActive());

    Eff<IEnv, Unit> StartRecording() =>
        from isRecording in IsRecording
        from _1 in guardnot(Recorder.IsRecordingActive(), Error.New("Recording is already active."))
        from _3 in liftEff(() =>
        {
            Recorder.SetRecordingActive(true);

            Logger.LogInformation("Started recording.");
        })
        select unit;

    Eff<IEnv, AudioStreamWav> StopRecording() =>
        from env in runtime<IEnv>()
        from isRecording in IsRecording
        from _1 in guard(isRecording, Error.New("Recording is not active."))
        from audio in liftEff(() =>
        {
            Recorder.SetRecordingActive(false);

            var audio = Recorder.GetRecording();

            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug(
                    "Stopped recording: {length:N} seconds ({size:N} bytes).",
                    audio.GetLength(),
                    audio.Data.Length
                );
            }

            return audio;
        })
        select audio;
}