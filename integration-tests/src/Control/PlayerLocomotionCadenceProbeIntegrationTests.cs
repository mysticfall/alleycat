using System.Globalization;
using AlleyCat.Control.Locomotion;
using AlleyCat.IntegrationTests.Support;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Control;

/// <summary>
/// CTRL-001 realised-motion cadence probes: recording suites that measure the response-model velocity, the
/// realised actor velocity, the evaluated animation state, the travel playback-rate parameter, and
/// representative foot-bone world positions across low-magnitude holds, from-rest starts, braking exits, and
/// reversals. The assertions state the contract the movement-coupling fix must achieve (cadence tracks
/// realised speed; the walking pose progresses during sustained travel; no full-speed opposite-direction
/// walk during reversal braking) with tolerances wide enough to admit any honest realised-speed coupling
/// while failing today's stick-magnitude scheduling.
/// </summary>
/// <remarks>
/// All probes run against fixture-owned synthetic animation content with production-equivalent wiring
/// (180-degree rig basis, XR controller input path, automatic physics/animation processing via awaited
/// physics frames). Production identifiers and timings stay out of the oracles: the unit-speed measure comes
/// from the fixture's own declared catalogue content.
/// </remarks>
public sealed class PlayerLocomotionCadenceProbeIntegrationTests
{
    private const float ProductionRigYawRadians = Mathf.Pi;
    private const int SettleTicks = 30;
    private const float CadenceTolerance = 0.4f;

    /// <summary>
    /// Sustained quarter-stick hold: once travel is established the travel playback rate must be coupled to
    /// the realised body speed (rate ≈ realised speed / declared unit speed) rather than to the raw stick
    /// magnitude. Today the rate is Lerp(minimum, maximum, magnitude), which stays near the stick-rate ceiling
    /// while the body crawls — the asserted contract fails until cadence tracks realised motion.
    /// </summary>
    [Fact]
    public async Task SustainedQuarterStickHold_TravelCadenceTracksRealisedSpeed()
    {
        SceneTree sceneTree = GetSceneTree();
        await using PlayerLocomotionProductionWiringHarness harness =
            await PlayerLocomotionProductionWiringHarness.CreateAsync(sceneTree, "cadence-quarter-hold", ProductionRigYawRadians);

        _ = await harness.RunAsync(sceneTree, SettleTicks);
        harness.PushMovement(new Vector2(0f, 0.25f));
        List<ProductionWiringSample> hold = await harness.RunAsync(sceneTree, 210);
        harness.ReleaseSticks();
        _ = await harness.RunAsync(sceneTree, 30);

        float unitSpeed = harness.DeclaredTravelUnitSpeed;
        List<ProductionWiringSample> sustained = SustainedTravelWindow(hold, minimumTicks: 60, warmupSeconds: 0.6f);
        Assert.True(sustained.Count >= 60, $"expected a sustained travel window, got {sustained.Count} samples");

        float meanRate = sustained.Average(sample => sample.TravelPlaybackRate);
        float meanSpeed = sustained.Average(sample => sample.ActorPlanarVelocity.Length());
        float matchedRate = meanSpeed / unitSpeed;
        float error = Mathf.Abs(meanRate - matchedRate);

        // Walking-pose progression contract (must keep holding after the fix): feet displace and the recorded
        // play positions advance while the body travels.
        float footTravel = FootPathLength(sustained);
        Assert.True(
            footTravel >= 0.2f,
            $"sustained quarter-stick travel showed no walking progression: total foot path {footTravel:0.000} m "
            + $"over {sustained.Count} ticks");

        Assert.True(
            error <= CadenceTolerance,
            string.Create(CultureInfo.InvariantCulture,
                $"quarter-stick sustained travel published playback rate {meanRate:0.000} while realised speed was "
                + $"{meanSpeed:0.000} m/s (matched rate {matchedRate:0.000}, unit speed {unitSpeed:0.000}); cadence "
                + $"error {error:0.000} exceeds the realised-speed coupling tolerance {CadenceTolerance} — playback "
                + $"rate is scheduled from stick magnitude, not realised motion"));
    }

    /// <summary>
    /// From-rest quarter-stick start: at the travel onset the playback rate follows the demanded-magnitude
    /// schedule (CTRL-001 TR4: the demand magnitude mapped onto the configurable playback-rate bounds,
    /// with the last published travel rate held through authored exits), and the onset window must keep
    /// the published rate within the suite's realised-motion tolerance of the body's matched rate.
    /// </summary>
    [Fact]
    public async Task FromRestQuarterStickStart_TravelOnsetCadenceTracksBodySpeed()
    {
        SceneTree sceneTree = GetSceneTree();
        await using PlayerLocomotionProductionWiringHarness harness =
            await PlayerLocomotionProductionWiringHarness.CreateAsync(sceneTree, "cadence-rest-start", ProductionRigYawRadians);

        _ = await harness.RunAsync(sceneTree, SettleTicks);
        harness.PushMovement(new Vector2(0f, 0.25f));
        List<ProductionWiringSample> run = await harness.RunAsync(sceneTree, 120);
        harness.ReleaseSticks();
        _ = await harness.RunAsync(sceneTree, 30);

        float unitSpeed = harness.DeclaredTravelUnitSpeed;
        int onsetStart = run.FindIndex(sample => sample.GraphNode == "PlayerTravel");
        Assert.True(onsetStart >= 0, "expected the authored entry to hand off into the travel state");

        var onset = run.Skip(onsetStart).Take(15).ToList();
        Assert.True(onset.Count >= 10, $"expected at least 10 onset samples, got {onset.Count}");

        float meanRate = onset.Average(sample => sample.TravelPlaybackRate);
        float meanSpeed = onset.Average(sample => sample.ActorPlanarVelocity.Length());
        float matchedRate = meanSpeed / unitSpeed;
        float error = Mathf.Abs(meanRate - matchedRate);

        Assert.True(
            error <= CadenceTolerance,
            string.Create(CultureInfo.InvariantCulture,
                $"travel onset from rest published playback rate {meanRate:0.000} while the body still moved at "
                + $"{meanSpeed:0.000} m/s (matched rate {matchedRate:0.000}); cadence error {error:0.000} exceeds the "
                + $"tolerance {CadenceTolerance} — the onset rate is matched to authored root speed, not body speed"));
    }

    /// <summary>
    /// Plain stop contract: release clears ordinary demand immediately, travels into the authored stop clip, the stop
    /// moves the body by its own root motion, and the player returns to idle finitely with no hidden movement.
    /// </summary>
    [Fact]
    public async Task ReleaseFromFullTravel_PlaysAuthoredStopIntoIdle()
    {
        SceneTree sceneTree = GetSceneTree();
        await using PlayerLocomotionProductionWiringHarness harness =
            await PlayerLocomotionProductionWiringHarness.CreateAsync(sceneTree, "cadence-braking-exit", ProductionRigYawRadians);

        _ = await harness.RunAsync(sceneTree, SettleTicks);
        harness.PushMovement(new Vector2(0f, 1f));
        _ = await harness.RunAsync(sceneTree, 150);
        harness.ReleaseSticks();
        List<ProductionWiringSample> braking = await harness.RunAsync(sceneTree, 150);

        Assert.All(braking, sample => Assert.True(sample.OrdinaryTravelDemand.IsZeroApprox()));
        for (int index = 1; index < braking.Count; index++)
        {
            Assert.Equal(braking[index - 1].MovementOperationCount + 1, braking[index].MovementOperationCount);
        }

        int stopStart = braking.FindIndex(sample =>
            sample.PlayerState is PlayerLocomotionState.ExitingForward or PlayerLocomotionState.ExitingBackward);
        Assert.InRange(stopStart, 0, 4);
        int completion = braking.FindIndex(stopStart + 1, sample => sample.PlayerState == PlayerLocomotionState.Idle);
        Assert.True(completion > stopStart, "authored stop did not complete finitely");
        Assert.Contains(braking.Skip(stopStart).Take(completion - stopStart), sample =>
            sample.GraphNode.StartsWith("PlayerF2I", StringComparison.Ordinal)
            || sample.GraphNode.StartsWith("PlayerB2I", StringComparison.Ordinal));
        List<ProductionWiringSample> moving = [.. braking
            .Skip(stopStart)
            .Take(completion - stopStart)
            .Where(sample => sample.RequestedMovement.LengthSquared() > 0.0000000001f)];
        Assert.True(moving.Count >= 2, $"authored stop exposed only {moving.Count} moving samples");
        Assert.All(moving, sample => Assert.True(
            new Vector2(sample.RequestedMovement.X, sample.RequestedMovement.Z).DistanceTo(
                new(sample.RealisedMovement.X, sample.RealisedMovement.Z)) <= 0.005f));
        int settled = braking.FindIndex(completion, sample => sample.FadingFromNode.Length == 0);
        Assert.True(settled >= completion, "idle crossfade did not finish");
        Assert.All(braking.Skip(settled + 1), sample =>
        {
            Assert.True(sample.RequestedMovement.IsZeroApprox());
            Assert.True(sample.RealisedMovement.IsZeroApprox());
        });
    }

    /// <summary>
    /// Reversal presentation contract: after a full forward bout the stick snaps to full backward. While the
    /// response-model velocity still carries the body forward (opposing the new demand), the graph must not
    /// present a full-rate, fully-committed new-direction walk. Today the reversal tick republishes the
    /// travel blend at exactly the new input with the full-stick rate — the contract fails until blending or
    /// cadence acknowledges the still-opposing realised motion.
    /// </summary>
    [Fact]
    public async Task ReversalDuringTravel_DoesNotPresentFullRateOppositeWalkWhileBodyStillTravelsForward()
    {
        SceneTree sceneTree = GetSceneTree();
        await using PlayerLocomotionProductionWiringHarness harness =
            await PlayerLocomotionProductionWiringHarness.CreateAsync(sceneTree, "cadence-reversal", ProductionRigYawRadians);

        Vector2 newInput = new(0f, -1f);
        _ = await harness.RunAsync(sceneTree, SettleTicks);
        harness.PushMovement(new Vector2(0f, 1f));
        _ = await harness.RunAsync(sceneTree, 150);
        harness.PushMovement(newInput);
        List<ProductionWiringSample> reversal = await harness.RunAsync(sceneTree, 50);
        harness.ReleaseSticks();
        _ = await harness.RunAsync(sceneTree, 20);

        List<ProductionWiringSample> opposing = [..
            reversal.Where(sample => sample.ResponseVelocity.Y > 0.1f)];
        Assert.True(opposing.Count >= 2,
            $"expected braking samples still moving forward after the reversal, got {opposing.Count}");

        int firstViolation = IndexOfFirstReversalViolation(opposing, newInput, out ProductionWiringSample violation);
        Assert.True(
            firstViolation < 0,
            string.Create(CultureInfo.InvariantCulture,
                $"reversal presented full-speed opposite-direction walking while the body still travelled forward: "
                + $"sample {firstViolation} has rate {violation.TravelPlaybackRate:0.000}, directional blends "
                + $"L {violation.DirectionalBlendLeft:0.###} / R {violation.DirectionalBlendRight:0.###} vs new input "
                + $"{newInput:0.###}, while response velocity {violation.ResponseVelocity:0.###} still opposes it"));
    }

    /// <summary>
    /// Supported-foot drift coverage note (CTRL-001 TR13): a synthetic-content supported-foot drift bound
    /// is not authorable against the current fixture — its foot-bone tracks are continuous linear sweeps
    /// with no stance phase, so per-tick supported-foot world displacement is structurally equal to body
    /// displacement (measured mean ratio 1.000, deterministic) and a bound on it would assert the fixture's
    /// authoring rather than detect gliding. The gliding anomaly guard therefore rests on the frozen-pose
    /// progression assertion above (foot path length plus advancing play positions) and on the
    /// production-rig photobooth trajectory pairing (CTRL-UAC-010), which measures supported-foot step
    /// ratios of 0.30-0.50 during sustained travel on the production clips.
    /// </summary>

    private static List<ProductionWiringSample> SustainedTravelWindow(
        IReadOnlyList<ProductionWiringSample> samples,
        int minimumTicks,
        float warmupSeconds)
    {
        int firstTravel = -1;
        for (int index = 0; index < samples.Count; index++)
        {
            if (samples[index].GraphNode == "PlayerTravel")
            {
                firstTravel = index;
                break;
            }
        }

        if (firstTravel < 0)
        {
            return [];
        }

        float travelStartTime = samples[firstTravel].TimeSeconds;
        return [..
            samples.Where(sample => sample.GraphNode == "PlayerTravel"
                && sample.TimeSeconds - travelStartTime >= warmupSeconds
                && sample.ResponseVelocity.Length() > 0.05f)
            .Take(minimumTicks * 3)];
    }

    private static int IndexOfFirstReversalViolation(
        IReadOnlyList<ProductionWiringSample> opposing,
        Vector2 newInput,
        out ProductionWiringSample violation)
    {
        violation = opposing[0];
        for (int index = 0; index < opposing.Count; index++)
        {
            ProductionWiringSample sample = opposing[index];
            float blendCommitment = Mathf.Min(
                sample.DirectionalBlendLeft.DistanceTo(newInput),
                sample.DirectionalBlendRight.DistanceTo(newInput));
            if (sample.TravelPlaybackRate > 0.6f && blendCommitment < 0.3f)
            {
                violation = sample;
                return index;
            }
        }

        return -1;
    }

    /// <summary>Total planar path length travelled by both foot bones over the sample window.</summary>
    private static float FootPathLength(IReadOnlyList<ProductionWiringSample> samples)
    {
        float total = 0f;
        for (int index = 1; index < samples.Count; index++)
        {
            total += (samples[index].LeftFootWorld - samples[index - 1].LeftFootWorld).Length();
            total += (samples[index].RightFootWorld - samples[index - 1].RightFootWorld).Length();
        }

        return total;
    }
}
