using System.Globalization;
using AlleyCat.Control.Locomotion;
using AlleyCat.IntegrationTests.Support;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Control;

/// <summary>
/// CTRL-001 stop-responsiveness regression on the real production player composition: the approved
/// contract (responsiveness over stop-continuation naturalness) requires, after an abrupt stick release
/// on EVERY lane, release-to-stop (planar velocity ≤ 5 mm/s for 3 consecutive ticks) within 400 ms AND
/// post-release displacement ≤ 200 mm; pure-lateral releases additionally may not carry the body more
/// than 100 mm forward (they must take the neutral stop policy, never the forward stop clip whose
/// authored root rotated the remaining motion into a ~0.5 m forward glide); and the post-release body
/// speed never rises above the release-tick speed. Each scenario writes its per-tick CSV trace (TR10 /
/// CTRL-TAC-002 evidence) under game/temp/ctrl001-stop-responsiveness/last-run/.
/// </summary>
/// <remarks>
/// Production content is required: the contract's targets are defined against the real stop clips —
/// catalogue forward 1.0 s / 0.6301 m endpoint and backward 1.2667 s / 0.7006 m, which the rig's
/// ≈0.837 runtime motion scale maps to ≈0.5275 m and ≈0.5864 m of authored displacement. The governed
/// rates measured in the traces are therefore ≈2.64x forward (displacement-bound) and ≈3.17x backward
/// (time-bound), with the braking presentation window ≈7–8 physics ticks before the cut freezes it.
/// The fixture-content wiring harness cannot reproduce the symptom. The production scene is driven
/// through the real controller XR input path (fake XR runtime, real action events).
/// </remarks>
public sealed class PlayerLocomotionStopResponsivenessIntegrationTests
{
    private const int SettleTicks = 30;
    private const int HoldTicks = 90;
    private const int MaximumPostReleaseTicks = 330;
    // Approved completion-ended coverage: retain a half-second quiet tail after both motion and presentation
    // finish. This is a temporal-coverage choice, not a duration inferred from the current animation's output.
    private const int QuietTailTicks = 30;
    private const int EntryReleaseHoldTicks = 12;
    private const int RotationReleaseTick = 210;
    private const float StopSpeed = 0.005f;
    private const float TargetStopSeconds = 0.4f;
    private const float TargetDisplacementMetres = 0.2f;
    private const float LateralForwardDisplacementMetres = 0.1f;

    /// <summary>
    /// The authored-stop lanes' no-rise envelope: production clamps the adapted stop root to the
    /// release-tick speed plus the no-rise tolerance (0.05 m/s) at the single motion-application point,
    /// so the body cannot accelerate past it by more than measurement noise. The neutral-stop lanes get
    /// the same continuity ceiling — a travel-window neutral freezes its fading source outright, but an
    /// entry-window neutral's fading source is the ungoverned entry clip, held to the clamp.
    /// </summary>
    private const float AuthoredStopBlendOvershootMetresPerSecond = 0.06f;

    [Fact]
    public Task ForwardFullAndHalfStick_StopInsideTheResponsivenessBudget()
        => RunLaneCasesAsync(true, ("fwd-full", new Vector2(0f, 1f)), ("fwd-half", new Vector2(0f, 0.5f)));

    [Fact]
    public Task BackwardFullStick_StopsInsideTheResponsivenessBudget()
        => RunScenarioAsync("back-full", new Vector2(0f, -1f), expectAuthoredStop: true);

    [Fact]
    public Task LateralRightAndLeftFullStick_TakeTheNeutralStopWithoutForwardGlide()
        => RunLaneCasesAsync(false,
            ("lat-right-full", new Vector2(1f, 0f)),
            ("lat-left-full", new Vector2(-1f, 0f)));

    [Fact]
    public Task DiagonalForwardRightFullStick_KeepsTheScaledAuthoredForwardStop()
        => RunScenarioAsync("diag-fwd-right-full", new Vector2(0.70710678f, 0.70710678f), expectAuthoredStop: true);

    [Fact]
    public Task ReleaseDuringEntry_StopsInsideTheResponsivenessBudget()
        => RunScenarioAsync("entry-release", new Vector2(0f, 1f), holdTicks: EntryReleaseHoldTicks, expectAuthoredStop: true);

    [Fact]
    public Task LateralReleaseDuringEntry_TakesTheNeutralStopWithoutForwardGlide()
        => RunScenarioAsync("lateral-entry-release", new Vector2(1f, 0f), holdTicks: EntryReleaseHoldTicks, expectAuthoredStop: false);

    [Fact]
    public Task ReleaseDuringTravelCrossfade_StopsInsideTheResponsivenessBudget()
        => RunScenarioAsync("crossfade-release", new Vector2(0f, 1f), adaptiveCrossfadeRelease: true, expectAuthoredStop: true);

    [Fact]
    public Task TurnPlusMove_ReleaseMoveThenBoth_StopsWithoutBodyMotion()
        => RunScenarioAsync(
            "turn-move-release-move-then-both",
            new Vector2(0f, 1f),
            rotation: new Vector2(1f, 0f),
            releaseMoveOnlyThenBoth: true,
            expectAuthoredStop: true);

    private static async Task RunLaneCasesAsync(bool expectAuthoredStop, params (string Scenario, Vector2 Movement)[] cases)
    {
        SceneTree sceneTree = GetSceneTree();
        await using ProductionPlayerSceneHarness harness =
            await ProductionPlayerSceneHarness.CreateAsync(sceneTree, "ctrl001-stop-responsiveness", "last-run");
        Transform3D initialActorTransform = harness.Actor.GlobalTransform;
        for (int index = 0; index < cases.Length; index++)
        {
            if (index > 0)
            {
                // Share only inside this fact: TEST-001 destroys runtime roots between facts. Completion and the
                // entire quiet tail have been asserted before placement for another independent lane stimulus.
                ProductionWiringSample settled = harness.CaptureSample();
                Assert.Equal(PlayerLocomotionState.Idle, settled.PlayerState);
                Assert.Equal(harness.Locomotion.IdleAnimationStateName.ToString(), settled.GraphNode);
                Assert.Equal(string.Empty, settled.FadingFromNode);
                Assert.True(settled.ActorPlanarVelocity.Length() <= StopSpeed);
                harness.ReleaseSticks();
                harness.Actor.GlobalTransform = initialActorTransform;
            }

            (string scenario, Vector2 movement) = cases[index];
            await RunScenarioAsync(scenario, movement, expectAuthoredStop: expectAuthoredStop, sharedHarness: harness);
        }
    }

    private static async Task RunScenarioAsync(
        string scenario,
        Vector2 movement,
        int? holdTicks = null,
        bool adaptiveCrossfadeRelease = false,
        Vector2 rotation = default,
        bool releaseMoveOnlyThenBoth = false,
        bool expectAuthoredStop = true,
        ProductionPlayerSceneHarness? sharedHarness = null)
    {
        SceneTree sceneTree = GetSceneTree();
        await using ProductionPlayerSceneHarness? ownedHarness = sharedHarness is null
            ? await ProductionPlayerSceneHarness.CreateAsync(sceneTree, "ctrl001-stop-responsiveness", "last-run")
            : null;
        ProductionPlayerSceneHarness harness = sharedHarness ?? ownedHarness!;

        Assert.True(harness.ControllerBound, "SETUP: the production PlayerController did not bind to the fake XR runtime.");
        _ = await harness.RunAsync(sceneTree, SettleTicks);

        harness.PushMovement(movement);
        if (rotation != default)
        {
            harness.PushRotation(rotation);
        }

        int hold = holdTicks ?? HoldTicks;
        for (int tick = 0; tick < hold; tick++)
        {
            await WaitForNextPhysicsFrameAsync(sceneTree);
            if (adaptiveCrossfadeRelease)
            {
                ProductionWiringSample sample = harness.CaptureSample();
                if (sample.GraphNode == "PlayerTravel" && sample.FadingFromNode.Length > 0)
                {
                    break;
                }
            }
        }

        ProductionWiringSample atRelease = harness.CaptureSample();
        Assert.True(atRelease.PlayerState is PlayerLocomotionState.TravellingForward
                or PlayerLocomotionState.TravellingDirectional
                or PlayerLocomotionState.EnteringForward
                or PlayerLocomotionState.EnteringBackward,
            $"SETUP: expected travel before release, got {atRelease.PlayerState} ({scenario}).");
        harness.PushMovement(Vector2.Zero);
        ulong releaseFrame = atRelease.PhysicsFrame;

        string note = string.Empty;
        List<ProductionWiringSample> samples = new(MaximumPostReleaseTicks);
        int completionTick = 0;
        int stoppedTicks = 0;
        // The deadline bounds completion, not its subsequent quiet tail.
        for (int tick = 1; tick <= (completionTick > 0 ? completionTick + QuietTailTicks : MaximumPostReleaseTicks); tick++)
        {
            await WaitForNextPhysicsFrameAsync(sceneTree);
            ProductionWiringSample sample = harness.CaptureSample();
            samples.Add(sample);
            Assert.Equal(releaseFrame + (ulong)tick, sample.PhysicsFrame);
            stoppedTicks = sample.ActorPlanarVelocity.Length() <= StopSpeed ? stoppedTicks + 1 : 0;

            if (releaseMoveOnlyThenBoth && tick == RotationReleaseTick)
            {
                harness.PushRotation(Vector2.Zero);
                note = "turn-released";
            }

            // The transient Idle before turn continuation is not the terminal state. Preserve every tick and
            // the scheduled second input release before considering completion (CTRL-TAC-002).
            bool allReleasesConsumed = !releaseMoveOnlyThenBoth || tick > RotationReleaseTick;
            if (completionTick == 0 && allReleasesConsumed && stoppedTicks >= 3
                && sample.PlayerState == PlayerLocomotionState.Idle
                && sample.GraphNode == harness.Locomotion.IdleAnimationStateName.ToString()
                && sample.FadingFromNode.Length == 0)
            {
                completionTick = tick;
            }

            if (completionTick > 0 && tick == completionTick + QuietTailTicks)
            {
                break;
            }
        }

        harness.WriteReleaseTraceCsv(scenario, atRelease, releaseFrame, samples, note);
        AssertStopResponsiveness(scenario, atRelease, releaseFrame, samples, expectAuthoredStop);
        Assert.True(completionTick > 0, $"({scenario}) stop presentation never completed in unfaded Idle.");
        Assert.Equal(completionTick + QuietTailTicks, samples.Count);
        Assert.All(samples.Skip(completionTick), sample =>
        {
            Assert.Equal(PlayerLocomotionState.Idle, sample.PlayerState);
            Assert.Equal(harness.Locomotion.IdleAnimationStateName.ToString(), sample.GraphNode);
            Assert.Equal(string.Empty, sample.FadingFromNode);
        });
    }

    private static void AssertStopResponsiveness(
        string scenario,
        ProductionWiringSample atRelease,
        ulong releaseFrame,
        List<ProductionWiringSample> samples,
        bool expectAuthoredStop)
    {
        float dt = 1f / Math.Max(1, Engine.PhysicsTicksPerSecond);
        float Ms(ProductionWiringSample sample)
        {
            return (sample.PhysicsFrame - releaseFrame) * dt * 1000f;
        }

        bool isAuthoredExit(ProductionWiringSample sample)
        {
            return sample.PlayerState is PlayerLocomotionState.ExitingForward or PlayerLocomotionState.ExitingBackward;
        }

        bool isNeutralExit(ProductionWiringSample sample)
        {
            return sample.PlayerState == PlayerLocomotionState.ExitingNeutral;
        }

        Assert.Contains(samples, expectAuthoredStop ? isAuthoredExit : isNeutralExit);

        int stop = -1;
        for (int index = 0; index < samples.Count - 2; index++)
        {
            if (samples[index].ActorPlanarVelocity.Length() <= StopSpeed
                && samples[index + 1].ActorPlanarVelocity.Length() <= StopSpeed
                && samples[index + 2].ActorPlanarVelocity.Length() <= StopSpeed)
            {
                stop = index;
                break;
            }
        }

        Assert.True(stop >= 0, $"({scenario}) the body never reached the ≤{StopSpeed * 1000f:0} mm/s stop criterion.");

        float stopMs = Ms(samples[stop]);
        Assert.True(stopMs <= TargetStopSeconds * 1000f,
            string.Create(CultureInfo.InvariantCulture,
                $"({scenario}) release-to-stop took {stopMs:0} ms, exceeding the {TargetStopSeconds * 1000f:0} ms budget."));

        Vector3 origin = atRelease.ActorPosition;
        Vector3 DisplacementOf(ProductionWiringSample sample)
        {
            return new(sample.ActorPosition.X - origin.X, 0f, sample.ActorPosition.Z - origin.Z);
        }
        Vector3 displacement = DisplacementOf(samples[stop]);
        Assert.True(displacement.Length() <= TargetDisplacementMetres,
            string.Create(CultureInfo.InvariantCulture,
                $"({scenario}) post-release displacement was {displacement.Length() * 1000f:0} mm, exceeding the "
                + $"{TargetDisplacementMetres * 1000f:0} mm budget (stopped at {stopMs:0} ms)."));

        // The post-release speed never rises beyond the stop's continuity envelope: the stop must
        // decelerate, never accelerate the body — the lateral forward-glide peak (baseline:
        // 0.836 m/s after a 0.745 m/s strafe, sustained for ~0.3 s) is gone, and every lane's adapted
        // root is clamped to the release-tick speed plus the no-rise tolerance (entry-window neutrals
        // drain a clamped entry source; travel-window neutrals freeze outright and measure ~0).
        float releaseSpeed = atRelease.ActorPlanarVelocity.Length();
        float speedCeiling = releaseSpeed + AuthoredStopBlendOvershootMetresPerSecond;
        ProductionWiringSample peak = samples[0];
        foreach (ProductionWiringSample sample in samples)
        {
            if (sample.ActorPlanarVelocity.Length() > peak.ActorPlanarVelocity.Length())
            {
                peak = sample;
            }
        }

        Assert.True(peak.ActorPlanarVelocity.Length() <= speedCeiling,
            string.Create(CultureInfo.InvariantCulture,
                $"({scenario}) post-release speed rose to {peak.ActorPlanarVelocity.Length():0.000} m/s at "
                + $"{Ms(peak):0} ms after releasing at {releaseSpeed:0.000} m/s (ceiling {speedCeiling:0.000})."));

        // Nothing keeps moving after the stop: the idle crossfade and any turn continuation are body-still.
        Assert.All(samples.Skip(stop + 3), sample =>
            Assert.True(sample.ActorPlanarVelocity.Length() <= StopSpeed,
                $"({scenario}) the body moved again after the measured stop (tick {sample.PhysicsFrame - releaseFrame})."));

        // The neutral-stop lanes must not rotate the remaining motion forward.
        if (!expectAuthoredStop)
        {
            Vector3 heading = new(-Mathf.Sin(atRelease.ActorYawRadians), 0f, -Mathf.Cos(atRelease.ActorYawRadians));
            float forwardComponent = displacement.Dot(heading);
            Assert.True(Mathf.Abs(forwardComponent) <= LateralForwardDisplacementMetres,
                string.Create(CultureInfo.InvariantCulture,
                    $"({scenario}) the lateral release carried the body {Mathf.Abs(forwardComponent) * 1000f:0} mm "
                    + $"forward (limit {LateralForwardDisplacementMetres * 1000f:0} mm)."));
        }
    }
}
