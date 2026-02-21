using AlleyCat.Animation;
using AlleyCat.Common;
using AlleyCat.Env;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Locomotion.Velocity;

[GlobalClass]
public partial class AnimVelocityCalculatorFactory : VelocityCalculatorFactory
{
    [Export] public AnimationTree? AnimationTree { get; set; }

    [Export] public StringName? PlaybackParameter { get; set; }

    [Export] public StringName? MovementParameter { get; set; }

    [Export] public StringName? IdleState { get; set; }

    [Export] public StringName? MovingState { get; set; }

    protected override Eff<IEnv, IVelocityCalculator> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from animationTree in AnimationTree.Require("AnimationTree is not set.")
        from playbackParam in AnimationParam.Create(PlaybackParameter).ToEff(identity)
        from playback in animationTree
            .Get(playbackParam)
            .As<AnimationNodeStateMachinePlayback>()
            .Require($"No playback node found at {playbackParam}.")
        from parameter in AnimationParam.Create(MovementParameter).ToEff(identity)
        from idleState in AnimationStateName.Create(IdleState).ToEff(identity)
        from movingState in AnimationStateName.Create(MovingState).ToEff(identity)
        select (IVelocityCalculator)new AnimVelocityCalculator(
            animationTree,
            playback,
            parameter,
            idleState,
            movingState
        );
}