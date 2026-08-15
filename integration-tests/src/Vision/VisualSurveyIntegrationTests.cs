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

            eyes._Process(1d);

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
