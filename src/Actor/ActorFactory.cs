using AlleyCat.Actor.Action;
using AlleyCat.Env;
using AlleyCat.Metadata;
using AlleyCat.Sense.Hearing;
using AlleyCat.Sense.Sight;
using AlleyCat.Service;
using AlleyCat.Service.Typed;
using AlleyCat.Speech.Voice;
using AlleyCat.Template;
using AlleyCat.Common;
using AlleyCat.Control;
using AlleyCat.Locomotion;
using AlleyCat.Rig.Human;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Actor;

[GlobalClass]
public partial class ActorFactory : NodeFactory<IActor>, IServiceFactory
{
    public const string MarkerGroup = "Body Parts";

    [Export] public string? Id { get; set; }

    [Export] public Gender Gender { get; set; } = Gender.Male;

    [Export] public CameraEyeSightFactory? Sight { get; set; }

    [Export] public VoiceFactory? Voice { get; set; }

    [Export] public HearingFactory? Hearing { get; set; }

    [Export] public HumanRigFactory? Rig { get; set; }

    [Export] public LocomotionFactory? Locomotion { get; set; }

    [Export] public AnimationTree? AnimationTree { get; set; }

    [Export] public Node3D? Root { get; set; }

    [Export] public ActionFactory[] Actions { get; set; } = [];

    [Export] public ControlFactory[] Controls { get; set; } = [];

    [Export] public TemplateContextProviderFactory[] ContextProviders { get; set; } = [];

    InstantiationOption IServiceFactory.Instantiation => InstantiationOption.Singleton;

    protected override Eff<IEnv, IActor> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from id in ActorId.Create(Id).ToEff(identity)
        from sight in Sight
            .Require("Sight is not set.")
            .Bind(v => v.TypedService)
        from voice in Voice
            .Require("Voice is not set.")
            .Bind(v => v.TypedService)
        from hearing in Hearing
            .Require("Hearing is not set.")
            .Bind(v => v.TypedService)
        from rig in Rig
            .Require("Rig is not set.")
            .Bind(v => v.TypedService)
        from locomotion in Locomotion
            .Require("Locomotion is not set.")
            .Bind(x => x.TypedService)
        from animationTree in AnimationTree.Require("AnimationTree is not set.")
        from root in Root.Require("Root is not set.")
        from actions in Actions
            .AsIterable()
            .ToSeq()
            .Traverse(x => x.TypedService)
        from controls in Controls
            .AsIterable()
            .ToSeq()
            .Traverse(x => x.TypedService)
        from contextProviders in ContextProviders
            .AsIterable()
            .Traverse(x => x.TypedService)
        from markers in GetTree()
            .GetNodesInGroup(MarkerGroup)
            .Where(x => x is IServiceFactory<IMarker>)
            .Cast<IServiceFactory<IMarker>>()
            .AsIterable()
            .ToSeq()
            .Traverse(x => x.TypedService)
        select (IActor)new Actor(
            id: id,
            gender: Gender,
            sight: sight,
            voice: voice,
            hearing: hearing,
            rig: rig,
            locomotion: locomotion,
            animationTree: animationTree,
            actions: actions,
            controls: controls,
            markers: markers,
            templateContextProviders: contextProviders
                .Add(new ActorContextProvider())
                .ToSeq(),
            globalTransform: lift(() => root.GlobalTransform)
        );
}