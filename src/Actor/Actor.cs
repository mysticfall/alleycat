using System.Text.Json;
using System.Text.Json.Serialization;
using AlleyCat.Actor.Action;
using AlleyCat.Animation;
using AlleyCat.Common;
using AlleyCat.Control;
using AlleyCat.Entity;
using AlleyCat.Env;
using AlleyCat.Locomotion;
using AlleyCat.Metadata;
using AlleyCat.Rig;
using AlleyCat.Rig.Human;
using AlleyCat.Sense;
using AlleyCat.Sense.Hearing;
using AlleyCat.Sense.Sight;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using AlleyCat.Template;
using Godot;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using static LanguageExt.Prelude;
using Error = LanguageExt.Common.Error;

namespace AlleyCat.Actor;

[JsonConverter(typeof(ActorIdJsonConverter))]
public readonly record struct ActorId : IEntityId
{
    public static readonly ActorId Player = new("Player");

    public string Value { get; }

    private ActorId(string value)
    {
        Value = value;
    }

    public static implicit operator string(ActorId id) => id.Value;

    public static Either<ParseError, ActorId> Create(string? value) =>
        Optional(value)
            .Filter(x => !string.IsNullOrWhiteSpace(x))
            .ToEither(new ParseError("Actor ID cannot be null or empty."))
            .Map(v => new ActorId(v));

    public override string ToString() => Value;

    private class ActorIdJsonConverter : JsonConverter<ActorId>
    {
        public override ActorId Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        )
        {
            var value = reader.GetString();

            return Create(value).ValueUnsafe();
        }

        public override void Write(
            Utf8JsonWriter writer,
            ActorId value,
            JsonSerializerOptions options
        )
        {
            writer.WriteStringValue(value.Value);
        }
    }
}

public interface IActor : IEntity<ActorId>,
    IWatcher,
    IListener,
    ISpeaker,
    IGendered,
    IRigged<HumanBone>,
    ILocomotive,
    ITemplateRenderable,
    IStatefulAnimatable,
    IMarked,
    IControllable
{
    Seq<IAction> Actions { get; }

    Seq<ISense> ISensing.Senses => Seq<ISense>(Hearing, Sight);
}

public static class ActorExtensions
{
    public static Eff<IEnv, ActionResult> Perform(this IActor actor, IActionRequest request) =>
        from action in actor.Actions
            .Find(x => x.Supports(request))
            .ToEff(Error.New(
                $"The actor {actor.Id} doesn't know how to perform {request}."
            ))
        from result in action.Perform(request, actor)
        select result;
}

public class Actor(
    ActorId id,
    Gender gender,
    ISight sight,
    IVoice voice,
    IHearing hearing,
    IRig<HumanBone> rig,
    ILocomotion locomotion,
    AnimationTree animationTree,
    Seq<IAction> actions,
    Seq<IControl> controls,
    Seq<IMarker> markers,
    Seq<ITemplateContextProvider> templateContextProviders,
    IO<Transform3D> globalTransform) : IActor
{
    public ActorId Id { get; } = id;

    public Gender Gender { get; } = gender;

    public ISight Sight { get; } = sight;

    public IVoice Voice { get; } = voice;

    public IHearing Hearing { get; } = hearing;

    public IRig<HumanBone> Rig { get; } = rig;

    public ILocomotion Locomotion { get; } = locomotion;

    public AnimationTree AnimationTree { get; } = animationTree;

    public Seq<ITemplateContextProvider> TemplateContextProviders { get; } =
        templateContextProviders;

    public Seq<IControl> Controls { get; } = controls;

    public Seq<IMarker> Markers { get; } = markers;

    public Seq<IAction> Actions { get; } = actions;

    public IO<Transform3D> GlobalTransform { get; } = globalTransform;

    IEntityId IEntity.Id => Id;

    IRig IRigged.Rig => Rig;

    public override string ToString() => $"Actor({Id})";
}

public interface IActorContainer
{
    Eff<IEnv, IActor> Player => FindActorById(ActorId.Player)
        .Bind(x => x.ToEff(Error.New("The player actor does not exist.")));

    Eff<IEnv, Seq<IActor>> Actors { get; }

    Eff<IEnv, Option<IActor>> FindActor(Func<IActor, bool> predicate) =>
        Actors.Map(x => x.Find(predicate));

    Eff<IEnv, Option<IActor>> FindActorById(ActorId id) =>
        FindActor(a => a.Id == id);
}