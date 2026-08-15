using AlleyCat.IntegrationTests.Support;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Vision;

/// <summary>Regression coverage for survey publication after public visual scanning was removed.</summary>
[Headless]
public sealed class VisualSurveyIntegrationTests
{
    /// <inheritdoc/>
    [Fact]
    public async Task Survey_PreservesVisibilityOcclusionAndSelfExclusionWithoutChangingEyePresentation()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new Node3D();
        var observer = new TestSubject("observer");
        var origin = new Marker3D();
        var eyes = new EyesBehaviour { EyeOrigin = origin, VisualSurveyIntervalSeconds = 0.05d, SaccadeAmplitude = 0f };
        observer.AddChild(origin);
        observer.AddChild(eyes);
        TestSubject visible = CreateSubject("visible", new Vector3(0f, 0f, -3f));
        TestSubject occluded = CreateSubject("occluded", new Vector3(2f, 0f, -3f));
        root.AddChild(observer);
        root.AddChild(visible);
        root.AddChild(occluded);
        root.AddChild(CreateOccluder(new Vector3(2f, 0f, -1.5f)));
        AddToTree(tree, root);
        await TestUtils.WaitForPhysicsFramesAsync(tree, 2);

        try
        {
            observer.AddToGroup("VisualSubjects");
            visible.AddToGroup("VisualSubjects");
            occluded.AddToGroup("VisualSubjects");
            Node3D? lookTargetBefore = eyes.LookTarget;
            float horizontalBefore = eyes.GetHorizontalLookSeekTime();
            float verticalBefore = eyes.GetVerticalLookSeekTime();
            List<VisualSurveyPercept> surveys = [];
            eyes.Perceived += percept => surveys.Add(Assert.IsType<VisualSurveyPercept>(percept));

            await VisualSurveyTestTrigger.TriggerPhysicsSurveyOnceAsync(tree, eyes);

            Assert.Equal(["test:visible"], Assert.Single(surveys).SubjectFullIDs);
            Assert.Same(lookTargetBefore, eyes.LookTarget);
            Assert.Equal(horizontalBefore, eyes.GetHorizontalLookSeekTime());
            Assert.Equal(verticalBefore, eyes.GetVerticalLookSeekTime());
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>Builds a subject fixture carrying a single visible cue.</summary>
    private static TestSubject CreateSubject(string id, Vector3 position)
    {
        var subject = new TestSubject(id) { Position = position };
        var cue = new StaticVisualCue { Prominence = 1f };
        subject.AddChild(cue);
        subject.VisualCues = [cue];
        return subject;
    }

    private static StaticBody3D CreateOccluder(Vector3 position)
    {
        var body = new StaticBody3D { Position = position, CollisionLayer = 1u << 4 };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.5f, 2f, 1.5f) } });
        return body;
    }

    private static void AddToTree(SceneTree tree, Node node) => (tree.CurrentScene ?? tree.Root).AddChild(node);

    private sealed partial class TestSubject(string id) : Node3D, IVisualSubject
    {
        public string Id { get; set; } = id;
        public string Type => "test";
        public IReadOnlyList<VisualCue> VisualCues { get; set; } = [];
    }
}

/// <summary>Live runtime coverage for visual surveys in the production Mirror Room route.</summary>
public sealed class MirrorRoomVisualSurveyRuntimeIntegrationTests
{
    private const string MirrorRoomScenePath = "res://assets/testing/mirror_room/mirror_room.tscn";

    /// <summary>Verifies installed characters publish visible subjects after their physics world has settled.</summary>
    [Fact]
    public async Task AfterNaturalInstallAndPhysicsSettling_PublishesValidSurveys()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        Node3D mirrorRoom = TestUtils.LoadPackedScene(MirrorRoomScenePath).Instantiate<Node3D>();
        AddToTree(tree, mirrorRoom);

        try
        {
            await TestUtils.WaitForFramesAsync(tree, 8);
            await TestUtils.WaitForPhysicsFramesAsync(tree, 4);

            EyesBehaviour playerEyes = mirrorRoom.GetNode<EyesBehaviour>("Actors/Player/Eyes");
            EyesBehaviour vadimEyes = mirrorRoom.GetNode<EyesBehaviour>("Actors/Vadim/Eyes");
            playerEyes.HorizontalSensingHalfAngleDegrees = 180f;
            playerEyes.VerticalSensingHalfAngleDegrees = 90f;
            vadimEyes.HorizontalSensingHalfAngleDegrees = 180f;
            vadimEyes.VerticalSensingHalfAngleDegrees = 90f;
            var playerSurveys = new List<VisualSurveyPercept>();
            var vadimSurveys = new List<VisualSurveyPercept>();
            playerEyes.Perceived += percept => playerSurveys.Add(Assert.IsType<VisualSurveyPercept>(percept));
            vadimEyes.Perceived += percept => vadimSurveys.Add(Assert.IsType<VisualSurveyPercept>(percept));

            await VisualSurveyTestTrigger.TriggerPhysicsSurveyOnceAsync(tree, playerEyes, vadimEyes);

            Assert.Equal(["char:vadim"], Assert.Single(playerSurveys).SubjectFullIDs);
            Assert.Equal(["char:ally"], Assert.Single(vadimSurveys).SubjectFullIDs);
        }
        finally
        {
            mirrorRoom.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    private static void AddToTree(SceneTree tree, Node node) => (tree.CurrentScene ?? tree.Root).AddChild(node);
}

/// <summary>
/// Shared trigger for visual-survey integration tests: runs one physics frame and invokes
/// <see cref="EyesBehaviour._PhysicsProcess"/> from within it, so the survey resolves a direct space state during the
/// physics step rather than from a process frame.
/// </summary>
file sealed class VisualSurveyTestTrigger
{
    public static async Task TriggerPhysicsSurveyOnceAsync(SceneTree tree, params EyesBehaviour[] eyes)
    {
        var completed = new TaskCompletionSource();
        void OnPhysicsFrame()
        {
            tree.PhysicsFrame -= OnPhysicsFrame;
            foreach (EyesBehaviour eye in eyes)
            {
                eye._PhysicsProcess(1d);
            }

            completed.SetResult();
        }

        tree.PhysicsFrame += OnPhysicsFrame;
        await completed.Task;
    }
}
