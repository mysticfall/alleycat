using System.Text;
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
    private const string ChainLinkScenePath = "res://assets/items/chain/chain_link.tscn";
    private const string MirrorRoomScenePath = "res://assets/testing/mirror_room/mirror_room.tscn";
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
    private const string MirrorRoomChainNodePath = "Items/Chain";
    private const string MirrorRoomStartAnchorAttachmentPath = "Items/StartAnchor/AttachmentPoint";
    private const string MirrorRoomEndWeightPath = "Items/EndWeight";
    private const string MirrorRoomEndWeightAttachmentPath = "Items/EndWeight/AttachmentPoint";

    private const float RotationToleranceRadians = 0.02f;
    private const float PositionToleranceMetres = 0.0015f;
    private const float AttachmentToleranceMetres = 0.02f;
    private const float MirrorRoomAttachmentToleranceMetres = 0.05f;
    private const float InterLinkAttachmentToleranceMetres = 0.03f;
    private const float MirrorRoomInterLinkAttachmentToleranceMetres = 0.06f;
    private const float MinimumWeightTravelMetres = 0.05f;
    private const int StructureLinkCount = 5;
    private const int InitialisationFrames = 8;
    private const double LongRunSimulationSeconds = 30.0;
    private const double StabilitySampleIntervalSeconds = 1.0;
    private const float PriorPinJointTensionInitialEndpointSpanMetres = 0.7819f;
    private const float PriorPinJointTensionMaxEndpointSpanMetres = 0.8839f;
    private const float MirrorRoomTensionMaxEndpointSpanGrowthMetres = 0.050f;
    private const float SustainedTensionEndWeightMassKg = 5.0f;
    private const float SustainedTensionMaxRestLengthGrowthRatio = 0.15f;
    private const int ConeTwistJointTypeValue = 1;

    /// <summary>
    /// Verifies authored link count, spacing, and 90-degree Z alternation.
    /// </summary>
    [Headless]
    [Fact]
    public void PhysicsChain_RebuildsConfiguredLinksWithAlternatingQuarterTurns()
    {
        Node3D chain = LoadPackedScene(ChainScenePath).Instantiate<Node3D>();
        Assert.Equal(-0.015f, (float)chain.Get("LinkGapAdjustment"));
        Assert.Equal(0.08f, (float)chain.Get("LinkMass"), 3);
        Assert.Equal(0.25f, (float)chain.Get("LinkLinearDamping"), 3);
        Assert.Equal(0.45f, (float)chain.Get("LinkAngularDamping"), 3);
        Assert.Equal(ConeTwistJointTypeValue, chain.Get("LinkJointType").AsInt32());
        Assert.Equal(ConeTwistJointTypeValue, chain.Get("AttachmentJointType").AsInt32());
        Assert.True((bool)chain.Get("UsePairedLinkJoints"));
        Assert.Equal(0.55f, (float)chain.Get("LinkJointSwingSpan"), 3);
        Assert.Equal(Mathf.Pi / 6f, (float)chain.Get("LinkJointTwistSpan"), 3);
        Assert.Equal(0.70f, (float)chain.Get("AttachmentJointSwingSpan"), 3);
        Assert.Equal(0.55f, (float)chain.Get("AttachmentJointTwistSpan"), 3);
        Assert.Equal(0.30f, (float)chain.Get("ConeTwistBias"), 3);
        Assert.Equal(0.80f, (float)chain.Get("ConeTwistSoftness"), 3);
        Assert.Equal(1.0f, (float)chain.Get("ConeTwistRelaxation"), 3);
        Assert.Equal(0.55f, (float)chain.Get("HingeJointLimitSpan"), 3);
        Assert.Equal(0.30f, (float)chain.Get("HingeJointBias"), 3);
        Assert.True((bool)chain.Get("EnableEndpointSpanGuard"));
        Assert.Equal(0.04f, (float)chain.Get("MaxEndpointSpanGrowth"), 3);
        Assert.Equal(2.0f, (float)chain.Get("JointDamping"), 3);
        Assert.Equal(0.65f, (float)chain.Get("JointBias"), 3);

        chain.Set("LinkCount", StructureLinkCount);
        _ = chain.Call("RebuildChainNow");

        Node linksContainer = chain.GetNode(LinksContainerPath);
        Node jointsContainer = chain.GetNode(JointsContainerPath);
        float linkPitch = (float)chain.Get("LinkPitch");
        float linkGapAdjustment = (float)chain.Get("LinkGapAdjustment");

        Assert.Equal(StructureLinkCount, linksContainer.GetChildCount());
        Assert.Equal((StructureLinkCount - 1) * 2, jointsContainer.GetChildCount());
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

        foreach (Node child in linksContainer.GetChildren())
        {
            RigidBody3D link = Assert.IsType<RigidBody3D>(child, exactMatch: false);
            Assert.False(link.CanSleep);
            Assert.Equal(0.08f, link.Mass, 3);
            Assert.Equal(0.25f, link.LinearDamp, 3);
            Assert.Equal(0.45f, link.AngularDamp, 3);
        }

        foreach (Node child in jointsContainer.GetChildren())
        {
            ConeTwistJoint3D joint = Assert.IsType<ConeTwistJoint3D>(child, exactMatch: false);
            AssertConeTwistParameters(joint, (float)chain.Get("LinkJointSwingSpan"), (float)chain.Get("LinkJointTwistSpan"), chain);
        }
    }

    /// <summary>
    /// Verifies authored mirror-room production generation uses constrained joints by default.
    /// </summary>
    [Headless]
    [Fact]
    public void PhysicsChain_MirrorRoomChain_UsesRotationLimitedJointDefaults()
    {
        Node3D mirrorRoom = LoadPackedScene(MirrorRoomScenePath).Instantiate<Node3D>();

        try
        {
            Node3D chain = Assert.IsType<Node3D>(mirrorRoom.GetNodeOrNull(MirrorRoomChainNodePath), exactMatch: false);
            _ = chain.Call("RebuildChainNow");
            Node linksContainer = chain.GetNode(LinksContainerPath);
            Node jointsContainer = chain.GetNode(JointsContainerPath);

            Assert.Equal(32, linksContainer.GetChildCount());
            Assert.Equal(62, jointsContainer.GetChildCount());
            Assert.Equal(ConeTwistJointTypeValue, chain.Get("LinkJointType").AsInt32());
            Assert.Equal(ConeTwistJointTypeValue, chain.Get("AttachmentJointType").AsInt32());
            Assert.True((bool)chain.Get("UsePairedLinkJoints"));
            Assert.True((bool)chain.Get("EnableEndpointSpanGuard"));

            foreach (Node child in jointsContainer.GetChildren())
            {
                ConeTwistJoint3D coneTwistJoint = Assert.IsType<ConeTwistJoint3D>(child, exactMatch: false);
                AssertConeTwistParameters(coneTwistJoint, (float)chain.Get("LinkJointSwingSpan"), (float)chain.Get("LinkJointTwistSpan"), chain);
            }

            Joint3D startAttachmentJoint = Assert.IsType<Joint3D>(mirrorRoom.GetNodeOrNull(StartAttachmentJointPath.Replace("Subject/Chain", "Items/Chain")), exactMatch: false);
            Joint3D endAttachmentJoint = Assert.IsType<Joint3D>(mirrorRoom.GetNodeOrNull(EndAttachmentJointPath.Replace("Subject/Chain", "Items/Chain")), exactMatch: false);
            AssertConeTwistParameters(Assert.IsType<ConeTwistJoint3D>(startAttachmentJoint, exactMatch: false), (float)chain.Get("AttachmentJointSwingSpan"), (float)chain.Get("AttachmentJointTwistSpan"), chain);
            AssertConeTwistParameters(Assert.IsType<ConeTwistJoint3D>(endAttachmentJoint, exactMatch: false), (float)chain.Get("AttachmentJointSwingSpan"), (float)chain.Get("AttachmentJointTwistSpan"), chain);
        }
        finally
        {
            mirrorRoom.Free();
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
    /// Verifies the chain test assets use the dedicated dynamic-body interaction layer while retaining world collision.
    /// </summary>
    [Headless]
    [Fact]
    public void PhysicsChain_TestAssets_UseDedicatedHandDynamicInteractionLayer()
    {
        RigidBody3D chainLink = LoadPackedScene(ChainLinkScenePath).Instantiate<RigidBody3D>();
        Node3D mirrorRoom = LoadPackedScene(MirrorRoomScenePath).Instantiate<Node3D>();

        try
        {
            RigidBody3D endWeight = Assert.IsType<RigidBody3D>(mirrorRoom.GetNodeOrNull("Items/EndWeight"), exactMatch: false);
            RigidBody3D startAnchor = Assert.IsType<RigidBody3D>(mirrorRoom.GetNodeOrNull("Items/StartAnchor"), exactMatch: false);

            AssertBodyCanCollideWithPlayerHands(chainLink);
            AssertBodyCanCollideWithPlayerHands(endWeight);
            AssertBodyCanCollideWithPlayerHands(startAnchor);
        }
        finally
        {
            chainLink.Free();
            mirrorRoom.Free();
        }
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
        Assert.Equal(18, jointsContainer.GetChildCount());

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

    /// <summary>
    /// Verifies the authored mirror-room 32-link chain stays connected under representative dynamic contact.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PhysicsChain_MirrorRoomThirtyTwoLinkChain_StaysConnectedUnderImpulseAndContact()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForPhysicsFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(MirrorRoomScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForPhysicsFramesAsync(sceneTree, InitialisationFrames);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected mirror-room scene to become current scene.");

        Node3D chain = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(MirrorRoomChainNodePath), exactMatch: false);
        Node linksContainer = chain.GetNode(LinksContainerPath);
        Node jointsContainer = chain.GetNode(JointsContainerPath);
        Marker3D chainStartAttachment = Assert.IsType<Marker3D>(chain.GetNodeOrNull(StartAttachmentPointPath), exactMatch: false);
        Marker3D chainEndAttachment = Assert.IsType<Marker3D>(chain.GetNodeOrNull("Links/Link32/EndAttachmentPoint"), exactMatch: false);
        Marker3D startAnchorAttachment = Assert.IsType<Marker3D>(sceneRoot.GetNodeOrNull(MirrorRoomStartAnchorAttachmentPath), exactMatch: false);
        RigidBody3D endWeight = Assert.IsType<RigidBody3D>(sceneRoot.GetNodeOrNull(MirrorRoomEndWeightPath), exactMatch: false);
        Marker3D endWeightAttachment = Assert.IsType<Marker3D>(sceneRoot.GetNodeOrNull(MirrorRoomEndWeightAttachmentPath), exactMatch: false);

        Assert.Equal(32, linksContainer.GetChildCount());
        Assert.Equal(62, jointsContainer.GetChildCount());
        Assert.Equal(ConeTwistJointTypeValue, chain.Get("LinkJointType").AsInt32());

        RigidBody3D contactBody = new()
        {
            Name = "MirrorRoomChainContactBody",
            CollisionLayer = 2,
            CollisionMask = 11,
            GravityScale = 0.0f,
            Mass = 0.35f,
            LinearDamp = 0.05f,
            AngularDamp = 0.05f,
            GlobalPosition = endWeight.GlobalPosition + new Vector3(0.18f, 0.05f, -0.16f),
        };
        contactBody.AddChild(new CollisionShape3D
        {
            Name = "CollisionShape3D",
            Shape = new SphereShape3D { Radius = 0.055f },
        });
        sceneRoot.GetNode<Node3D>("Items").AddChild(contactBody);
        await WaitForPhysicsFramesAsync(sceneTree, 2);

        Vector3 startWeightPosition = endWeight.GlobalPosition;
        float longestObservedWeightTravel = 0f;
        float longestObservedContactTravel = 0f;
        float worstObservedStartAttachmentGap = 0f;
        float worstObservedEndAttachmentGap = 0f;
        float worstObservedInterLinkGap = 0f;

        endWeight.ApplyCentralImpulse(new Vector3(0.65f, 0.05f, -0.35f));
        contactBody.ApplyCentralImpulse(new Vector3(-0.35f, -0.02f, 0.28f));

        int stabilitySampleCount = (int)(LongRunSimulationSeconds / StabilitySampleIntervalSeconds);
        for (int sampleIndex = 0; sampleIndex < stabilitySampleCount; sampleIndex++)
        {
            await WaitForPhysicsSecondsAsync(sceneTree, StabilitySampleIntervalSeconds);

            float elapsedSeconds = (sampleIndex + 1) * (float)StabilitySampleIntervalSeconds;
            longestObservedWeightTravel = Math.Max(longestObservedWeightTravel, endWeight.GlobalPosition.DistanceTo(startWeightPosition));
            longestObservedContactTravel = Math.Max(longestObservedContactTravel, contactBody.GlobalPosition.DistanceTo(startWeightPosition));

            float startAttachmentGap = startAnchorAttachment.GlobalPosition.DistanceTo(chainStartAttachment.GlobalPosition);
            float endAttachmentGap = endWeightAttachment.GlobalPosition.DistanceTo(chainEndAttachment.GlobalPosition);
            worstObservedStartAttachmentGap = Math.Max(worstObservedStartAttachmentGap, startAttachmentGap);
            worstObservedEndAttachmentGap = Math.Max(worstObservedEndAttachmentGap, endAttachmentGap);

            Assert.True(
                startAttachmentGap <= MirrorRoomAttachmentToleranceMetres,
                $"Mirror-room start attachment should remain locked. Elapsed: {elapsedSeconds:F1}s, gap: {startAttachmentGap:F4} m.");
            Assert.True(
                endAttachmentGap <= MirrorRoomAttachmentToleranceMetres,
                $"Mirror-room end attachment should remain locked. Elapsed: {elapsedSeconds:F1}s, gap: {endAttachmentGap:F4} m.");

            for (int linkIndex = 0; linkIndex < linksContainer.GetChildCount() - 1; linkIndex++)
            {
                var backAttachment = (Vector3)chain.Call("GetLinkBackAttachmentGlobalPosition", linkIndex);
                var nextFrontAttachment = (Vector3)chain.Call("GetLinkFrontAttachmentGlobalPosition", linkIndex + 1);
                float attachmentGap = backAttachment.DistanceTo(nextFrontAttachment);
                worstObservedInterLinkGap = Math.Max(worstObservedInterLinkGap, attachmentGap);

                Assert.True(
                    attachmentGap <= MirrorRoomInterLinkAttachmentToleranceMetres,
                    $"Mirror-room 32-link chain should remain connected. Elapsed: {elapsedSeconds:F1}s, links {linkIndex + 1}/{linkIndex + 2}, gap: {attachmentGap:F4} m.");
            }
        }

        Assert.True(
            longestObservedWeightTravel >= MinimumWeightTravelMetres,
            $"Mirror-room end weight should move under representative impulse. Maximum observed travel: {longestObservedWeightTravel:F4} m.");
        Assert.True(
            longestObservedContactTravel >= MinimumWeightTravelMetres,
            $"Mirror-room contact body should move into the chain test volume. Maximum observed travel: {longestObservedContactTravel:F4} m.");
        Assert.True(worstObservedStartAttachmentGap <= MirrorRoomAttachmentToleranceMetres);
        Assert.True(worstObservedEndAttachmentGap <= MirrorRoomAttachmentToleranceMetres);
        Assert.True(worstObservedInterLinkGap <= MirrorRoomInterLinkAttachmentToleranceMetres);

        GD.Print(
            $"ITEM-001 mirror-room 32-link metrics: duration={LongRunSimulationSeconds:F0}s, max_weight_travel={longestObservedWeightTravel:F4} m, " +
            $"max_contact_travel={longestObservedContactTravel:F4} m, max_start_gap={worstObservedStartAttachmentGap:F4} m, " +
            $"max_end_gap={worstObservedEndAttachmentGap:F4} m, max_inter_link_gap={worstObservedInterLinkGap:F4} m.");
    }

    /// <summary>
    /// Samples the authored mirror-room chain under sustained lengthwise tension, including visual end gaps and orientation interlock metrics.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PhysicsChain_MirrorRoomThirtyTwoLinkChain_DiagnosesSustainedLengthwiseTension()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForPhysicsFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(MirrorRoomScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForPhysicsFramesAsync(sceneTree, InitialisationFrames);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected mirror-room scene to become current scene.");

        Node3D chain = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(MirrorRoomChainNodePath), exactMatch: false);
        Node linksContainer = chain.GetNode(LinksContainerPath);
        Node jointsContainer = chain.GetNode(JointsContainerPath);
        Marker3D chainStartAttachment = Assert.IsType<Marker3D>(chain.GetNodeOrNull(StartAttachmentPointPath), exactMatch: false);
        Marker3D chainEndAttachment = Assert.IsType<Marker3D>(chain.GetNodeOrNull("Links/Link32/EndAttachmentPoint"), exactMatch: false);
        Marker3D startAnchorAttachment = Assert.IsType<Marker3D>(sceneRoot.GetNodeOrNull(MirrorRoomStartAnchorAttachmentPath), exactMatch: false);
        RigidBody3D endWeight = Assert.IsType<RigidBody3D>(sceneRoot.GetNodeOrNull(MirrorRoomEndWeightPath), exactMatch: false);
        Marker3D endWeightAttachment = Assert.IsType<Marker3D>(sceneRoot.GetNodeOrNull(MirrorRoomEndWeightAttachmentPath), exactMatch: false);

        Assert.Equal(32, linksContainer.GetChildCount());
        Assert.Equal(62, jointsContainer.GetChildCount());
        Assert.Equal(ConeTwistJointTypeValue, chain.Get("LinkJointType").AsInt32());

        var diagnostics = ChainStretchDiagnostics.Create(
            chain,
            linksContainer,
            startAnchorAttachment,
            chainStartAttachment,
            endWeightAttachment,
            chainEndAttachment);

        Assert.True(diagnostics.UnexpectedLinkPropertySummary.Length == 0, diagnostics.UnexpectedLinkPropertySummary);

        ChainInterlockMetrics interlockMetrics = MeasureInitialInterlockMetrics(chain, linksContainer, jointsContainer);
        string interlockReport = BuildInterlockReport(interlockMetrics);
        Assert.True(interlockMetrics.MaxAbsConsecutiveXAxisDot <= 0.05f, interlockReport);
        Assert.True(interlockMetrics.MinAbsConsecutiveZAxisDot >= 0.99f, interlockReport);

        Vector3 initialStart = chainStartAttachment.GlobalPosition;
        Vector3 initialEnd = chainEndAttachment.GlobalPosition;
        Vector3 tensionDirection = (initialEnd - initialStart).Normalized();
        float initialEndpointSpan = initialStart.DistanceTo(initialEnd);
        float specMaximumEndpointSpanGrowth = initialEndpointSpan * SustainedTensionMaxRestLengthGrowthRatio;
        float configuredMaximumEndpointSpanGrowth = Math.Min(MirrorRoomTensionMaxEndpointSpanGrowthMetres, specMaximumEndpointSpanGrowth);
        float maxEndpointSpan = initialEndpointSpan;
        float maxProjectedEndpointSpan = initialEndpointSpan;
        float maxProjectedAdjacentOriginDistance = 0f;
        float maxAdjacentOriginDistance = 0f;
        float maxEndWeightSpeed = 0f;
        const int tensionFrameCount = 720;
        const float tensionForceNewtons = 8.0f;

        endWeight.Mass = SustainedTensionEndWeightMassKg;

        for (int frame = 0; frame < tensionFrameCount; frame++)
        {
            endWeight.ApplyCentralForce(tensionDirection * tensionForceNewtons);
            await WaitForNextPhysicsFrameAsync(sceneTree);

            diagnostics.SampleFrame();
            Vector3 currentStart = chainStartAttachment.GlobalPosition;
            Vector3 currentEnd = chainEndAttachment.GlobalPosition;
            maxEndpointSpan = Math.Max(maxEndpointSpan, currentStart.DistanceTo(currentEnd));
            maxProjectedEndpointSpan = Math.Max(maxProjectedEndpointSpan, (currentEnd - currentStart).Dot(tensionDirection));
            maxEndWeightSpeed = Math.Max(maxEndWeightSpeed, endWeight.LinearVelocity.Length());

            for (int linkIndex = 0; linkIndex < linksContainer.GetChildCount() - 1; linkIndex++)
            {
                RigidBody3D currentLink = Assert.IsType<RigidBody3D>(linksContainer.GetChild(linkIndex), exactMatch: false);
                RigidBody3D nextLink = Assert.IsType<RigidBody3D>(linksContainer.GetChild(linkIndex + 1), exactMatch: false);
                Vector3 originDelta = nextLink.GlobalPosition - currentLink.GlobalPosition;
                maxProjectedAdjacentOriginDistance = Math.Max(maxProjectedAdjacentOriginDistance, Mathf.Abs(originDelta.Dot(tensionDirection)));
                maxAdjacentOriginDistance = Math.Max(maxAdjacentOriginDistance, originDelta.Length());
            }
        }

        float priorPinJointEndpointSpanGrowth = PriorPinJointTensionMaxEndpointSpanMetres - PriorPinJointTensionInitialEndpointSpanMetres;

        string report = "ITEM-001 mirror-room lengthwise tension diagnostic: "
            + $"frames={tensionFrameCount}, end_weight_mass={endWeight.Mass:F1} kg, additional_force={tensionForceNewtons:F1} N, "
            + $"links={linksContainer.GetChildCount()}, joints={jointsContainer.GetChildCount()}, "
            + $"link_joint_type={chain.Get("LinkJointType").AsInt32()}, link_swing_span={(float)chain.Get("LinkJointSwingSpan"):F3} rad, link_twist_span={(float)chain.Get("LinkJointTwistSpan"):F3} rad, "
            + $"span_guard_enabled={(bool)chain.Get("EnableEndpointSpanGuard")}, max_endpoint_span_growth_setting={(float)chain.Get("MaxEndpointSpanGrowth"):F4} m, "
            + $"initial_endpoint_span={initialEndpointSpan:F4} m, max_endpoint_span={maxEndpointSpan:F4} m, "
            + $"endpoint_span_growth={maxEndpointSpan - initialEndpointSpan:F4} m, configured_growth_limit={configuredMaximumEndpointSpanGrowth:F4} m, "
            + $"spec_15_percent_limit={specMaximumEndpointSpanGrowth:F4} m, prior_pin_growth={priorPinJointEndpointSpanGrowth:F4} m, "
            + $"max_projected_endpoint_span={maxProjectedEndpointSpan:F4} m, max_projected_adjacent_origin_distance={maxProjectedAdjacentOriginDistance:F4} m, "
            + $"max_adjacent_origin_distance={maxAdjacentOriginDistance:F4} m, max_adjacent_visual_end_gap={diagnostics.MaxAdjacentAttachmentGap:F4} m, "
            + $"max_endpoint_attachment_gap={diagnostics.MaxEndpointAttachmentGap:F4} m, max_end_weight_speed={maxEndWeightSpeed:F4} m/s, "
            + interlockReport;

        GD.Print(report);

        Assert.True(maxEndWeightSpeed > 0.05f, report + "\nThe end weight did not move enough to apply sustained lengthwise tension.");
        Assert.True(
            diagnostics.MaxAdjacentAttachmentGap <= 0.12f,
            report + "\nAdjacent visual link-end gap exceeded sustained-tension diagnostic tolerance 0.120 m.");
        Assert.True(
            diagnostics.MaxEndpointAttachmentGap <= 0.10f,
            report + "\nEndpoint attachment gap exceeded sustained-tension diagnostic tolerance 0.100 m.");
        Assert.True(
            maxEndpointSpan - initialEndpointSpan <= configuredMaximumEndpointSpanGrowth,
            report + $"\nEndpoint span growth under a {SustainedTensionEndWeightMassKg:F1} kg end weight should be bounded to <= {configuredMaximumEndpointSpanGrowth:F3} m, below the 15% rest-length spec limit {specMaximumEndpointSpanGrowth:F4} m and the prior PinJoint3D diagnostic growth {priorPinJointEndpointSpanGrowth:F4} m.");
        Assert.True(
            maxEndpointSpan < PriorPinJointTensionMaxEndpointSpanMetres,
            report + $"\nRotation-limited default should stay below the prior unconstrained PinJoint3D max endpoint span {PriorPinJointTensionMaxEndpointSpanMetres:F4} m.");
    }

    private static void AssertConeTwistParameters(ConeTwistJoint3D joint, float expectedSwingSpan, float expectedTwistSpan, Node sourceChain)
    {
        Assert.Equal(expectedSwingSpan, joint.GetParam(ConeTwistJoint3D.Param.SwingSpan), 3);
        Assert.Equal(expectedTwistSpan, joint.GetParam(ConeTwistJoint3D.Param.TwistSpan), 3);
        Assert.Equal((float)sourceChain.Get("ConeTwistBias"), joint.GetParam(ConeTwistJoint3D.Param.Bias), 3);
        Assert.Equal((float)sourceChain.Get("ConeTwistSoftness"), joint.GetParam(ConeTwistJoint3D.Param.Softness), 3);
        Assert.Equal((float)sourceChain.Get("ConeTwistRelaxation"), joint.GetParam(ConeTwistJoint3D.Param.Relaxation), 3);
    }

    private static void AssertBodyCanCollideWithPlayerHands(PhysicsBody3D body)
    {
        const uint dynamicInteractionLayer = 2;
        const uint environmentLayer = 1;

        Assert.True((body.CollisionLayer & dynamicInteractionLayer) != 0, $"{body.Name} should use the dedicated dynamic interaction layer.");
        Assert.True((body.CollisionMask & environmentLayer) != 0, $"{body.Name} collision mask should retain world collision.");
    }

    private static ChainInterlockMetrics MeasureInitialInterlockMetrics(Node3D chain, Node linksContainer, Node jointsContainer)
    {
        float maxAbsConsecutiveXAxisDot = 0f;
        float minAbsConsecutiveZAxisDot = 1f;
        float maxJointToCurrentBackGap = 0f;
        float maxJointToNextFrontGap = 0f;

        for (int linkIndex = 0; linkIndex < linksContainer.GetChildCount() - 1; linkIndex++)
        {
            RigidBody3D currentLink = Assert.IsType<RigidBody3D>(linksContainer.GetChild(linkIndex), exactMatch: false);
            RigidBody3D nextLink = Assert.IsType<RigidBody3D>(linksContainer.GetChild(linkIndex + 1), exactMatch: false);
            maxAbsConsecutiveXAxisDot = Math.Max(
                maxAbsConsecutiveXAxisDot,
                Mathf.Abs(currentLink.GlobalTransform.Basis.X.Normalized().Dot(nextLink.GlobalTransform.Basis.X.Normalized())));
            minAbsConsecutiveZAxisDot = Math.Min(
                minAbsConsecutiveZAxisDot,
                Mathf.Abs(currentLink.GlobalTransform.Basis.Z.Normalized().Dot(nextLink.GlobalTransform.Basis.Z.Normalized())));

            int primaryJointIndex = jointsContainer.GetChildCount() == (linksContainer.GetChildCount() - 1) * 2
                ? linkIndex * 2
                : linkIndex;
            Node3D joint = Assert.IsType<Node3D>(jointsContainer.GetChild(primaryJointIndex), exactMatch: false);
            var currentBack = (Vector3)chain.Call("GetLinkBackAttachmentGlobalPosition", linkIndex);
            var nextFront = (Vector3)chain.Call("GetLinkFrontAttachmentGlobalPosition", linkIndex + 1);
            maxJointToCurrentBackGap = Math.Max(maxJointToCurrentBackGap, joint.GlobalPosition.DistanceTo(currentBack));
            maxJointToNextFrontGap = Math.Max(maxJointToNextFrontGap, joint.GlobalPosition.DistanceTo(nextFront));
        }

        return new ChainInterlockMetrics(
            maxAbsConsecutiveXAxisDot,
            minAbsConsecutiveZAxisDot,
            maxJointToCurrentBackGap,
            maxJointToNextFrontGap);
    }

    private static string BuildInterlockReport(ChainInterlockMetrics metrics)
        => $"interlock(max_abs_consecutive_x_axis_dot={metrics.MaxAbsConsecutiveXAxisDot:F4}, "
            + $"min_abs_consecutive_z_axis_dot={metrics.MinAbsConsecutiveZAxisDot:F4}, "
            + $"max_joint_to_current_back_gap={metrics.MaxJointToCurrentBackGap:F4} m, "
            + $"max_joint_to_next_front_gap={metrics.MaxJointToNextFrontGap:F4} m)";

    private readonly record struct ChainInterlockMetrics(
        float MaxAbsConsecutiveXAxisDot,
        float MinAbsConsecutiveZAxisDot,
        float MaxJointToCurrentBackGap,
        float MaxJointToNextFrontGap);

    private sealed class ChainStretchDiagnostics
    {
        private const float ExpectedLinkMass = 0.08f;
        private const float ExpectedLinkLinearDamping = 0.25f;
        private const float ExpectedLinkAngularDamping = 0.45f;

        private readonly Node3D _chain;
        private readonly Node _linksContainer;
        private readonly Marker3D _startAnchorAttachment;
        private readonly Marker3D _chainStartAttachment;
        private readonly Marker3D _endWeightAttachment;
        private readonly Marker3D _chainEndAttachment;

        private ChainStretchDiagnostics(
            Node3D chain,
            Node linksContainer,
            Marker3D startAnchorAttachment,
            Marker3D chainStartAttachment,
            Marker3D endWeightAttachment,
            Marker3D chainEndAttachment,
            string unexpectedLinkPropertySummary)
        {
            _chain = chain;
            _linksContainer = linksContainer;
            _startAnchorAttachment = startAnchorAttachment;
            _chainStartAttachment = chainStartAttachment;
            _endWeightAttachment = endWeightAttachment;
            _chainEndAttachment = chainEndAttachment;
            UnexpectedLinkPropertySummary = unexpectedLinkPropertySummary;
        }

        public float MaxAdjacentAttachmentGap
        {
            get; private set;
        }

        public float MaxEndpointAttachmentGap
        {
            get; private set;
        }

        public string UnexpectedLinkPropertySummary
        {
            get;
        }

        public static ChainStretchDiagnostics Create(
            Node3D chain,
            Node linksContainer,
            Marker3D startAnchorAttachment,
            Marker3D chainStartAttachment,
            Marker3D endWeightAttachment,
            Marker3D chainEndAttachment)
            => new(
                chain,
                linksContainer,
                startAnchorAttachment,
                chainStartAttachment,
                endWeightAttachment,
                chainEndAttachment,
                BuildUnexpectedLinkPropertySummary(linksContainer));

        public void SampleFrame()
        {
            float startEndpointGap = _startAnchorAttachment.GlobalPosition.DistanceTo(_chainStartAttachment.GlobalPosition);
            float endEndpointGap = _endWeightAttachment.GlobalPosition.DistanceTo(_chainEndAttachment.GlobalPosition);
            MaxEndpointAttachmentGap = Math.Max(MaxEndpointAttachmentGap, Math.Max(startEndpointGap, endEndpointGap));

            for (int linkIndex = 0; linkIndex < _linksContainer.GetChildCount() - 1; linkIndex++)
            {
                var backAttachment = (Vector3)_chain.Call("GetLinkBackAttachmentGlobalPosition", linkIndex);
                var nextFrontAttachment = (Vector3)_chain.Call("GetLinkFrontAttachmentGlobalPosition", linkIndex + 1);
                float attachmentGap = backAttachment.DistanceTo(nextFrontAttachment);
                MaxAdjacentAttachmentGap = Math.Max(MaxAdjacentAttachmentGap, attachmentGap);
            }
        }

        private static string BuildUnexpectedLinkPropertySummary(Node linksContainer)
        {
            StringBuilder builder = new();
            foreach (Node child in linksContainer.GetChildren())
            {
                RigidBody3D link = Assert.IsType<RigidBody3D>(child, exactMatch: false);
                if (link.CanSleep || link.Sleeping || !Mathf.IsEqualApprox(link.Mass, ExpectedLinkMass)
                    || !Mathf.IsEqualApprox(link.LinearDamp, ExpectedLinkLinearDamping) || !Mathf.IsEqualApprox(link.AngularDamp, ExpectedLinkAngularDamping))
                {
                    _ = builder.Append(link.Name)
                        .Append("(can_sleep=").Append(link.CanSleep)
                        .Append(", sleeping=").Append(link.Sleeping)
                        .Append(", mass=").Append(link.Mass.ToString("F3"))
                        .Append(", linear_damp=").Append(link.LinearDamp.ToString("F3"))
                        .Append(", angular_damp=").Append(link.AngularDamp.ToString("F3"))
                        .Append("); ");
                }
            }

            return builder.ToString();
        }
    }
}
