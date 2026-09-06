using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Core.Threading;
using AlleyCat.Core.Time;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.AI.Watch;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI.Watch;

/// <summary>Headless runtime coverage for bounded watch identities and ordinary removal.</summary>
[Headless]
public sealed class WatchRegistryIntegrationTests
{
    /// <summary>Registry binding exposes authorable watch functions and monotonically allocates opaque IDs.</summary>
    [Fact]
    public async Task ProximityTool_ArmsBoundedNonReusedWatchesAndUnwatchIsOrdinary()
    {
        var owner = new TestCharacter("owner");
        var subject = new TestCharacter("subject");
        var mind = new TestMind(owner);
        mind.SetClockForTest(new TestClock());
        var registry = new WatchRegistry();
        var tool = new ProximityWatchTool();
        registry.Conditions = [tool];
        mind.AddChild(registry);
        var context = new ScenarioContext(owner, new TestScene([owner, subject]));

        try
        {
            IReadOnlyList<Microsoft.Extensions.AI.AITool> functions = registry.BindSessionAndCreateTools(
                context,
                mind,
                new ImmediateDispatcher());

            Assert.Equal("watch_proximity", Assert.Single(functions).Name);
            await AddPositionAsync(mind, subject.FullId, 3f);
            AgentToolResult first = await tool.ArmAsync(registry, subject.FullId, maximumDistance: 2f);
            Assert.Contains("w1", first.Message, StringComparison.Ordinal);
            Assert.Contains("Outside", first.Message, StringComparison.Ordinal);
            Assert.Contains("Current retained distance is 3.", first.Message, StringComparison.Ordinal);
            Assert.Empty(first.Observations);

            for (int index = 2; index <= WatchRegistry.DefaultMaximumActiveWatches; index++)
            {
                AgentToolResult armed = await tool.ArmAsync(registry, subject.FullId, maximumDistance: 2f);
                Assert.Contains($"w{index}", armed.Message, StringComparison.Ordinal);
            }

            AgentToolResult capacity = await tool.ArmAsync(registry, subject.FullId, maximumDistance: 2f);
            Assert.Contains("32 active watches", capacity.Message, StringComparison.Ordinal);
            Assert.Empty(capacity.Observations);

            AgentToolResult removed = registry.Unwatch("w1");
            AgentToolResult missing = registry.Unwatch("w1");
            AgentToolResult replacement = await tool.ArmAsync(registry, subject.FullId, maximumDistance: 2f);
            Assert.Equal("Removed watch w1.", removed.Message);
            Assert.Contains("nothing was removed", missing.Message, StringComparison.Ordinal);
            Assert.Contains("w33", replacement.Message, StringComparison.Ordinal);
            Assert.Equal(
                [.. Enumerable.Range(2, 32).Select(static id => $"w{id}")],
                registry.GetActiveWatchSnapshot().Select(static watch => watch.WatchId));
        }
        finally
        {
            registry.EndSession();
            mind.Free();
        }
    }

    /// <summary>Proximity setup rejects invalid subject IDs and non-finite or negative limits before arming.</summary>
    [Fact]
    public async Task ProximityTool_RejectsInvalidParametersBeforeArming()
    {
        var owner = new TestCharacter("owner");
        var mind = new TestMind(owner);
        var registry = new WatchRegistry();
        var tool = new ProximityWatchTool();
        registry.Conditions = [tool];
        mind.AddChild(registry);
        _ = registry.BindSessionAndCreateTools(
            new ScenarioContext(owner, new TestScene([owner])),
            mind,
            new ImmediateDispatcher());

        try
        {
            _ = await Assert.ThrowsAsync<ArgumentException>(() => tool.ArmAsync(registry, "item:subject", 1f));
            _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => tool.ArmAsync(registry, "char:subject", float.NaN));
            _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => tool.ArmAsync(registry, "char:subject", -0.1f));
            Assert.Empty(registry.GetActiveWatchSnapshot());
        }
        finally
        {
            registry.EndSession();
            mind.Free();
        }
    }

    /// <summary>Only retained relative-position evidence drives the complete Unknown, Outside, and Inside transition table.</summary>
    [Fact]
    public async Task ProximityWatch_EmitsOnlyMeaningfulEvidenceTransitionsAndReturnsToUnknownOnExpiry()
    {
        var owner = new TestCharacter("owner");
        var subject = new TestCharacter("subject");
        var clock = new TestClock();
        var mind = new TestMind(owner);
        mind.SetClockForTest(clock);
        var registry = new WatchRegistry();
        var tool = new ProximityWatchTool
        {
            OutcomeImportance = 2.5f,
            ImmediateReconsideration = true,
        };
        registry.Conditions = [tool];
        mind.AddChild(registry);
        _ = registry.BindSessionAndCreateTools(
            new ScenarioContext(owner, new TestScene([owner, subject])),
            mind,
            new ImmediateDispatcher());

        try
        {
            _ = await tool.ArmAsync(registry, subject.FullId, maximumDistance: 2f);
            await AddPositionAsync(mind, subject.FullId, 3f);
            Assert.Empty(mind.PersistentEvents);
            ProximityWatchStatus outside = Assert.IsType<ProximityWatchStatus>(
                Assert.Single(registry.GetActiveWatchSnapshot()).Status);
            Assert.Equal("Outside", outside.State);
            Assert.Equal(3f, outside.Evidence!.Distance);

            await AddPositionAsync(mind, subject.FullId, 2f);
            ObservedProximityTransition entered = Assert.IsType<ObservedProximityTransition>(
                Assert.Single(mind.PersistentEvents).Payload);
            Assert.Equal(ProximityTransition.Entered, entered.Transition);
            Assert.Equal(2.5f, entered.Importance);
            Assert.True(entered.RequiresImmediateReconsideration);
            Assert.Equal(2f, entered.Distance);
            Assert.DoesNotContain("w1", entered.Render(owner), StringComparison.Ordinal);

            await AddPositionAsync(mind, subject.FullId, 3f);
            ObservedProximityTransition left = Assert.IsType<ObservedProximityTransition>(mind.PersistentEvents[^1].Payload);
            Assert.Equal(ProximityTransition.Left, left.Transition);

            clock.CurrentSeconds = RelativePositionObservationLifetimePolicy.DefaultEvidenceLifetimeSeconds + 1d;
            mind.ProcessExpiryForTest();
            Assert.Equal(
                "Unknown",
                Assert.IsType<ProximityWatchStatus>(Assert.Single(registry.GetActiveWatchSnapshot()).Status).State);
            Assert.Equal(2, mind.PersistentEvents.Count);

            await AddPositionAsync(mind, subject.FullId, 1f);
            ObservedProximityTransition reentered = Assert.IsType<ObservedProximityTransition>(mind.PersistentEvents[^1].Payload);
            Assert.Equal(ProximityTransition.Entered, reentered.Transition);
            Assert.Equal(3, mind.PersistentEvents.Count);
        }
        finally
        {
            registry.EndSession();
            mind.Free();
        }
    }

    /// <summary>Fatal cleanup prevents an already queued watch outcome or later evidence from reaching the Mind.</summary>
    [Fact]
    public async Task RegistryEndSession_DropsDeferredOutcomesAndDetachesEvidenceSubscription()
    {
        var owner = new TestCharacter("owner");
        var subject = new TestCharacter("subject");
        var mind = new TestMind(owner);
        mind.SetClockForTest(new TestClock());
        var registry = new WatchRegistry();
        var tool = new ProximityWatchTool();
        registry.Conditions = [tool];
        mind.AddChild(registry);
        _ = registry.BindSessionAndCreateTools(
            new ScenarioContext(owner, new TestScene([owner, subject])),
            mind,
            new ImmediateDispatcher());

        try
        {
            _ = await tool.ArmAsync(registry, subject.FullId, maximumDistance: 2f);
            await AddPositionAsync(mind, subject.FullId, 3f);
            // Queue source evidence, then end the registry before Mind's deferred serial worker can evaluate it.
            mind.EnqueueForTest(new ObservedRelativePosition(
                subject.FullId,
                1f,
                RelativeDirection.Front,
                RelativeDirection.Front));
            registry.EndSession();
            await mind.DrainForTestAsync();

            Assert.Empty(registry.GetActiveWatchSnapshot());
            Assert.Empty(mind.PersistentEvents);

            await AddPositionAsync(mind, subject.FullId, 1f);
            Assert.Empty(mind.PersistentEvents);

            // Direct-child exit is an idempotent second lifetime boundary with no retained watch state.
            registry._ExitTree();
            Assert.Empty(registry.GetActiveWatchSnapshot());
        }
        finally
        {
            registry.EndSession();
            mind.Free();
        }
    }

    /// <summary>Concurrent Minds keep even a shared authored condition resource isolated by registry runtime state.</summary>
    [Fact]
    public async Task Registries_KeepIDsEvidenceAndUnwatchActionsIndependentAcrossMinds()
    {
        var firstOwner = new TestCharacter("first_owner");
        var secondOwner = new TestCharacter("second_owner");
        var subject = new TestCharacter("subject");
        var firstMind = new TestMind(firstOwner);
        var secondMind = new TestMind(secondOwner);
        firstMind.SetClockForTest(new TestClock());
        secondMind.SetClockForTest(new TestClock());
        var firstRegistry = new WatchRegistry();
        var secondRegistry = new WatchRegistry();
        var sharedTool = new ProximityWatchTool();
        firstRegistry.Conditions = [sharedTool];
        secondRegistry.Conditions = [sharedTool];
        firstMind.AddChild(firstRegistry);
        secondMind.AddChild(secondRegistry);
        _ = firstRegistry.BindSessionAndCreateTools(
            new ScenarioContext(firstOwner, new TestScene([firstOwner, subject])),
            firstMind,
            new ImmediateDispatcher());
        _ = secondRegistry.BindSessionAndCreateTools(
            new ScenarioContext(secondOwner, new TestScene([secondOwner, subject])),
            secondMind,
            new ImmediateDispatcher());

        try
        {
            AgentToolResult firstArm = await sharedTool.ArmAsync(firstRegistry, subject.FullId, 2f);
            AgentToolResult secondArm = await sharedTool.ArmAsync(secondRegistry, subject.FullId, 2f);
            Assert.Contains("w1", firstArm.Message, StringComparison.Ordinal);
            Assert.Contains("w1", secondArm.Message, StringComparison.Ordinal);

            await AddPositionAsync(firstMind, subject.FullId, 3f);
            await AddPositionAsync(firstMind, subject.FullId, 1f);
            _ = Assert.Single(firstMind.PersistentEvents);
            Assert.Empty(secondMind.PersistentEvents);
            Assert.Equal(
                "Inside",
                Assert.IsType<ProximityWatchStatus>(Assert.Single(firstRegistry.GetActiveWatchSnapshot()).Status).State);
            Assert.Equal(
                "Unknown",
                Assert.IsType<ProximityWatchStatus>(Assert.Single(secondRegistry.GetActiveWatchSnapshot()).Status).State);

            _ = firstRegistry.Unwatch("w1");
            Assert.Empty(firstRegistry.GetActiveWatchSnapshot());
            _ = Assert.Single(secondRegistry.GetActiveWatchSnapshot());
            await AddPositionAsync(secondMind, subject.FullId, 1f);
            _ = Assert.Single(secondMind.PersistentEvents);
        }
        finally
        {
            firstRegistry.EndSession();
            secondRegistry.EndSession();
            firstMind.Free();
            secondMind.Free();
        }
    }

    private static async Task AddPositionAsync(TestMind mind, string subjectId, float distance)
    {
        mind.EnqueueForTest(new ObservedRelativePosition(
            subjectId,
            distance,
            RelativeDirection.Front,
            RelativeDirection.Front));
        await mind.DrainForTestAsync();
    }

    private sealed partial class TestMind(ICharacter owner) : AlleyCat.Mind.Mind
    {
        public IReadOnlyList<AcceptedObservationEntry> PersistentEvents => GetPersistentEventTimelineSnapshot();

        public void EnqueueForTest(ObservedRelativePosition observation) => EnqueueObservation(observation);

        public Task DrainForTestAsync() => DrainPerceptionsForTestingAsync();

        public void ProcessExpiryForTest() => ProcessRetainedObservationExpiry();

        public void SetClockForTest(IGameClock clock) => SetGameClockLoaderForTesting(() => clock);

        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    private sealed class TestClock : IGameClock
    {
        public double CurrentSeconds
        {
            get;
            set;
        }

        public double NowSeconds => CurrentSeconds;
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

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; } = Transform3D.Identity;
    }

    private sealed class TestScene(IReadOnlyCollection<ICharacter> characters) : ISceneContext
    {
        public IReadOnlyCollection<ICharacter> Characters => characters;

        public ICharacter Player => characters.First();

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId)
            => characters.SingleOrDefault(character => string.Equals(character.FullId, fullId, StringComparison.Ordinal));

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException($"Missing '{fullId}'.");
    }
}
