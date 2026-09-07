using AlleyCat.Core.Installer;
using AlleyCat.IK;
using AlleyCat.Rigging;
using AlleyCat.XR.Mock;
using Godot;

namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Test-only driver node that hosts the game service root with a deterministic mock XR runtime and exposes the
/// XR-002 hand-tracking mock hooks through GDScript-friendly signatures for photobooth visual fixtures.
/// </summary>
/// <remarks>
/// <para>
/// Visual verification runners execute as <c>SceneTree</c> scripts without autoloads, so neither <see cref="Game" />
/// nor <see cref="XRManager" /> services exist by default. This driver assembles the same production-shaped service
/// fixture the C# integration tests build (game root + XR manager + mock runtime), which lets the player rig's
/// <see cref="OpticalFingerTrackingModifier" />, VRIK hand-pose providers, and hand behaviours resolve their runtime
/// services exactly as in production.
/// </para>
/// <para>
/// GDScript interop deliberately uses <see cref="string" /> side and joint names instead of crossing enum
/// marshalling boundaries. Failures are reported through return values plus <see cref="LastError" /> rather than
/// exceptions so GDScript runners can fail fast with readable messages.
/// </para>
/// </remarks>
[GlobalClass]
public partial class OpticalHandTrackingScenarioDriver : Node
{
    private const string MockRuntimeScenePath = "res://assets/xr/mock_runtime.tscn";

    private Game? _game;

    private XRManager? _xrManager;

    private MockXRRuntimeNode? _runtime;

    /// <summary>
    /// Whether <see cref="Setup" /> has successfully assembled the game service root and mock runtime.
    /// </summary>
    [Export]
    public bool IsSetupComplete
    {
        get;
        private set;
    }

    /// <summary>
    /// Human-readable description of the most recent failure; empty when the last operation succeeded.
    /// </summary>
    [Export]
    public string LastError
    {
        get;
        protected set;
    } = string.Empty;

    /// <summary>
    /// Assembles the game service root with the mock XR runtime. Must be called after the driver entered the tree
    /// and before the photobooth scene containing the player rig is added.
    /// </summary>
    /// <returns><see langword="true" /> when the fixture is ready.</returns>
    public bool Setup()
    {
        if (IsSetupComplete)
        {
            return true;
        }

        try
        {
            if (ResourceLoader.Load<PackedScene>(MockRuntimeScenePath) is not { } mockScene)
            {
                LastError = $"Failed to load the mock runtime scene at '{MockRuntimeScenePath}'.";
                return false;
            }

            _game = new Game
            {
                Name = "OpticalHandTrackingVisualTestGame",
            };

            _xrManager = new XRManager
            {
                Name = "XR",
            };
            _game.AddChild(_xrManager);

            // The game root registers scene-owned service registrars on _EnterTree, so the XR manager must already
            // be parented before the game enters the tree. Global startup is bypassed in the visual-test context,
            // leaving only the service provider build.
            GetTree().Root.AddChild(_game);

            _runtime = mockScene.Instantiate<MockXRRuntimeNode>();
            _xrManager.AddChild(_runtime);
            _ = _runtime.Initialise(new SubViewport(), maximumRefreshRate: 90);
            _xrManager.Runtime = _runtime;

            // The photobooth camera rigs render through their own SubViewports; the mock head camera must never
            // claim the root viewport.
            if (_runtime.GetNodeOrNull<Camera3D>("MainCamera") is { } mainCamera)
            {
                mainCamera.Current = false;
            }

            IsSetupComplete = true;

            return true;
        }
        catch (Exception exception)
        {
            LastError = $"Optical hand tracking scenario driver setup failed: {exception}";
            return false;
        }
    }

    /// <summary>
    /// Places the mock head camera that drives the VRIK head target.
    /// </summary>
    /// <param name="globalTransform">World-space head camera transform.</param>
    /// <returns><see langword="true" /> when applied.</returns>
    public bool SetHeadCameraTransform(Transform3D globalTransform)
    {
        if (!RequireRuntime(out MockXRRuntimeNode runtime))
        {
            return false;
        }

        Camera3D? mainCamera = runtime.GetNodeOrNull<Camera3D>("MainCamera");
        if (mainCamera is null)
        {
            LastError = "Mock runtime is missing the MainCamera node.";
            return false;
        }

        mainCamera.GlobalTransform = globalTransform;

        return true;
    }

    /// <summary>
    /// Moves a controller hand-position calibration anchor, which drives the VRIK hand target in controller mode.
    /// </summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <param name="globalTransform">World-space controller hand transform.</param>
    /// <returns><see langword="true" /> when applied.</returns>
    public bool SetControllerHandTransform(string sideName, Transform3D globalTransform)
    {
        if (!RequireRuntime(out MockXRRuntimeNode runtime) || !TryParseSide(sideName, out LimbSide side))
        {
            return false;
        }

        Node3D handPosition = (side == LimbSide.Right ? runtime.RightHandController : runtime.LeftHandController)
            .HandPositionNode;
        handPosition.GlobalTransform = globalTransform;

        return true;
    }

    /// <summary>
    /// Sets the per-side controller tracked flag used by hand-pose mode arbitration (XR-002 TR2).
    /// </summary>
    /// <param name="tracked">Whether the controllers are tracked.</param>
    public void SetControllersTracked(bool tracked)
    {
        if (RequireRuntime(out MockXRRuntimeNode runtime))
        {
            runtime.SetControllerHandTracked(LimbSide.Left, tracked);
            runtime.SetControllerHandTracked(LimbSide.Right, tracked);
        }
    }

    /// <summary>
    /// Sets the per-side optical tracked flag used by hand-pose mode arbitration (XR-002 TR2).
    /// </summary>
    /// <param name="tracked">Whether the hands are optically tracked.</param>
    public void SetOpticalHandsTracked(bool tracked)
    {
        if (RequireRuntime(out MockXRRuntimeNode runtime))
        {
            runtime.SetOpticalHandTracked(LimbSide.Left, tracked);
            runtime.SetOpticalHandTracked(LimbSide.Right, tracked);
        }
    }

    /// <summary>
    /// Re-evaluates the committed hand-pose mode from the current per-side tracked state (XR-002 TR3-TR4).
    /// </summary>
    /// <returns><see langword="true" /> when the committed mode changed.</returns>
    public bool EvaluateHandTrackingMode()
        => RequireRuntime(out MockXRRuntimeNode runtime) && runtime.EvaluateHandTrackingMode();

    /// <summary>
    /// Atomically commits the controller mode through bilateral controller agreement (XR-002 TR3).
    /// </summary>
    /// <returns><see langword="true" /> when the committed mode changed.</returns>
    public bool CommitControllerMode()
        => RequireRuntime(out MockXRRuntimeNode runtime)
           && runtime.SetHandObservations(XRHandSourceObservation.Controller, XRHandSourceObservation.Controller);

    /// <summary>
    /// Atomically commits the optical mode through bilateral optical agreement (XR-002 TR3).
    /// </summary>
    /// <returns><see langword="true" /> when the committed mode changed.</returns>
    public bool CommitOpticalMode()
        => RequireRuntime(out MockXRRuntimeNode runtime)
           && runtime.SetHandObservations(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);

    /// <summary>
    /// Gets the committed global hand-pose mode name: <c>Controller</c> or <c>Optical</c>.
    /// </summary>
    /// <returns>Committed mode name.</returns>
    public string GetCommittedMode()
        => RequireRuntime(out MockXRRuntimeNode runtime) ? runtime.HandTrackingMode.ToString() : "Unavailable";

    /// <summary>
    /// Injects a full tracked joint pose for one hand with a uniform flexion scale, mirroring the deterministic
    /// injection used by the XR-002 integration tests: each joint basis is <c>wrist * cumulativeLocalFlex</c>, so
    /// the retargeted destination local rotation equals the joint's own scaled flexion.
    /// </summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <param name="flexScale">Flexion multiplier; ~0 is an open hand, ~2 is a strong curl.</param>
    /// <param name="skipJointName">Joint name to leave untouched (no sample injected); empty injects everything.</param>
    /// <returns><see langword="true" /> when injected.</returns>
    public bool InjectTrackedHandPose(string sideName, float flexScale, string skipJointName)
        => RequireRuntime(out MockXRRuntimeNode runtime)
            && TryParseSide(sideName, out LimbSide side)
            && (string.IsNullOrEmpty(skipJointName) || TryParseJoint(skipJointName, out XRHandJoint _))
            && InjectTrackedHandPose(runtime, side, flexScale, null, 0.0f, skipJointName, 0.0f, 0.0f);

    /// <summary>
    /// Injects a tracked hand pose with a uniform flexion scale plus anatomical contamination components for the
    /// XR-002 visual scenarios: a deliberate spread about the source palm axis on the non-thumb proximals and an
    /// artificial roll about the source longitudinal direction on every joint. The injected local is the
    /// right-composed product <c>flex · spread · roll</c>, matching the deterministic integration stimulus.
    /// </summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <param name="flexScale">Flexion multiplier; ~0 is an open hand, ~2 is a strong curl.</param>
    /// <param name="proximalSpreadRadians">Signed spread rotation about the palm axis applied to proximals only.</param>
    /// <param name="longitudinalRollRadians">Roll rotation about the source longitudinal direction applied to every joint.</param>
    /// <param name="skipJointName">Joint name to leave untouched (no sample injected); empty injects everything.</param>
    /// <returns><see langword="true" /> when injected.</returns>
    public bool InjectTrackedHandPoseWithSpreadAndRoll(
        string sideName,
        float flexScale,
        float proximalSpreadRadians,
        float longitudinalRollRadians,
        string skipJointName)
        => RequireRuntime(out MockXRRuntimeNode runtime)
            && TryParseSide(sideName, out LimbSide side)
            && (string.IsNullOrEmpty(skipJointName) || TryParseJoint(skipJointName, out XRHandJoint _))
            && InjectTrackedHandPose(
                runtime,
                side,
                flexScale,
                null,
                0.0f,
                skipJointName,
                proximalSpreadRadians,
                longitudinalRollRadians);

    /// <summary>
    /// Injects a tracked hand pose with one source joint given a distinct local flexion scale. This is limited to
    /// the XR-002 visual fixture, where it isolates one segment's control-vs-freeze comparison without changing
    /// the rest of the hand pose.
    /// </summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <param name="flexScale">Flexion multiplier for every joint except <paramref name="overriddenJointName"/>.</param>
    /// <param name="overriddenJointName">Source joint whose local flexion uses <paramref name="overriddenJointFlexScale"/>.</param>
    /// <param name="overriddenJointFlexScale">Flexion multiplier for <paramref name="overriddenJointName"/>.</param>
    /// <param name="skipJointName">Joint name to leave untouched; empty injects every joint.</param>
    /// <returns><see langword="true" /> when injected.</returns>
    public bool InjectTrackedHandPoseWithJointFlex(
        string sideName,
        float flexScale,
        string overriddenJointName,
        float overriddenJointFlexScale,
        string skipJointName)
        => RequireRuntime(out MockXRRuntimeNode runtime)
            && TryParseSide(sideName, out LimbSide side)
            && TryParseJoint(overriddenJointName, out XRHandJoint overriddenJoint)
            && (string.IsNullOrEmpty(skipJointName) || TryParseJoint(skipJointName, out XRHandJoint _))
            && InjectTrackedHandPose(
                runtime,
                side,
                flexScale,
                overriddenJoint,
                overriddenJointFlexScale,
                skipJointName,
                0.0f,
                0.0f);

    private static bool InjectTrackedHandPose(
        MockXRRuntimeNode runtime,
        LimbSide side,
        float flexScale,
        XRHandJoint? overriddenJoint,
        float overriddenJointFlexScale,
        string skipJointName,
        float proximalSpreadRadians,
        float longitudinalRollRadians)
    {
        float wristYaw = side == LimbSide.Left ? 0.8f : -1.1f;
        Basis wristRotation = Basis.Identity.Rotated(Vector3.Up, wristYaw);
        Vector3 wristOrigin = new(side == LimbSide.Left ? -0.25f : 0.25f, 1.0f, -0.35f);

        runtime.SetHandJointSample(side, XRHandJoint.Wrist, new Transform3D(wristRotation, wristOrigin));

        Dictionary<XRHandJoint, Basis> cumulative = [];
        foreach (XRHandJoint joint in XRHandJoints.TrackedJoints)
        {
            if (joint == XRHandJoint.Wrist)
            {
                cumulative[joint] = Basis.Identity;
                continue;
            }

            // Synthesised in the empirically verified delivered source frame (XR-002 TR17): flexion twists
            // positively about the local hinge +X, spread rotates about the local palmward +Z, and roll rotates
            // about the local longitudinal +Y (FingerAnatomicalMath.Source* constants).
            float jointFlexScale = joint == overriddenJoint ? overriddenJointFlexScale : flexScale;
            Quaternion local = new(Vector3.Right, JointLocalFlexRadians(joint) * jointFlexScale);
            if (proximalSpreadRadians != 0.0f && XRHandJoints.TryGetNonThumbDestinationIndex(joint, out int index)
                && index % FingerRestNeutralMath.BonesPerChain == 0)
            {
                local *= new Quaternion(Vector3.Back, proximalSpreadRadians);
            }

            if (longitudinalRollRadians != 0.0f)
            {
                local *= new Quaternion(Vector3.Up, longitudinalRollRadians);
            }

            Basis localBasis = new(local);
            XRHandJoint parent = XRHandJoints.GetRequiredSourceParent(joint)
                ?? throw new InvalidOperationException($"Joint {joint} unexpectedly lacks a source parent.");
            cumulative[joint] = cumulative[parent] * localBasis;

            if (joint.ToString() == skipJointName)
            {
                continue;
            }

            Vector3 translation = wristOrigin + new Vector3(0.0f, -0.03f * (int)joint, 0.02f);
            runtime.SetHandJointSample(side, joint, new Transform3D(wristRotation * cumulative[joint], translation));
        }

        return true;
    }

    /// <summary>
    /// Clears every joint sample on both hands.
    /// </summary>
    public void ClearAllJointSamples()
    {
        if (!RequireRuntime(out MockXRRuntimeNode runtime))
        {
            return;
        }

        foreach (LimbSide side in new[] { LimbSide.Left, LimbSide.Right })
        {
            foreach (XRHandJoint joint in XRHandJoints.TrackedJoints)
            {
                runtime.ClearHandJointSample(side, joint);
            }
        }
    }

    /// <summary>
    /// Removes one joint sample, invalidating it for provider queries (XR-002 TR22 freeze scenarios).
    /// </summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <param name="jointName">Tracked joint name, for example <c>IndexIntermediate</c>.</param>
    /// <returns><see langword="true" /> when cleared.</returns>
    public bool ClearJointSample(string sideName, string jointName)
    {
        if (!RequireRuntime(out MockXRRuntimeNode runtime)
            || !TryParseSide(sideName, out LimbSide side)
            || !TryParseJoint(jointName, out XRHandJoint joint))
        {
            return false;
        }

        runtime.ClearHandJointSample(side, joint);

        return true;
    }

    /// <summary>
    /// Sets a per-joint tracked flag without mutating the stored sample.
    /// </summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <param name="jointName">Tracked joint name.</param>
    /// <param name="tracked">Whether the joint is actively tracked.</param>
    /// <returns><see langword="true" /> when applied.</returns>
    public bool SetJointTracked(string sideName, string jointName, bool tracked)
    {
        if (!RequireRuntime(out MockXRRuntimeNode runtime)
            || !TryParseSide(sideName, out LimbSide side)
            || !TryParseJoint(jointName, out XRHandJoint joint))
        {
            return false;
        }

        runtime.SetHandJointTracked(side, joint, tracked);

        return true;
    }

    /// <summary>
    /// Injects a raw origin-space optical wrist sample that drives the VRIK hand target while optical mode is
    /// committed (XR-002 TR5, TR10). The driver keeps the mock runtime origin at the world identity, so
    /// origin-space equals world-space at world scale one.
    /// </summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <param name="originSpaceWrist">Raw wrist transform relative to the XR origin.</param>
    /// <returns><see langword="true" /> when injected.</returns>
    public bool SetOpticalWristSample(string sideName, Transform3D originSpaceWrist)
    {
        if (!RequireRuntime(out MockXRRuntimeNode runtime) || !TryParseSide(sideName, out LimbSide side))
        {
            return false;
        }

        runtime.SetOpticalWristSample(side, originSpaceWrist);

        return true;
    }

    /// <summary>
    /// Injects an optical wrist sample expressed as an exact world-space target, compensating the XR origin
    /// transform and calibration anchor at injection time so the VRIK hand target lands on the requested
    /// transform (XR-002 TR10). The mock caches the composed world transform, keeping the target stable even
    /// though VRIK repositions the mock origin node every frame.
    /// </summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <param name="worldWrist">Desired world-space wrist transform.</param>
    /// <returns><see langword="true" /> when injected.</returns>
    public bool SetOpticalWristWorldSample(string sideName, Transform3D worldWrist)
    {
        if (!RequireRuntime(out MockXRRuntimeNode runtime) || !TryParseSide(sideName, out LimbSide side))
        {
            return false;
        }

        Transform3D originGlobal = runtime.OriginNode.GlobalTransform;
        Transform3D anchor = Transform3D.Identity;
        if (runtime.GetNodeOrNull<Node3D>($"{(side == LimbSide.Right ? "Right" : "Left")}OpticalHand/WristAnchor/OpticalHandAnchor")
            is { } seededAnchor)
        {
            anchor = seededAnchor.Transform;
        }

        // Composed wrist = originGlobal * (sample * anchor), so the exact sample is the inverse composition.
        Transform3D originSpaceSample = originGlobal.AffineInverse() * (worldWrist * anchor.AffineInverse());
        runtime.SetOpticalWristSample(side, originSpaceSample);

        return true;
    }

    /// <summary>
    /// Marks one optical wrist as lost, freezing it at its retained world-space transform (XR-002 TR5, TR25).
    /// </summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <returns><see langword="true" /> when applied.</returns>
    public bool MarkOpticalWristLost(string sideName)
    {
        if (!RequireRuntime(out MockXRRuntimeNode runtime) || !TryParseSide(sideName, out LimbSide side))
        {
            return false;
        }

        runtime.MarkOpticalWristLost(side);

        return true;
    }

    /// <summary>
    /// Runs the production character installer for an instanced player root, materialising the template-owned rig
    /// (AnimationTree, hands, VRIK, locomotion) exactly as in the reference player fixture.
    /// </summary>
    /// <param name="playerRoot">Root node of the instanced player character scene.</param>
    /// <returns><see langword="true" /> when the rig is installed and ready.</returns>
    public bool InstallPlayerRig(Node playerRoot)
    {
        if (playerRoot.GetNodeOrNull("AnimationTree") is not null
            && playerRoot.GetNodeOrNull("Female/GeneralSkeleton") is not null)
        {
            return true;
        }

        SceneInstaller? installer = FindInstaller(playerRoot);
        if (installer is null)
        {
            LastError = $"No character scene installer found under '{playerRoot.GetPath()}'.";
            return false;
        }

        try
        {
            SceneInstallationResult result = installer.Install(new SceneInstallationContext(playerRoot));
            if (!result.Succeeded)
            {
                LastError = string.Join('\n', result.Errors);
                return false;
            }
        }
        catch (Exception exception)
        {
            LastError = $"Player rig installation failed: {exception}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Binds the installed player VRIK to the mock XR runtime, mirroring the production
    /// <c>PlayerVRIKStartupBinder</c> handshake. Must be called after the player rig is installed and its
    /// AnimationTree activated: the installer defers a pose-state-machine restart that invalidates bindings
    /// made before animation activation.
    /// </summary>
    /// <param name="playerRoot">Root node of the installed player character.</param>
    /// <returns><see langword="true" /> when the player VRIK is bound.</returns>
    public bool BindPlayerVRIK(Node playerRoot)
    {
        PlayerVRIK? playerVrik = playerRoot.GetNodeOrNull<PlayerVRIK>("VRIK");
        if (playerVrik is null || !playerVrik.BindToXRServices())
        {
            LastError = $"Failed to bind the player VRIK under '{playerRoot.GetPath()}' to the mock XR runtime.";
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        // The game service root intentionally outlives the driver; Godot teardown owns final cleanup, and
        // freeing the service root while rig nodes still resolve services crashes at shutdown.
        _game = null;
        _xrManager = null;
        _runtime = null;
        IsSetupComplete = false;
    }

    private bool RequireRuntime(out MockXRRuntimeNode runtime)
    {
        if (IsSetupComplete && _runtime is not null && IsInstanceValid(_runtime))
        {
            runtime = _runtime;
            return true;
        }

        LastError = "Optical hand tracking scenario driver is not set up; call Setup() first.";
        runtime = null!;

        return false;
    }

    private static SceneInstaller? FindInstaller(Node playerRoot)
    {
        foreach (string installerName in new[] { "PlayerCharacterInstaller", "NPCCharacterInstaller", "BaseCharacterInstaller" })
        {
            if (playerRoot.GetNodeOrNull<SceneInstaller>(installerName) is { } named)
            {
                return named;
            }
        }

        return FindDescendantInstaller(playerRoot);
    }

    private static SceneInstaller? FindDescendantInstaller(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is SceneInstaller installer)
            {
                return installer;
            }

            if (FindDescendantInstaller(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private bool TryParseSide(string sideName, out LimbSide side)
    {
        if (string.Equals(sideName, nameof(LimbSide.Left), StringComparison.OrdinalIgnoreCase))
        {
            side = LimbSide.Left;
            return true;
        }

        if (string.Equals(sideName, nameof(LimbSide.Right), StringComparison.OrdinalIgnoreCase))
        {
            side = LimbSide.Right;
            return true;
        }

        LastError = $"Unknown hand side '{sideName}'; expected 'Left' or 'Right'.";
        side = LimbSide.Left;

        return false;
    }

    private bool TryParseJoint(string jointName, out XRHandJoint joint)
    {
        if (Enum.TryParse(jointName, ignoreCase: true, out joint) && Enum.IsDefined(joint))
        {
            return true;
        }

        LastError = $"Unknown hand joint '{jointName}'.";
        joint = XRHandJoint.Wrist;

        return false;
    }

    private static float JointLocalFlexRadians(XRHandJoint joint)
        => joint switch
        {
            XRHandJoint.ThumbMetacarpal => 0.21f,
            XRHandJoint.ThumbProximal => 0.34f,
            XRHandJoint.ThumbDistal => 0.42f,
            XRHandJoint.IndexMetacarpal => 0.12f,
            XRHandJoint.IndexProximal => 0.30f,
            XRHandJoint.IndexIntermediate => 0.51f,
            XRHandJoint.IndexDistal => 0.40f,
            XRHandJoint.MiddleMetacarpal => 0.10f,
            XRHandJoint.MiddleProximal => 0.27f,
            XRHandJoint.MiddleIntermediate => 0.48f,
            XRHandJoint.MiddleDistal => 0.37f,
            XRHandJoint.RingMetacarpal => 0.11f,
            XRHandJoint.RingProximal => 0.24f,
            XRHandJoint.RingIntermediate => 0.45f,
            XRHandJoint.RingDistal => 0.34f,
            XRHandJoint.LittleMetacarpal => 0.09f,
            XRHandJoint.LittleProximal => 0.18f,
            XRHandJoint.LittleIntermediate => 0.39f,
            XRHandJoint.LittleDistal => 0.28f,
            XRHandJoint.Wrist => throw new ArgumentOutOfRangeException(nameof(joint), joint, "Not a finger joint."),
            _ => throw new ArgumentOutOfRangeException(nameof(joint), joint, "Not a finger joint."),
        };
}
