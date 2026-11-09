using System.Runtime.CompilerServices;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Core.Threading;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Scene;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Tests the AI-013 image payload pipeline at the runner level (TR-9/TR-10): an image-carrying tool result is
/// model-visible as an image part within its tool exchange through the real runtime-bound tool wrapper, stays
/// visible through continuations of its action chain — transport retries and invalid-response recovery — and is
/// disposed to a textual placeholder at the next fresh scheduling turn while the exchange, its tool call, and its
/// arguments remain retained (AI-002 TR-17 as amended, TR-20).
/// </summary>
public sealed class AgentSessionRunnerVisualContextTests
{
    private const string Instructions = "Private test instructions.";

    private const string MinimapToolName = "minimap_like";

    private const string ViewToolName = "view_like";

    private static readonly byte[] _minimapBytes = [1, 2, 3, 4];

    private static readonly byte[] _viewBytes = [5, 6, 7, 8];

    /// <summary>
    /// A tool result carrying an image is presented as an image part within its tool exchange (AI-013 TR-10): the
    /// unchanged textual result stays the function result content, and an adjacent presentation message inside the
    /// same exchange carries a label plus the image bytes at the exchange's position.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithImageCarryingToolResult_PresentsTheImageWithinItsExchange()
    {
        using CancellationTokenSource lifetime = new();
        VisualContextImage minimap = CreateMinimap(centreX: 1.5, centreY: 2.5, radius: 10, gameTime: 42.5);
        var tool = new VisualContextTool(MinimapToolName, minimap);
        ScriptedSessionClient client = new(
            Respond(CreateCall("map-call", MinimapToolName)),
            Respond(CreateCall("map-call-2", MinimapToolName)),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(client, [tool.Function]);

        await runner.RunAsync(lifetime.Token);

        IReadOnlyList<ChatMessage> replay = client.Requests[1];
        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User, ChatRole.User],
            replay.Select(static message => message.Role));
        // The textual result channel is unchanged (AI-013 TR-5): the function result content carries the message.
        FunctionResultContent result = Assert.IsType<FunctionResultContent>(Assert.Single(replay[2].Contents));
        Assert.Equal("map-call", result.CallId);
        Assert.Equal("Map ready.", result.Result?.ToString());
        // The image part sits inside the same exchange, immediately after its tool-result message, in a message
        // whose label identifies the capture (AI-013 TR-10).
        ChatMessage presentation = replay[3];
        Assert.Equal(ChatRole.User, presentation.Role);
        Assert.Contains(
            "Visual context from your tool action: minimap image centred at (1.5, 2.5) covering a 10 m radius, "
                + "captured at game time 42.5 s.",
            presentation.Text,
            StringComparison.Ordinal);
        DataContent image = Assert.Single(presentation.Contents.OfType<DataContent>());
        Assert.True(image.HasTopLevelMediaType("image"));
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(_minimapBytes, image.Data.ToArray());
        VisualContextImage payload = Assert.IsType<VisualContextImage>(image);
        Assert.Equal(VisualContextImageKind.Minimap, payload.Kind);
        Assert.Equal(42.5, payload.GameTimeSeconds);
    }

    /// <summary>
    /// The disposal boundary is exactly the next fresh scheduling turn (AI-013 TR-9): the replacement request
    /// carries no image content anywhere, the presentation is replaced in place by the placeholder recording kind,
    /// area, and capture game time, and the exchange — tool call, arguments, and result — remains retained
    /// forever. A new image of the new chain stays visible, and the already-issued request of the old chain keeps
    /// the exact image it was sent with.
    /// </summary>
    [Fact]
    public async Task RunAsync_NextFreshSchedulingTurn_DisposesImagesToPlaceholdersAndKeepsTheExchange()
    {
        using CancellationTokenSource lifetime = new();
        TaskCompletionSource replacementStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource replacementCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        VisualContextImage staleImage = CreateMinimap(centreX: 1.5, centreY: 2.5, radius: 10, gameTime: 42.5);
        VisualContextImage freshImage = CreateMinimap(
            centreX: -3,
            centreY: 4,
            radius: 12,
            gameTime: 60,
            bytes: [0xAB, 0xCD]);
        var tool = new VisualContextTool(MinimapToolName, staleImage, freshImage);
        ScriptedSessionClient client = new(
            Respond(CreateCall("stale-call", MinimapToolName)),
            HoldUntilCancelledStep(replacementStarted, replacementCancelled),
            Respond(CreateCall("fresh-call", MinimapToolName)),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(client, [tool.Function]);

        Task runTask = runner.RunAsync(lifetime.Token);
        await replacementStarted.Task;
        runner.InvalidateForFreshTurn();
        await replacementCancelled.Task;
        await runTask;

        // The creating chain's request keeps the exact image it was sent with.
        Assert.Contains(client.Requests[1].SelectMany(static message => message.Contents), static content =>
            content is DataContent data && data.HasTopLevelMediaType("image"));

        // The replacement request never carries the stale image (AI-013 TR-9).
        IReadOnlyList<ChatMessage> replacement = client.Requests[2];
        Assert.DoesNotContain(
            replacement.SelectMany(static message => message.Contents),
            static content => content is DataContent data && data.HasTopLevelMediaType("image"));
        FunctionCallContent retainedCall = Assert.Single(
            replacement.SelectMany(static message => message.Contents).OfType<FunctionCallContent>(),
            call => call.CallId == "stale-call");
        Assert.NotNull(retainedCall.Arguments);
        FunctionResultContent retainedResult = Assert.Single(
            replacement.SelectMany(static message => message.Contents).OfType<FunctionResultContent>(),
            result => result.CallId == "stale-call");
        Assert.Equal("Map ready.", retainedResult.Result?.ToString());
        // The presentation message stays at its exchange position, carrying the placeholder in place of the pixels.
        ChatMessage disposed = replacement[3];
        Assert.Equal(ChatRole.User, disposed.Role);
        Assert.Contains(
            "[Image no longer available — minimap image centred at (1.5, 2.5) covering a 10 m radius, captured at "
                + "game time 42.5 s. Capture again to see current visuals.]",
            disposed.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(disposed.Contents, static content => content is DataContent);

        // The new chain's image is present and the stale one never reappears (AI-013 TR-9).
        DataContent newImage = Assert.Single(ImageParts(client.Requests[3]));
        Assert.Equal(new byte[] { 0xAB, 0xCD }, newImage.Data.ToArray());
    }

    /// <summary>
    /// A continuation of the action chain does not dispose (AI-013 TR-9): the invalid-response recovery request is
    /// a rematerialisation of the same chain, so its replay still carries the image.
    /// </summary>
    [Fact]
    public async Task RunAsync_InvalidResponseRecovery_KeepsTheImageAlive()
    {
        using CancellationTokenSource lifetime = new();
        VisualContextImage image = CreateView(gameTime: 7.25);
        var tool = new VisualContextTool(ViewToolName, image);
        ScriptedSessionClient client = new(
            Respond(CreateCall("view-call", ViewToolName)),
            Respond(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ordinary text"))),
            Respond(CreateCall("view-call-2", ViewToolName)),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(
            client,
            [tool.Function],
            invalidResponseRecoveryPolicy: new InvalidResponseRecoveryPolicy(3, [TimeSpan.Zero]));

        await runner.RunAsync(lifetime.Token);

        Assert.Equal(4, client.Requests.Count);
        int[] replayIndexes = [1, 2];
        foreach (int replayIndex in replayIndexes)
        {
            DataContent replayImage = Assert.Single(ImageParts(client.Requests[replayIndex]));
            Assert.Equal(_viewBytes, replayImage.Data.ToArray());
            Assert.Equal("image/jpeg", replayImage.MediaType);
        }
    }

    /// <summary>
    /// An exact transport retry reuses the frozen image-carrying request byte-identically (AI-013 TR-9, AI-002
    /// TR-4): the retry replays the same image part at the same exchange position.
    /// </summary>
    [Fact]
    public async Task RunAsync_TransportRetryWithinTheChain_ReusesTheFrozenImageCarryingRequest()
    {
        using CancellationTokenSource lifetime = new();
        VisualContextImage image = CreateMinimap(centreX: 0, centreY: 0, radius: 5, gameTime: 1);
        var tool = new VisualContextTool(MinimapToolName, image);
        ScriptedSessionClient client = new(
            Respond(CreateCall("map-call", MinimapToolName)),
            FailStep(new HttpRequestException("reset")),
            Respond(CreateCall("map-call-2", MinimapToolName)),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(client, [tool.Function], retryDelays: [TimeSpan.Zero]);

        await runner.RunAsync(lifetime.Token);

        Assert.Equal(4, client.Requests.Count);
        int[] retryIndexes = [1, 2];
        foreach (int retryIndex in retryIndexes)
        {
            DataContent retryImage = Assert.Single(ImageParts(client.Requests[retryIndex]));
            Assert.Equal(_minimapBytes, retryImage.Data.ToArray());
        }
    }

    /// <summary>
    /// A batch generating several images presents them together in one message within the same exchange: each
    /// image keeps its own per-kind media type and the label identifies every capture (AI-013 TR-10).
    /// </summary>
    [Fact]
    public async Task RunAsync_TwoImageCallsInOneBatch_PresentTogetherInOneExchangeMessage()
    {
        using CancellationTokenSource lifetime = new();
        VisualContextImage minimap = CreateMinimap(centreX: 1, centreY: 1, radius: 8, gameTime: 3);
        VisualContextImage view = CreateView(gameTime: 3);
        var minimapTool = new VisualContextTool(MinimapToolName, minimap);
        var viewTool = new VisualContextTool(ViewToolName, view);
        ChatMessage batch = new(
            ChatRole.Assistant,
            [
                new FunctionCallContent("map-call", MinimapToolName, new Dictionary<string, object?>()),
                new FunctionCallContent("view-call", ViewToolName, new Dictionary<string, object?>()),
            ]);
        ScriptedSessionClient client = new(
            Respond(batch),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(client, [minimapTool.Function, viewTool.Function]);

        await runner.RunAsync(lifetime.Token);

        IReadOnlyList<ChatMessage> replay = client.Requests[1];
        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User, ChatRole.User],
            replay.Select(static message => message.Role));
        ChatMessage presentation = replay[3];
        Assert.Equal(
            "Visual context from your tool actions:\n"
            + "- minimap image centred at (1, 1) covering a 8 m radius, captured at game time 3 s\n"
            + "- view capture through your own eyes, captured at game time 3 s",
            presentation.Contents.OfType<TextContent>().Single().Text);
        DataContent[] images = [.. presentation.Contents.OfType<DataContent>()];
        Assert.Equal(["image/png", "image/jpeg"], images.Select(static image => image.MediaType));
        Assert.Equal(2, images.Length);
        Assert.Equal(_minimapBytes, images[0].Data.ToArray());
        Assert.Equal(_viewBytes, images[1].Data.ToArray());
    }

    /// <summary>
    /// The fabricated auto-mode exchange arrives through the request-context suffix exactly once per request,
    /// immediately before the fresh scene-status message (AI-013 TR-8): it is never appended to the retained
    /// transcript — a later request carries no earlier cycle's fabricated call IDs — its images flow through the
    /// cycle-image capture at their carrying message position, including an image nested inside a fabricated
    /// tool-result's content (AI-011 TR-26–TR-27), and a plain scene-status-only suffix is unaffected.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithAFabricatedAutoExchangeSuffix_PresentsItOncePerRequestAndNeverRetainsIt()
    {
        using CancellationTokenSource lifetime = new();
        var contextSource = new FabricatedExchangeContextSource(OpenAIChatClientKind.Responses);
        var sink = new RecordingTranscriptSink();
        ScriptedSessionClient client = new(
            Respond(CreateCall("plain-call", "plain_action")),
            EndQuietly(lifetime));
        AgentSessionRunner runner = new(
            client,
            Instructions,
            [new ChatMessage(ChatRole.User, "Bootstrap")],
            [CreatePlainFunction()],
            NullLogger.Instance,
            invalidResponseRecoveryPolicy: new InvalidResponseRecoveryPolicy(3, [TimeSpan.Zero]),
            requestContextSource: contextSource,
            transcriptSink: sink);

        await runner.RunAsync(lifetime.Token);

        IReadOnlyList<ChatMessage> first = client.Requests[0];
        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User],
            first.Select(static message => message.Role));
        Assert.Equal("scene:0", first[^1].Text);
        string[] firstCallIds = [.. first[1].Contents.OfType<FunctionCallContent>().Select(static call => call.CallId!)];
        Assert.All(firstCallIds, callId => Assert.StartsWith("runtime-visual-context-", callId, StringComparison.Ordinal));

        // The accepted response completed the logical request, so the next request rebuilds the fabricated
        // exchange from fresh captures: new call IDs, and no earlier fabricated call anywhere in the replay.
        IReadOnlyList<ChatMessage> second = client.Requests[1];
        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.User],
            second.Select(static message => message.Role));
        string[] secondCallIds = [.. second[3].Contents.OfType<FunctionCallContent>().Select(static call => call.CallId!)];
        Assert.All(secondCallIds, callId => Assert.StartsWith("runtime-visual-context-", callId, StringComparison.Ordinal));
        Assert.DoesNotContain(
            second.SelectMany(static message => message.Contents).OfType<FunctionCallContent>(),
            call => firstCallIds.Contains(call.CallId));

        // Each cycle's transcript captured both fabricated images at the fabricated tool-result message's
        // position — the in-result Responses transport — and nothing else claims an image (AI-011 TR-26–TR-27).
        int[] fabricatedToolMessageIndexes = [2, 4];
        Assert.Equal(2, sink.Cycles.Count);
        for (int cycleIndex = 0; cycleIndex < sink.Cycles.Count; cycleIndex++)
        {
            MindSessionCycleTranscript cycle = sink.Cycles[cycleIndex];
            Assert.Equal(
                [fabricatedToolMessageIndexes[cycleIndex], fabricatedToolMessageIndexes[cycleIndex]],
                cycle.MessageImages.Select(static messageImage => messageImage.RequestMessageIndex));
            Assert.Equal(
                [VisualContextImageKind.Minimap, VisualContextImageKind.View],
                cycle.MessageImages.Select(static messageImage => messageImage.Image.Kind));
        }
    }

    /// <summary>The image parts of one captured request — the transport-neutral model-visible image surface.</summary>
    private static DataContent[] ImageParts(IReadOnlyList<ChatMessage> request)
        => [.. request
            .SelectMany(static message => message.Contents)
            .OfType<DataContent>()
            .Where(static content => content.HasTopLevelMediaType("image"))];

    private static VisualContextImage CreateMinimap(
        double centreX,
        double centreY,
        double radius,
        double gameTime,
        byte[]? bytes = null)
        => VisualContextImage.Minimap(
            bytes ?? _minimapBytes,
            centreX,
            centreY,
            radius,
            gameTime);

    private static VisualContextImage CreateView(double gameTime, byte[]? bytes = null)
        => VisualContextImage.View(bytes ?? _viewBytes, gameTime);

    private static ChatMessage CreateCall(string callId, string name, IDictionary<string, object?>? arguments = null)
        => new(
            ChatRole.Assistant,
            [new FunctionCallContent(callId, name, arguments ?? new Dictionary<string, object?>())]);

    private static AIFunction CreatePlainFunction()
        => AIFunctionFactory.Create(
            () => "Done.",
            new AIFunctionFactoryOptions
            {
                Name = "plain_action",
                Description = "Do nothing, successfully.",
                ExcludeResultSchema = true,
            });

    private static Func<CancellationToken, Task<ChatResponse>> Respond(ChatMessage message)
        => _ => Task.FromResult(new ChatResponse(message));

    private static Func<CancellationToken, Task<ChatResponse>> Respond(ChatResponse response)
        => _ => Task.FromResult(response);

    private static Func<CancellationToken, Task<ChatResponse>> FailStep(Exception exception)
        => _ => Task.FromException<ChatResponse>(exception);

    private static Func<CancellationToken, Task<ChatResponse>> EndQuietly(CancellationTokenSource lifetime)
        => cancellationToken =>
        {
            lifetime.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        };

    private static Func<CancellationToken, Task<ChatResponse>> HoldUntilCancelledStep(
        TaskCompletionSource requestStarted,
        TaskCompletionSource? cancellationObserved = null)
        => async cancellationToken =>
        {
            _ = requestStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _ = cancellationObserved?.TrySetResult();
                throw;
            }

            return new ChatResponse();
        };

    private static AgentSessionRunner CreateRunner(
        ScriptedSessionClient client,
        IList<AITool> tools,
        IReadOnlyList<TimeSpan>? retryDelays = null,
        IInvalidResponseRecoveryPolicy? invalidResponseRecoveryPolicy = null)
        => new(
            client,
            Instructions,
            [new ChatMessage(ChatRole.User, "Bootstrap")],
            tools,
            NullLogger.Instance,
            retryDelays: retryDelays,
            invalidResponseRecoveryPolicy: invalidResponseRecoveryPolicy
                ?? new InvalidResponseRecoveryPolicy(3, [TimeSpan.Zero]),
            requestContextSource: new SceneStatusContextSource());

    /// <summary>
    /// Request-context source fabricating the auto-mode visual-context exchange through the production composer
    /// (AI-013 TR-8): each materialisation rebuilds the exchange from fresh captures — distinct bytes and fresh
    /// fabricated call IDs — and renders the fresh scene-status message last.
    /// </summary>
    private sealed class FabricatedExchangeContextSource(OpenAIChatClientKind chatClientKind)
        : IAgentRequestContextSource
    {
        private long _callSequence;
        private int _materialisationCount;

        public ValueTask<AgentRequestContext> MaterialiseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = _materialisationCount++;
            VisualContextImage minimap = CreateMinimap(
                centreX: index,
                centreY: index,
                radius: 10,
                gameTime: index,
                bytes: [(byte)(0x10 + index), 0x11]);
            VisualContextImage view = CreateView(gameTime: index, bytes: [(byte)(0x20 + index), 0x21]);
            IReadOnlyList<ChatMessage> exchange = VisualContextAutoExchange.Compose(
                [minimap, view],
                chatClientKind,
                () => VisualContextAutoExchange.FormatCallId(++_callSequence));
            return ValueTask.FromResult(new AgentRequestContext(
                [],
                [.. exchange, new ChatMessage(ChatRole.User, $"scene:{index}")],
                index));
        }

        public void Retain(object? confirmation)
        {
        }

        public void Confirm(object? confirmation)
        {
        }

        public void Discard(object? confirmation)
        {
        }
    }

    /// <summary>Captures every recorded cycle for transcript assertions.</summary>
    private sealed class RecordingTranscriptSink : IAgentSessionTranscriptSink
    {
        public List<MindSessionCycleTranscript> Cycles { get; } = [];

        public void Record(MindSessionCycleTranscript cycle) => Cycles.Add(cycle);
    }

    /// <summary>Context source rendering only the fresh scene-status suffix, as the production source does.</summary>
    private sealed class SceneStatusContextSource : IAgentRequestContextSource
    {
        private int _materialisationCount;

        public ValueTask<AgentRequestContext> MaterialiseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = _materialisationCount++;
            return ValueTask.FromResult(new AgentRequestContext(
                [],
                [new ChatMessage(ChatRole.User, $"scene:{index}")],
                index));
        }

        public void Retain(object? confirmation)
        {
        }

        public void Confirm(object? confirmation)
        {
        }

        public void Discard(object? confirmation)
        {
        }
    }

    /// <summary>
    /// Image-returning tool created through the real runtime-bound wrapper — <c>AgentTool.CreateFunction</c> with
    /// a delegate returning an image-carrying <see cref="AgentToolResult" /> — so coverage spans the tool result
    /// envelope, the delivery envelope, and the runner in one production path. Each invocation returns the next
    /// queued image payload.
    /// </summary>
    private sealed class VisualContextTool(string toolName, params VisualContextImage[] images)
    {
        public AIFunction Function { get; } = CreateFunction(toolName, images);

        private static AIFunction CreateFunction(string toolName, VisualContextImage[] images)
        {
            var character = new TestCharacter("owner");
            var scene = new TestSceneContext([character]);
            var mind = (TestMind)RuntimeHelpers.GetUninitializedObject(typeof(TestMind));
            _ = mind.WithOwner(character);
            var queue = new Queue<VisualContextImage>(images);
            return AgentTool.CreateFunction(
                (CancellationToken _) => Task.FromResult(new AgentToolResult(
                    "Map ready.",
                    image: queue.Dequeue())),
                new ScenarioContext(character, scene),
                mind,
                new ImmediateDispatcher(),
                toolName,
                "Returns visual context.");
        }
    }

    private sealed partial class TestMind : AlleyCat.Mind.Mind
    {
        private ICharacter _owner = null!;

        public TestMind WithOwner(ICharacter owner)
        {
            _owner = owner;
            return this;
        }

        protected override ICharacter ResolveOwningCharacter() => _owner;
    }

    private sealed class ImmediateDispatcher : IMainThreadDispatcher
    {
        public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return ValueTask.CompletedTask;
        }

        public ValueTask InvokeAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return action(cancellationToken);
        }
    }

    private sealed class TestCharacter(string id) : ICharacter
    {
        public string Id { get; set; } = id;

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

    private sealed record TestSceneContext(IReadOnlyCollection<ICharacter> Characters) : ISceneContext
    {
        public ICharacter Player => throw new InvalidOperationException(
            "Scene context contains no player character. Scene authoring guarantees the player is present.");

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId)
            => Characters.FirstOrDefault(character => string.Equals(character.FullId, fullId, StringComparison.Ordinal));

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException();
    }

    private sealed class ScriptedSessionClient(params Func<CancellationToken, Task<ChatResponse>>[] steps)
        : IChatClient
    {
        private readonly Queue<Func<CancellationToken, Task<ChatResponse>>> _steps = new(steps);

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add([.. messages]);
            return _steps.Count == 0
                ? throw new InvalidOperationException("The scripted session client ran out of scripted steps.")
                : _steps.Dequeue()(cancellationToken);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
