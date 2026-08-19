using System.Collections;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Core.Threading;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Isolated observation timeline and tool-wrapper coverage for the base Mind.
/// </summary>
[Headless]
public sealed partial class MindSchedulingIntegrationTests
{
    /// <summary>
    /// Timeline snapshots preserve insertion order and cannot mutate either the snapshot or its private source.
    /// </summary>
    [Fact]
    public void TimelineSnapshot_IsOrderedStableAndImmutable()
    {
        TestMind mind = new()
        {
            Enabled = false
        };
        TestObservation first = new(0f, "first");
        TestObservation second = new(0f, "second");
        TestObservation later = new(0f, "later");

        mind.ObserveForTest(first);
        mind.ObserveForTest(second);
        IReadOnlyList<AgentObservation> snapshot = mind.GetTimelineForTest();

        Assert.Collection(
            snapshot,
            item => Assert.Equal("first", Assert.IsType<TestObservation>(item).Value),
            item => Assert.Equal("second", Assert.IsType<TestObservation>(item).Value));
        IList mutableView = Assert.IsAssignableFrom<IList>(snapshot);
        _ = Assert.Throws<NotSupportedException>(() => mutableView.Add(later));

        mind.ObserveForTest(later);
        Assert.Equal(snapshot, mind.GetTimelineForTest().Take(2));
        Assert.Collection(
            mind.GetTimelineForTest(),
            item => Assert.Equal("first", Assert.IsType<TestObservation>(item).Value),
            item => Assert.Equal("second", Assert.IsType<TestObservation>(item).Value),
            item => Assert.Equal("later", Assert.IsType<TestObservation>(item).Value));
        mind.Free();
    }

    /// <summary>
    /// Every committed observation carries one non-null, monotonically non-decreasing game-time stamp in seconds.
    /// </summary>
    [Fact]
    public void ObservationIntake_StampsCommittedObservationsWithNonDecreasingGameSeconds()
    {
        TestMind mind = new()
        {
            Enabled = false,
            ObservationImportanceThreshold = 1f,
        };

        mind.ObserveForTest(new TestObservation(1f, "first"));
        mind.ObserveForTest(new TestObservation(1f, "second"));

        IReadOnlyList<AgentObservation> timeline = mind.GetTimelineForTest();
        Assert.Collection(
            timeline,
            item => Assert.Equal("first", Assert.IsType<TestObservation>(item).Value),
            item => Assert.Equal("second", Assert.IsType<TestObservation>(item).Value));
        foreach (AgentObservation observation in timeline)
        {
            _ = Assert.NotNull(observation.ObservedAt);
        }

        Assert.True(
            timeline[0].ObservedAt <= timeline[1].ObservedAt,
            "ObservedAt stamps must be monotonically non-decreasing.");
        mind.Free();
    }

    /// <summary>
    /// Tool wrappers await envelopes, project only messages, stamp actors, and preserve ordered atomic ingestion.
    /// </summary>
    [Fact]
    public async Task AgentToolWrapper_AwaitsProjectsAndAtomicallyIngestsStampedOrderedObservations()
    {
        TestMind mind = new()
        {
            Enabled = false
        };
        ScenarioContext context = CreateToolContext(mind.Owner);
        IMainThreadDispatcher dispatcher = Game.Instance.GetRequiredService<IMainThreadDispatcher>();
        ToolHost.Reset();
        AIFunction function = AgentTool.CreateFunction(ToolHost.WaitForResultAsync, context, mind, dispatcher, "test_tool");
        ValueTask<object?> invocation = function.InvokeAsync([], CancellationToken.None);

        Assert.Empty(mind.GetTimelineForTest());
        ToolHost.Complete(new AgentToolResult(
            "model acknowledgement",
            [
                new ObservedSpeech("spoofed-actor", "raw-self-device", "first"),
                new TestObservation(1f, "second"),
            ]));
        object? projected = await invocation;

        Assert.Equal("model acknowledgement", projected);
        Assert.Collection(
            mind.GetTimelineForTest(),
            item =>
            {
                ObservedSpeech speech = Assert.IsType<ObservedSpeech>(item);
                Assert.Equal("char:scheduling_owner", speech.ActorId);
                Assert.Equal("raw-self-device", speech.VoiceId);
            },
            item => Assert.Equal("second", Assert.IsType<TestObservation>(item).Value));
        mind.Free();
    }

    /// <summary>
    /// Empty/null-message envelopes are valid while invalid batches, exceptions, cancellation, and null results ingest nothing.
    /// </summary>
    [Fact]
    public async Task AgentToolWrapper_HandlesEmptyAndFailureResultsWithoutPartialIngestion()
    {
        TestMind mind = new()
        {
            Enabled = false
        };
        ScenarioContext context = CreateToolContext(mind.Owner);
        IMainThreadDispatcher dispatcher = Game.Instance.GetRequiredService<IMainThreadDispatcher>();
        AIFunction emptyFunction = AgentTool.CreateFunction(ToolHost.EmptyAsync, context, mind, dispatcher);
        AIFunction invalidBatchFunction = AgentTool.CreateFunction(ToolHost.InvalidBatchAsync, context, mind, dispatcher);
        AIFunction throwingFunction = AgentTool.CreateFunction(ToolHost.ThrowAsync, context, mind, dispatcher);
        AIFunction nullFunction = AgentTool.CreateFunction(ToolHost.NullAsync, context, mind, dispatcher);
        AIFunction cancellingFunction = AgentTool.CreateFunction(ToolHost.CancelAsync, context, mind, dispatcher);

        Assert.Null(await emptyFunction.InvokeAsync([], CancellationToken.None));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(invalidBatchFunction.InvokeAsync([], CancellationToken.None).AsTask);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(throwingFunction.InvokeAsync([], CancellationToken.None).AsTask);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(nullFunction.InvokeAsync([], CancellationToken.None).AsTask);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            cancellingFunction.InvokeAsync([], cancellation.Token).AsTask);

        Assert.Empty(mind.GetTimelineForTest());
        mind.Free();
    }

    private sealed partial class TestMind : MindBase
    {
        private readonly TestCharacter _character = new();

        public new ICharacter Owner => _character;

        public void ObserveForTest(AgentObservation observation) => Observe(observation);

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        protected override ICharacter ResolveOwningCharacter() => _character;
    }

    private static ScenarioContext CreateToolContext(ICharacter owner)
        => new(owner, new TestSceneContext([owner]));

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

    private static class ToolHost
    {
        private static TaskCompletionSource<AgentToolResult> _completion = CreateCompletion();

        public static Task<AgentToolResult> WaitForResultAsync() => _completion.Task;

        public static ValueTask<AgentToolResult> EmptyAsync()
            => ValueTask.FromResult(new AgentToolResult());

        public static Task<AgentToolResult> InvalidBatchAsync()
            => Task.FromResult(new AgentToolResult(
                observations:
                [
                    new TestObservation(1f, "valid"),
                    new TestObservation(float.NaN, "invalid"),
                ]));

        public static Task<AgentToolResult> ThrowAsync()
            => Task.FromException<AgentToolResult>(new InvalidOperationException("tool failed"));

        public static ValueTask<AgentToolResult> NullAsync()
            => ValueTask.FromResult<AgentToolResult>(null!);

        public static async ValueTask<AgentToolResult> CancelAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new AgentToolResult();
        }

        public static void Reset() => _completion = CreateCompletion();

        public static void Complete(AgentToolResult result) => _completion.SetResult(result);

        private static TaskCompletionSource<AgentToolResult> CreateCompletion()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record TestObservation(float Importance, string Value) : AgentObservation
    {
        public override string TypeKey => "test.scheduling";

        public override float CalculateImportance(ObservationContext context) => Importance;
    }

    private sealed class TestCharacter : ICharacter
    {
        public string Id { get; set; } = "scheduling_owner";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();
    }
}
