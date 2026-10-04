using AlleyCat.IntegrationTests.Support;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Control;

/// <summary>
/// CTRL-001 movement-frame reproduction coverage: sustained cardinal holds and yaw holds driven through the
/// production-equivalent input path (XR controller events → <c>PlayerController</c> → <c>Move</c>/<c>Rotate</c>)
/// against fixture-owned animation content wired with the production rig topology — a root-motion reference
/// node rotated 180 degrees about Y relative to the actor, mirroring the production visual-rig container
/// (<c>reference_female_player.tscn</c> binds <c>RootMotionReference = ../Female</c> whose container basis is
/// that 180-degree rotation).
/// </summary>
/// <remarks>
/// <para>
/// Expected actor-semantic frame (normative reference: <c>NPCNavigationSemanticFrame</c>): forward is −Z and
/// right is +X in the actor's own yaw frame, independent of the imported visual orientation. These tests
/// assert signed world displacement against independently placed world markers and the heading-derived
/// expected direction, so they fail when semantic translation is converted through the visual-rig basis
/// instead of the actor frame.
/// </para>
/// <para>
/// Displacement assertions are deliberately tolerant of the documented Jolt wall-clock nondeterminism
/// (net displacement sign, direction within 60 degrees, and a conservative minimum distance — never exact
/// distances or per-tick positions). Physics and animation run automatically through awaited physics frames.
/// </para>
/// </remarks>
public sealed class PlayerLocomotionMovementFrameIntegrationTests
{
    private const float ProductionRigYawRadians = Mathf.Pi;
    private const int SettleTicks = 30;
    private const int HoldTicks = 150;
    private const int ReleaseTailTicks = 15;
    private const float MinimumNetDisplacementMetres = 0.3f;
    private const float DirectionDotFloor = 0.5f;

    [Fact]
    public async Task ForwardHold_ProductionRigBasis_MovesAlongActorForward()
        => await AssertSustainedHoldDirectionAsync(
            "wiring-forward-180",
            rigYawRadians: ProductionRigYawRadians,
            actorHeadingRadians: 0f,
            input: new Vector2(0f, 1f));

    [Fact]
    public async Task BackwardHold_ProductionRigBasis_MovesAlongActorBackward()
        => await AssertSustainedHoldDirectionAsync(
            "wiring-backward-180",
            rigYawRadians: ProductionRigYawRadians,
            actorHeadingRadians: 0f,
            input: new Vector2(0f, -1f));

    [Fact]
    public async Task RightHold_ProductionRigBasis_MovesAlongActorRight()
        => await AssertSustainedHoldDirectionAsync(
            "wiring-right-180",
            rigYawRadians: ProductionRigYawRadians,
            actorHeadingRadians: 0f,
            input: new Vector2(1f, 0f));

    [Fact]
    public async Task LeftHold_ProductionRigBasis_MovesAlongActorLeft()
        => await AssertSustainedHoldDirectionAsync(
            "wiring-left-180",
            rigYawRadians: ProductionRigYawRadians,
            actorHeadingRadians: 0f,
            input: new Vector2(-1f, 0f));

    [Fact]
    public async Task ForwardHold_RotatedActorHeading_MovesAlongRotatedActorForward()
        => await AssertSustainedHoldDirectionAsync(
            "wiring-forward-180-heading90",
            rigYawRadians: ProductionRigYawRadians,
            actorHeadingRadians: Mathf.Pi / 2f,
            input: new Vector2(0f, 1f));

    /// <summary>
    /// Parent-transform coverage (CTRL-001 TR12): with the actor parented under a 90-degree-rotated
    /// intermediate node and the production 180-degree visual-rig basis, a forward hold must move along the
    /// actor's world forward — the parent-rotated semantic direction. The conversion derives the world frame
    /// from the body's global basis, so a non-identity parent transform must compose into the movement
    /// direction rather than being ignored or replaced by the rig basis.
    /// </summary>
    [Fact]
    public async Task ForwardHold_RotatedParentTransform_MovesAlongWorldActorForward()
        => await AssertSustainedHoldDirectionAsync(
            "wiring-forward-180-parent90",
            rigYawRadians: ProductionRigYawRadians,
            actorHeadingRadians: 0f,
            input: new Vector2(0f, 1f),
            parentYawRadians: Mathf.Pi / 2f);

    /// <summary>
    /// Identity-basis control: with an unrotated root-motion reference the same forward hold must move along
    /// actor forward. This case passes today and proves the 180-degree cases fail specifically because of the
    /// visual-rig basis in the semantic-velocity conversion, not because of the harness or the input path.
    /// </summary>
    [Fact]
    public async Task ForwardHold_IdentityRigBasis_MovesAlongActorForward()
        => await AssertSustainedHoldDirectionAsync(
            "wiring-forward-identity",
            rigYawRadians: 0f,
            actorHeadingRadians: 0f,
            input: new Vector2(0f, 1f));

    /// <summary>
    /// Yaw chain measurement (stage R3 evidence): a held +X right-stick sample must produce a negative
    /// response-model yaw rate (ANIM-002 response-coordinate convention: positive turn requests actor-right,
    /// which is a negative local-Y yaw rate), a clockwise (negative) accumulated actor yaw, and a viewpoint
    /// that turns toward the actor's initial right. Stationary turning is delivered through the authored
    /// turn-in-place pivots (CTRL-001 TR11: the PlayerTurnInPlaceL/R states playing the authored pivot
    /// clips); these tests pin the yaw sign, the sustained rate, and the stationary net through the authored
    /// root rotation alone.
    /// </summary>
    [Fact]
    public async Task RightStickHold_TurnsActorTowardItsRight()
        => await AssertSustainedYawDirectionAsync(
            "wiring-yaw-right",
            stick: new Vector2(1f, 0f),
            expectedYawSign: -1f);

    [Fact]
    public async Task LeftStickHold_TurnsActorTowardItsLeft()
        => await AssertSustainedYawDirectionAsync(
            "wiring-yaw-left",
            stick: new Vector2(-1f, 0f),
            expectedYawSign: 1f);

    private static async Task AssertSustainedHoldDirectionAsync(
        string fixtureName,
        float rigYawRadians,
        float actorHeadingRadians,
        Vector2 input,
        float parentYawRadians = 0f)
    {
        SceneTree sceneTree = GetSceneTree();
        await using PlayerLocomotionProductionWiringHarness harness =
            await PlayerLocomotionProductionWiringHarness.CreateAsync(
                sceneTree,
                fixtureName,
                rigYawRadians,
                actorHeadingRadians,
                parentYawRadians: parentYawRadians);

        Assert.True(harness.ControllerBound, "Expected the PlayerController to bind to the fake XR runtime.");

        // Independently placed world marker: the expected direction is derived from the authored heading and
        // parent rotation by trigonometry, never from the actor's or rig's runtime bases.
        float worldHeadingRadians = actorHeadingRadians + parentYawRadians;
        Vector3 expectedDirection = SemanticInputToWorld(worldHeadingRadians, input);
        Vector3 spawn = harness.SpawnPosition;
        Marker3D marker = harness.PlaceWorldMarker("ExpectedDirectionMarker", spawn + (expectedDirection * 5f));
        Vector3 markerDirection = (marker.GlobalPosition - spawn).Normalized();
        Assert.True(
            markerDirection.Dot(expectedDirection) >= 0.999f,
            $"world marker direction {markerDirection} does not match the expected direction {expectedDirection}");

        _ = await harness.RunAsync(sceneTree, SettleTicks);
        ProductionWiringSample before = harness.CaptureSample();
        harness.PushMovement(input);
        List<ProductionWiringSample> hold = await harness.RunAsync(sceneTree, HoldTicks);
        harness.ReleaseSticks();
        _ = await harness.RunAsync(sceneTree, ReleaseTailTicks);

        Vector3 net = harness.CaptureSample().ActorPosition - before.ActorPosition;
        float netLength = net.Length();
        float directionDot = netLength > 0.0001f ? net.Normalized().Dot(expectedDirection) : 0f;

        // Response-model chain evidence: the controller-owned response velocity must already agree with the
        // input in actor-semantic coordinates — a failure here would indict the model, not the frame.
        Vector2 meanResponse = Mean(hold, sample => sample.ResponseVelocity);
        float responseAgreement = meanResponse.Length() > 0.0001f
            ? meanResponse.Normalized().Dot(input.Normalized())
            : 0f;
        Assert.True(
            responseAgreement > 0.7f,
            $"response-model velocity {meanResponse} disagrees with held input {input}; the model, not the frame, is failing");

        Assert.True(
            netLength >= MinimumNetDisplacementMetres,
            $"{fixtureName}: sustained hold {input} produced only {netLength:0.000} m of net displacement "
            + $"(response mean {meanResponse:0.000}); the body never meaningfully moved");
        Assert.True(
            directionDot >= DirectionDotFloor,
            $"{fixtureName}: sustained hold {input} at heading {actorHeadingRadians:0.00} rad "
            + $"(parent {parentYawRadians:0.00} rad, world heading {worldHeadingRadians:0.00} rad) moved the actor "
            + $"by net {net:0.000} (length {netLength:0.000} m) — direction dot vs expected {expectedDirection} "
            + $"is {directionDot:0.000}; semantic translation is frame-inverted "
            + $"(controller sample {hold[0].MovementSample:0.###}, response {meanResponse:0.###})");
    }

    private static async Task AssertSustainedYawDirectionAsync(string fixtureName, Vector2 stick, float expectedYawSign)
    {
        SceneTree sceneTree = GetSceneTree();
        await using PlayerLocomotionProductionWiringHarness harness =
            await PlayerLocomotionProductionWiringHarness.CreateAsync(sceneTree, fixtureName, ProductionRigYawRadians);

        Assert.True(harness.ControllerBound, "Expected the PlayerController to bind to the fake XR runtime.");

        _ = await harness.RunAsync(sceneTree, SettleTicks);
        ProductionWiringSample before = harness.CaptureSample();
        Vector3 initialRight = SemanticInputToWorld(0f, new Vector2(1f, 0f));
        harness.PushRotation(stick);
        List<ProductionWiringSample> hold = await harness.RunAsync(sceneTree, 80);
        harness.ReleaseSticks();
        _ = await harness.RunAsync(sceneTree, ReleaseTailTicks);

        ProductionWiringSample after = harness.CaptureSample();
        float accumulatedYaw = 0f;
        float previous = before.ActorYawRadians;
        foreach (ProductionWiringSample sample in hold)
        {
            accumulatedYaw += Mathf.AngleDifference(previous, sample.ActorYawRadians);
            previous = sample.ActorYawRadians;
        }

        // Direction probe early in the turn: ~0.33 s in, the slew ramp has settled and the actor has swept
        // roughly 30 degrees, comfortably before the 90-degree point where a dot-against-initial-right would
        // stop discriminating the turn direction. A turn toward the actor's right sweeps the forward vector
        // toward +initial-right (positive dot) while the accumulated yaw is negative, and vice versa.
        ProductionWiringSample early = hold[Math.Min(20, hold.Count - 1)];
        float earlyViewpointDotRight = early.ViewpointForwardWorld.Dot(initialRight);
        float earlyForwardDotRight = early.ActorForwardWorld.Dot(initialRight);
        float expectedForwardDotRightSign = -expectedYawSign;
        var settled = hold.Skip(20).ToList();
        float meanYawRate = settled.Count > 0 ? settled.Average(sample => sample.ResponseYawRate) : 0f;
        float stationaryNet = (after.ActorPosition - before.ActorPosition).Length();

        // Full chain: controller sample → response-model yaw rate → RotateY → actor heading → viewpoint.
        Assert.True(
            Mathf.Sign(accumulatedYaw) == expectedYawSign && Mathf.Abs(accumulatedYaw) > 0.5f,
            $"{fixtureName}: rotation stick {stick} accumulated {Mathf.RadToDeg(accumulatedYaw):0.0}° of yaw "
            + $"(expected sign {expectedYawSign}, magnitude > 28°); chain: controller sample {hold[0].RotationSample:0.###}, "
            + $"mean response yaw rate {meanYawRate:0.00} rad/s, early viewpoint·initial-right {earlyViewpointDotRight:0.000}");
        Assert.True(
            Mathf.Sign(meanYawRate) == expectedYawSign && Mathf.Abs(meanYawRate) > 1f,
            $"{fixtureName}: response-model yaw rate {meanYawRate:0.00} rad/s does not match the stick demand "
            + $"{stick:0.###} with the expected sign {expectedYawSign}");
        Assert.True(
            earlyForwardDotRight * expectedForwardDotRightSign > 0.2f,
            $"{fixtureName}: early-turn actor forward {early.ActorForwardWorld:0.000} did not tip toward the "
            + $"turned side (forward·initial-right {earlyForwardDotRight:0.000}, expected sign "
            + $"{expectedForwardDotRightSign})");
        Assert.True(
            earlyViewpointDotRight * expectedForwardDotRightSign > 0.2f,
            $"{fixtureName}: early-turn viewpoint forward {early.ViewpointForwardWorld:0.000} did not follow the "
            + $"actor yaw (viewpoint·initial-right {earlyViewpointDotRight:0.000}, expected sign "
            + $"{expectedForwardDotRightSign})");
        Assert.True(
            stationaryNet < 0.05f,
            $"{fixtureName}: stationary turn translated the body by {stationaryNet:0.000} m");
    }

    /// <summary>
    /// Heading-derived semantic-to-world conversion: response coordinates map X lateral (right) and Y forward
    /// onto the actor's yaw frame, where forward is −Z and right is +X at heading zero. This is computed from
    /// the authored heading by trigonometry so it never consults the actor's or rig's runtime bases.
    /// </summary>
    private static Vector3 SemanticInputToWorld(float headingRadians, Vector2 input)
        => new(
            (input.X * Mathf.Cos(headingRadians)) - (input.Y * Mathf.Sin(headingRadians)),
            0f,
            (-input.X * Mathf.Sin(headingRadians)) - (input.Y * Mathf.Cos(headingRadians)));

    private static Vector2 Mean(IReadOnlyList<ProductionWiringSample> samples, Func<ProductionWiringSample, Vector2> selector)
    {
        Vector2 total = Vector2.Zero;
        foreach (ProductionWiringSample sample in samples)
        {
            total += selector(sample);
        }

        return samples.Count > 0 ? total / samples.Count : Vector2.Zero;
    }
}
