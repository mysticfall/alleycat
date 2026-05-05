using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Items;

/// <summary>
/// Integration coverage for ITEM-001 reusable physics chain assembly and attachment behaviour.
/// </summary>
public sealed class PhysicsChainIntegrationTests
{
    private const string ChainScenePath = "res://assets/items/chain/physics_chain.tscn";
    private const string VisualTestScenePath = "res://tests/items/chain/physics_chain_visual_test.tscn";
    private const string ChainNodePath = "Subject/Chain";
    private const string LinksContainerPath = "Links";
    private const string JointsContainerPath = "Joints";
    private const string StartAttachmentPointPath = "Links/Link01/StartAttachmentPoint";
    private const string EndAttachmentPointPath = "Links/Link10/EndAttachmentPoint";
    private const string StartAnchorAttachmentPath = "Subject/StartAnchor/AttachmentPoint";
    private const string EndWeightPath = "Subject/EndWeight";
    private const string EndWeightAttachmentPath = "Subject/EndWeight/AttachmentPoint";
    private const string StartAttachmentJointPath = "Subject/Chain/AttachmentJoints/StartAttachmentJoint";
    private const string EndAttachmentJointPath = "Subject/Chain/AttachmentJoints/EndAttachmentJoint";

    private const float RotationToleranceRadians = 0.02f;
    private const float PositionToleranceMetres = 0.0015f;
    private const float AttachmentToleranceMetres = 0.02f;
    private const float InterLinkAttachmentToleranceMetres = 0.03f;
    private const float MinimumWeightTravelMetres = 0.05f;
    private const int StructureLinkCount = 5;
    private const int InitialisationFrames = 8;
    private const double LongRunSimulationSeconds = 30.0;
    private const double StabilitySampleIntervalSeconds = 1.0;

    /// <summary>
    /// Verifies authored link count, spacing, and 90-degree Z alternation.
    /// </summary>
    [Headless]
    [Fact]
    public void PhysicsChain_RebuildsConfiguredLinksWithAlternatingQuarterTurns()
    {
        Node3D chain = LoadPackedScene(ChainScenePath).Instantiate<Node3D>();
        Assert.Equal(-0.015f, (float)chain.Get("LinkGapAdjustment"));

        chain.Set("LinkCount", StructureLinkCount);
        _ = chain.Call("RebuildChainNow");

        Node linksContainer = chain.GetNode(LinksContainerPath);
        Node jointsContainer = chain.GetNode(JointsContainerPath);
        float linkPitch = (float)chain.Get("LinkPitch");
        float linkGapAdjustment = (float)chain.Get("LinkGapAdjustment");

        Assert.Equal(StructureLinkCount, linksContainer.GetChildCount());
        Assert.Equal(StructureLinkCount - 1, jointsContainer.GetChildCount());
        Assert.NotNull(chain.GetNodeOrNull(StartAttachmentPointPath));
        Assert.NotNull(chain.GetNodeOrNull($"Links/Link{StructureLinkCount:00}/EndAttachmentPoint"));

        for (int linkIndex = 1; linkIndex < linksContainer.GetChildCount(); linkIndex++)
        {
            RigidBody3D previousLink = Assert.IsType<RigidBody3D>(linksContainer.GetChild(linkIndex - 1), exactMatch: false);
            RigidBody3D currentLink = Assert.IsType<RigidBody3D>(linksContainer.GetChild(linkIndex), exactMatch: false);

            Vector3 localDelta = currentLink.Position - previousLink.Position;
            Assert.True(Mathf.IsZeroApprox(localDelta.X), $"Expected zero X offset between links {linkIndex} and {linkIndex + 1}.");
            Assert.True(Mathf.IsZeroApprox(localDelta.Y), $"Expected zero Y offset between links {linkIndex} and {linkIndex + 1}.");
            Assert.True(
                Mathf.Abs(localDelta.Z + (linkPitch + linkGapAdjustment)) <= PositionToleranceMetres,
                $"Expected consecutive links to be separated by the measured chain pitch plus gap adjustment. Observed Z delta: {localDelta.Z:F4}, expected: {-(linkPitch + linkGapAdjustment):F4}.");

            float zRotationDelta = Mathf.Wrap(
                currentLink.Rotation.Z - previousLink.Rotation.Z,
                -Mathf.Pi,
                Mathf.Pi);

            Assert.True(
                Mathf.Abs(Mathf.Abs(zRotationDelta) - (Mathf.Pi * 0.5f)) <= RotationToleranceRadians,
                $"Expected a 90-degree Z rotation offset between links {linkIndex} and {linkIndex + 1}, observed {zRotationDelta:F4} rad.");
        }
    }

    /// <summary>
    /// Verifies chain-end attachments accept non-rigid physics bodies permitted by Joint3D.
    /// </summary>
    [Headless]
    [Fact]
    public void PhysicsChain_AttachmentsAcceptAnyPhysicsBody3DType()
    {
        Node3D root = new();
        Node3D chain = LoadPackedScene(ChainScenePath).Instantiate<Node3D>();

        StaticBody3D startBody = new()
        {
            Name = "StartBody",
        };

        Marker3D startAnchor = new()
        {
            Name = "StartAnchor",
            Position = new Vector3(0f, -0.05f, 0f),
        };

        startBody.AddChild(startAnchor);

        CharacterBody3D endBody = new()
        {
            Name = "EndBody",
        };

        Marker3D endAnchor = new()
        {
            Name = "EndAnchor",
            Position = new Vector3(0f, 0.05f, 0f),
        };

        endBody.AddChild(endAnchor);

        root.AddChild(chain);
        root.AddChild(startBody);
        root.AddChild(endBody);

        _ = chain.Call("RebuildChainNow");

        Joint3D startJoint = Assert.IsType<Joint3D>(chain.Call("AttachStartBody", startBody, startAnchor).AsGodotObject(), exactMatch: false);
        Joint3D endJoint = Assert.IsType<Joint3D>(chain.Call("AttachEndBody", endBody, endAnchor).AsGodotObject(), exactMatch: false);

        Assert.NotEqual(new NodePath(), startJoint.NodeA);
        Assert.NotEqual(new NodePath(), startJoint.NodeB);
        Assert.NotEqual(new NodePath(), endJoint.NodeA);
        Assert.NotEqual(new NodePath(), endJoint.NodeB);

        int linkCount = (int)chain.Get("LinkCount");
        Marker3D startAttachmentPoint = Assert.IsType<Marker3D>(chain.GetNodeOrNull(StartAttachmentPointPath), exactMatch: false);
        Marker3D endAttachmentPoint = Assert.IsType<Marker3D>(chain.GetNodeOrNull($"Links/Link{linkCount:00}/EndAttachmentPoint"), exactMatch: false);

        Assert.True(startAnchor.GlobalPosition.DistanceTo(startAttachmentPoint.GlobalPosition) <= PositionToleranceMetres);
        Assert.True(endAnchor.GlobalPosition.DistanceTo(endAttachmentPoint.GlobalPosition) <= PositionToleranceMetres);
    }

    /// <summary>
    /// Verifies the chain stays connected and keeps both end attachments under impulse for the full 30-second acceptance window.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PhysicsChain_StaysConnectedAndKeepsEndAttachmentsForThirtySecondsUnderImpulse()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForPhysicsFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(VisualTestScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForPhysicsFramesAsync(sceneTree, InitialisationFrames);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected physics-chain visual test scene to become current scene.");

        Node3D chain = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(ChainNodePath), exactMatch: false);
        Node linksContainer = chain.GetNode(LinksContainerPath);
        Node jointsContainer = chain.GetNode(JointsContainerPath);
        Marker3D chainStartAttachment = Assert.IsType<Marker3D>(chain.GetNodeOrNull(StartAttachmentPointPath), exactMatch: false);
        Marker3D chainEndAttachment = Assert.IsType<Marker3D>(chain.GetNodeOrNull(EndAttachmentPointPath), exactMatch: false);
        Marker3D startAnchorAttachment = Assert.IsType<Marker3D>(sceneRoot.GetNodeOrNull(StartAnchorAttachmentPath), exactMatch: false);
        RigidBody3D endWeight = Assert.IsType<RigidBody3D>(sceneRoot.GetNodeOrNull(EndWeightPath), exactMatch: false);
        Marker3D endWeightAttachment = Assert.IsType<Marker3D>(sceneRoot.GetNodeOrNull(EndWeightAttachmentPath), exactMatch: false);
        Joint3D startAttachmentJoint = Assert.IsType<Joint3D>(sceneRoot.GetNodeOrNull(StartAttachmentJointPath), exactMatch: false);
        Joint3D endAttachmentJoint = Assert.IsType<Joint3D>(sceneRoot.GetNodeOrNull(EndAttachmentJointPath), exactMatch: false);

        Assert.NotNull(startAttachmentJoint);
        Assert.NotNull(endAttachmentJoint);
        Assert.Equal(10, linksContainer.GetChildCount());
        Assert.Equal(9, jointsContainer.GetChildCount());

        Vector3 startWeightPosition = endWeight.GlobalPosition;
        float longestObservedWeightTravel = 0f;
        float worstObservedStartAttachmentGap = 0f;
        float worstObservedEndAttachmentGap = 0f;
        float worstObservedInterLinkGap = 0f;

        endWeight.ApplyCentralImpulse(new Vector3(0.55f, 0f, -0.25f));

        int stabilitySampleCount = (int)(LongRunSimulationSeconds / StabilitySampleIntervalSeconds);
        for (int sampleIndex = 0; sampleIndex < stabilitySampleCount; sampleIndex++)
        {
            await WaitForPhysicsSecondsAsync(sceneTree, StabilitySampleIntervalSeconds);

            float elapsedSeconds = (sampleIndex + 1) * (float)StabilitySampleIntervalSeconds;
            longestObservedWeightTravel = Math.Max(longestObservedWeightTravel, endWeight.GlobalPosition.DistanceTo(startWeightPosition));

            float startAttachmentGap = startAnchorAttachment.GlobalPosition.DistanceTo(chainStartAttachment.GlobalPosition);
            worstObservedStartAttachmentGap = Math.Max(worstObservedStartAttachmentGap, startAttachmentGap);
            Assert.True(
                startAttachmentGap <= AttachmentToleranceMetres,
                $"Start anchor attachment point should remain locked to the chain start throughout the 30-second simulation. Elapsed: {elapsedSeconds:F1}s, gap: {startAttachmentGap:F4} m.");

            float endAttachmentGap = endWeightAttachment.GlobalPosition.DistanceTo(chainEndAttachment.GlobalPosition);
            worstObservedEndAttachmentGap = Math.Max(worstObservedEndAttachmentGap, endAttachmentGap);
            Assert.True(
                endAttachmentGap <= AttachmentToleranceMetres,
                $"End weight attachment point should remain locked to the chain end throughout the 30-second simulation. Elapsed: {elapsedSeconds:F1}s, gap: {endAttachmentGap:F4} m.");

            for (int linkIndex = 0; linkIndex < linksContainer.GetChildCount() - 1; linkIndex++)
            {
                var backAttachment = (Vector3)chain.Call("GetLinkBackAttachmentGlobalPosition", linkIndex);
                var nextFrontAttachment = (Vector3)chain.Call("GetLinkFrontAttachmentGlobalPosition", linkIndex + 1);
                float attachmentGap = backAttachment.DistanceTo(nextFrontAttachment);
                worstObservedInterLinkGap = Math.Max(worstObservedInterLinkGap, attachmentGap);

                Assert.True(
                    attachmentGap <= InterLinkAttachmentToleranceMetres,
                    "Consecutive link attachment points should remain near each other during the full 30-second simulation. " +
                    $"Elapsed: {elapsedSeconds:F1}s, links {linkIndex + 1}/{linkIndex + 2}, gap: {attachmentGap:F4} m.");
            }
        }

        Assert.True(
            longestObservedWeightTravel >= MinimumWeightTravelMetres,
            $"End weight should visibly travel after the applied impulse. Maximum observed travel over {LongRunSimulationSeconds:F0}s: {longestObservedWeightTravel:F4} m.");

        Assert.True(
            worstObservedStartAttachmentGap <= AttachmentToleranceMetres,
            $"Start anchor attachment gap exceeded tolerance during long-run verification. Worst observed gap: {worstObservedStartAttachmentGap:F4} m.");

        Assert.True(
            worstObservedEndAttachmentGap <= AttachmentToleranceMetres,
            $"End attachment gap exceeded tolerance during long-run verification. Worst observed gap: {worstObservedEndAttachmentGap:F4} m.");

        Assert.True(
            worstObservedInterLinkGap <= InterLinkAttachmentToleranceMetres,
            $"Inter-link attachment gap exceeded tolerance during long-run verification. Worst observed gap: {worstObservedInterLinkGap:F4} m.");

        GD.Print(
            $"ITEM-001 long-run metrics: duration={LongRunSimulationSeconds:F0}s, max_weight_travel={longestObservedWeightTravel:F4} m, " +
            $"max_start_gap={worstObservedStartAttachmentGap:F4} m, max_end_gap={worstObservedEndAttachmentGap:F4} m, " +
            $"max_inter_link_gap={worstObservedInterLinkGap:F4} m.");
    }
}
