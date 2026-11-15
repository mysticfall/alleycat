using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Godot-runtime coverage for the visible-cue description lines presented beside a captured view (playtest R1's
/// multi-cue work): the lines come from the vision layer's own most recent visual survey — never a new geometric
/// test — render owner-anchored through each cue's authored description, and compose nothing when the vision has
/// no survey knowledge or no description renders.
/// </summary>
[Headless]
public sealed class VisibleCueDescriptionsIntegrationTests
{
    /// <summary>After one survey, every visible cue of every visible subject renders its authored description.</summary>
    [Fact]
    public async Task ComposeLines_AfterASurvey_RendersEveryVisibleCuesAuthoredDescription()
    {
        await using SurveyFixture fixture = new();
        SurveyCharacter observer = fixture.AddObserver();
        TestVisualSubjectCharacter subject = fixture.AddSubject(
            "subject",
            new Vector3(0f, 0f, -3f),
            cueDescription: "She looks to be a plain-looking woman in her early 20s.");
        _ = subject;

        await fixture.WaitForSurveyAsync();

        IReadOnlyList<string> lines = await VisibleCueDescriptions.ComposeLinesAsync(
            observer,
            fixture.Scene);

        Assert.Equal(
        [
            "`char:subject`'s face: She looks to be a plain-looking woman in her early 20s.",
        ],
        lines);
    }

    /// <summary>Multiple subjects order by full ID, and each subject's cues keep their authored order.</summary>
    [Fact]
    public async Task ComposeLines_WithMultipleSubjects_OrdersByFullIdThenCueOrder()
    {
        await using SurveyFixture fixture = new();
        SurveyCharacter observer = fixture.AddObserver();
        _ = fixture.AddSubject(
            "zulu",
            new Vector3(-1f, 0f, -3f),
            cueDescription: "Zulu's face.");
        _ = fixture.AddSubject(
            "alfa",
            new Vector3(1f, 0f, -3f),
            cueDescription: "Alfa's face.");

        await fixture.WaitForSurveyAsync();

        IReadOnlyList<string> lines = await VisibleCueDescriptions.ComposeLinesAsync(
            observer,
            fixture.Scene);

        Assert.Equal(["`char:alfa`'s face: Alfa's face.", "`char:zulu`'s face: Zulu's face."], lines);
    }

    /// <summary>Vision without survey knowledge composes nothing — honest omission, never fabricated detail.</summary>
    [Fact]
    public async Task ComposeLines_WithoutSurveyKnowledge_ComposesNothing()
    {
        await using SurveyFixture fixture = new();
        SurveyCharacter observer = fixture.AddObserver(surveyIntervalSeconds: 3600d);
        _ = fixture.AddSubject("subject", new Vector3(0f, 0f, -3f), cueDescription: "Unsurveyed.");

        await TestUtils.WaitForPhysicsFramesAsync(TestUtils.GetSceneTree(), 2);

        IReadOnlyList<string> lines = await VisibleCueDescriptions.ComposeLinesAsync(
            observer,
            fixture.Scene);

        Assert.Empty(lines);
    }

    /// <summary>A blank authored description is skipped while other cues still render.</summary>
    [Fact]
    public async Task ComposeLines_WithABlankDescription_SkipsThatCueHonestly()
    {
        await using SurveyFixture fixture = new();
        SurveyCharacter observer = fixture.AddObserver();
        _ = fixture.AddSubject("subject", new Vector3(0f, 0f, -3f), cueDescription: "  ");

        await fixture.WaitForSurveyAsync();

        IReadOnlyList<string> lines = await VisibleCueDescriptions.ComposeLinesAsync(
            observer,
            fixture.Scene);

        Assert.Empty(lines);
    }

    /// <summary>The delivered-message append presents the lines as one labelled list block (playtest R4).</summary>
    [Fact]
    public async Task AppendToMessage_PresentsTheLinesAsOneLabelledListBlock()
    {
        await using SurveyFixture fixture = new();
        SurveyCharacter observer = fixture.AddObserver();
        _ = fixture.AddSubject("subject", new Vector3(0f, 0f, -3f), cueDescription: "A weathered face.");

        await fixture.WaitForSurveyAsync();

        string message = await VisibleCueDescriptions.AppendToMessageAsync(
            "View delivered: view capture.",
            observer,
            fixture.Scene);

        Assert.Equal(
            "View delivered: view capture."
            + "\nVisible details:"
            + "\n- `char:subject`'s face: A weathered face.",
            message);
    }

    /// <summary>
    /// Minimal in-tree survey fixture: a node-backed observer character hosting a real <see cref="EyesBehaviour" />
    /// with a fast survey interval, plus node-backed subject characters with authored face cues.
    /// </summary>
    private sealed class SurveyFixture : IAsyncDisposable
    {
        private readonly Node3D _root = new() { Name = $"VisibleCueDescriptions-{Guid.NewGuid():N}" };
        private readonly List<ICharacter> _characters = [];
        private readonly EyesBehaviour _eyes;

        public SurveyFixture()
        {
            _eyes = new EyesBehaviour
            {
                VisualSurveyIntervalSeconds = 0.05d,
                SaccadeAmplitude = 0f,
            };
            SceneTree sceneTree = TestUtils.GetSceneTree();
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(_root);
        }

        public FakeSurveySceneContext Scene { get; private set; } = new([]);

        public SurveyCharacter AddObserver(double surveyIntervalSeconds = 0.05d)
        {
            _eyes.VisualSurveyIntervalSeconds = surveyIntervalSeconds;
            var origin = new Marker3D { Name = "EyeOrigin" };
            SurveyCharacter observer = new([_eyes])
            {
                Name = "Observer",
            };
            observer.AddChild(origin);
            observer.AddChild(_eyes);
            _eyes.EyeOrigin = origin;
            _root.AddChild(observer);
            _characters.Add(observer);
            Scene = new FakeSurveySceneContext([.. _characters]);
            return observer;
        }

        public TestVisualSubjectCharacter AddSubject(string id, Vector3 position, string cueDescription)
        {
            TestVisualSubjectCharacter subject = new(id, cueDescription)
            {
                Name = $"Subject-{id}",
                Position = position,
            };
            _root.AddChild(subject);
            subject.AddToGroup("VisualSubjects");
            _characters.Add(subject);
            Scene = new FakeSurveySceneContext([.. _characters]);
            return subject;
        }

        /// <summary>Waits until the eyes' survey snapshot exists, so the composition reads settled knowledge.</summary>
        public async Task WaitForSurveyAsync()
        {
            SceneTree sceneTree = TestUtils.GetSceneTree();
            for (int frame = 0; frame < 200 && (_eyes as IVisibleCueSurveySource).LastVisibleCueSurvey is null; frame++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(sceneTree, 2);
            }

            Assert.NotNull((_eyes as IVisibleCueSurveySource).LastVisibleCueSurvey);
        }

        public async ValueTask DisposeAsync()
        {
            _root.QueueFree();
            SceneTree sceneTree = TestUtils.GetSceneTree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    private sealed record FakeSurveySceneContext(IReadOnlyCollection<ICharacter> Characters) : ISceneContext
    {
        public ICharacter Player => throw new InvalidOperationException(
            "Scene context contains no player character. Scene authoring guarantees the player is present.");

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId)
            => Characters.FirstOrDefault(character => string.Equals(character.FullId, fullId, StringComparison.Ordinal));

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException($"No test scene entry exists for '{fullId}'.");
    }

    /// <summary>Node-backed observer character whose vision component is its real eyes behaviour.</summary>
    private sealed partial class SurveyCharacter(IReadOnlyList<IComponent> components) : Node3D, ICharacter
    {
        public string Id { get; set; } = "observer";

        public string Type => "char";

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = components;

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];
    }

    /// <summary>Node-backed subject character owning one authored face cue.</summary>
    private sealed partial class TestVisualSubjectCharacter(string id, string cueDescription) : Node3D, ICharacter
    {
        private readonly StaticVisualCue _cue = new()
        {
            Name = "FaceVisualCue",
            ID = "face",
            Prominence = 1f,
            Description = cueDescription,
        };

        public string Id { get; set; } = id;

        public string Type => "char";

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues => [_cue];

        public override void _EnterTree() => AddChild(_cue);
    }

}
