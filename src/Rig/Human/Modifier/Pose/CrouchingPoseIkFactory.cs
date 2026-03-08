using AlleyCat.Animation;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Rig.Ik;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Human.Modifier.Pose;

[GlobalClass]
public partial class CrouchingPoseIkFactory : HumanIkModifierFactory
{
    [Export] public AnimationTree? AnimationTree { get; set; }

    [ExportGroup("Animation Parameters")]
    [Export]
    public StringName? RootPlaybackParameter { get; set; } = "parameters/playback";

    [Export] public StringName? IdlePlaybackParameter { get; set; } = "parameters/Idle/playback";

    [Export] public StringName? SeekParameter { get; set; } = "parameters/Idle/Crouching/TimeSeek/seek_request";

    [ExportGroup("Animation States")]
    [Export]
    public StringName? IdleState { get; set; } = "Idle";

    [Export] public StringName? StandingState { get; set; } = "Standing";

    [Export] public StringName? CrouchingState { get; set; } = "Crouching";

    protected override Eff<IEnv, IIkModifier> CreateService(
        IRig<HumanBone> rig,
        IObservable<Duration> onIkProcess,
        ILoggerFactory loggerFactory
    ) =>
        from animationTree in AnimationTree.Require("AnimationTree is not set.")
        from rootPlaybackParam in AnimationParam.Create(RootPlaybackParameter).ToEff(identity)
        from rootPlayback in animationTree
            .Get(rootPlaybackParam)
            .As<AnimationNodeStateMachinePlayback>()
            .Require($"No root playback node found at {rootPlaybackParam}.")
        from idlePlaybackParam in AnimationParam.Create(IdlePlaybackParameter).ToEff(identity)
        from idlePlayback in animationTree
            .Get(idlePlaybackParam)
            .As<AnimationNodeStateMachinePlayback>()
            .Require($"No idle playback node found at {idlePlaybackParam}.")
        from seekParameter in AnimationParam.Create(SeekParameter).ToEff(identity)
        from idleState in AnimationStateName.Create(IdleState).ToEff(identity)
        from standingState in AnimationStateName.Create(StandingState).ToEff(identity)
        from crouchingState in AnimationStateName.Create(CrouchingState).ToEff(identity)
        select (IIkModifier)new CrouchingPoseIk(
            rig,
            animationTree,
            rootPlayback,
            idlePlayback,
            seekParameter,
            idleState,
            standingState,
            crouchingState,
            onIkProcess,
            loggerFactory
        );
}