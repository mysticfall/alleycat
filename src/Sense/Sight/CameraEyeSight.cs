using AlleyCat.Common;
using AlleyCat.Transform;
using AlleyCat.Logging;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Sense.Sight;

public class CameraEyeSight(
    Camera3D camera,
    VectorRange2 eyesRotationRange,
    AnimationTree animationTree,
    EyeAnimParams eyeAnimParams,
    Duration eyesBlinkInterval,
    Duration eyesBlinkVariation,
    IObservable<Duration> onPhysicsProcess,
    ILoggerFactory? loggerFactory = null
) : ICameraSight, IEyeSight
{
    public Camera3D Camera => camera;

    public IObservable<Duration> OnPhysicsProcess => onPhysicsProcess;

    public VectorRange2 EyesRotationRange => eyesRotationRange;

    public AnimationTree AnimationTree => animationTree;

    public EyeAnimParams EyeAnimParams => eyeAnimParams;

    public Duration EyesBlinkInterval => eyesBlinkInterval;

    public Duration EyesBlinkVariation => eyesBlinkVariation;

    public ILogger Logger { get; } = loggerFactory.GetLogger<CameraEyeSight>();

    public ILoggerFactory? LoggerFactory => loggerFactory;

    public IO<Option<ILocatable3d>> LookAt => _lookAt.ValueIO;

    public IO<Unit> SetLookAt(ILocatable3d target) => _lookAt.SwapIO(_ => Some(target)).IgnoreF().As();

    public IO<Unit> ClearLookAt() => _lookAt.SwapIO(_ => None).IgnoreF().As();

    private readonly Atom<Option<ILocatable3d>> _lookAt = Atom<Option<ILocatable3d>>(None);
}