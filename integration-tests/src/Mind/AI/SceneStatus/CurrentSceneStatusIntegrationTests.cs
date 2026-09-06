using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Core.Threading;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.SceneStatus;
using AlleyCat.Mind.AI.Watch;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Templating;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI.SceneStatus;

/// <summary>Headless integration coverage for typed current-scene-status stack composition and validation.</summary>
[Headless]
public sealed class CurrentSceneStatusIntegrationTests
{
    private const string CurrentScenePromptPath = "res://assets/characters/prompts/current_scene.tres";

    /// <inheritdoc/>
    [Fact]
    public async Task Stack_CompilesAtSessionStartAndRendersEachFreshSnapshotWithItsTypedRoot()
    {
        FakeCharacter owner = new("owner");
        TestProjector projector = new()
        {
            ProjectorID = "test-projector",
        };
        AgenticMind mind = new();
        mind.AddChild(projector);
        RecordingCompiler compiler = new();
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<ITemplateCompiler>(compiler)
            .BuildServiceProvider();
        PromptStack stack = CreateStack("test-projector", typeof(AttendedCharactersSceneStatus));
        PromptSectionBuildContext promptContext = new(services, new FakeScene([owner]), owner);

        try
        {
            var registry = SceneStatusProjectorRegistry.Discover(mind);
            CompiledSceneStatusPrompt compiled = await stack.CompileSceneStatusAsync(promptContext, registry);

            Assert.Equal(1, compiler.CompileCount);
            Assert.Equal(0, projector.ProjectCount);
            registry.ValidateProjections(CreateStatusContext(owner, timestamp: 10d), stack.Sections);
            Assert.Equal(1, projector.ProjectCount);

            string first = await compiled.RenderAsync(CreateStatusContext(owner, timestamp: 11d));
            string second = await compiled.RenderAsync(CreateStatusContext(owner, timestamp: 12d));

            Assert.Equal("<Typed Status>\nroot:11\n</Typed Status>", first);
            Assert.Equal("<Typed Status>\nroot:12\n</Typed Status>", second);
            Assert.Equal(3, projector.ProjectCount);
            Assert.Equal(2, compiler.Template.RootRenderCount);
            Assert.Equal(0, compiler.Template.DictionaryRenderCount);
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// The compiled authored current-scene stack omits an empty watch section and preserves the populated generic watch identity output.
    /// </summary>
    [Fact]
    public async Task CompiledCurrentSceneStatus_OmitsEmptyWatchesAndRendersPopulatedGenericIdentifiersOnly()
    {
        FakeCharacter owner = new("owner");
        FakeCharacter subject = new("subject");
        TestAgenticMind mind = new(owner);
        WatchRegistry watchRegistry = new();
        ProximityWatchTool watchTool = new();
        watchRegistry.Conditions = [watchTool];
        WatchSceneStatusProjector watchProjector = new()
        {
            ProjectorID = WatchSceneStatusProjector.ProjectorIDValue,
            Registry = watchRegistry,
        };
        mind.AddChild(new AttendedCharacterSceneStatusProjector
        {
            ProjectorID = AttendedCharacterSceneStatusProjector.ProjectorIDValue,
        });
        mind.AddChild(watchRegistry);
        mind.AddChild(watchProjector);
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<ITemplateCompiler>(new FluidTemplateCompiler())
            .BuildServiceProvider();
        PromptStack stack = Assert.IsType<PromptStack>(ResourceLoader.Load(CurrentScenePromptPath), exactMatch: false);
        FakeScene scene = new([owner, subject]);
        PromptSectionBuildContext promptContext = new(services, scene, owner);

        try
        {
            var projectors = SceneStatusProjectorRegistry.Discover(mind);
            CompiledSceneStatusPrompt compiled = await stack.CompileSceneStatusAsync(promptContext, projectors);
            string empty = await compiled.RenderAsync(CreateStatusContext(owner, scene, timestamp: 1d));

            Assert.DoesNotContain("<Active Watches>", empty, StringComparison.Ordinal);
            Assert.DoesNotContain("</Active Watches>", empty, StringComparison.Ordinal);
            Assert.DoesNotContain("Active Watches", empty, StringComparison.Ordinal);
            Assert.DoesNotContain("No active watches", empty, StringComparison.OrdinalIgnoreCase);

            _ = watchRegistry.BindSessionAndCreateTools(
                new ScenarioContext(owner, scene),
                mind,
                new ImmediateDispatcher());
            _ = await watchTool.ArmAsync(watchRegistry, subject.FullId, maximumDistance: 2f);

            string populated = await compiled.RenderAsync(CreateStatusContext(owner, scene, timestamp: 2d));

            Assert.Contains("<Active Watches>", populated, StringComparison.Ordinal);
            Assert.Contains("- WatchId: w1, ConditionId: proximity, SubjectId: char:subject", populated, StringComparison.Ordinal);
            Assert.DoesNotContain("Unknown", populated, StringComparison.Ordinal);
            Assert.DoesNotContain("Evidence", populated, StringComparison.Ordinal);
            Assert.DoesNotContain("Status", populated, StringComparison.Ordinal);
        }
        finally
        {
            watchRegistry.EndSession();
            mind.Free();
        }
    }

    /// <inheritdoc/>
    [Fact]
    public async Task Wiring_FailsClearlyForMissingDuplicateAndIncompatibleProjectors()
    {
        FakeCharacter owner = new("owner");
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<ITemplateCompiler>(new RecordingCompiler())
            .BuildServiceProvider();
        PromptSectionBuildContext promptContext = new(services, new FakeScene([owner]), owner);

        AgenticMind missingMind = new();
        try
        {
            var missingRegistry = SceneStatusProjectorRegistry.Discover(missingMind);
            InvalidOperationException missing = await Assert.ThrowsAsync<InvalidOperationException>(
                () => CreateStack("missing", typeof(AttendedCharactersSceneStatus)).CompileSceneStatusAsync(promptContext, missingRegistry));
            Assert.Contains("requires direct projector ID 'missing'", missing.Message, StringComparison.Ordinal);
        }
        finally
        {
            missingMind.Free();
        }

        AgenticMind duplicateMind = new();
        TestProjector first = new()
        {
            ProjectorID = "duplicate"
        };
        TestProjector second = new()
        {
            ProjectorID = "duplicate"
        };
        duplicateMind.AddChild(first);
        duplicateMind.AddChild(second);
        try
        {
            InvalidOperationException duplicate = Assert.Throws<InvalidOperationException>(
                () => SceneStatusProjectorRegistry.Discover(duplicateMind));
            Assert.Contains("duplicate direct scene-status projector ID 'duplicate'", duplicate.Message, StringComparison.Ordinal);
        }
        finally
        {
            duplicateMind.Free();
        }

        AgenticMind incompatibleMind = new();
        NullProjectionProjector incompatibleProjector = new()
        {
            ProjectorID = "incompatible",
        };
        incompatibleMind.AddChild(incompatibleProjector);
        try
        {
            var incompatibleRegistry = SceneStatusProjectorRegistry.Discover(incompatibleMind);
            PromptStack stack = CreateStack("incompatible", typeof(AttendedCharactersSceneStatus));
            _ = await stack.CompileSceneStatusAsync(promptContext, incompatibleRegistry);

            InvalidOperationException incompatible = Assert.Throws<InvalidOperationException>(
                () => incompatibleRegistry.ValidateProjections(CreateStatusContext(owner, timestamp: 1d), stack.Sections));
            Assert.Contains("returned a null projection", incompatible.Message, StringComparison.Ordinal);
        }
        finally
        {
            incompatibleMind.Free();
        }
    }

    /// <summary>Projects current fresh-scene attention and excludes evidence removed by retention expiry.</summary>
    [Fact]
    public void AttendedCharacterProjector_UsesFreshSceneAttentionAndNonExpiredRetainedEvidenceOnly()
    {
        FakeCharacter owner = new("owner");
        FakeCharacter attended = new("attended");
        FakeCharacter expired = new("expired");
        AcceptedObservationEntry[] retainedLog =
        [
            Entry(
                sequenceID: 1,
                observedAt: 42d,
                new ObservedRelativePosition(
                    attended.FullId,
                    1.5f,
                    RelativeDirection.Front,
                    RelativeDirection.Right),
                isRetained: true),
            Entry(
                sequenceID: 2,
                observedAt: 43d,
                new ObservedVisualDescription(attended.FullId, "A yellow coat."),
                isRetained: true),
            // Mind has expired this finite evidence before status capture, so it remains absent despite attention.
            Entry(
                sequenceID: 3,
                observedAt: 40d,
                new ObservedRelativePosition(
                    expired.FullId,
                    2f,
                    RelativeDirection.Back,
                    RelativeDirection.Left),
                isRetained: false),
        ];
        SceneStatusBuildContext context = new(
            owner,
            new FakeScene([owner, attended, expired]),
            new AttentionSnapshot(50d, new Dictionary<string, float>(StringComparer.Ordinal)
            {
                [attended.FullId] = 0.5f,
                [expired.FullId] = 0.4f,
                ["char:not_in_fresh_scene"] = 0.8f,
            }),
            retainedLog,
            [],
            timestamp: 50d);
        AttendedCharacterSceneStatusProjector projector = new();

        try
        {
            AttendedCharactersSceneStatus projection = Assert.IsType<AttendedCharactersSceneStatus>(projector.Project(context));

            Assert.Equal([attended.FullId, expired.FullId], projection.AttendedCharacters.Select(character => character.FullId));
            AttendedCharacterSceneStatus attendedStatus = projection.AttendedCharacters[0];
            Assert.Equal("A yellow coat.", attendedStatus.VisualDescription);
            Assert.Equal(43d, attendedStatus.VisualDescriptionObservedAt);
            Assert.Equal(7d, attendedStatus.VisualDescriptionAgeSeconds);
            AttendedRelativePositionSceneStatus position = Assert.IsType<AttendedRelativePositionSceneStatus>(attendedStatus.RelativePosition);
            Assert.Equal(1.5f, position.Distance);
            Assert.Equal(42d, attendedStatus.RelativePositionObservedAt);
            Assert.Equal(8d, attendedStatus.RelativePositionAgeSeconds);

            AttendedCharacterSceneStatus expiredStatus = projection.AttendedCharacters[1];
            Assert.Null(expiredStatus.RelativePosition);
            Assert.Null(expiredStatus.RelativePositionObservedAt);
            Assert.Null(expiredStatus.VisualDescription);
        }
        finally
        {
            projector.Free();
        }
    }

    private static PromptStack CreateStack(string projectorID, Type rootType)
        => new()
        {
            Sections =
            [
                new ProjectionPromptSection
                {
                    Name = "Typed Status",
                    ProjectorID = projectorID,
                    RootTypeName = rootType.AssemblyQualifiedName!,
                    TemplateSource = "{{ Value }}",
                },
            ],
        };

    private static SceneStatusBuildContext CreateStatusContext(ICharacter owner, double timestamp)
        => CreateStatusContext(owner, new FakeScene([owner]), timestamp);

    private static SceneStatusBuildContext CreateStatusContext(ICharacter owner, ISceneContext scene, double timestamp)
        => new(
            owner,
            scene,
            new AttentionSnapshot(timestamp, new Dictionary<string, float>(StringComparer.Ordinal)),
            [],
            [],
            timestamp);

    private static AcceptedObservationEntry Entry(
        long sequenceID,
        double observedAt,
        Observation observation,
        bool isRetained)
        => new(
            sequenceID,
            observedAt,
            observation with
            {
                ObservedAt = observedAt,
            },
            new ObservationSchedulingMetadata(0.1f, RequiresFreshTurn: false),
            isRetained);

    private sealed partial class TestProjector : SceneStatusProjector
    {
        public int ProjectCount
        {
            get; private set;
        }

        public override Type ProjectionType => typeof(AttendedCharactersSceneStatus);

        public override ISceneStatusProjection Project(SceneStatusBuildContext context)
        {
            ProjectCount++;
            return new AttendedCharactersSceneStatus([], context.Timestamp);
        }
    }

    private sealed partial class NullProjectionProjector : SceneStatusProjector
    {
        public override Type ProjectionType => typeof(AttendedCharactersSceneStatus);

        public override ISceneStatusProjection Project(SceneStatusBuildContext context)
        {
            _ = context;
            return null!;
        }
    }

    private sealed partial class TestAgenticMind(ICharacter owner) : AgenticMind
    {
        protected override ICharacter ResolveOwningCharacter() => owner;
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

    private sealed class RecordingCompiler : ITemplateCompiler
    {
        public int CompileCount
        {
            get; private set;
        }

        public RootOnlyTemplate Template { get; } = new();

        public ITemplate Compile(string source)
        {
            Assert.Equal("{{ Value }}", source);
            CompileCount++;
            return Template;
        }
    }

    private sealed class RootOnlyTemplate : IRootedTemplate
    {
        public int DictionaryRenderCount
        {
            get; private set;
        }

        public int RootRenderCount
        {
            get; private set;
        }

        public ValueTask<string> RenderAsync(IReadOnlyDictionary<string, object?> context)
        {
            _ = context;
            DictionaryRenderCount++;
            throw new Xunit.Sdk.XunitException("Projection sections must render their typed root, never a dictionary.");
        }

        public ValueTask<string> RenderRootedAsync(object root, IReadOnlyDictionary<string, object?> namedValues)
        {
            Assert.Empty(namedValues);
            AttendedCharactersSceneStatus projection = Assert.IsType<AttendedCharactersSceneStatus>(root);
            RootRenderCount++;
            return ValueTask.FromResult($"root:{projection.Timestamp:0}");
        }
    }

    private sealed class FakeCharacter(string id) : ICharacter
    {
        public string Id { get; set; } = id;

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; } = Transform3D.Identity;
    }

    private sealed class FakeScene(IReadOnlyCollection<ICharacter> characters) : ISceneContext
    {
        public IReadOnlyCollection<ICharacter> Characters => characters;

        public ICharacter Player => Characters.First();

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId) => Characters.SingleOrDefault(character => string.Equals(character.FullId, fullId, StringComparison.Ordinal));

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException($"Missing '{fullId}'.");
    }
}
