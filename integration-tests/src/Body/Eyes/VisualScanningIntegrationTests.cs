using AlleyCat.Body.Eyes;
using AlleyCat.Context;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Body.Eyes;

/// <summary>Godot physics coverage for synchronous visual-cue scanning.</summary>
[Headless]
public sealed class VisualScanningIntegrationTests
{
    private const uint VisionOccluderLayer = 1u << 4;
    private static readonly StringName _visualSubjectsGroupName = new("VisualSubjects");

    /// <summary>Only grouped, non-self subjects with samples inside both sensing half-angles are reported.</summary>
    [Fact]
    public async Task Scan_UsesStrictGroupFiltersSelfAndHonoursHorizontalAndVerticalSensingBounds()
    {
        Node3D root = new();
        TestVisualSubject self = CreateSubject(Vector3.Zero, CreateCue());
        EyesBehaviour eyes = AddEyes(self);
        TestVisualSubject visibleSubject = CreateSubject(new Vector3(0f, 0f, -3f), CreateCue());
        TestVisualSubject ungroupedSubject = CreateSubject(new Vector3(0f, 0f, -2f), CreateCue());
        TestVisualSubject horizontalOutsideSubject = CreateSubject(new Vector3(3f, 0f, -3f), CreateCue());
        TestVisualSubject verticalOutsideSubject = CreateSubject(new Vector3(0f, 3f, -3f), CreateCue());
        root.AddChild(self);
        root.AddChild(visibleSubject);
        root.AddChild(ungroupedSubject);
        root.AddChild(horizontalOutsideSubject);
        root.AddChild(verticalOutsideSubject);

        try
        {
            await AddRootAndWaitForPhysicsAsync(root);
            visibleSubject.AddToGroup(_visualSubjectsGroupName);
            horizontalOutsideSubject.AddToGroup(_visualSubjectsGroupName);
            verticalOutsideSubject.AddToGroup(_visualSubjectsGroupName);
            eyes.HorizontalSensingHalfAngleDegrees = 40f;
            eyes.VerticalSensingHalfAngleDegrees = 40f;

            VisualScanResult result = Assert.Single(eyes.Scan());
            Assert.Same(visibleSubject, result.Subject);
        }
        finally
        {
            root.QueueFree();
        }
    }

    /// <summary>Point, sphere, and oriented-box bounds use their representative physics samples.</summary>
    [Fact]
    public async Task Scan_UsesPointSphereAndOrientedBoxRepresentativeSamples()
    {
        Node3D root = new();
        TestVisualSubject pointSubject = CreateSubject(new Vector3(0f, 0f, -3f), CreateCue(new PointVisualBounds()));
        TestVisualSubject sphereSubject = CreateSubject(new Vector3(0f, 0f, -3f), CreateCue(new SphereVisualBounds { Radius = 1f }));
        TestVisualSubject boxSubject = CreateSubject(new Vector3(0f, 0f, -3f), CreateCue(new OrientedBoxVisualBounds { Size = new Vector3(2f, 2f, 0.2f) }));
        EyesBehaviour eyes = AddEyes(root);
        root.AddChild(pointSubject);
        root.AddChild(sphereSubject);
        root.AddChild(boxSubject);
        root.AddChild(CreateOccluder(new Vector3(0f, 0f, -1.5f), new Vector3(0.2f, 0.2f, 0.2f)));

        try
        {
            await AddRootAndWaitForPhysicsAsync(root);
            pointSubject.AddToGroup(_visualSubjectsGroupName);
            sphereSubject.AddToGroup(_visualSubjectsGroupName);
            boxSubject.AddToGroup(_visualSubjectsGroupName);

            IReadOnlyList<VisualScanResult> results = eyes.Scan();
            Assert.DoesNotContain(results, result => ReferenceEquals(result.Subject, pointSubject));
            Assert.Contains(results, result => ReferenceEquals(result.Subject, sphereSubject));
            Assert.Contains(results, result => ReferenceEquals(result.Subject, boxSubject));
        }
        finally
        {
            root.QueueFree();
        }
    }

    /// <summary>Positive cue ranges limit visibility while zero ranges remain unlimited.</summary>
    [Fact]
    public async Task Scan_HonoursFinitePositiveAndUnlimitedCueRanges()
    {
        Node3D root = new();
        EyesBehaviour eyes = AddEyes(root);
        TestVisualSubject limitedSubject = CreateSubject(new Vector3(0f, 0f, -3f), CreateCue(maxVisibleDistance: 2f));
        TestVisualSubject unlimitedSubject = CreateSubject(new Vector3(0f, 0f, -3f), CreateCue(maxVisibleDistance: 0f));
        root.AddChild(limitedSubject);
        root.AddChild(unlimitedSubject);

        try
        {
            await AddRootAndWaitForPhysicsAsync(root);
            limitedSubject.AddToGroup(_visualSubjectsGroupName);
            unlimitedSubject.AddToGroup(_visualSubjectsGroupName);

            VisualScanResult result = Assert.Single(eyes.Scan());
            Assert.Same(unlimitedSubject, result.Subject);
        }
        finally
        {
            root.QueueFree();
        }
    }

    /// <summary>A non-subject VisualSubjects group member fails the scan at the strict discovery boundary.</summary>
    [Fact]
    public async Task Scan_RejectsInvalidVisualSubjectsGroupMember()
    {
        Node3D root = new();
        EyesBehaviour eyes = AddEyes(root);
        Node3D invalidMember = new()
        {
            Name = "InvalidVisualSubject"
        };
        root.AddChild(invalidMember);

        try
        {
            await AddRootAndWaitForPhysicsAsync(root);
            invalidMember.AddToGroup(_visualSubjectsGroupName);

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(eyes.Scan);
            Assert.Contains("VisualSubjects", exception.Message);
            Assert.Contains(nameof(IVisualSubject), exception.Message);
        }
        finally
        {
            root.QueueFree();
        }
    }

    /// <summary>Results remain a scan-time snapshot when subject cue membership changes afterwards.</summary>
    [Fact]
    public async Task Scan_ReturnsImmutableScanTimeResults()
    {
        Node3D root = new();
        EyesBehaviour eyes = AddEyes(root);
        StaticVisualCue cue = CreateCue();
        TestVisualSubject subject = CreateSubject(new Vector3(0f, 0f, -3f), cue);
        root.AddChild(subject);

        try
        {
            await AddRootAndWaitForPhysicsAsync(root);
            subject.AddToGroup(_visualSubjectsGroupName);
            VisualScanResult result = Assert.Single(eyes.Scan());

            subject.VisualCues = [];
            subject.RemoveFromGroup(_visualSubjectsGroupName);

            Assert.Same(subject, result.Subject);
            Assert.Same(cue, Assert.Single(result.VisibleCues));
            Assert.Empty(eyes.Scan());
        }
        finally
        {
            root.QueueFree();
        }
    }

    /// <summary>Only VisionOccluder geometry blocks scan rays, and endpoint-supporting colliders pass within tolerance.</summary>
    [Fact]
    public async Task Scan_UsesOnlyVisionOccludersAndHonoursEndpointTolerance()
    {
        Node3D root = new();
        EyesBehaviour eyes = AddEyes(root);
        Assert.Equal(VisionOccluderLayer, eyes.VisionOcclusionMask);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => eyes.VisionOcclusionMask = 1u << 1);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => eyes.VisionOcclusionMask = VisionOccluderLayer | (1u << 1));
        TestVisualSubject subject = CreateSubject(new Vector3(0f, 0f, -3f), CreateCue());
        root.AddChild(subject);
        root.AddChild(CreateOccluder(new Vector3(0f, 0f, -1.5f), Vector3.One, collisionLayer: 1u << 1));

        try
        {
            await AddRootAndWaitForPhysicsAsync(root);
            subject.AddToGroup(_visualSubjectsGroupName);
            _ = Assert.Single(eyes.Scan());

            StaticBody3D supportingCollider = CreateOccluder(new Vector3(0f, 0f, -3f), new Vector3(1f, 1f, 0.02f));
            root.AddChild(supportingCollider);
            await WaitForPhysicsFramesAsync(GetSceneTree(), 2);
            _ = Assert.Single(eyes.Scan());

            eyes.VisionEndpointTolerance = 0.001f;
            Assert.Empty(eyes.Scan());
        }
        finally
        {
            root.QueueFree();
        }
    }

    /// <summary>A target's torso or rear geometry obstructs an otherwise front-facing logo cue.</summary>
    [Fact]
    public async Task Scan_ReportsNoFrontLogoWhenTorsoOrRearGeometryIntervenes()
    {
        Node3D root = new();
        EyesBehaviour eyes = AddEyes(root);
        StaticVisualCue frontLogo = CreateCue();
        frontLogo.Name = "FrontLogo";
        TestVisualSubject subject = CreateSubject(new Vector3(0f, 0f, -3f), frontLogo);
        StaticBody3D torso = CreateOccluder(new Vector3(0f, 0f, -2.5f), new Vector3(1f, 2f, 0.4f));
        torso.Name = "RearTorso";
        root.AddChild(subject);
        root.AddChild(torso);

        try
        {
            await AddRootAndWaitForPhysicsAsync(root);
            subject.AddToGroup(_visualSubjectsGroupName);

            Assert.Empty(eyes.Scan());
        }
        finally
        {
            root.QueueFree();
        }
    }

    /// <summary>An intervening hand on the VisionOccluder layer blocks a visible cue.</summary>
    [Fact]
    public async Task Scan_ReportsNoCueWhenAnInterveningHandBlocksIt()
    {
        Node3D root = new();
        EyesBehaviour eyes = AddEyes(root);
        TestVisualSubject subject = CreateSubject(new Vector3(0f, 0f, -3f), CreateCue());
        StaticBody3D hand = CreateOccluder(new Vector3(0f, 0f, -1.5f), new Vector3(0.3f, 0.3f, 0.3f));
        hand.Name = "InterveningHand";
        root.AddChild(subject);
        root.AddChild(hand);

        try
        {
            await AddRootAndWaitForPhysicsAsync(root);
            subject.AddToGroup(_visualSubjectsGroupName);

            Assert.Empty(eyes.Scan());
        }
        finally
        {
            root.QueueFree();
        }
    }

    private static EyesBehaviour AddEyes(Node3D parent)
    {
        var eyes = new EyesBehaviour();
        var eyeOrigin = new Marker3D { Name = "EyeOrigin" };
        parent.AddChild(eyes);
        parent.AddChild(eyeOrigin);
        eyes.EyeOrigin = eyeOrigin;
        return eyes;
    }

    private static StaticVisualCue CreateCue(VisualBounds? bounds = null, float maxVisibleDistance = 0f)
        => new()
        {
            Prominence = 1f,
            Bounds = bounds,
            MaxVisibleDistance = maxVisibleDistance
        };

    private static TestVisualSubject CreateSubject(Vector3 position, params VisualCue[] cues)
    {
        var subject = new TestVisualSubject { Position = position, VisualCues = cues };
        foreach (VisualCue cue in cues)
        {
            subject.AddChild(cue);
        }

        return subject;
    }

    private static StaticBody3D CreateOccluder(Vector3 position, Vector3 size, uint collisionLayer = VisionOccluderLayer)
    {
        var body = new StaticBody3D { Position = position, CollisionLayer = collisionLayer };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        return body;
    }

    private static async Task AddRootAndWaitForPhysicsAsync(Node3D root)
    {
        SceneTree sceneTree = GetSceneTree();
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
        await WaitForNextFrameAsync(sceneTree);
        Assert.True(root.IsInsideTree(), $"Expected '{root.Name}' to enter the test scene tree.");
        await WaitForPhysicsFramesAsync(sceneTree, 2);
    }

    private sealed partial class TestVisualSubject : Node3D, IVisualSubject
    {
        public IReadOnlyList<VisualCue> VisualCues { get; set; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();
    }
}
