using System.Reflection;
using System.Runtime.CompilerServices;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Core.Threading;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Vision;
using Microsoft.Extensions.AI;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Unit coverage for action-tool result contracts and metadata.
/// </summary>
public sealed class AgentToolTests
{
    /// <summary>The trusted turn context exposes exactly the approved immutable binding triple.</summary>
    [Fact]
    public void ScenarioContext_PublicSurface_IsExactlyCharacterSceneContextAndScenario()
    {
        Type contextType = typeof(ScenarioContext);
        PropertyInfo[] declaredProperties = contextType.GetProperties(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        FieldInfo[] declaredFields = contextType.GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        MethodInfo[] meaningfulDeclaredMethods =
        [
            .. contextType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName),
        ];

        Assert.False(typeof(IServiceProvider).IsAssignableFrom(contextType));
        Assert.Collection(
            declaredProperties.OrderBy(property => property.Name, StringComparer.Ordinal),
            property =>
            {
                Assert.Equal(nameof(ScenarioContext.Character), property.Name);
                Assert.Equal(typeof(ICharacter), property.PropertyType);
                Assert.True(property.CanRead);
                Assert.False(property.CanWrite);
            },
            property =>
            {
                Assert.Equal(nameof(ScenarioContext.Scenario), property.Name);
                Assert.Equal(typeof(Scenario), property.PropertyType);
                Assert.True(property.CanRead);
                Assert.False(property.CanWrite);
            },
            property =>
            {
                Assert.Equal(nameof(ScenarioContext.SceneContext), property.Name);
                Assert.Equal(typeof(ISceneContext), property.PropertyType);
                Assert.True(property.CanRead);
                Assert.False(property.CanWrite);
            });
        Assert.Empty(declaredFields);
        Assert.Empty(meaningfulDeclaredMethods);
    }

    /// <summary>The trusted context rejects either missing required capability and carries the nullable scenario.</summary>
    [Fact]
    public void ScenarioContext_Constructor_RejectsNullDependenciesAndAcceptsNullableScenario()
    {
        var character = new TestCharacter("owner");
        var scene = new TestSceneContext([character]);
        var scenario = new Scenario("Owner is guarding the market stall.");

        Assert.Equal("character", Assert.Throws<ArgumentNullException>(() => new ScenarioContext(null!, scene)).ParamName);
        Assert.Equal("sceneContext", Assert.Throws<ArgumentNullException>(() => new ScenarioContext(character, null!)).ParamName);

        var withoutScenario = new ScenarioContext(character, scene);
        var withScenario = new ScenarioContext(character, scene, scenario);

        Assert.Null(withoutScenario.Scenario);
        Assert.Same(scenario, withScenario.Scenario);
        Assert.Same(character, withScenario.Character);
        Assert.Same(scene, withScenario.SceneContext);
    }

    /// <summary>
    /// Result envelopes own an immutable ordered snapshot and permit optional messages and empty observations.
    /// </summary>
    [Fact]
    public void AgentToolResult_SnapshotsOrderedObservationsAndAllowsEmptyState()
    {
        List<AgentObservation> source =
        [
            new TestObservation("first"),
            new TestObservation("second"),
        ];

        var populated = new AgentToolResult("Done.", source);
        var empty = new AgentToolResult();
        source.Clear();

        Assert.Equal("Done.", populated.Message);
        Assert.Equal(["first", "second"], populated.Observations.Cast<TestObservation>().Select(x => x.Value));
        Assert.Null(empty.Message);
        Assert.Empty(empty.Observations);
    }

    /// <summary>
    /// Tool delegates with non-envelope return contracts are rejected before invocation.
    /// </summary>
    [Fact]
    public void CreateFunction_WithWrongDelegateResultType_FailsEarly()
    {
        var fixture = new ToolFixture();

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => fixture.CreateFunction(ToolHost.WrongResult));

        Assert.Contains(nameof(AgentToolResult), exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tool resources pass authored metadata through to the generated AI function.
    /// </summary>
    [Fact]
    public void CreateFunction_WithResourceMetadata_UsesConfiguredNameAndDescription()
    {
        var fixture = new ToolFixture();
        AIFunction function = fixture.CreateFunction(ToolHost.ValidResult, "speak", "Speak aloud.");

        Assert.Equal("speak", function.Name);
        Assert.Equal("Speak aloud.", function.Description);
    }

    /// <summary>
    /// Production delegates start only when the shared dispatcher flushes their deferred submission.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_DefersDelegateStartThroughDispatcherExactlyOnce()
    {
        var dispatcher = new DeferredDispatcher();
        var fixture = new ToolFixture(dispatcher);
        ToolHost.ResetCapture();
        AIFunction function = fixture.CreateFunction(ToolHost.CaptureCancellationToken);
        using CancellationTokenSource invocationCancellation = new();

        ValueTask<object?> invocation = function.InvokeAsync([], invocationCancellation.Token);

        Assert.Equal(1, dispatcher.SubmissionCount);
        Assert.Equal(invocationCancellation.Token, dispatcher.SubmissionCancellationToken);
        Assert.Null(ToolHost.CapturedCancellationToken);
        Assert.False(invocation.IsCompleted);

        dispatcher.Flush();

        Assert.Null(await invocation);
        Assert.Equal(invocationCancellation.Token, ToolHost.CapturedCancellationToken);
    }

    /// <summary>
    /// Trusted context is omitted from schema, cannot be overridden, and supplies exact captured objects to delegates.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_BindsTrustedContextOutsideModelSchema()
    {
        var fixture = new ToolFixture();
        ToolHost.ResetCapture();
        AIFunction function = fixture.CreateFunction(ToolHost.CaptureContext);

        Assert.DoesNotContain(nameof(ScenarioContext), function.JsonSchema.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("context", function.JsonSchema.ToString(), StringComparison.OrdinalIgnoreCase);

        object? result = await function.InvokeAsync(
            new AIFunctionArguments { ["context"] = "model override" },
            CancellationToken.None);

        Assert.Null(result);
        Assert.Same(fixture.Character, ToolHost.CapturedContext!.Character);
        Assert.Same(fixture.Scene, ToolHost.CapturedContext.SceneContext);
        Assert.Same(fixture.Scenario, ToolHost.CapturedContext.Scenario);
        Assert.Same(fixture.Character, ToolHost.CapturedContext.SceneContext.Find(((IIdentifiable)fixture.Character).FullId));
    }

    /// <summary>Ownership mismatch fails before dispatcher submission or delegate execution.</summary>
    [Fact]
    public async Task InvokeAsync_WithMismatchedOwner_FailsBeforeDispatch()
    {
        var dispatcher = new DeferredDispatcher();
        var fixture = new ToolFixture(dispatcher);
        var other = new TestCharacter("other");
        AIFunction function = AgentTool.CreateFunction(
            ToolHost.ValidResult,
            new ScenarioContext(other, fixture.Scene),
            fixture.Mind,
            dispatcher);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            function.InvokeAsync([], CancellationToken.None).AsTask);
        Assert.Equal(0, dispatcher.SubmissionCount);
    }

    private sealed class DeferredDispatcher : IMainThreadDispatcher
    {
        private Func<CancellationToken, ValueTask>? _action;
        private TaskCompletionSource? _completion;
        private CancellationToken _submissionCancellationToken;

        public int SubmissionCount
        {
            get; private set;
        }

        public CancellationToken SubmissionCancellationToken
        {
            get; private set;
        }

        public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask InvokeAsync(
            Func<CancellationToken, ValueTask> action,
            CancellationToken cancellationToken = default)
        {
            SubmissionCount++;
            SubmissionCancellationToken = cancellationToken;
            _submissionCancellationToken = cancellationToken;
            _action = action;
            _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return new ValueTask(_completion.Task);
        }

        public void Flush()
        {
            Func<CancellationToken, ValueTask> action = Assert.IsType<Func<CancellationToken, ValueTask>>(_action);
            TaskCompletionSource completion = Assert.IsType<TaskCompletionSource>(_completion);
            _ = FlushAsync(action, completion, _submissionCancellationToken);
        }

        private static async Task FlushAsync(
            Func<CancellationToken, ValueTask> action,
            TaskCompletionSource completion,
            CancellationToken cancellationToken)
        {
            try
            {
                await action(cancellationToken);
                _ = completion.TrySetResult();
            }
            catch (Exception exception)
            {
                _ = completion.TrySetException(exception);
            }
        }
    }

    private static class ToolHost
    {
        public static int ValidResultInvocationCount
        {
            get; private set;
        }

        public static Task<string> WrongResult() => Task.FromResult("wrong");

        public static ScenarioContext? CapturedContext
        {
            get; private set;
        }

        public static CancellationToken? CapturedCancellationToken
        {
            get; private set;
        }

        public static void ResetCapture()
        {
            CapturedContext = null;
            CapturedCancellationToken = null;
        }

        public static ValueTask<AgentToolResult> CaptureContext(ScenarioContext context)
        {
            CapturedContext = context;
            return ValueTask.FromResult(new AgentToolResult());
        }

        public static ValueTask<AgentToolResult> ValidResult()
        {
            ValidResultInvocationCount++;
            return ValueTask.FromResult(new AgentToolResult());
        }

        public static ValueTask<AgentToolResult> CaptureCancellationToken(CancellationToken cancellationToken)
        {
            CapturedCancellationToken = cancellationToken;
            return ValueTask.FromResult(new AgentToolResult());
        }
    }

    private sealed class ToolFixture
    {
        private readonly IMainThreadDispatcher _dispatcher;

        public ToolFixture(IMainThreadDispatcher? dispatcher = null)
        {
            _dispatcher = dispatcher ?? new ImmediateDispatcher();
            Scene = new TestSceneContext([Character]);
            _ = Mind.WithOwner(Character);
        }

        public TestCharacter Character { get; } = new("owner");

        public Scenario Scenario { get; } = new("The owner is resting by the fountain.");

        public TestMind Mind { get; } = (TestMind)RuntimeHelpers.GetUninitializedObject(typeof(TestMind));

        public TestSceneContext Scene
        {
            get;
        }

        public AIFunction CreateFunction(Delegate method, string? name = null, string? description = null)
            => AgentTool.CreateFunction(
                method,
                new ScenarioContext(Character, Scene, Scenario),
                Mind,
                _dispatcher,
                name,
                description);
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

    private sealed class TestCharacter(string id) : ICharacter
    {
        public string Id { get; set; } = id;

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();
    }

    private sealed record TestObservation(string Value) : AgentObservation
    {
        public override string TypeKey => "test.tool-result";

        public override float CalculateImportance(ObservationContext context) => 1f;
    }
}
