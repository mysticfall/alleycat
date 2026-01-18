using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AlleyCat.Actor.Action;
using AlleyCat.Ai.Agent.Tool;
using AlleyCat.Ai.Lore;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Template;
using AlleyCat.Speech;
using LanguageExt;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using OpenAI.Responses;
using static LanguageExt.Prelude;
using ChatMessage = OpenAI.Chat.ChatMessage;
using ChatResponseFormat = Microsoft.Extensions.AI.ChatResponseFormat;
using Error = LanguageExt.Common.Error;

namespace AlleyCat.Ai.Agent;

public interface IAgenticMind : IMind
{
    AIAgent Agent { get; }

    ITemplate Instructions { get; }

    Seq<IAgentTool> Tools => Seq<IAgentTool>(
        new LoreTool(LoreBook, LoggerFactory)
    );

    ILoreBook LoreBook { get; }

    ChatOptions ChatOptions => new()
    {
        //FIXME: Changing this to a structured format requires setting the ToolMode to `Auto`, which prevents the agent 
        //       from invoking tools.
        ResponseFormat = ChatResponseFormat.Text
    };

    Seq<JsonDerivedType> ObservationTypes => Seq(
        new JsonDerivedType(typeof(ObservedSpeech), "speech")
    );

    bool IsImportant(IObservation observation) => observation switch
    {
        ObservedSpeech speech => speech.Actor != Actor.Id,
        _ => false
    };

    Eff<IEnv, Unit> IMind.Think(SourceT<IO, IObservation> observations)
    {
        var thread = Agent.GetNewThread();

        var serialiserOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
                .WithAddedModifier(info =>
                {
                    if (info.Type != typeof(IObservation)) return;

                    ObservationTypes.Iter(type =>
                    {
                        var options = info.PolymorphismOptions ??= new JsonPolymorphismOptions();

                        options.DerivedTypes.Add(type);
                    });
                })
        };

        return
            from env in runtime<IEnv>()
            from context in liftEff(() =>
            {
                var services = new ServiceCollection();

                services.AddSingleton(env);

                return new AgentContext(this, services.BuildServiceProvider());
            })
            from ct in cancelToken
            from _ in liftIO(async () =>
            {
                var includeInstructions = true;

                Duration duration = 0.Seconds();

                while (!ct.IsCancellationRequested)
                {
                    var turn =
                        from events in WaitFor(duration)
                        from nextDuration in RunAgent(context, events, includeInstructions)
                        select nextDuration;
                    
                    var result = await turn.RunAsync(env);
                    
                    result.Match(
                        x =>
                        {
                            includeInstructions = false;
                            duration = x;
                        },
                        e =>
                        {
                            duration = 3.Seconds();
                    
                            if (Logger.IsEnabled(LogLevel.Error))
                            {
                                Logger.LogError(
                                    e,
                                    "Failed to run the agent loop. Retrying in {duration} seconds.",
                                    duration.Milliseconds / 1000
                                );
                            }
                        }
                    );
                }
            })
            select unit;

        Eff<IEnv, Seq<IObservation>> WaitFor(Duration duration) =>
            from _1 in liftEff(() =>
            {
                if (Logger.IsEnabled(LogLevel.Trace))
                {
                    Logger.LogTrace("Observing events for {duration} seconds.", duration.Milliseconds / 1000);
                }
            })
            from env in runtime<IEnv>()
            from ct in cancelToken
            from actors in env.Scene.Actors
            let others = actors.Filter(x => x.Id != Actor.Id)
            from events in observations
                .TakeFor(duration)
                .Bind(x => IsImportant(x)
                    ? SourceT.lift<IO, Seq<IObservation>>(Seq(Seq(x), Seq<IObservation>()))
                    : SourceT.pure<IO, Seq<IObservation>>(Seq(x))
                )
                .CollectUntil(x => x.Item.IsEmpty || ct.IsCancellationRequested)
                .Map(x => x.Fold(Seq<IObservation>(), (s, y) => s + y))
                .As()
            from _2 in liftIO(async () =>
            {
                var isAnyoneSpeaking = others
                    .Traverse(x => x.IsSpeaking)
                    .Map(x => x.Exists(identity));

                var interval = Schedule.spaced(200.Milliseconds());

                Logger.LogDebug("Waiting for other actors to finish speaking.");

                await repeatWhile(interval, isAnyoneSpeaking, identity).RunAsync();
            })
            from additionalEvents in observations
                .TakeFor(500.Milliseconds())
                .CollectUntil(_ => ct.IsCancellationRequested)
                .As()
            select events + additionalEvents;

        Eff<IEnv, Duration> RunAgent(
            AgentContext context,
            Seq<IObservation> events,
            bool includeInstructions = true
        ) =>
            from _ in liftEff(() =>
            {
                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug("Running agent with input: {events}", events);
                }
            })
            from env in runtime<IEnv>()
            from actors in env.Scene.Actors
            // TODO: Implement dialogue target management API.
            from targetActor in actors
                .Find(x => x.Id != Actor.Id)
                .ToEff(Error.New("Failed to find the target actor."))
            from actions in Actor.Actions
                .OfType<IAiAction>()
                .AsIterable()
                .ToSeq()
                .Traverse(x => x.CreateAiFunction(Actor))
                .As()
            from tools in Tools
                .Traverse(x => x.CreateFunction(context))
                .Map(x => x + actions)
                .As()
            from actorContext in Actor.CreateTemplateContext(Actor)
            from targetContext in targetActor.CreateTemplateContext(Actor)
            from instructions in includeInstructions
                ? Instructions.Render(Map<object, object?>(
                    ("actor", actorContext),
                    ("target", targetContext),
                    ("loreentries", LoreBook
                        .TableOfContents
                        .Bind(x => x.AsIterable().ToSeq())
                    )
                )).Map(Optional)
                : SuccessEff<Option<string>>(None)
            from cancellationToken in cancelToken
            from response in liftIO(async () =>
            {
                var chatOptions = ChatOptions;

                chatOptions.AllowMultipleToolCalls = true;
                chatOptions.Tools = tools.Cast<AITool>().ToList();
                chatOptions.ToolMode = ChatToolMode.RequireAny;
                chatOptions.RawRepresentationFactory = _ => new CreateResponseOptions
                {
                    StoredOutputEnabled = false
                };

                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug("Running agent with instructions: {instructions}", instructions);
                }

                var input = new AgentInput(events);
                var inputText = JsonSerializer.Serialize(input, serialiserOptions);

                var messages = instructions
                    .Map(ChatMessage (x) => new DeveloperChatMessage(x))
                    .ToList()
                    .Add(new UserChatMessage(inputText));

                var response = await Agent.RunAsync(
                    messages,
                    thread,
                    new ChatClientAgentRunOptions
                    {
                        ChatOptions = chatOptions
                    },
                    cancellationToken
                );

                return response;
            })
            from _2 in response.Content
                .AsIterable()
                .Find(x => x.Kind == ChatMessageContentPartKind.Refusal)
                .Match(
                    x => FailEff<Unit>(
                        Error.New($"The agent refused the request: {x.Text}")
                    ),
                    () => unitEff
                )
            from firstMessage in response.Content
                .AsIterable()
                .FindBack(x => x.Kind == ChatMessageContentPartKind.Text)
                .ToEff(Error.New("No text content in response."))
            let text = firstMessage.Text
            from _3 in liftEff(() =>
            {
                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug("Assistant response: {text}", text);
                }
            })
            from output in AgentResponse.Parse(text)
                .MapFail(_ => new ParseError($"Failed to parse assistant JSON response: {text}"))
            select (Duration)output.NextUpdate.Seconds();
    }

    // ReSharper disable once NotAccessedPositionalProperty.Local
    private readonly record struct AgentInput(Seq<IObservation> Observations);
}