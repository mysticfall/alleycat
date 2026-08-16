using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Mind.Attention;
using AlleyCat.Scene;
using AlleyCat.Sense;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.Attention;

/// <summary>Focused runtime coverage for Mind-owned attention-to-gaze selector composition and adaptation.</summary>
[Headless]
public sealed class AttentionGazeTargetSelectorIntegrationTests
{
    /// <summary>Binding waits for projection, refreshes Vision safely, and never makes the selector a character component.</summary>
    [Fact]
    public void Lifecycle_WaitsForProjectionRefreshesVisionAndKeepsMindChildOutOfComponents()
    {
        TestVision firstVision = new();
        TestCharacter character = new(hasComponentProjection: false, firstVision);
        TestMind mind = new(character);
        MutableSceneContext scene = new();
        TestVisualCue cue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject subject = new("target", [cue]);
        scene.Add(subject);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene);
        try
        {
            mind.ReinforceAttention(subject.FullId, 1f);
            selector._Ready();
            selector._Process(0d);

            Assert.Empty(firstVision.SetTargets);
            Assert.Equal(1, character.ComponentsRefreshedHandlerCount);
            Assert.Same(mind, selector.GetParent());
            Assert.False(selector is IComponent);
            Assert.DoesNotContain(character.Components, component => ReferenceEquals(selector, component));

            TestVision secondVision = new();
            character.RefreshComponents(secondVision);
            selector._Process(0d);

            Assert.Empty(firstVision.SetTargets);
            Assert.Equal([cue], secondVision.SetTargets);
            Assert.Equal(0, secondVision.ClearCount);
            selector._ExitTree();
            Assert.Equal(0, secondVision.ClearCount);

            Assert.Equal(0, character.ComponentsRefreshedHandlerCount);
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            firstVision.Dispose();
            cue.Free();
        }
    }

    /// <summary>Current snapshot identities resolve only through scene context and assign valid published cue references.</summary>
    [Fact]
    public void Evaluate_ResolvesVisualCuesFromSnapshotAndRebindsOnProjectionRefresh()
    {
        TestVision firstVision = new();
        TestVision secondVision = new();
        TestCharacter character = new(hasComponentProjection: true, firstVision);
        TestMind mind = new(character);
        MutableSceneContext scene = new();
        TestVisualCue cue = new()
        {
            ID = "body",
            Prominence = 2f
        };
        TestVisualSubject subject = new("target", [cue]);
        scene.Add(subject);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene);
        try
        {
            mind.ReinforceAttention(subject.FullId, 1f);
            selector._Ready();
            selector._Process(0d);

            Assert.Equal([cue], firstVision.SetTargets);
            Assert.Equal([subject.FullId], scene.FindRequests);

            character.RefreshComponents(secondVision);
            selector._Process(0d);

            Assert.Equal([cue], secondVision.SetTargets);
            Assert.Equal(0, secondVision.ClearCount);
            Assert.Equal(0, firstVision.ClearCount);
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            cue.Free();
        }
    }

    /// <summary>Periodic evaluation does not catch up and applies changed assignments only on normal evaluation boundaries.</summary>
    [Fact]
    public void Cadence_EvaluatesWithoutCatchUpAndChangesTargetAtBoundary()
    {
        TestVision vision = new();
        TestCharacter character = new(hasComponentProjection: true, vision);
        TestMind mind = new(character);
        MutableSceneContext scene = new();
        TestVisualCue alphaCue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualCue bravoCue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject alpha = new("alpha", [alphaCue]);
        TestVisualSubject bravo = new("bravo", [bravoCue]);
        scene.Add(alpha);
        scene.Add(bravo);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene);
        selector.EvaluationIntervalSeconds = 1f;
        selector.PrimaryDwellSeconds = 1f;
        selector.SecondaryDwellSeconds = 0.5f;
        try
        {
            mind.ReinforceAttention(alpha.FullId, 0.5f);
            mind.ReinforceAttention(bravo.FullId, 0.2f);
            selector._Ready();
            selector._Process(0d);

            Assert.Equal([alphaCue], vision.SetTargets);

            mind.ReinforceAttention(bravo.FullId, 1f);
            selector._Process(0.9d);
            Assert.Equal([alphaCue], vision.SetTargets);

            selector._Process(0.1d);
            Assert.Equal([alphaCue, bravoCue], vision.SetTargets);

            selector._Process(100d);
            Assert.Equal([alphaCue, bravoCue], vision.SetTargets);
            Assert.Equal(0, vision.ClearCount);
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            alphaCue.Free();
            bravoCue.Free();
        }
    }

    /// <summary>Unresolved, nonvisual, disabled, and malformed cue paths are skipped and no candidates restores fallback.</summary>
    [Fact]
    public void Evaluate_SkipsInvalidResolutionPathsAndClearsWhenNoCandidatesRemain()
    {
        TestVision vision = new();
        TestCharacter character = new(hasComponentProjection: true, vision);
        TestMind mind = new(character);
        MutableSceneContext scene = new();
        TestVisualCue disabledCue = new()
        {
            ID = "disabled",
            Prominence = 0f
        };
        TestVisualCue malformedCue = new()
        {
            ID = "malformed",
            Prominence = float.NaN
        };
        TestVisualCue validCue = new()
        {
            ID = "valid",
            Prominence = 1f
        };
        TestVisualSubject invalidSubject = new("invalid", [disabledCue, malformedCue]);
        TestVisualSubject validSubject = new("valid", [validCue]);
        scene.Add(new NonVisualIdentifiable("nonvisual"));
        scene.Add(invalidSubject);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene);
        try
        {
            mind.ReinforceAttention("char:missing", 1f);
            mind.ReinforceAttention("char:nonvisual", 1f);
            mind.ReinforceAttention(invalidSubject.FullId, 1f);
            selector._Ready();
            selector._Process(0d);

            Assert.Empty(vision.SetTargets);
            Assert.Equal(1, vision.ClearCount);

            scene.Add(validSubject);
            mind.ReinforceAttention(validSubject.FullId, 1f);
            selector.RequestEvaluation();
            selector._Process(0d);
            Assert.Equal([validCue], vision.SetTargets);

            scene.Remove(validSubject.FullId);
            selector.RequestEvaluation();
            selector._Process(0d);
            Assert.Equal(2, vision.ClearCount);
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            disabledCue.Free();
            malformedCue.Free();
            validCue.Free();
        }
    }

    /// <summary>Cadence, dwell, and probability authoring is rejected before the selector can bind runtime dependencies.</summary>
    [Fact]
    public void Authoring_RejectsInvalidCadenceDwellAndProbabilityBeforeActivation()
    {
        AssertInvalidAuthoring(selector => selector.EvaluationIntervalSeconds = float.NaN);
        AssertInvalidAuthoring(selector => selector.PrimaryDwellSeconds = 0f);
        AssertInvalidAuthoring(selector => selector.SecondaryDwellSeconds = 2f);
        AssertInvalidAuthoring(selector => selector.SecondaryGlanceProbability = 1.1f);
    }

    private static AttentionGazeTargetSelector CreateSelector(TestMind mind, ISceneContext scene)
    {
        AttentionGazeTargetSelector selector = new()
        {
            Name = "AttentionGazeTargetSelector",
            EvaluationIntervalSeconds = 0.5f,
            PrimaryDwellSeconds = 2f,
            SecondaryDwellSeconds = 0.5f,
            SecondaryGlanceProbability = 0f,
        };
        selector.SetSceneContextLoaderForTesting(() => scene);
        selector.SetRandomForTesting(new FixedRandom());
        mind.AddChild(selector);
        return selector;
    }

    private static void AssertInvalidAuthoring(Action<AttentionGazeTargetSelector> configure)
    {
        TestMind mind = new(new TestCharacter(hasComponentProjection: false));
        AttentionGazeTargetSelector selector = new()
        {
            EvaluationIntervalSeconds = 0.5f,
            PrimaryDwellSeconds = 2f,
            SecondaryDwellSeconds = 0.5f,
            SecondaryGlanceProbability = 0f,
        };
        mind.AddChild(selector);
        configure(selector);
        try
        {
            _ = Assert.Throws<InvalidOperationException>(selector._Ready);
        }
        finally
        {
            selector.Free();
            mind.Free();
        }
    }

    private sealed partial class TestMind(ICharacter owner) : MindBase
    {
        protected override ICharacter ResolveOwningCharacter() => owner;

        protected override Task ProcessObservationsAsync(
            IReadOnlyList<AlleyCat.Mind.Observation.Observation> observations,
            IReadOnlyList<AlleyCat.Mind.Observation.Observation> timelineSnapshot,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void ReinforceAttention(string fullId, float contribution)
            => ReinforceAttention(
                fullId,
                contribution,
                AttentionSettings.Create(maximum: 1f, decayPerSecond: 0f, retentionThreshold: 0f, contextThreshold: 0f));
    }

    private sealed class TestCharacter(bool hasComponentProjection, params IComponent[] components) : ICharacter, IComponentProjectionNotifier
    {
        private IComponent[] _components = components;
        private Action? _componentsRefreshed;

        public string Id { get; set; } = "owner";

        public IReadOnlyList<IComponent> Components => _components;

        public bool HasComponentProjection { get; private set; } = hasComponentProjection;

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public int ComponentsRefreshedHandlerCount
        {
            get; private set;
        }

        public event Action? ComponentsRefreshed
        {
            add
            {
                _componentsRefreshed += value;
                ComponentsRefreshedHandlerCount++;
            }
            remove
            {
                _componentsRefreshed -= value;
                ComponentsRefreshedHandlerCount--;
            }
        }

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();

        public void RefreshComponents(params IComponent[] components)
        {
            _components = components;
            HasComponentProjection = true;
            _componentsRefreshed?.Invoke();
        }
    }

    private sealed class MutableSceneContext : ISceneContext
    {
        private readonly Dictionary<string, IIdentifiable> _entries = new(StringComparer.Ordinal);

        public IReadOnlyCollection<ICharacter> Characters => [];

        public ContentContext Content => ContentContext.Default;

        public List<string> FindRequests { get; } = [];

        public void Add(IIdentifiable identifiable) => _entries.Add(identifiable.FullId, identifiable);

        public void Remove(string fullId) => _ = _entries.Remove(fullId);

        public IIdentifiable? Find(string fullId)
        {
            FindRequests.Add(fullId);
            return _entries.GetValueOrDefault(fullId);
        }

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException($"No test scene entry exists for '{fullId}'.");
    }

    private sealed class TestVisualSubject(string id, IReadOnlyList<VisualCue> visualCues) : IVisualSubject
    {
        public string Id { get; set; } = id;

        public string Type => "char";

        public string FullId => $"{Type}:{Id}";

        public IReadOnlyList<VisualCue> VisualCues { get; } = visualCues;
    }

    private sealed class NonVisualIdentifiable(string id) : IIdentifiable
    {
        public string Id { get; set; } = id;

        public string Type => "char";

        public string FullId => $"{Type}:{Id}";
    }

    private sealed partial class TestVisualCue : VisualCue
    {
        public override string Describe(ISceneContext scene, IHasVision observer) => string.Empty;
    }

    private sealed class TestVision : IVision, IDisposable
    {
        public Node3D? LookTarget
        {
            get; set;
        }

        public event Action<IPercept>? Perceived
        {
            add
            {
            }
            remove
            {
            }
        }

        public IReadOnlyList<Type> PerceptTypes { get; } = [];

        public List<VisualCue> SetTargets { get; } = [];

        public int ClearCount
        {
            get; private set;
        }

        public void SetLookTarget(Node3D? target)
        {
            LookTarget = target;
            SetTargets.Add(Assert.IsAssignableFrom<VisualCue>(target));
        }

        public void ClearLookTarget()
        {
            LookTarget = null;
            ClearCount++;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FixedRandom : IAttentionGazeRandom
    {
        public double NextUnitInterval() => 0d;
    }
}
