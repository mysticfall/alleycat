using AlleyCat.Animation;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Service.Typed;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Sense.Sight;

[GlobalClass]
public partial class CameraEyeSightFactory : NodeFactory<ISight>
{
    [Export] public Camera3D? Camera { get; set; }

    [Export] public bool Debug { get; set; }

    [ExportGroup("Animation")] [Export] public AnimationTree? AnimationTree { get; set; }

    [Export]
    public VectorRange2 EyesRotationRange { get; set; } =
        new(new Vector2(-50, -48), new Vector2(50, 42));

    [Export(PropertyHint.Range, "2.0,10.0")]
    public float EyesBlinkInterval { get; set; } = 5.0f;

    [Export(PropertyHint.Range, "0.0,5.0")]
    public float EyesBlinkVariation { get; set; } = 3.0f;

    [Export]
    public string? EyesBlinkParameter { get; set; } =
        "parameters/BlendTree/Blink Eyes/request";

    [Export]
    public string? EyesUpDownParameter { get; set; } =
        "parameters/BlendTree/Eyes Up Down/seek_request";

    [Export]
    public string? EyesRightLeftParameter { get; set; } =
        "parameters/BlendTree/Eyes Right Left/seek_request";

    protected override Eff<IEnv, ISight> CreateService(ILoggerFactory loggerFactory) =>
        from camera in Camera.Require("Camera is not set.")
        from animationTree in AnimationTree.Require("AnimationTree is not set.")
        from eyesBlinkParameter in AnimationParam
            .Create(EyesBlinkParameter)
            .ToEff(identity)
        from eyesUpDownParameter in AnimationParam
            .Create(EyesUpDownParameter)
            .ToEff(identity)
        from eyesRightLeftParameter in AnimationParam
            .Create(EyesRightLeftParameter)
            .ToEff(identity)
        select (ISight)new CameraEyeSight(
            camera,
            EyesRotationRange,
            animationTree,
            new EyeAnimParams(
                eyesBlinkParameter,
                eyesUpDownParameter,
                eyesRightLeftParameter
            ),
            EyesBlinkInterval,
            EyesBlinkVariation,
            OnPhysicsProcess,
            loggerFactory
        );
}