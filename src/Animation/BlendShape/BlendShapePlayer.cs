using System.Reactive;
using System.Reactive.Linq;
using AlleyCat.Async;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Logging;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;
using Unit = LanguageExt.Unit;

namespace AlleyCat.Animation.BlendShape;

public interface IBlendShapePlayer : IRunnable, IFrameAware, ILoggable
{
    protected Seq<MeshInstance3D> Meshes { get; }

    protected BlendShapeSet BlendShapes { get; }

    IO<Option<BlendShapeAnim>> Animation { get; }

    IO<Unit> Play(BlendShapeAnim anim);

    IO<Unit> Stop();

    Eff<IEnv, IDisposable> IRunnable.Run => IO.lift(() =>
    {
        var mappings = Meshes.AsIterable()
            .Map(m => (m, BlendShapeMapping.FromMesh(m, BlendShapes)))
            .ToSeq();

        if (Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug(
                "Generated blendshape mappings for {:D} meshes.",
                mappings.Length
            );
        }

        var onAnimChange = OnProcess
            .Select(_ => Animation.Run())
            .DistinctUntilChanged()
            .Timestamp();

        var onAnimProcess = OnProcess
            .CombineLatest(onAnimChange)
            .Select(x => x.Second.Value
                .ToObservable()
                .Select(y => new Timestamped<BlendShapeAnim>(y, x.Second.Timestamp)))
            .Switch();

        onAnimProcess.Subscribe(x =>
        {
            var (frames, frameRate) = x.Value;
            var started = x.Timestamp;

            var elapsed = (DateTime.Now - started).TotalSeconds;

            var current = (int)(elapsed * frameRate);

            if (current >= frames.Length)
            {
                Logger.LogDebug("Animation finished.");

                Stop().Run();

                return;
            }

            if (Logger.IsEnabled(LogLevel.Trace))
            {
                Logger.LogTrace("Playing frame: {:N}.", current);
            }

            var blendShapes = frames[current];

            mappings.Iter(tuple =>
            {
                var (mesh, mapping) = tuple;

                mapping.Indexes.Pairs.Iter(i =>
                {
                    blendShapes.Find(i.Key).Match(
                        v => { mesh.SetBlendShapeValue(i.Value, v); },
                        () =>
                        {
                            if (Logger.IsEnabled(LogLevel.Warning))
                            {
                                Logger.LogWarning(
                                    "Missing blendshape: mesh={mesh}, name={name}",
                                    mesh.Name,
                                    i.Key
                                );
                            }
                        });
                });
            });
        });

        return onAnimProcess
            .Do(_ => { },
                e => Logger.LogError(e, "Failed to play a blendshape animation.")
            )
            .Retry()
            .Subscribe();
    });

    private readonly struct BlendShapeMapping
    {
        public Map<string, int> Indexes { get; init; }

        public static BlendShapeMapping FromMesh(
            MeshInstance3D mesh,
            BlendShapeSet blendShapes
        ) => new()
        {
            Indexes = blendShapes
                .BlendShapes
                .AsIterable()
                .Map(name => (name, mesh.FindBlendShapeByName(name)))
                .Filter(x => x.Item2 != -1)
                .ToMap()
        };
    }
}

public class BlendShapePlayer(
    Seq<MeshInstance3D> meshes,
    BlendShapeSet blendShapes,
    IObservable<Duration> onProcess,
    ILoggerFactory? loggerFactory = null
) : IBlendShapePlayer
{
    public Seq<MeshInstance3D> Meshes => meshes;

    public BlendShapeSet BlendShapes => blendShapes;

    public IObservable<Duration> OnProcess => onProcess;

    public ILogger Logger { get; } = loggerFactory.GetLogger<BlendShapePlayer>();

    public ILoggerFactory? LoggerFactory => loggerFactory;

    public IO<Option<BlendShapeAnim>> Animation => IO.lift(() => _animation);

    private Option<BlendShapeAnim> _animation;

    public IO<Unit> Play(BlendShapeAnim anim) => IO.lift(() => { _animation = anim; });

    public IO<Unit> Stop() => IO.lift(() => { _animation = None; });
}