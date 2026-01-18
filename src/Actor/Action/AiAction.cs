using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using AlleyCat.Ai.Agent.Tool;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Logging;
using LanguageExt;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Actor.Action;

public readonly partial record struct AiFunctionName
{
    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_-]{0,9}$")]
    private static partial Regex NamePattern();

    public string Value { get; }

    private AiFunctionName(string value)
    {
        Value = value;
    }

    public static implicit operator string(AiFunctionName name) => name.Value;

    public static Either<ParseError, AiFunctionName> Create(string? value) =>
        Optional(value)
            .Filter(x => !string.IsNullOrWhiteSpace(x))
            .ToEither(new ParseError("AI function name cannot be null or empty."))
            .Bind<AiFunctionName>(x => NamePattern().IsMatch(x)
                ? Right(new AiFunctionName(x))
                : Left(new ParseError($"Invalid AI function name: \"{value}\"."))
            );

    public override string ToString() => Value;
}

public interface IAiAction : IAction
{
    AiFunctionName FunctionName { get; }

    Delegate Signature { get; }

    Eff<IEnv, AIFunction> CreateAiFunction(IActor actor);
}

public interface IAiAction<TReq> : IAiAction, IAction<TReq>, ILoggable where TReq : IActionRequest
{
    Either<ParseError, TReq> ParseArgs(AIFunctionArguments arguments);

    Eff<IEnv, AIFunction> IAiAction.CreateAiFunction(IActor actor) =>
        from env in runtime<IEnv>()
        let serialiserOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
                .WithAddedModifier(ActionToolResponse.PolymorphicModifier)
        }
        let func = AIFunctionFactory.Create(
            Signature,
            FunctionName,
            serializerOptions: serialiserOptions
        )
        select (AIFunction)new AiFunctionDelegate(
            actor,
            func,
            ParseArgs,
            Perform,
            env,
            Logger
        );

    private sealed class AiFunctionDelegate(
        IActor actor,
        AIFunction inner,
        Func<AIFunctionArguments, Either<ParseError, TReq>> parser,
        Func<TReq, IActor, Eff<IEnv, ActionResult>> action,
        IEnv env,
        ILogger logger
    ) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken
        )
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Invoking AI function '{name}' with arguments: {args}", Name, arguments);
            }

            var process = (
                from request in parser(arguments).ToEff(identity)
                from result in action(request, actor)
                select new ActionToolResponse(Name, result)
            ).As();

            var fin = await process.RunAsync(env);

            return fin.Match(
                identity,
                e =>
                {
                    logger.LogError(e, "Failed to execute AI function.");

                    return new ActionToolResponse(
                        Name,
                        new ActionResult.Failure(e.Message)
                    );
                });
        }
    }
}

public readonly record struct ActionToolResponse(
    // ReSharper disable once NotAccessedPositionalProperty.Global
    string Action,
    // ReSharper disable once NotAccessedPositionalProperty.Global
    ActionResult Result
) : IToolResponse
{
    public static Action<JsonTypeInfo> PolymorphicModifier { get; } = info =>
    {
        if (info.Type != typeof(IToolResponse)) return;

        var options = info.PolymorphismOptions ??= new JsonPolymorphismOptions();
        var derivedType = new JsonDerivedType(typeof(ActionToolResponse), "action");

        options.DerivedTypes.Add(derivedType);
    };
}