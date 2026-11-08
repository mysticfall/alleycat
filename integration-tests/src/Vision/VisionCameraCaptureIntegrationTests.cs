using System.Reflection;
using System.Security.Cryptography;
using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Vision;

/// <summary>
/// Integration coverage for the VISION-001 vision image-capture feature (TR 53-62): camera
/// resolution, template rigs and defaults, one-shot freshness and idle cost, player live-viewport
/// preservation, per-capture camera re-sync, and overlap/failure/lifetime behaviour.
/// </summary>
public sealed class VisionCameraCaptureIntegrationTests
{
    private const string ReferenceFemaleBaseScenePath =
        "res://assets/characters/templates/reference_female/reference_female_base.tscn";
    private const string ReferenceFemaleNpcScenePath =
        "res://assets/characters/templates/reference_female/reference_female_npc.tscn";
    private const string ReferenceFemalePlayerScenePath =
        "res://assets/characters/templates/reference_female/reference_female_player.tscn";
    private const string ReferenceMaleBaseScenePath =
        "res://assets/characters/templates/reference_male/reference_male_base.tscn";
    private const string ReferenceMaleNpcScenePath =
        "res://assets/characters/templates/reference_male/reference_male_npc.tscn";

    /// <summary>
    /// AC71: an unconfigured vision component reads a null <c>Camera</c> and capture fails clearly
    /// instead of yielding an empty or stale image.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CameraResolution_UnconfiguredComponent_ReadsNullAndFailsCaptureClearly()
    {
        SceneTree sceneTree = GetSceneTree();
        EyesBehaviour eyes = new()
        {
            Name = "Eyes",
            VisualSurveyIntervalSeconds = 30d
        };
        Node3D holder = new()
        {
            Name = "UnconfiguredVisionHolder"
        };
        holder.AddChild(eyes);
        await AddChildToRootAsync(sceneTree, holder);
        try
        {
            Assert.Null(eyes.Camera);

            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => eyes.CaptureImage().AsTask());
            Assert.Contains("without a configured vision camera", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            holder.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// AC71 (simulated XR evidence — a fake XR manager standing in for the OpenXR runtime): the
    /// player template's <c>IVision.Camera</c> is the identical <c>Camera3D</c> behind
    /// <c>IXRRuntime.Camera.CameraNode</c>, never the inherited template rig camera, and capture
    /// before the runtime is available fails clearly.
    /// </summary>
    [Fact]
    public async Task CameraResolution_PlayerTemplate_ResolvesIdenticalFakeXRCamera()
    {
        SceneTree sceneTree = GetSceneTree();
        Node playerRoot = LoadPackedScene(ReferenceFemalePlayerScenePath).Instantiate();
        await AddChildToRootAsync(sceneTree, playerRoot);
        Game? global = null;
        try
        {
            EyesBehaviour eyes = playerRoot.GetNode<EyesBehaviour>("Eyes");

            // Before the XR runtime exists the player camera is unavailable: capture fails clearly.
            Assert.Null(eyes.Camera);
            InvalidOperationException beforeRuntime = await Assert.ThrowsAsync<InvalidOperationException>(
                () => eyes.CaptureImage().AsTask());
            Assert.Contains("without a configured vision camera", beforeRuntime.Message, StringComparison.Ordinal);

            global = new Game { Name = "VisionCapturePlayerGlobal" };
            FakeVisionXRManager xrManager = new()
            {
                Name = "XR"
            };
            global.AddChild(xrManager);
            global._EnterTree();
            _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, global);
            await WaitForFramesAsync(sceneTree, 4);

            Assert.NotNull(xrManager.Runtime);
            Camera3D xrCameraNode = xrManager.Runtime.Camera.CameraNode;
            Assert.Same(xrCameraNode, eyes.Camera);
            Assert.Same(xrCameraNode, ((IVision)eyes).Camera);
            Camera3D? rigCamera = eyes.VisionCamera;
            Assert.NotNull(rigCamera);
            Assert.NotSame(rigCamera, eyes.Camera);
        }
        finally
        {
            playerRoot.QueueFree();
            global?.QueueFree();

            await WaitForFramesAsync(sceneTree, 3);
        }
    }

    /// <summary>
    /// AC71: the NPC template's <c>IVision.Camera</c> resolves to the template-authored vision rig
    /// camera.
    /// </summary>
    [Headless]
    [Fact]
    public void CameraResolution_NpcTemplate_ResolvesTemplateRigCamera()
    {
        Node npcRoot = LoadPackedScene(ReferenceFemaleNpcScenePath).Instantiate();
        try
        {
            EyesBehaviour eyes = npcRoot.GetNode<EyesBehaviour>("Eyes");
            Camera3D rigCamera = npcRoot.GetNode<Camera3D>(
                "Female/GeneralSkeleton/Head/Viewpoint/VisionViewport/VisionCamera");
            Assert.Same(rigCamera, eyes.VisionCamera);
            Assert.Same(rigCamera, eyes.Camera);
        }
        finally
        {
            npcRoot.Free();
        }
    }

    /// <summary>
    /// AC72: reference female and male base and NPC templates load with the documented vision rig
    /// under <c>Head/Viewpoint</c>, and <c>IVision.Camera</c> resolves to the authored
    /// <c>Camera3D</c> carrying the documented overridable defaults.
    /// </summary>
    [Headless]
    [Fact]
    public void Templates_BaseAndNpcVariants_LoadWithDocumentedVisionRigDefaults()
    {
        AssertTemplateVisionRig(ReferenceFemaleBaseScenePath, "Female");
        AssertTemplateVisionRig(ReferenceFemaleNpcScenePath, "Female");
        AssertTemplateVisionRig(ReferenceMaleBaseScenePath, "Male");
        AssertTemplateVisionRig(ReferenceMaleNpcScenePath, "Male");
    }

    /// <summary>
    /// AC74: with no capture in flight, advancing frames leaves the rig's texture hash unchanged,
    /// proving zero per-frame rendering while idle, and the rest update mode stays
    /// <c>Disabled</c>.
    /// </summary>
    [Fact]
    public async Task IdleRig_NoCaptureInFlight_AdvancedFramesLeaveTextureHashUnchanged()
    {
        SceneTree sceneTree = GetSceneTree();
        await using CaptureRigFixture fixture = await CaptureRigFixture.CreateAsync(sceneTree);
        Image firstCapture = await fixture.Eyes.CaptureImage().AsTask();
        Assert.False(firstCapture.IsEmpty());

        string hashBefore = HashTexture(fixture.Viewport);
        string[] idleHashes = new string[3];
        for (int read = 0; read < idleHashes.Length; read++)
        {
            await WaitForFramesAsync(sceneTree, 5);
            idleHashes[read] = HashTexture(fixture.Viewport);
        }

        Assert.All(idleHashes, hash => Assert.Equal(hashBefore, hash));
        Assert.Equal(SubViewport.UpdateMode.Disabled, fixture.Viewport.RenderTargetUpdateMode);
    }

    /// <summary>
    /// AC73: a scene change made while idle appears in the next capture; the readback is never a
    /// stale pre-change texture, completes within the bounded frame count, publishes no percepts,
    /// and restores the rest update mode.
    /// </summary>
    [Fact]
    public async Task OneShotCapture_AfterIdleSceneChange_NextCaptureIsFreshAndDistinct()
    {
        SceneTree sceneTree = GetSceneTree();
        await using CaptureRigFixture fixture = await CaptureRigFixture.CreateAsync(sceneTree);
        int perceptCount = 0;
        fixture.Eyes.Perceived += _ => perceptCount++;
        VisualCue? lookTargetBefore = fixture.Eyes.LookTarget;

        ulong frameBefore = Engine.GetProcessFrames();
        Image firstCapture = await fixture.Eyes.CaptureImage().AsTask();
        ulong frameAfter = Engine.GetProcessFrames();
        Assert.False(firstCapture.IsEmpty());
        Assert.Equal(768, firstCapture.GetWidth());
        Assert.Equal(768, firstCapture.GetHeight());
        Assert.InRange(frameAfter - frameBefore, 0ul, 2ul);

        string staleHash = HashTexture(fixture.Viewport);
        fixture.SceneBox.MaterialOverride = CreateEmissiveMaterial(new Color(0.05f, 0.05f, 0.95f));
        await WaitForFramesAsync(sceneTree, 15);
        Assert.Equal(staleHash, HashTexture(fixture.Viewport));

        Image secondCapture = await fixture.Eyes.CaptureImage().AsTask();
        Assert.False(secondCapture.IsEmpty());
        Assert.NotEqual(HashImage(firstCapture), HashImage(secondCapture));
        Assert.NotEqual(staleHash, HashImage(secondCapture));
        Assert.Equal(HashImage(secondCapture), HashTexture(fixture.Viewport));

        Assert.Equal(SubViewport.UpdateMode.Disabled, fixture.Viewport.RenderTargetUpdateMode);
        Assert.Equal(0, perceptCount);
        Assert.Same(lookTargetBefore, fixture.Eyes.LookTarget);
    }

    /// <summary>
    /// AC75 (simulated player path — a runtime-resolved camera in a continuously updating
    /// viewport): capture preserves the live viewport's update mode and rendering settings, never
    /// moves the camera, and reads the frame back from the live viewport itself.
    /// </summary>
    [Fact]
    public async Task PlayerPathCapture_LiveViewport_PreservesUpdateModeAndSettings()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "PlayerLiveViewportFixture"
        };
        SubViewport liveViewport = new()
        {
            Name = "XRLiveViewport",
            Size = new Vector2I(640, 360),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            TransparentBg = false,
            OwnWorld3D = false,
        };
        root.AddChild(liveViewport);
        Transform3D authoredXRCameraTransform = new(Basis.Identity, new Vector3(0f, 1.5f, 2f));
        Camera3D xrCamera = new()
        {
            Name = "XRCamera",
            Fov = 90f,
            Near = 0.01f,
            Current = true,
            Transform = authoredXRCameraTransform,
        };
        liveViewport.AddChild(xrCamera);
        root.AddChild(CreateEmissiveBox("SceneBox", new Vector3(0f, 1.5f, -2.5f), new Color(0.9f, 0.6f, 0.1f), 1.2f));
        Marker3D eyeOrigin = new()
        {
            Name = "EyeOrigin"
        };
        root.AddChild(eyeOrigin);
        EyesBehaviour eyes = new()
        {
            Name = "Eyes",
            EyeOrigin = eyeOrigin,
            VisualSurveyIntervalSeconds = 30d,
        };
        root.AddChild(eyes);
        eyes.SetRuntimeVisionCameraResolver(() => xrCamera);
        await AddChildToRootAsync(sceneTree, root);
        try
        {
            Assert.Same(xrCamera, eyes.Camera);
            Assert.Equal(SubViewport.UpdateMode.Always, liveViewport.RenderTargetUpdateMode);

            Task<Image> captureTask = eyes.CaptureImage().AsTask();
            Assert.Equal(SubViewport.UpdateMode.Always, liveViewport.RenderTargetUpdateMode);
            Image image = await captureTask;

            Assert.False(image.IsEmpty());
            Assert.Equal(640, image.GetWidth());
            Assert.Equal(360, image.GetHeight());
            Assert.Equal(SubViewport.UpdateMode.Always, liveViewport.RenderTargetUpdateMode);
            Assert.Equal(new Vector2I(640, 360), liveViewport.Size);
            Assert.False(liveViewport.TransparentBg);
            Assert.False(liveViewport.OwnWorld3D);
            Assert.Equal(90f, xrCamera.Fov, precision: 4);
            Assert.True(xrCamera.Current);
            Assert.True(xrCamera.Transform.IsEqualApprox(authoredXRCameraTransform),
                "The capture path must never move the runtime-resolved player camera.");
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// AC76: per-capture re-sync holds at capture time — the rig camera's global transform equals
    /// <c>EyeOrigin.GlobalTransform * authoredLocalOffset</c> including under whole-body rotation
    /// (±30° yaw tracked exactly); an unsynced world-origin camera fails the assertion.
    /// </summary>
    [Fact]
    public async Task PerCaptureResync_CameraGlobalTransformTracksEyeOriginUnderRotation()
    {
        SceneTree sceneTree = GetSceneTree();
        CharacterBody3D npcRoot = LoadPackedScene(ReferenceFemaleNpcScenePath).Instantiate<CharacterBody3D>();
        npcRoot.ProcessMode = Node.ProcessModeEnum.Disabled;
        await AddChildToRootAsync(sceneTree, npcRoot);
        try
        {
            EyesBehaviour eyes = npcRoot.GetNode<EyesBehaviour>("Eyes");
            Node3D eyeOrigin = eyes.EyeOrigin
                ?? throw new InvalidOperationException("Expected the NPC template to author an eye origin.");
            Camera3D camera = eyes.VisionCamera
                ?? throw new InvalidOperationException("Expected the NPC template to author a vision camera.");
            SubViewport viewport = npcRoot.GetNode<SubViewport>(
                "Female/GeneralSkeleton/Head/Viewpoint/VisionViewport");
            Transform3D authoredLocalOffset = camera.Transform;
            Assert.Equal(Mathf.DegToRad(-7.5f), authoredLocalOffset.Basis.GetEuler().X, precision: 4);

            // The rig camera starts unsynced at the world origin — the exact regression the re-sync
            // exists to prevent.
            Assert.Equal(Vector3.Zero, camera.GlobalPosition);
            Vector3 neutralForward = -eyeOrigin.GlobalBasis.Z.Normalized();
            Assert.True(eyeOrigin.GlobalPosition.LengthSquared() > 0.5f, "Expected a head-height eye origin.");

            foreach (float yawDegrees in new[] { 0f, 30f, -30f })
            {
                npcRoot.Basis = Basis.FromEuler(new Vector3(0f, Mathf.DegToRad(yawDegrees), 0f));
                float anchorYawDelta = Mathf.RadToDeg(
                    neutralForward.SignedAngleTo(-eyeOrigin.GlobalBasis.Z.Normalized(), Vector3.Up));
                Assert.True(Mathf.Abs(Mathf.Abs(anchorYawDelta) - Mathf.Abs(yawDegrees)) < 0.5f,
                    $"Expected the anchor to track the {yawDegrees}° body rotation (measured {anchorYawDelta:F2}°).");

                Image image = await eyes.CaptureImage().AsTask();
                Assert.False(image.IsEmpty());

                Transform3D expected = eyeOrigin.GlobalTransform * authoredLocalOffset;
                Assert.True(camera.GlobalTransform.IsEqualApprox(expected),
                    $"Camera global transform must equal EyeOrigin * authoredLocalOffset at {yawDegrees}°.");
                Assert.True(camera.GlobalPosition.DistanceTo(eyeOrigin.GlobalPosition) < 0.01f,
                    "The synced camera must render from the eye origin, not the world origin.");
            }

            Assert.Equal(SubViewport.UpdateMode.Disabled, viewport.RenderTargetUpdateMode);
        }
        finally
        {
            npcRoot.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// AC77: concurrent captures serialise and each yields its own fresh frame — with the scene
    /// colour flipped deterministically every frame while both are in flight, the two results must
    /// differ.
    /// </summary>
    [Fact]
    public async Task ConcurrentCaptures_SerialiseAndYieldDistinctFreshFrames()
    {
        SceneTree sceneTree = GetSceneTree();
        await using CaptureRigFixture fixture = await CaptureRigFixture.CreateAsync(sceneTree);
        Color[] flipColours =
        [
            new Color(1f, 0f, 0f),
            new Color(0f, 0f, 1f),
            new Color(0f, 1f, 0f),
            new Color(1f, 1f, 0f),
        ];
        int flipIndex = 0;

        Task<Image> firstCapture = fixture.Eyes.CaptureImage().AsTask();
        Task<Image> secondCapture = fixture.Eyes.CaptureImage().AsTask();
        // The test flips the subject colour at the start of every awaited frame while both captures
        // are in flight, so whichever fresh frame each serialised capture renders, it observes a
        // different colour than the other.
        while (!firstCapture.IsCompleted || !secondCapture.IsCompleted)
        {
            ApplyEmissiveColour(fixture.SceneBox, flipColours[flipIndex % flipColours.Length]);
            flipIndex++;
            await WaitForNextFrameAsync(sceneTree);
        }

        Image[] images = await Task.WhenAll(firstCapture, secondCapture);
        Assert.True(flipIndex >= 2, "Expected each serialised capture to render its own frame.");

        Assert.All(images, image => Assert.False(image.IsEmpty()));
        Assert.All(images, image => Assert.Equal(768, image.GetWidth()));
        Assert.All(images, image => Assert.Equal(768, image.GetHeight()));
        Assert.NotEqual(HashImage(images[0]), HashImage(images[1]));
        Assert.Equal(SubViewport.UpdateMode.Disabled, fixture.Viewport.RenderTargetUpdateMode);
    }

    /// <summary>
    /// AC77: an invalid or freed camera or viewport fails clearly by throwing, never by hanging or
    /// returning an empty image.
    /// </summary>
    [Fact]
    public async Task CaptureFailure_FreedCameraOrViewportMidCapture_ThrowsClearly()
    {
        SceneTree sceneTree = GetSceneTree();

        // Freed before the call: the camera reads null and capture fails at the configuration check.
        await using (CaptureRigFixture preFreed = await CaptureRigFixture.CreateAsync(sceneTree))
        {
            preFreed.Camera.Free();
            Assert.Null(preFreed.Eyes.Camera);
            InvalidOperationException configurationFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => preFreed.Eyes.CaptureImage().AsTask());
            Assert.Contains("without a configured vision camera", configurationFailure.Message, StringComparison.Ordinal);
        }

        // Freed while in flight: the mid-capture checkpoint faults the pending capture.
        await using (CaptureRigFixture freedCamera = await CaptureRigFixture.CreateAsync(sceneTree))
        {
            Task<Image> pending = freedCamera.Eyes.CaptureImage().AsTask();
            freedCamera.Camera.Free();
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
            await WaitForFramesAsync(sceneTree, 2);
        }

        await using (CaptureRigFixture freedViewport = await CaptureRigFixture.CreateAsync(sceneTree))
        {
            Task<Image> pending = freedViewport.Eyes.CaptureImage().AsTask();
            freedViewport.Viewport.Free();
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// AC77: teardown mid-capture invalidates in-flight captures without hangs or late writes —
    /// the pending capture faults at its next checkpoint before the one-shot mode is ever armed,
    /// so the rig viewport keeps its rest update mode.
    /// </summary>
    [Fact]
    public async Task CaptureLifetime_TeardownMidCapture_InvalidatesWithoutLateWrites()
    {
        SceneTree sceneTree = GetSceneTree();
        await using CaptureRigFixture fixture = await CaptureRigFixture.CreateAsync(sceneTree);

        Task<Image> pending = fixture.Eyes.CaptureImage().AsTask();
        Assert.False(pending.IsCompleted, "Expected the capture to be in flight across frame boundaries.");
        fixture.RemoveChild(fixture.Eyes);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        await WaitForFramesAsync(sceneTree, 3);
        Assert.Equal(SubViewport.UpdateMode.Disabled, fixture.Viewport.RenderTargetUpdateMode);
        Assert.True(fixture.Camera.IsInsideTree());
    }

    private static void AssertTemplateVisionRig(string scenePath, string characterRootName)
    {
        Node root = LoadPackedScene(scenePath).Instantiate();
        try
        {
            Marker3D viewpoint = root.GetNode<Marker3D>($"{characterRootName}/GeneralSkeleton/Head/Viewpoint");
            SubViewport viewport = root.GetNode<SubViewport>(
                $"{characterRootName}/GeneralSkeleton/Head/Viewpoint/VisionViewport");
            Camera3D camera = root.GetNode<Camera3D>(
                $"{characterRootName}/GeneralSkeleton/Head/Viewpoint/VisionViewport/VisionCamera");
            Assert.Same(viewpoint, viewport.GetParent());
            Assert.Same(viewport, camera.GetParent());

            Assert.Equal(new Vector2I(768, 768), viewport.Size);
            Assert.False(viewport.TransparentBg);
            Assert.False(viewport.OwnWorld3D);
            Assert.Equal(SubViewport.UpdateMode.Disabled, viewport.RenderTargetUpdateMode);

            Assert.Equal(70f, camera.Fov, precision: 4);
            Assert.Equal(Camera3D.KeepAspectEnum.Height, camera.KeepAspect);
            Assert.Equal(0.05f, camera.Near, precision: 4);
            Assert.True(camera.Current);
            Vector3 cameraEuler = camera.Transform.Basis.GetEuler();
            Assert.Equal(-7.5f, Mathf.RadToDeg(cameraEuler.X), precision: 2);
            Assert.Equal(0f, Mathf.RadToDeg(cameraEuler.Y), precision: 2);
            Assert.Equal(0f, Mathf.RadToDeg(cameraEuler.Z), precision: 2);
            Assert.Equal(Vector3.Zero, camera.Transform.Origin);

            EyesBehaviour eyes = root.GetNode<EyesBehaviour>("Eyes");
            Assert.Same(viewpoint, eyes.EyeOrigin);
            Assert.Same(camera, eyes.VisionCamera);
            Assert.Same(camera, eyes.Camera);
        }
        finally
        {
            root.Free();
        }
    }

    private static async Task AddChildToRootAsync(SceneTree sceneTree, Node child)
    {
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, child);
        await WaitForNextFrameAsync(sceneTree);
        Assert.True(child.IsInsideTree(), $"Expected '{child.Name}' to enter the test scene tree.");
    }

    private static string HashTexture(SubViewport viewport) => HashImage(viewport.GetTexture().GetImage());

    private static string HashImage(Image image) => Convert.ToHexString(SHA256.HashData(image.GetData()));

    private static StandardMaterial3D CreateEmissiveMaterial(Color colour)
        => new()
        {
            AlbedoColor = colour,
            EmissionEnabled = true,
            Emission = colour,
            EmissionEnergyMultiplier = 4f,
        };

    private static void ApplyEmissiveColour(MeshInstance3D box, Color colour)
    {
        StandardMaterial3D material = Assert.IsType<StandardMaterial3D>(box.MaterialOverride, exactMatch: false);
        material.AlbedoColor = colour;
        material.Emission = colour;
    }

    private static MeshInstance3D CreateEmissiveBox(string name, Vector3 position, Color colour, float size)
        => new()
        {
            Name = name,
            Mesh = new BoxMesh { Size = new Vector3(size, size, size) },
            Position = position,
            MaterialOverride = CreateEmissiveMaterial(colour),
        };

    /// <summary>
    /// Minimal authored vision rig matching the template defaults, with a deterministic per-frame
    /// colour-flipping subject so consecutive captures observably render distinct frames.
    /// </summary>
    private sealed partial class CaptureRigFixture : Node3D, IAsyncDisposable
    {
        public EyesBehaviour Eyes
        {
            get;
            private set;
        } = null!;

        public SubViewport Viewport
        {
            get;
            private set;
        } = null!;

        public Camera3D Camera
        {
            get;
            private set;
        } = null!;

        public MeshInstance3D SceneBox
        {
            get;
            private set;
        } = null!;

        public static async Task<CaptureRigFixture> CreateAsync(SceneTree sceneTree)
        {
            CaptureRigFixture fixture = new();
            Marker3D eyeOrigin = new()
            {
                Name = "EyeOrigin",
                Position = new Vector3(0f, 1.5f, 0f)
            };
            fixture.AddChild(eyeOrigin);

            SubViewport viewport = new()
            {
                Name = "VisionViewport",
                Size = new Vector2I(768, 768),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
                TransparentBg = false,
                OwnWorld3D = false,
            };
            eyeOrigin.AddChild(viewport);

            Camera3D camera = new()
            {
                Name = "VisionCamera",
                Fov = 70f,
                KeepAspect = Camera3D.KeepAspectEnum.Height,
                Near = 0.05f,
                Current = true,
                Transform = new Transform3D(Basis.FromEuler(new Vector3(Mathf.DegToRad(-7.5f), 0f, 0f)), Vector3.Zero),
            };
            viewport.AddChild(camera);

            StandardMaterial3D boxMaterial = CreateEmissiveMaterial(new Color(0.95f, 0.05f, 0.05f));
            MeshInstance3D sceneBox = new()
            {
                Name = "SceneBox",
                Mesh = new BoxMesh { Size = new Vector3(0.8f, 0.8f, 0.8f) },
                Position = new Vector3(0f, 1.5f, -2.5f),
                MaterialOverride = boxMaterial,
            };
            fixture.AddChild(sceneBox);

            EyesBehaviour eyes = new()
            {
                Name = "Eyes",
                EyeOrigin = eyeOrigin,
                VisionCamera = camera,
                VisualSurveyIntervalSeconds = 30d,
            };
            fixture.AddChild(eyes);

            fixture.Eyes = eyes;
            fixture.Viewport = viewport;
            fixture.Camera = camera;
            fixture.SceneBox = sceneBox;

            _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, fixture);
            await WaitForFramesAsync(sceneTree, 2);
            Assert.True(fixture.IsInsideTree(), "Expected the capture rig fixture to enter the test scene tree.");
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            QueueFree();
            if (Engine.GetMainLoop() is SceneTree sceneTree)
            {
                await WaitForFramesAsync(sceneTree, 2);
            }
        }
    }

    /// <summary>
    /// Fake XR manager (simulated XR evidence): installs a fake runtime whose camera node stands in
    /// for the OpenXR runtime camera, following the established production-harness pattern.
    /// </summary>
    private sealed partial class FakeVisionXRManager : XRManager
    {
        private static readonly FieldInfo _runtimeBackingField = typeof(XRManager)
            .GetField("<Runtime>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected XRManager runtime backing field.");

        public FakeVisionXRRuntime RuntimeInstance { get; } = new();

        public override void _Ready()
        {
            _runtimeBackingField.SetValue(this, RuntimeInstance);
            InitialisationAttempted = true;
            InitialisationSucceeded = true;
            _ = EmitSignal(SignalName.Initialised, true);
        }
    }

    private sealed class FakeVisionXRRuntime : IXRRuntime
    {
        private readonly FakeVisionXRHandController _rightController = new();
        private readonly FakeVisionXRHandController _leftController = new();
        private readonly XRControllerHandTracking _handTracking;

        public FakeVisionXRRuntime()
        {
            OriginNode = new Node3D();
            CameraNode = new Camera3D();
            _handTracking = new XRControllerHandTracking(_rightController, _leftController);
        }

        public Node3D OriginNode
        {
            get;
        }

        public Camera3D CameraNode
        {
            get;
        }

        public IXROrigin Origin => new FakeVisionXROrigin(OriginNode);

        public IXRCamera Camera => new FakeVisionXRCamera(CameraNode);

        public IXRHandController RightHandController => _rightController;

        public IXRHandController LeftHandController => _leftController;

        public XRHandTrackingMode HandTrackingMode => XRHandTrackingMode.Controller;

        public IXRHandJointProvider OpticalHandJoints => XREmptyHandJointProvider.Instance;

        public IXRHandPoseSource GetHandPoseSource(LimbSide side) => _handTracking.GetHandPoseSource(side);

#pragma warning disable CS0067
        public event Action? PoseRecentered;

        public event Action? HandTrackingModeChanged;
#pragma warning restore CS0067

        public bool Initialise(SubViewport viewport, int maximumRefreshRate)
        {
            _ = viewport;
            _ = maximumRefreshRate;
            return true;
        }
    }

    private sealed partial class FakeVisionXRHandController : Node3D, IXRHandController
    {
#pragma warning disable CS0067
        public event Action<string>? ActionButtonPressed;

        public event Action<string>? ActionButtonReleased;

        public event Action<string, float>? ActionFloatInputChanged;

        public event Action<string, Vector2>? ActionVector2InputChanged;
#pragma warning restore CS0067

        public Node3D ControllerNode => this;

        public Node3D HandPositionNode => this;
    }

    private sealed record FakeVisionXROrigin(Node3D OriginNode) : IXROrigin
    {
        public float WorldScale { get; set; } = 1f;
    }

    private sealed record FakeVisionXRCamera(Camera3D CameraNode) : IXRCamera;
}
