using AlleyCat.Character;
using AlleyCat.Core;
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
/// Godot-runtime coverage for the subject's own head-direction line of the vision-capture presentation (playtest
/// R5): the line states the direction of the subject's active-gaze head turn relative to their body facing, in
/// second person, per bearing bucket; an untilled head — no look target, or one within the front cone — composes
/// nothing, and no line ever names or describes another character.
/// </summary>
[Headless]
public sealed class VisualContextGazeIntegrationTests
{
    /// <summary>A look target to the subject's right turns the head right.</summary>
    [Fact]
    public async Task HeadTurnToTheRight_ComposesTheSecondPersonRightLine()
    {
        await using GazeFixture fixture = new();
        GazeSubjectCharacter subject = fixture.AddSubject("x");
        VisualCue cue = fixture.AddStandaloneCue("window", new Vector3(2f, 1.5f, 0f));
        subject.Vision.LookTarget = cue;

        Assert.Equal(
            ["Your head is turned to your right."],
            VisualContextGaze.ComposeSubjectHeadDirectionLines(subject));
        Assert.Equal(
            "View delivered: view capture. Your head is turned to your right.",
            VisualContextGaze.AppendSubjectHeadDirection("View delivered: view capture.", subject));
    }

    /// <summary>A look target to the subject's left turns the head left.</summary>
    [Fact]
    public async Task HeadTurnToTheLeft_ComposesTheSecondPersonLeftLine()
    {
        await using GazeFixture fixture = new();
        GazeSubjectCharacter subject = fixture.AddSubject("x");
        VisualCue cue = fixture.AddStandaloneCue("window", new Vector3(-2f, 1.5f, 0f));
        subject.Vision.LookTarget = cue;

        Assert.Equal(
            ["Your head is turned to your left."],
            VisualContextGaze.ComposeSubjectHeadDirectionLines(subject));
    }

    /// <summary>A look target behind the subject composes the over-shoulder line, sided by the turn direction.</summary>
    [Fact]
    public async Task HeadTurnBehind_ComposesTheSidedOverShoulderLines()
    {
        await using GazeFixture fixture = new();
        GazeSubjectCharacter rightShoulder = fixture.AddSubject("r");
        VisualCue rightCue = fixture.AddStandaloneCue("door", new Vector3(1f, 1.5f, 1.2f));
        rightShoulder.Vision.LookTarget = rightCue;

        GazeSubjectCharacter leftShoulder = fixture.AddSubject("l");
        VisualCue leftCue = fixture.AddStandaloneCue("poster", new Vector3(-1f, 1.5f, 1.2f));
        leftShoulder.Vision.LookTarget = leftCue;

        Assert.Equal(
            ["Your head is turned back over your right shoulder."],
            VisualContextGaze.ComposeSubjectHeadDirectionLines(rightShoulder));
        Assert.Equal(
            ["Your head is turned back over your left shoulder."],
            VisualContextGaze.ComposeSubjectHeadDirectionLines(leftShoulder));
    }

    /// <summary>A look target within the front cone aligns head and body facing: no line, honestly.</summary>
    [Fact]
    public async Task HeadTurnWithinTheFrontCone_ComposesNothing()
    {
        await using GazeFixture fixture = new();
        GazeSubjectCharacter subject = fixture.AddSubject("x");
        VisualCue cue = fixture.AddStandaloneCue("poster", new Vector3(0.5f, 1.5f, -3f));
        subject.Vision.LookTarget = cue;

        Assert.Empty(VisualContextGaze.ComposeSubjectHeadDirectionLines(subject));
        Assert.Equal(
            "View delivered: view capture.",
            VisualContextGaze.AppendSubjectHeadDirection("View delivered: view capture.", subject));
    }

    /// <summary>No look target means no head turn: no line, and the delivered message is unchanged.</summary>
    [Fact]
    public async Task WithoutALookTarget_ComposesNoLineAndReturnsTheMessageUnchanged()
    {
        await using GazeFixture fixture = new();
        GazeSubjectCharacter subject = fixture.AddSubject("x");

        Assert.Empty(VisualContextGaze.ComposeSubjectHeadDirectionLines(subject));
        Assert.Equal(
            "View delivered: view capture.",
            VisualContextGaze.AppendSubjectHeadDirection("View delivered: view capture.", subject));
    }

    /// <summary>
    /// Minimal in-tree fixture: node-backed subject characters with controllable vision look targets and authored
    /// standalone cues positioned in world space.
    /// </summary>
    private sealed class GazeFixture : IAsyncDisposable
    {
        private readonly Node3D _root = new() { Name = $"VisualContextGaze-{Guid.NewGuid():N}" };

        public GazeFixture()
        {
            SceneTree sceneTree = TestUtils.GetSceneTree();
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(_root);
        }

        public GazeSubjectCharacter AddSubject(string id)
        {
            var vision = new FakeVision();
            var character = new GazeSubjectCharacter(id, vision)
            {
                Name = $"Subject-{id}",
            };
            _root.AddChild(character);
            return character;
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
