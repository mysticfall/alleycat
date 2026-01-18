using System.ComponentModel;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Speech;
using AlleyCat.Logging;
using LanguageExt;
using LanguageExt.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Actor.Action;

public readonly record struct SpeakRequest(
    DialogueText Message,
    Option<ActorId> Target = default
) : IActionRequest;

public class SpeakAction(
    AiFunctionName name,
    ILoggerFactory? loggerFactory = null
) : IAiAction<SpeakRequest>
{
    [Description(
        "Speak to another character. Don't exceed the word limit. " +
        "Call multiple times for longer dialogue instead.")
    ]
    private static Task<ActionToolResponse> Speak(
        [Description("Speech content. Max 20 words, no emotes.")]
        string message,
        [Description("The target character's ID.")]
        string? target = null
    ) => throw new NotImplementedException();

    public AiFunctionName FunctionName => name;

    public Delegate Signature { get; } = Speak;

    public ILogger Logger { get; } = loggerFactory.GetLogger<SpeakAction>();

    public ILoggerFactory? LoggerFactory => loggerFactory;

    public Either<ParseError, SpeakRequest> ParseArgs(AIFunctionArguments arguments) =>
        from message in Optional(arguments.GetValueOrDefault("message"))
            .ToEither(new ParseError("Missing argument: 'message'."))
            .Bind(x => DialogueText
                .Create(x.ToString())
            )
        from target in Optional(arguments.GetValueOrDefault("target"))
            .Traverse(x => ActorId.Create(x.ToString()))
            .As()
        select new SpeakRequest(message, target);

    public Eff<IEnv, ActionResult> Perform(SpeakRequest request, IActor actor) =>
        from env in runtime<IEnv>()
        //FIXME: Unused for now.
        from target in request.Target
            .Traverse(env.Scene.FindActorById)
            .As()
            .Bind(x => x.ToEff(
                Error.New($"Failed to find the target actor: {request}")
            ))
        from _1 in actor.Speak(request.Message)
        select (ActionResult)new ActionResult.Success();
}