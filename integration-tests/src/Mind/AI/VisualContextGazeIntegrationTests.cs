using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Scene;
using AlleyCat.Sense;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Godot-runtime coverage for the observed-character look-direction descriptor of the AI-013 visual-context
/// presentation (playtest R1): a character-owned look target composes a looking-toward line, a non-character
/// target composes a directional line against the subject's body facing, no readable target composes nothing, and
/// the observer itself is never described.
/// </summary>
[Headless]
public sealed class VisualContextGazeIntegrationTests
{
    /// <summary>A look target owned by a character composes the character-anchored line.</summary>
    [Fact]
    public async Task CharacterOwnedTarget_ComposesTheLookingTowardLine()
    {
        await using GazeFixture fixture = new();
        GazeSubjectCharacter observer = fixture.AddObserver("observer");
        GazeSubjectCharacter subject = fixture.AddSubject("x");
        GazeSubjectCharacter player = fixture.AddSubject("player");
        VisualCue cue = GazeFixture.AddCue(player, "face", new Vector3(0f, 0f, -2f));
        subject.Vision.LookTarget = cue;

        IReadOnlyList<string> lines = VisualContextGaze.ComposeOrientationLines(observer, fixture.Scene);

        Assert.Equal(["`char:x` is looking toward `char:player`."], lines);
        Assert.Equal(
            "Minimap delivered: minimap image. `char:x` is looking toward `char:player`.",
            VisualContextGaze.AppendOrientationLines("Minimap delivered: minimap image.", observer, fixture.Scene));
    }

    /// <summary>A look target owned by no character composes a directional line against the subject's body facing.</summary>
    [Fact]
    public async Task NonCharacterTargetToTheLeft_ComposesTheDirectionalLine()
    {
        await using GazeFixture fixture = new();
        GazeSubjectCharacter observer = fixture.AddObserver("observer");
        GazeSubjectCharacter subject = fixture.AddSubject("x");
        // The subject faces -Z at the origin, so a cue at -X lies to the subject's left.
        VisualCue cue = fixture.AddStandaloneCue("poster", new Vector3(-2f, 1.5f, 0f));
        subject.Vision.LookTarget = cue;

        IReadOnlyList<string> lines = VisualContextGaze.ComposeOrientationLines(observer, fixture.Scene);

        Assert.Equal(["`char:x` is looking to their own left."], lines);
    }

    /// <summary>A non-character target within the front cone composes the honest straight-ahead line.</summary>
    [Fact]
    public async Task NonCharacterTargetAhead_ComposesTheStraightAheadLine()
    {
        await using GazeFixture fixture = new();
        GazeSubjectCharacter observer = fixture.AddObserver("observer");
        GazeSubjectCharacter subject = fixture.AddSubject("x");
        VisualCue cue = fixture.AddStandaloneCue("poster", new Vector3(0.5f, 1.5f, -3f));
        subject.Vision.LookTarget = cue;

        IReadOnlyList<string> lines = VisualContextGaze.ComposeOrientationLines(observer, fixture.Scene);

        Assert.Equal(["`char:x` is looking straight ahead."], lines);
    }

    /// <summary>No look target composes nothing, and the delivered message is returned unchanged.</summary>
    [Fact]
    public async Task WithoutALookTarget_ComposesNoLinesAndReturnsTheMessageUnchanged()
    {
        await using GazeFixture fixture = new();
        GazeSubjectCharacter observer = fixture.AddObserver("observer");
        _ = fixture.AddSubject("x");

        Assert.Empty(VisualContextGaze.ComposeOrientationLines(observer, fixture.Scene));
        Assert.Equal(
            "View delivered: view capture.",
            VisualContextGaze.AppendOrientationLines("View delivered: view capture.", observer, fixture.Scene));
    }

    /// <summary>The observer's own gaze is never described, even while it holds a look target.</summary>
    [Fact]
    public async Task ObserverOwnGaze_IsNeverDescribed()
    {
        await using GazeFixture fixture = new();
        GazeSubjectCharacter observer = fixture.AddObserver("observer");
        GazeSubjectCharacter subject = fixture.AddSubject("x");
        VisualCue cue = GazeFixture.AddCue(subject, "face", new Vector3(0f, 0f, 2f));
        observer.Vision.LookTarget = cue;

        IReadOnlyList<string> lines = VisualContextGaze.ComposeOrientationLines(observer, fixture.Scene);

        Assert.Empty(lines);
    }

    /// <summary>Subjects without a readable look target are skipped while others still compose their line.</summary>
    [Fact]
    public async Task MixedSubjects_ComposeOnlyTheReadableOnesInFullIdOrder()
    {
        await using GazeFixture fixture = new();
        GazeSubjectCharacter observer = fixture.AddObserver("observer");
        GazeSubjectCharacter silent = fixture.AddSubject("a");
        GazeSubjectCharacter bravo = fixture.AddSubject("b");
        GazeSubjectCharacter alpha = fixture.AddSubject("c");
        VisualCue bravoTarget = fixture.AddStandaloneCue("window", new Vector3(2f, 1.5f, 0f));
        bravo.Vision.LookTarget = bravoTarget;
        VisualCue alphaTarget = fixture.AddStandaloneCue("door", new Vector3(0f, 1.5f, 2f));
        alpha.Vision.LookTarget = alphaTarget;
        silent.Vision.LookTarget = null;

        IReadOnlyList<string> lines = VisualContextGaze.ComposeOrientationLines(observer, fixture.Scene);

        Assert.Equal(
        [
            "`char:b` is looking to their own right.",
            "`char:c` is looking back over their own shoulder.",
        ],
        lines);
    }

    /// <summary>
    /// Minimal in-tree fixture: an observer and gaze-subject characters with controllable vision look targets,
    /// plus authored cues that may or may not be owned by a character.
    /// </summary>
    private sealed class GazeFixture : IAsyncDisposable
    {
        private readonly Node3D _root = new() { Name = $"VisualContextGaze-{Guid.NewGuid():N}" };
        private readonly List<ICharacter> _characters = [];

        public GazeFixture()
        {
            SceneTree sceneTree = TestUtils.GetSceneTree();
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(_root);
        }

        public GazeSceneContext Scene { get; private set; } = new([]);

        public GazeSubjectCharacter AddObserver(string id)
        {
            GazeSubjectCharacter observer = AddSubject(id);
            return observer;
        }

        public GazeSubjectCharacter AddSubject(string id)
        {
            var vision = new FakeVision();
            var character = new GazeSubjectCharacter(id, vision)
            {
                Name = $"Subject-{id}",
            };
            _root.AddChild(character);
            _characters.Add(character);
            Scene = new GazeSceneContext([.. _characters]);
            return character;
        }

        /// <summary>Adds a cue owned by a character subject, positioned relative to its owner.</summary>
        public static VisualCue AddCue(GazeSubjectCharacter owner, string cueId, Vector3 position)
        {
            TestVisualCue cue = new()
            {
                Name = $"Cue-{cueId}",
                ID = cueId,
                Prominence = 1f,
                Position = owner.Position + position,
            };
            owner.AddChild(cue);
            return cue;
        }

        /// <summary>Adds a cue owned by no character — a plain prop — positioned in world space.</summary>
        public VisualCue AddStandaloneCue(string cueId, Vector3 position)
        {
            TestVisualCue cue = new()
            {
                Name = $"Cue-{cueId}",
                ID = cueId,
                Prominence = 1f,
                Position = position,
            };
            _root.AddChild(cue);
            return cue;
        }

        public async ValueTask DisposeAsync()
        {
            _root.QueueFree();
            SceneTree sceneTree = TestUtils.GetSceneTree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    private sealed record GazeSceneContext(IReadOnlyCollection<ICharacter> Characters) : ISceneContext
    {
        public ICharacter Player => throw new InvalidOperationException(
            "Scene context contains no player character. Scene authoring guarantees the player is present.");

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId)
            => Characters.FirstOrDefault(character => string.Equals(character.FullId, fullId, StringComparison.Ordinal));

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException($"No test scene entry exists for '{fullId}'.");
    }

    /// <summary>Node-backed character exposing a controllable vision component so it can hold a look target.</summary>
    private sealed partial class GazeSubjectCharacter(string id, FakeVision vision) : Node3D, ICharacter
    {
        public string Id { get; set; } = id;

        public string Type => "char";

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [vision];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public FakeVision Vision => vision;
    }

    private sealed partial class TestVisualCue : VisualCue
    {
        public override ValueTask<string> Describe(ISceneContext scene, IHasVision observer)
            => ValueTask.FromResult(string.Empty);
    }

    /// <summary>Plain controllable vision double whose look target the tests assign directly.</summary>
    private sealed class FakeVision : IVision
    {
        public VisualCue? LookTarget
        {
            get; set;
        }

        public Camera3D? Camera => null;

        public ValueTask<Image> CaptureImage()
            => throw new InvalidOperationException("The fake vision never captures a real image.");

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

        public void SetLookTarget(VisualCue? target) => LookTarget = target;

        public void ClearLookTarget() => LookTarget = null;
    }
}
