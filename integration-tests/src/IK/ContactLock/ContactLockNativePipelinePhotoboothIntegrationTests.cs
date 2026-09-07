using AlleyCat.Tests.IK;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK.ContactLock;

/// <summary>
/// Non-visual coverage for the exact two-neutral-role scene shared by the Gate B photobooth evidence.
/// </summary>
public sealed class ContactLockNativePipelinePhotoboothIntegrationTests
{
    private const string ScenePath = "res://tests/ik/contact_lock_native_pipeline_photobooth.tscn";
    private const int MaximumPhysicsFramesForNativeEvidence = 16;

    /// <summary>
    /// Verifies the Player-left and NPC-right anchors, opposite ordinary fallbacks, native end-bone proximity, and
    /// pole/knee anomaly guards from the photobooth capture set.
    /// </summary>
    [Fact]
    public async Task GateA_PhotoboothNativePipeline_SeparatesRoleAnchorsFallbacksAndAnatomicalPoleCues()
    {
        SceneTree sceneTree = GetSceneTree();
        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(ScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        ContactLockNativePipelinePhotobooth controller = await WaitForConfigurationAsync(sceneTree);
        Assert.True(
            controller.HasCompletedInitialNativeSettlement,
            "Configuration must wait until both minimal fixture roles have received fresh native physics evidence.");

        controller.ApplyGateA();
        Assert.False(controller.HasFreshNativeEvidence());
        await WaitForFreshNativeEvidenceAsync(sceneTree, controller);

        Assert.True(
            controller.GetAvatarForwardWorld("Player").Dot(Vector3.Back) > 0.99f,
            "The identity minimal fixture must map its rig avatar +Z semantic direction to world +Z before visual capture.");

        AssertRoleFoot(controller, "Player", "Left", expectedPublished: true);
        AssertRoleFoot(controller, "Player", "Right", expectedPublished: false);
        AssertRoleFoot(controller, "NPC", "Left", expectedPublished: false);
        AssertRoleFoot(controller, "NPC", "Right", expectedPublished: true);
        controller.AssertGateAScenario();
    }

    /// <summary>
    /// Verifies every labelled Gate B scenario-A state: invalid fixture evidence is refused, ordinary fallback is
    /// selected, and a fresh native completion signal still reaches the fallback without promoting contact evidence.
    /// </summary>
    [Fact]
    public async Task GateB_PhotoboothScenarioA_RefusesContactDespiteFreshOrdinaryFallbackNativeSolve()
    {
        SceneTree sceneTree = GetSceneTree();
        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(ScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        ContactLockNativePipelinePhotobooth controller = await WaitForConfigurationAsync(sceneTree);
        controller.ApplyGateBScenarioA();
        Assert.False(controller.HasFreshNativeEvidence());
        await WaitForFreshNativeEvidenceAsync(sceneTree, controller);
        controller.AssertGateBScenarioA();

        AssertGateBRoleFoot(controller, "Player", "Left", "FallbackMasking");
        AssertGateBRoleFoot(controller, "Player", "Right", "Invalid");
        AssertGateBRoleFoot(controller, "NPC", "Left", "Stale");
        AssertGateBRoleFoot(controller, "NPC", "Right", "Missing");
    }

    /// <summary>
    /// Verifies every labelled Gate B scenario-B state, including the Player/NPC and left/right complements of
    /// scenario A. The pole and knee assertions guard against a visually plausible back-pole or straight-knee capture.
    /// </summary>
    [Fact]
    public async Task GateB_PhotoboothScenarioB_RefusesContactDespiteFreshOrdinaryFallbackNativeSolve()
    {
        SceneTree sceneTree = GetSceneTree();
        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(ScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        ContactLockNativePipelinePhotobooth controller = await WaitForConfigurationAsync(sceneTree);
        controller.ApplyGateBScenarioB();
        Assert.False(controller.HasFreshNativeEvidence());
        await WaitForFreshNativeEvidenceAsync(sceneTree, controller);
        controller.AssertGateBScenarioB();

        AssertGateBRoleFoot(controller, "Player", "Left", "Missing");
        AssertGateBRoleFoot(controller, "Player", "Right", "Stale");
        AssertGateBRoleFoot(controller, "NPC", "Left", "Contradictory");
        AssertGateBRoleFoot(controller, "NPC", "Right", "FallbackMasking");
        Assert.True(
            controller.GetGateBContradictoryPairSeparation("NPC", "Left") >= 0.29f,
            "The contradictory marker must show two visibly separated magenta elements, not a merged capsule.");
        Assert.InRange(
            controller.GetGateBContradictoryBridgeLength("NPC", "Left"),
            0.29f,
            0.30f);
        Assert.True(
            controller.GetGateBMarkerTargetEndClearance("NPC", "Left") > 0.12f,
            "The contradictory split pair must remain clear of the cyan target and magenta native end markers.");
        Assert.True(
            controller.GetGateBStatusLabelTargetEndClearance("NPC", "Left") > 0.55f,
            "The contradictory status panel must occupy a separate label lane rather than crossing cyan or magenta geometry.");
    }

    private static async Task<ContactLockNativePipelinePhotobooth> WaitForConfigurationAsync(SceneTree sceneTree)
    {
        for (int frame = 0; frame < MaximumPhysicsFramesForNativeEvidence; frame++)
        {
            Node? sceneRoot = sceneTree.CurrentScene;
            ContactLockNativePipelinePhotobooth? controller = sceneRoot?.GetNodeOrNull<ContactLockNativePipelinePhotobooth>("NativePipelineController");
            if (controller is { IsConfigured: true })
            {
                return controller;
            }

            await WaitForNextPhysicsFrameAsync(sceneTree);
        }

        throw new Xunit.Sdk.XunitException("The shared native photobooth fixture did not finish configuring on physics frames.");
    }

    private static async Task WaitForFreshNativeEvidenceAsync(
        SceneTree sceneTree,
        ContactLockNativePipelinePhotobooth controller)
    {
        for (int frame = 0; frame < MaximumPhysicsFramesForNativeEvidence; frame++)
        {
            await WaitForNextPhysicsFrameAsync(sceneTree);
            if (controller.HasFreshNativeEvidence())
            {
                return;
            }
        }

        throw new Xunit.Sdk.XunitException(
            "All asserted native TwoBoneIK3D modifiers must emit fresh ModificationProcessed evidence after a Gate B scenario applies.");
    }

    private static void AssertRoleFoot(
        ContactLockNativePipelinePhotobooth controller,
        string role,
        string foot,
        bool expectedPublished)
    {
        Assert.Equal(expectedPublished, controller.IsAnchorPublished(role, foot));
        Assert.True(
            controller.GetEndTargetError(role, foot) <= ContactLockNativePipelinePhotobooth.FootTargetToleranceMetres,
            $"{role} {foot} native TwoBoneIK3D end-bone should reach its provider target.");
        Assert.True(
            controller.GetSignalTargetError(role, foot) <= ContactLockNativePipelinePhotobooth.SignalTargetToleranceMetres,
            $"{role} {foot} native TwoBoneIK3D completion signal should sample the independently resolved target.");
        Assert.True(
            controller.GetPoleForwardOffset(role, foot) > 0.01f,
            $"{role} {foot} pole forward offset was {controller.GetPoleForwardOffset(role, foot):F4} m; this fails the back-pole visual anomaly.");
        Assert.True(
            controller.GetKneeLateralOffset(role, foot) > 0.015f,
            $"{role} {foot} knee must retain visible flexion/lateral offset; this fails the straight-knee visual anomaly.");
        Assert.True(
            controller.GetVisibleDirectionalMarkerForward(role, foot).Dot(controller.GetAvatarForwardWorld(role)) > 0.99f,
            $"{role} {foot} cyan pole-forward arrow must geometrically point along rig +Z/world +Z, not only claim it in text.");
    }

    private static void AssertGateBRoleFoot(
        ContactLockNativePipelinePhotobooth controller,
        string role,
        string foot,
        string expectedInjectedEvidence)
    {
        Assert.Equal(expectedInjectedEvidence, controller.GetGateBInjectedEvidenceStatus(role, foot));
        Assert.Equal(ExpectedMarkerShape(expectedInjectedEvidence), controller.GetGateBVisibleMarkerShape(role, foot));
        Assert.Equal(ExpectedMarkerColour(expectedInjectedEvidence), controller.GetGateBVisibleMarkerColour(role, foot));
        Assert.False(controller.IsAnchorPublished(role, foot));
        Assert.Equal("OrdinaryFallback", controller.GetGateBSelectedProvider(role, foot));
        Assert.True(
            controller.IsGateBFreshSignalNativeSolveSuccessful(role, foot),
            $"{role} {foot} must visibly retain a fresh native ordinary-fallback solve while contact stays refused.");
        Assert.Equal("PreIKRejected", controller.GetGateBPostIKDecision(role, foot));
        Assert.Equal("ContactRefusedDespiteFallbackSolve", controller.GetGateBBlockedOutcome(role, foot));
        AssertRoleFoot(controller, role, foot, expectedPublished: false);

        if (expectedInjectedEvidence == "Missing")
        {
            Assert.True(
                controller.GetGateBVisibleApertureDiameter(role, foot) >= 0.11f,
                $"{role} {foot} missing-evidence marker must retain a visibly hollow central aperture.");
            Assert.True(
                controller.GetGateBMarkerTargetEndClearance(role, foot) > 0.12f,
                $"{role} {foot} hollow missing marker must remain separate from cyan target and magenta end geometry.");
            Assert.True(
                controller.GetGateBStatusLabelTargetEndClearance(role, foot) > 0.55f,
                $"{role} {foot} status panel must remain in a separate visual lane from cyan and magenta geometry.");
        }
    }

    private static string ExpectedMarkerShape(string injectedEvidence) => injectedEvidence switch
    {
        "Missing" => "HollowRing",
        "Stale" => "TallPrism",
        "Invalid" => "WarningCone",
        "Contradictory" => "SplitPair",
        "FallbackMasking" => "MaskingStack",
        _ => throw new ArgumentOutOfRangeException(nameof(injectedEvidence), injectedEvidence, null),
    };

    private static string ExpectedMarkerColour(string injectedEvidence) => injectedEvidence switch
    {
        "Missing" => "Red",
        "Stale" => "Violet",
        "Invalid" => "Amber",
        "Contradictory" => "Magenta",
        "FallbackMasking" => "OrangeAndBlack",
        _ => throw new ArgumentOutOfRangeException(nameof(injectedEvidence), injectedEvidence, null),
    };
}
