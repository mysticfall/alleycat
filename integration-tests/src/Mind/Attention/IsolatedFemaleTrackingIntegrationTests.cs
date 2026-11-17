using System.Reflection;
using System.Text.Json;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.IK;
using AlleyCat.IntegrationTests.Vision;
using AlleyCat.Mind.Attention;
using AlleyCat.Rigging.Physics;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.Attention;

/// <summary>
/// Conditional current-fixture evidence: real scheduled female head/eye pipeline, with fixed-rest
/// and native-Idle scenarios. This does not establish historical or universal animation correctness.
/// </summary>
public sealed partial class IsolatedFemaleTrackingIntegrationTests
{
    /// <summary>
    /// AI-009's fixed partial-influence matrix: the real NPC release ramp reaches the authored
    /// modifier group without being rounded to full/zero, while native upstream remains independent.
    /// No midpoint face angle is prescribed: three serial modifiers do not form one linear blend.
    /// </summary>
    [Fact]
    public async Task Head_PartialInfluence_ReachesRealModifiersWithoutRecursiveUpstream()
    {
        await using Fixture rig = await Fixture.CreateAsync("partial_influence");
        await rig.WarmUpAsync();
        await rig.CentreAsync(rig.Anchor(40, 0, 1.4f));
        await rig.ObserveAsync("full_influence_control", 1.6);
        _ = rig.CheckHeadInfluence("full", requireFull: true);
        rig.Eyes.ClearLookTarget();
        double start = Fixture.Now;
        int partialFrames = 0;
        bool partialPoseMoves = false;
        do
        {
            await rig.FrameAsync("partial_influence_release");
            float influence = rig.CheckHeadInfluence("release");
            if (influence is > 0 and < 1)
            {
                partialFrames++;
                partialPoseMoves |= rig.Samples[^1].Yaw > 0;
                rig.CheckFrozenUpstream();
            }
        } while (Fixture.Now - start < 1.6);

        rig.Check(partialFrames > 0 && partialPoseMoves,
            $"non-vacuous partial NPC influence: {partialFrames} presented frames, aimed pose {partialPoseMoves}");
        _ = rig.CheckHeadInfluence("zero", requireZero: true);
        await rig.ReleaseAsync();
        rig.Finish();
    }

    /// <summary>AI-009 UR3/TR7/AC3: interrupt actual command easing, not a settled Hold.</summary>
    [Fact]
    public async Task Hold_InConeInterruptsEasing_RetainsCommandWithoutChasingPreviousTarget()
    {
        await using Fixture rig = await Fixture.CreateAsync("hold-interrupted-easing");
        await rig.WarmUpAsync();
        StaticVisualCue previous = rig.Anchor(10, 0, 1.4f);
        StaticVisualCue glance = rig.Anchor(5, 0, 1.4f);
        List<HoldObservation> observations = [];
        List<object> events = [];
        void Record(string phase)
        {
            observations.Add(rig.ObserveHold(phase));
        }
        void Event(string kind)
        {
            events.Add(new
            {
                Kind = kind,
                Time = Fixture.Now,
                Frame = Engine.GetProcessFrames()
            });
        }

        rig.Eyes.SetLookTarget(previous);
        Event("previous-assignment");
        double setupStart = Fixture.Now;
        HoldObservation start = rig.ObserveHold("before-centring");
        HoldObservation pre;
        do
        {
            await rig.FrameAsync("prior-easing");
            Record("prior-easing");
            pre = observations[^1];
        } while (Fixture.Now - setupStart < 2 && !(pre.Mode == "Centring"
            && Math.Abs(pre.HeldYaw - pre.OutputYaw) > 0.5
            && Math.Abs(pre.OutputYaw - start.OutputYaw) > 0.05));
        rig.Check(pre.Mode == "Centring" && Math.Abs(pre.HeldYaw - pre.OutputYaw) > 0.5
            && Math.Abs(pre.OutputYaw - start.OutputYaw) > 0.05,
            "SETUP centring must be observably easing before interruption (>0.5° remaining, >0.05° progress)");

        rig.Eyes.SetLookTarget(glance);
        double assigned = Fixture.Now;
        Event("glance-assignment");
        bool moved = false;
        do
        {
            await rig.FrameAsync("in-cone-glance");
            Record("in-cone-glance");
            if (!moved && Fixture.Now - assigned >= 0.125)
            {
                rig.MoveAnchor(previous, -10, 0, 1.4f);
                moved = true;
                Event("previous-target-moved");
            }
        } while (Fixture.Now - assigned < 0.25);
        double returned = Fixture.Now;
        rig.Eyes.SetLookTarget(previous);
        Event("return-assignment");
        // Return is independent of assertions and I/O; keep all consecutive observations through >=1.4 s.
        do
        {
            await rig.FrameAsync("return");
            Record("return");
        } while (Fixture.Now - returned < 1.4);
        Event("return-window-end");
        double settledStart = Fixture.Now;
        do
        {
            await rig.FrameAsync("settled");
            Record("settled");
        } while (Fixture.Now - settledStart < 0.5);
        await rig.ReleaseAsync();
        Record("release-end");
        Event("release-end");

        HoldObservation[] held = [.. observations.Where(o => o.Phase == "in-cone-glance")];
        rig.Check(moved && held.Length > 1 && returned - assigned is >= 0.25 and <= 0.6,
            $"complete brief glance {returned - assigned:F6}s, {held.Length} observations, previous target moved={moved}");
        foreach (HoldObservation observation in held)
        {
            rig.Check(observation.Mode == "Hold" && Math.Abs(observation.AnchorYaw) < rig.ComfortConeHorizontalDegrees,
                $"glance stays in-cone/Hold: {observation.Mode}, local yaw {observation.AnchorYaw:F6}°");
            rig.Check(Math.Abs(observation.HeldYaw - pre.HeldYaw) < 0.000001
                && Math.Abs(observation.HeldPitch - pre.HeldPitch) < 0.000001,
                $"captured command retained: pre {pre.HeldYaw:F6}/{pre.HeldPitch:F6}°, now {observation.HeldYaw:F6}/{observation.HeldPitch:F6}°");
        }
        double beforeError = Math.Abs(pre.HeldYaw - pre.OutputYaw);
        double afterError = Math.Abs(pre.HeldYaw - held[^1].OutputYaw);
        rig.Check(afterError < beforeError && Math.Abs(held[^1].OutputYaw - pre.OutputYaw) > 0.05,
            $"continued command easing: error {beforeError:F6}→{afterError:F6}°, output {pre.OutputYaw:F6}→{held[^1].OutputYaw:F6}°");
        string directory = System.Environment.GetEnvironmentVariable("ALLEYCAT_HOLD_EVIDENCE_DIRECTORY")
            ?? ProjectSettings.GlobalizePath("res://temp/ai-009-isolated/hold-interrupted-easing");
        _ = Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, "hold-observations.jsonl"), observations.Select(o => JsonSerializer.Serialize(o)));
        File.WriteAllText(Path.Combine(directory, "hold-events.json"), JsonSerializer.Serialize(events));
        rig.Finish();
    }

    /// <summary>
    /// Native female Idle plus one complete acquisition, fixed fixation and same-identity moving fixation.
    /// Immutable authored bilateral optical references; no production gaze helper or output calibration.
    /// </summary>
    [Fact]
    public async Task Eyes_NativeIdleAndSameMovingTarget_MaintainPresentedBilateralFixation()
    {
        await using Fixture rig = await Fixture.CreateAsync("animated-transform");
        rig.ConfigureAnimatedEvidence();
        rig.StartNativeIdle();
        await rig.CentreAsync(rig.Anchor(0, 0, 6));
        await rig.ObserveAsync("animated_preassignment", 0.5);
        rig.CheckPreAssignmentAlignment();

        Sample before = rig.Samples[^1];
        StaticVisualCue anchor = rig.Anchor(5, 2, 6);
        ulong identity = anchor.GetInstanceId();
        FixationAcquisitionWindow window = rig.AssignLookTargetControlled(anchor);
        double assigned = rig.Assignment!.Value.Time;
        int first = rig.Samples.Count;
        do
        {
            await rig.FrameAsync("animated_fixed");
        } while (Fixture.Now - assigned < 5);

        double movingStarted = Fixture.Now;
        do
        {
            // Controlled between-frame movement: same object and assignment, no acquisition reset.
            double motion = Math.Min(Fixture.Now - movingStarted, 5);
            rig.MoveAnchor(anchor, (float)(5 + (2.4 * motion)), (float)(2 + (1.8 * motion)), 6);
            await rig.FrameAsync("animated_moving");
            rig.Check(rig.Eyes.LookTarget?.GetInstanceId() == identity, "same target identity throughout movement");
        } while (Fixture.Now - movingStarted < 5);
        await rig.ObserveAsync("animated_settled", 1);

        Sample[] samples = [.. rig.Samples.Skip(first)];
        FixationWindowResult result = window.Evaluate();
        double overshoot = samples.Where(s => s.Time - assigned <= 0.4).Max(s => Math.Max(
            DirectFixationGeometry.AngleBetweenDegrees(before.LeftEye!.Actual, s.LeftEye!.Actual)
                - DirectFixationGeometry.AngleBetweenDegrees(before.LeftEye.Actual, s.LeftEye.Expected),
            DirectFixationGeometry.AngleBetweenDegrees(before.RightEye!.Actual, s.RightEye!.Actual)
                - DirectFixationGeometry.AngleBetweenDegrees(before.RightEye.Actual, s.RightEye.Expected)));
        rig.Check(result.IsOnsetWithinBound, $"animated fixation onset {result.OnsetFrame} <=2 presented frames");
        rig.Check(result.IsCompletionWithinDeadline,
            $"animated acquisition {result.CompletionSeconds ?? double.PositiveInfinity:F6}s <=0.4s");
        rig.Check(result.IsFixationMaintained,
            $"animated maintained bilateral max {result.MaxPostCompletionErrorDegrees:F6}° <=0.5°");
        rig.Check(overshoot <= 4, $"animated acquisition overshoot {Math.Max(0, overshoot):F6}° <=4°");
        rig.Check(samples.Zip(samples.Skip(1)).All(pair => pair.Second.Frame == pair.First.Frame + 1),
            "complete consecutive presented frame sequence: no skipped observations");
        rig.Check(samples.Max(s => s.Yaw) - samples.Min(s => s.Yaw) > 1
            || samples.Max(s => s.Pitch) - samples.Min(s => s.Pitch) > 1,
            "actual solved face has >1° animation/target-driven motion (non-vacuity, not an anatomical bound)");
        rig.WriteAnimatedOutcome(result, Math.Max(0, overshoot), assigned, movingStarted, identity);
        rig.Finish();
    }

    /// <inheritdoc/>
    [Fact]
    public async Task Roll_DiagonalSweepAndLateralHold_RespectFrozenBounds()
    {
        await using Fixture rig = await Fixture.CreateAsync("roll");
        await rig.WarmUpAsync();
        await rig.CentreAsync(rig.Anchor(0, 0, 1.4f));
        await rig.ObserveAsync("straight", 1);
        for (int i = 0; i < 4; i++)
        {
            rig.Eyes.SetLookTarget(rig.Anchor(i % 2 == 0 ? 35 : -35, i % 2 == 0 ? -25 : 25, 1.1f));
            await rig.ObserveAsync($"diagonal_{i}", 1);
        }
        rig.Eyes.SetLookTarget(rig.Anchor(35, -25, 1.1f));
        await rig.ObserveAsync("diagonal_settle", 2.6);
        rig.Eyes.SetLookTarget(rig.Anchor(55, 0, 1.15f));
        await rig.ObserveAsync("lateral_hold", 3.2);
        await rig.ReleaseAsync();
        Sample[] active = [.. rig.Samples.Where(s => s.Phase.StartsWith("diagonal", StringComparison.Ordinal)
            || s.Phase == "lateral_hold")];
        double transient = active.Max(s => Math.Abs(s.Roll));
        double mean = 0;
        foreach (Sample end in active)
        {
            Sample[] window = [.. active.Where(s => s.Time >= end.Time - 0.5 && s.Time <= end.Time)];
            if (end.Time - window[0].Time >= 0.48)
            {
                mean = Math.Max(mean, Math.Abs(window.Average(s => s.Roll)));
            }
        }
        rig.Check(transient <= 3.5, $"transient roll {transient:F4}° <=3.5°");
        rig.Check(mean <= 2, $"0.5s mean roll {mean:F4}° <=2°");
        rig.Finish();
    }

    /// <summary>
    /// Measured native-upstream motion for the animated-domain fixture: the angle between the live
    /// upstream face basis and its authored rest face, in degrees.
    /// </summary>
    private static double AnimatedUpstreamMotion(Fixture rig)
    {
        OrientingUpstreamSnapshot? upstream = rig.Controller.LastUpstreamSnapshot;
        if (upstream is null)
        {
            return 0d;
        }

        Basis restFace = (rig.FixtureSkeleton.GlobalBasis * rig.FixtureSkeleton.GetBoneGlobalRest(rig.HeadBoneIndex).Basis
            * rig.FixtureViewpoint.Basis).Orthonormalized();
        return Mathf.RadToDeg((-restFace.Z).AngleTo(-upstream.UpstreamFaceBasisWorld.Z));
    }

    /// <summary>
    /// PGI-B1 regression: with the chain animated by the real native Idle clip and a deep downward
    /// anchor assigned, the production controller must CONSUME the animation-owned upstream — the
    /// shared frame builds on the animated pose (status fold-governed, alpha=0 reconstruction within
    /// 0.1 mm of the animated upstream head), the published origin separates from the old excluded
    /// orbit by more than 5 mm, and the upstream genuinely varies through the window. Refusing the
    /// frame and republishing the uncorrected orbit is the defect this regression pins.
    /// Runs windowed: the harness observation loop awaits FramePostDraw (renderer-coupled pacing).
    /// </summary>
    [Fact]
    public async Task Fold_NativeIdleUpstream_ConsumesAnimatedPoseAndPublishesFold()
    {
        await using Fixture rig = await Fixture.CreateAsync("fold-animated-consume");
        rig.StartNativeIdle();
        // No WarmUpAsync here: its release contract asserts a static upstream rest, which the native
        // Idle animation legitimately violates — centre directly like the animated-eyes fact.
        await rig.CentreAsync(rig.Anchor(0, 0, 6));
        await rig.ObserveAsync("idle_pre", 0.5);
        StaticVisualCue deep = rig.Anchor(0, -60, 1);
        rig.Eyes.SetLookTarget(deep);
        await rig.ObserveAsync("idle_downward", 3);

        rig.Check(AnimatedUpstreamMotion(rig) > 0.05,
            "the native Idle must genuinely animate the upstream for this consumption check to be non-vacuous");
        rig.Check(rig.Controller.LastFoldFrame is not null,
            $"the animated upstream must be consumed by the shared frame (status '{rig.Controller.LastFoldStatus}')");
        rig.Check(rig.Controller.LastFoldStatus == "fold_governed",
            $"the deep animated demand must be fold-governed, found '{rig.Controller.LastFoldStatus}'");
        DownwardFoldEvaluation evaluation = Assert.IsType<DownwardFoldEvaluation>(rig.Controller.LastFoldEvaluation);
        rig.Check(evaluation.Alpha0ReconstructionMillimetres <= 0.1d,
            $"animated alpha=0 FK reconstruction {evaluation.Alpha0ReconstructionMillimetres:F6} mm must stay <=0.1 mm");
        rig.Check(
            (evaluation.PublishedOriginWorld - evaluation.OrbitTargetWorld).Length() * 1000d > 5d,
            $"the animated fold publication must separate from the excluded orbit (delta " +
            $"{(evaluation.PublishedOriginWorld - evaluation.OrbitTargetWorld).Length() * 1000d:F3} mm)");
        rig.Check(rig.Controller.GetTargetIntent().DesiredInfluence > 0.99f,
            "the deep animated demand must be fully engaged");
        rig.Finish();
    }

    /// <inheritdoc/>
    [Fact]
    public async Task Stoop_LowTarget_DistributesAnatomicalPitchAgainstFaceDenominator()
    {
        await using Fixture rig = await Fixture.CreateAsync("stoop");
        await rig.WarmUpAsync();
        await rig.CentreAsync(rig.Anchor(0, 0, 1.4f));
        await rig.ObserveAsync("straight", 1);
        rig.Eyes.SetLookTarget(rig.Anchor(0, -40, 1));
        await rig.ObserveAsync("stoop_glance", 0.22);
        await rig.ObserveAsync("stoop_settle", 2.38);
        await rig.ObserveAsync("stoop_hold", 2.5);
        Sample[] hold = [.. rig.Samples.Where(s => s.Phase == "stoop_hold")];
        foreach (Sample sample in hold)
        {
            rig.Check(sample.Pitch < -2, $"face denominator must be downward and nonzero: {sample.Pitch:F4}°");
            // The frozen Head bound is magnitude share; same-sign minima apply to Neck/UpperChest.
            rig.Check(Math.Abs(sample.Head / sample.Pitch) <= 0.55,
                $"Head magnitude share {Math.Abs(sample.Head / sample.Pitch):P2} <=55%");
            // Adopted head counter-bend guard (user decision 2026-10-12): the head contribution must stay
            // same-signed with the face aim (chin continues the stoop line; never tilts back up against it).
            // Bound chosen from tuning evidence: the tuned cone set saturates at a 38° chain curl against the
            // ~41° demand, leaving ~3° of same-signed head flexion; the alternative small-extension form
            // (extension <=5°) was not needed and the stricter same-signed bound is asserted instead.
            rig.Check(sample.Head / sample.Pitch >= 0,
                $"head counter-bend guard: Head {sample.Head:F4}° must be same-signed with face pitch {sample.Pitch:F4}°");
            rig.Check(sample.Neck / sample.Pitch >= 0.1, $"Neck share {sample.Neck / sample.Pitch:P2} >=10%, same-signed");
            rig.Check(sample.UpperChest / sample.Pitch >= 0.1,
                $"UpperChest share {sample.UpperChest / sample.Pitch:P2} >=10%, same-signed");
            rig.Check(sample.Residual < 3, $"actual face-to-target aim {sample.Residual:F4}° <3°");
            // Closure is a measurement-validity guard, NEVER the share denominator.
            rig.Check(Math.Abs(sample.Head + sample.Neck + sample.UpperChest + sample.Chest + sample.Spine
                - sample.Pitch) < 1, "sagittal joint contributions must close to actual face pitch within1°");
        }
        await rig.ReleaseAsync();
        rig.Finish();
    }

    /// <inheritdoc/>
    [Headless]
    [Fact]
    public async Task Eyes_FortyDegreeReassignment_RespectFrozenTransitBounds()
    {
        Sample[] control = await RunFortyDegreeTransitAsync(perturbHostPacing: false);
        Sample[] yielded = await RunFortyDegreeTransitAsync(perturbHostPacing: true);
        Assert.Equal(control.Length, yielded.Length);
        for (int i = 0; i < control.Length; i++)
        {
            Assert.Equal(control[i].Time, yielded[i].Time);
            Assert.Equal(control[i].Yaw, yielded[i].Yaw);
            Assert.Equal(control[i].Pitch, yielded[i].Pitch);
            Assert.Equal(control[i].Residual, yielded[i].Residual);
            Assert.Equal(control[i].CommandYaw, yielded[i].CommandYaw);
            Assert.Equal(control[i].LeftEye?.ErrorDegrees, yielded[i].LeftEye?.ErrorDegrees);
            Assert.Equal(control[i].RightEye?.ErrorDegrees, yielded[i].RightEye?.ErrorDegrees);
        }
    }

    private static async Task<Sample[]> RunFortyDegreeTransitAsync(bool perturbHostPacing)
    {
        await using Fixture rig = await Fixture.CreateAsync(perturbHostPacing ? "eyes-yielded" : "eyes", manual: true);
        rig.PerturbHostPacing = perturbHostPacing;
        await rig.WarmUpAsync();
        await rig.CentreAsync(rig.Anchor(0, 0, 1.4f));
        await rig.ObserveAsync("straight", 1);
        // Oracle measurement-validity baseline: on the centred straight anchor the independent
        // direct-fixation oracle must read both eyes aligned; if this fires the oracle, not the
        // production eyes, is at fault.
        rig.CheckPreAssignmentAlignment();
        Sample before = rig.Samples[^1];
        // Fixed 90 Hz simulation matches this scenario's nominal 135-frame/1.5-second series.
        // Assignment follows the preceding fully evaluated state; the next simulated presentation is
        // eligible frame 1. This is skeletal/optical state evidence, not renderer/presented-image timing.
        // Saccades are disabled for this precision window and eye smoothing remains 0.08.
        FixationAcquisitionWindow window = rig.AssignLookTargetControlled(rig.Anchor(40, 0, 1.2f));
        double assigned = rig.Assignment!.Value.Time;
        int assignmentIndex = rig.Samples.Count;
        for (int i = 0; i < 135; i++)
        {
            await rig.FrameAsync("transit");
        }

        await rig.CentreCurrentAsync();
        // Complete transit window: assignment through centring completion, then the settled hold.
        int transitEnd = rig.Samples.Count;
        await rig.ObserveAsync("transit_settled", 1.5);
        Sample[] samples = [.. rig.Samples.Where(s => s.Phase == "transit")];
        int response = Array.FindIndex(samples, s => Math.Abs(s.EyeYaw - before.EyeYaw) > 0.5) + 1;
        double overshoot = samples.Max(s => s.EyeYaw - s.AnchorYaw);
        // User-approved redefinition (2026-10-12): opposed gaze-error semantics. The literal world-gaze signal
        // measured geometric bearing drift of a target-locked gaze (head-origin translation), not eye lag, so the
        // excursion bounds now apply to the signed gaze error against the anchor: gaze yaw minus the anchor
        // bearing yaw, both measured from the current face origin in the neutral face frame. While the head
        // advances toward the anchor, the gaze error must not deepen (the eye must not fall further behind).
        // The scan starts after the approved response ramp; response, overshoot and centring bounds are unchanged.
        double opposed = 0, longest = 0, start = 0;
        Sample previous = samples[Math.Max(response - 1, 0)];
        for (int i = Math.Max(response, 1); i < samples.Length; i++)
        {
            Sample sample = samples[i];
            double previousError = previous.GazeYaw - previous.AnchorBearingYaw;
            double error = sample.GazeYaw - sample.AnchorBearingYaw;
            if (sample.Yaw - previous.Yaw > 0.05 && error - previousError < -0.02)
            {
                opposed += previousError - error;
                if (start == 0)
                {
                    start = previous.Time;
                }

                longest = Math.Max(longest, sample.Time - start);
            }
            else
            {
                start = 0;
            }

            previous = sample;
        }
        Sample? centred = samples.FirstOrDefault(s => s.Residual <= 2);
        rig.Check(response is > 0 and <= 2, $"eye response {response} frames <=2");
        rig.Check(overshoot <= 4, $"eye overshoot {overshoot:F4}° <=4°");
        rig.Check(opposed <= 0.5, $"opposed gaze-error excursion {opposed:F4}° <=0.5°");
        rig.Check(longest <= 0.15, $"opposed gaze-error duration {longest * 1000:F3}ms <=150ms");
        rig.Check(centred is not null && centred.Time - assigned <= 0.8,
            $"head centres in {(centred is null ? double.PositiveInfinity : centred.Time - assigned):F4}s <=0.8s");
        // Direct-fixation oracle (VISION-001 TR51/52): both eyes against independently derived
        // constrained directions from immutable authored optical references, over the complete window
        // from the controlled assignment through the 1.5 s settled hold. Frozen bounds: onset ≤2
        // eligible presented frames, acquisition completion ≤0.4 s from actual assignment, then
        // maintained ≤0.5° alignment.
        FixationWindowResult fixation = window.Evaluate();
        rig.Check(fixation.OnsetFrame is not null,
            "fixation onset detected: an eye moved >0.5° from the pre-assignment presented reference");
        rig.Check(fixation.IsOnsetWithinBound,
            $"fixation onset frame {fixation.OnsetFrame} <=2 eligible presented frames");
        rig.Check(fixation.CompletionSeconds is not null,
            "acquisition completed: both eyes entered <=0.5° of their constrained directions");
        rig.Check(fixation.IsCompletionWithinDeadline,
            $"acquisition completes {fixation.CompletionSeconds ?? double.PositiveInfinity:F4}s "
                + $"<=0.4s from actual assignment (eligible frame {fixation.CompletionFrame})");
        rig.Check(fixation.IsFixationMaintained,
            $"fixation maintained: worst post-acquisition eye error {fixation.MaxPostCompletionErrorDegrees:F4}° <=0.5°");
        // Approved stall rule (2026-10-12): no full freeze >=150 ms anywhere in the complete transit
        // (assignment through centring) while actively engaged. The sustained transit target is never
        // cleared or replaced, so the designed-dwell exclusion cannot qualify it — the exclusion
        // context still anchors the corrected semantics to this test's actual assignment time and
        // pre-assignment pose (TEST-6).
        // User-authorised saturation-park exclusion (E1 resolution, 2026-10-13): the mid-transit dwell
        // where the head parks within freeze tolerance of the commanded saturation pose (the anchor held
        // on the cone boundary) until centring eligibility (assignment + the authored centring delay) is
        // a designed dwell, not a stall. The commanded pose is the policy command held through the park.
        double eligibility = assigned + rig.CentringDelaySeconds;
        Sample parkCommand = rig.Samples.Last(s => s.Phase == "transit" && s.Time <= eligibility - (1d / 90d));
        double rawFreeze = FindLongestFullFreezeSeconds(
            rig.Samples.GetRange(assignmentIndex, transitEnd - assignmentIndex),
            exclusion: null);
        double freeze = FindLongestFullFreezeSeconds(
            rig.Samples.GetRange(assignmentIndex, transitEnd - assignmentIndex),
            exclusion: null,
            parkExclusion: new SaturationParkExclusion(
                CommandedYawDegrees: parkCommand.CommandYaw,
                CommandedPitchDegrees: parkCommand.CommandPitch,
                GeometricResidualDegrees: rig.ComfortConeHorizontalDegrees,
                CentringEligibilityTimeSeconds: eligibility));
        rig.Check(freeze < 0.15,
            $"stall: longest full freeze over complete transit {freeze * 1000:F3}ms <150ms after the "
                + $"designed saturation-park exclusion (raw {rawFreeze * 1000:F3}ms; commanded park pose "
                + $"{parkCommand.CommandYaw:F3}°/{parkCommand.CommandPitch:F3}°, cone residual "
                + $"{rig.ComfortConeHorizontalDegrees:F1}°, eligibility {eligibility:F3}s)");
        // Negative control: a non-moving head must not pass either the unchanged centring oracle
        // or the freeze oracle. Retain the real assignment, command and full transit sample times.
        Sample[] frozen = [.. rig.Samples.GetRange(assignmentIndex, transitEnd - assignmentIndex)
            .Select(s => s with { Yaw = before.Yaw, Pitch = before.Pitch, Residual = 40 })];
        Assert.DoesNotContain(frozen, s => s.Residual <= 2 && s.Time - assigned <= 0.8);
        Assert.True(FindLongestFullFreezeSeconds(frozen, exclusion: null) >= 0.15,
            "A frozen-head mutation over the complete real transit must fail the stall criterion.");
        await rig.ReleaseAsync();
        rig.Finish();
        return [.. rig.Samples];
    }

    /// <summary>AI-009 TR14–15: the actual Begin owns exactly one evaluation before CCDIK/copy/twist/final.</summary>
    [Headless]
    [Fact]
    public async Task BeginStage_AdvancesControllerOnceBeforeRealHeadModifiersAndFinalPose()
    {
        await using Fixture rig = await Fixture.CreateAsync("begin-order", manual: true);
        rig.Eyes.SetLookTarget(rig.Anchor(40, 0, 1.2f));
        await rig.ObserveAsync("engaged", 0.3);
        OrientingAim before = rig.Controller.LastPublishedAim;
        ulong sequence = rig.Controller.LastUpstreamSnapshot!.EvaluationSequence;
        // Process calls and host frame pumping cannot become a second policy clock.
        rig.Controller._Process(0.5);
        rig.Controller._Process(0.5);
        await WaitForFramesAsync(GetSceneTree(), 2);
        Assert.Equal(before, rig.Controller.LastPublishedAim);
        Assert.Equal(sequence, rig.Controller.LastUpstreamSnapshot!.EvaluationSequence);
        await rig.FrameAsync("next-begin");
        Assert.Equal(sequence + 1, rig.Controller.LastUpstreamSnapshot!.EvaluationSequence);
        Assert.NotEqual(before, rig.Controller.LastPublishedAim);
        // FrameAsync asserts the complete native event order and exactly one final evaluation.
        rig.Finish();
    }

    /// <summary>
    /// DIAGNOSTIC one-eye fault sensitivity (TEST-3): corrupting ONLY the right eye's immutable
    /// expected reference by a known 2° must turn the right-eye oracle red by exactly that amount
    /// while the left eye stays green — the bilateral observation cannot be silently blinded by a
    /// single-eye fault. This method is diagnostic by name and never runs in the acceptance path; the
    /// acceptance facts leave <see cref="Fixture.DiagnosticRightEyeExpectedFaultDegrees"/> at zero.
    /// Saccades remain disabled (fixture constructor) and the straight centred anchor keeps the
    /// healthy window aligned, so any divergence is the injected fault alone.
    /// </summary>
    [Fact]
    public async Task Diagnostic_OneEyeExpectedReferenceFault_OnlyThatEyeTurnsRed()
    {
        const float injectedFaultDegrees = 2f;
        await using Fixture rig = await Fixture.CreateAsync("eyes-one-eye-fault");
        await rig.WarmUpAsync();
        await rig.CentreAsync(rig.Anchor(0, 0, 1.4f));
        await rig.ObserveAsync("healthy", 0.3);
        if (rig.Samples[^1] is not { LeftEye: { } healthyLeft, RightEye: { } healthyRight })
        {
            rig.Check(false, "the healthy window must record target-backed observations for both eyes");
            rig.Finish();
            return;
        }

        rig.Check(healthyLeft.ErrorDegrees <= 0.5,
            $"healthy baseline: LEFT eye {healthyLeft.ErrorDegrees:F4}° <=0.5°");
        rig.Check(healthyRight.ErrorDegrees <= 0.5,
            $"healthy baseline: RIGHT eye {healthyRight.ErrorDegrees:F4}° <=0.5°");

        rig.DiagnosticRightEyeExpectedFaultDegrees = injectedFaultDegrees;
        await rig.ObserveAsync("fault", 0.3);
        Sample[] faultSamples = [.. rig.Samples.Where(s => s.Phase == "fault")];
        rig.Check(faultSamples.Length > 0, "the fault window recorded observations");
        foreach (Sample sample in faultSamples)
        {
            if (sample.LeftEye is not { } left || sample.RightEye is not { } right)
            {
                rig.Check(false, "every fault-window observation must be target-backed for both eyes");
                continue;
            }

            rig.Check(left.ErrorDegrees <= 0.5,
                $"fault window: healthy LEFT eye stays green {left.ErrorDegrees:F4}° <=0.5°");
            rig.Check(right.ErrorDegrees is >= 1.9 and <= 2.1,
                $"fault window: faulty RIGHT eye reads the injected fault {right.ErrorDegrees:F4}° "
                    + $"in [1.9, 2.1] (injected {injectedFaultDegrees}°)");
        }

        rig.DiagnosticRightEyeExpectedFaultDegrees = 0f;
        await rig.ReleaseAsync();
        rig.Finish();
    }

    /// <summary>
    /// Approved full-freeze detector (2026-10-12 limits, frozen) with the TEST-6 corrected
    /// designed-dwell exclusion semantics: see <see cref="FullFreezeDetector"/>. Delegates to the
    /// shared detector so the boundary replays exercise the identical acceptance code.
    /// </summary>
    private static double FindLongestFullFreezeSeconds(
        IReadOnlyList<Sample> series,
        DwellExclusion? exclusion = null,
        SaturationParkExclusion? parkExclusion = null)
        => FullFreezeDetector.FindLongestFullFreezeSeconds([.. series.Select(ToFreezeSample)], exclusion, parkExclusion);

    private static FreezeSample ToFreezeSample(Sample sample)
        => new(sample.Time, sample.Yaw, sample.Pitch, sample.Roll, sample.Residual, sample.Influence);

    /// <summary>
    /// One-off stall red validation (2026-10-12, diagnostic removed after the recorded run): with head-modifier
    /// execution suppressed via manual skeleton modifier callback mode while the face-to-anchor residual stayed
    /// at 40.0° with influence above 0.5 and face ranges 0.00000°, the detector fired at 899.109 ms — proving it
    /// detects genuine full freezes. Evidence: temp/ai-009-isolated/stall-red-validation/.
    /// </summary>
    /// <summary>
    /// Side-view stoop evidence record (2026-10-12, capture diagnostic removed after the recorded run): the
    /// pre-tuning pose showed a steep over-curled chain with the head cocked back at the skull base (chin
    /// lifted, +21.58° head extension), the tuned pose a smooth milder curve with the chin continuing down
    /// (−2.73° flexion). Frames and pose-confirmation lines: temp/ai-009-isolated/screens/.
    /// </summary>
    private sealed record HoldObservation(string Phase, double Time, ulong Frame, string Mode,
        double HeldYaw, double HeldPitch, double OutputYaw, double OutputPitch, double SolvedYaw,
        double SolvedPitch, double AnchorYaw, ulong TargetIdentity, float[] TargetPosition,
        ulong PreviousTargetIdentity, float[] PreviousTargetPosition,
        float[] RequestedPosition, float[] MediatedRequestedPosition, float[] MediatedRealisedPosition,
        float[] SolvedHeadPosition, float[] SolvedViewpointPosition);

    private sealed record EyeObservation(Vector3 Actual, Vector3 Expected, double ErrorDegrees);

    private sealed record Sample(string Phase, double Time, long Sequence, ulong Frame, double Yaw, double Pitch,
        double Roll, double EyeYaw, double AnchorYaw, double GazeYaw, double Residual,
        double Head, double Neck, double UpperChest, double Chest, double Spine, float Influence,
        double GazeResidual, double AnchorBearingYaw, Vector3 ViewpointPosition,
        EyeObservation? LeftEye, EyeObservation? RightEye, double CommandYaw, double CommandPitch);

    private sealed partial class Fixture : IAsyncDisposable
    {
        private const string SkeletonPath = "Female/GeneralSkeleton";
        private static readonly string[] _chain = ["Head", "Neck", "UpperChest", "Chest", "Spine"];
        private readonly SceneTree _tree = GetSceneTree();
        private readonly int _oldFps = Engine.MaxFps;
        private readonly int _oldPhysics = Engine.PhysicsTicksPerSecond;
        private readonly List<string> _checks = [];
        private readonly List<string> _failures = [];
        private readonly Basis[] _solved = new Basis[5];
        private readonly Basis[] _neutralJoints = new Basis[5];
        private readonly Basis[] _neutralParents = new Basis[5];
        private readonly string _name;
        private Node3D? _observedHoldTarget;
        private Node3D? _previousHoldTarget;
        private readonly Node3D _root;
        private readonly Node3D _eye;
        private readonly Basis _eyeNeutral;
        private readonly Node3D _rightEye;
        private readonly Basis _rightEyeNeutral;
        private readonly CharacterIK _ik;
        private readonly DynamicPhysicalRig _physical;
        private Basis _neutralFace;
        private Vector3 _neutralOrigin;
        private readonly int[] _indices;
        private int _passes;
        private int _failedCount;
        private Basis _solvedFace = Basis.Identity;
        private long _eventSequence;
        private string? _animatedEvidenceDirectory;
        private readonly List<string> _animatedTrace = [];
        private AnimationPlayer? _nativePlayer;
        private double _previousClipTime;
        private int _nativeLoopCount;
        private Vector3? _firstNativeUpstreamForward;
        private double _maxNativeUpstreamMotionDegrees;

        public void ConfigureAnimatedEvidence()
        {
            string label = System.Environment.GetEnvironmentVariable("ALLEYCAT_ANIMATED_TRANSFORM_EVIDENCE")
                ?? (DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-transform");
            _animatedEvidenceDirectory = ProjectSettings.GlobalizePath("res://temp/ai-009-design-review/animated-eyes/" + label);
            _ = Directory.CreateDirectory(_animatedEvidenceDirectory);
            Camera3D camera = _root.GetChildren().OfType<Camera3D>().Single();
            camera.Reparent(FixtureSkeleton.GetNode<Node3D>("Head"), false);
            camera.Transform = new Transform3D(Basis.Identity, new Vector3(0, 0.033f, 0.4f));
            camera.Projection = Camera3D.ProjectionType.Orthogonal;
            camera.KeepAspect = Camera3D.KeepAspectEnum.Width;
            camera.Size = 0.14f;
            camera.Near = 0.01f;
            File.WriteAllText(Path.Combine(_animatedEvidenceDirectory, "renderer.json"), JsonSerializer.Serialize(new
            {
                GPU = RenderingServer.GetVideoAdapterName(),
                Vendor = RenderingServer.GetVideoAdapterVendor(),
                Method = RenderingServer.GetCurrentRenderingMethod(),
                CameraSize = camera.Size,
                CameraLocal = Matrix(camera.Transform)
            }));
        }

        public void StartNativeIdle()
        {
            _nativePlayer = _root.GetNode<AnimationPlayer>("UpstreamNeutral");
            Animation source = ResourceLoader.Load<Animation>("res://assets/characters/reference/female/animations/Idle.tres");
            var clip = (Animation)source.Duplicate(true);
            for (int track = 0; track < clip.GetTrackCount(); track++)
            {
                clip.TrackSetPath(track, new NodePath(clip.TrackGetPath(track).ToString()
                    .Replace("%GeneralSkeleton", "Female/GeneralSkeleton", StringComparison.Ordinal)));
                Assert.Equal(source.TrackGetKeyCount(track), clip.TrackGetKeyCount(track));
                for (int key = 0; key < clip.TrackGetKeyCount(track); key++)
                {
                    Assert.Equal(source.TrackGetKeyTime(track, key), clip.TrackGetKeyTime(track, key));
                    Assert.Equal(source.TrackGetKeyValue(track, key), clip.TrackGetKeyValue(track, key));
                }
            }
            Assert.Equal(new NodePath("Female/GeneralSkeleton:Head"), clip.TrackGetPath(48));
            Assert.True(clip.TrackGetKeyCount(48) > 1);
            Assert.Equal(3.8333333, clip.Length, precision: 6);
            AnimationLibrary library = new();
            Assert.Equal(Error.Ok, library.AddAnimation("Idle", clip));
            Assert.Equal(Error.Ok, _nativePlayer.AddAnimationLibrary("native", library));
            _nativePlayer.Play("native/Idle");
            Assert.Same(_eye, Eyes.LeftEye);
            Assert.Same(_rightEye, Eyes.RightEye);
            Assert.Equal(0.08f, Eyes.LookSmoothingTime);
            Assert.Equal(35f, Eyes.MaxHorizontalAngleDegrees);
            Assert.Equal(25f, Eyes.MaxVerticalAngleDegrees);
            Assert.Equal(0f, Eyes.SaccadeAmplitude);
        }

        public void MoveAnchor(StaticVisualCue anchor, float yaw, float pitch, float distance)
        {
            float y = Mathf.DegToRad(yaw), p = Mathf.DegToRad(pitch);
            Vector3 direction = new(-Mathf.Sin(y) * Mathf.Cos(p), Mathf.Sin(p), -Mathf.Cos(y) * Mathf.Cos(p));
            anchor.GlobalPosition = _neutralOrigin + (_neutralFace * direction * distance);
        }

        public void WriteAnimatedOutcome(FixationWindowResult result, double overshoot, double assigned,
            double movingStarted, ulong identity)
        {
            Check(_nativeLoopCount >= 2, $"native Idle completed {_nativeLoopCount} loops >=2 through fixed/moving windows");
            Check(_maxNativeUpstreamMotionDegrees > 1,
                $"engine upstream native face moved {_maxNativeUpstreamMotionDegrees:F6}° >1° before head solving");
            File.WriteAllText(Path.Combine(_animatedEvidenceDirectory!, "outcome.json"), JsonSerializer.Serialize(new
            {
                Result = result,
                OvershootDegrees = overshoot,
                Assignment = assigned,
                MovingStart = movingStarted,
                TargetIdentity = identity,
                NativeLoops = _nativeLoopCount,
                NativeUpstreamMotionDegrees = _maxNativeUpstreamMotionDegrees,
                FixedWindowSeconds = movingStarted - assigned,
                End = Samples[^1].Time
            }));
        }

        private static float[] Vector(Vector3 vector) => [vector.X, vector.Y, vector.Z];
        private static float[][] Matrix(Transform3D transform)
            => [Vector(transform.Basis.X), Vector(transform.Basis.Y), Vector(transform.Basis.Z), Vector(transform.Origin)];

        /// <summary>The open direct-fixation window, fed by every post-draw observation.</summary>
        public FixationAcquisitionWindow? ActiveFixationWindow
        {
            get;
            set;
        }

        /// <summary>The exact timestamp and monotonic sequence of the controlled assignment, if any.</summary>
        public (double Time, long Sequence)? Assignment
        {
            get;
            private set;
        }

        /// <summary>
        /// DIAGNOSTIC ONLY (TEST-3 one-eye fault sensitivity; never set by acceptance tests): when
        /// non-zero, the right eye's independently expected reference is rotated by this many degrees
        /// about a perpendicular axis. The right-eye oracle must then read red by exactly this amount
        /// while the left eye stays green, proving the bilateral observation cannot be silently
        /// blinded. Acceptance facts leave this at zero.
        /// </summary>
        public float DiagnosticRightEyeExpectedFaultDegrees
        {
            get;
            set;
        }

        private Fixture(string name, CharacterIK? characterIK = null)
        {
            _name = name;
            _root = (Node3D)ResourceLoader.Load<GDScript>("res://tests/ai009_head_orientation/isolated_female_tracking.gd").New();
            FixtureSkeleton = _root.GetNode<Skeleton3D>(SkeletonPath);
            FixtureViewpoint = FixtureSkeleton.GetNode<Marker3D>("Head/Viewpoint");
            _eye = FixtureSkeleton.GetNode<Node3D>("Head/Eyes/LeftEye");
            _eyeNeutral = _eye.Basis;
            _rightEye = FixtureSkeleton.GetNode<Node3D>("Head/Eyes/RightEye");
            _rightEyeNeutral = _rightEye.Basis;
            // Fail-closed bilateral reference validation (TEST-3): both pre-entry captured neutrals
            // must equal the template's authored transforms — the same scene-state source the legacy
            // transit test loads through — proving the two immutable references describe one authored
            // setup moment rather than live output. Any divergence names the eye and throws.
            Basis authoredLeftNeutral = DirectFixationReferences.LoadAuthoredEyeNeutral(
                DirectFixationReferences.FemaleTemplatePath, "GeneralSkeleton/Head/Eyes/LeftEye");
            Basis authoredRightNeutral = DirectFixationReferences.LoadAuthoredEyeNeutral(
                DirectFixationReferences.FemaleTemplatePath, "GeneralSkeleton/Head/Eyes/RightEye");
            DirectFixationReferences.ValidateCapturedAgainstAuthored(_eyeNeutral, authoredLeftNeutral, "LeftEye");
            DirectFixationReferences.ValidateCapturedAgainstAuthored(
                _rightEyeNeutral, authoredRightNeutral, "RightEye");
            _indices = [.. _chain.Select(FixtureSkeleton.FindBone)];
            Basis skeletonWorld = _root.GetNode<Node3D>("Female").Basis * FixtureSkeleton.Basis;
            _neutralFace = (skeletonWorld * FixtureSkeleton.GetBoneGlobalRest(_indices[0]).Basis * FixtureViewpoint.Basis).Orthonormalized();
            for (int i = 0; i < 5; i++)
            {
                _solved[i] = FixtureSkeleton.GetBoneRest(_indices[i]).Basis.Orthonormalized();
                _neutralJoints[i] = _solved[i];
                _neutralParents[i] = skeletonWorld * FixtureSkeleton.GetBoneGlobalRest(FixtureSkeleton.GetBoneParent(_indices[i])).Basis;
            }
            _physical = new DynamicPhysicalRig { Name = "DynamicPhysicalRig", TargetSkeleton = FixtureSkeleton, Enabled = false };
            FixtureSkeleton.AddChild(_physical);
            AnimationTree eyeTree = CreateEyeTree(_root);
            Eyes = new EyesBehaviour
            {
                Name = "Eyes",
                EyeOrigin = FixtureViewpoint,
                LeftEye = _eye,
                RightEye = FixtureSkeleton.GetNode<Node3D>("Head/Eyes/RightEye"),
                AnimationTree = eyeTree,
                MaxHorizontalAngleDegrees = 35,
                MaxVerticalAngleDegrees = 25,
                LookSmoothingTime = 0.08f,
                SaccadeAmplitude = 0,
                SaccadeInterval = 0,
                MinimumBlinkInterval = 1000,
                MaximumBlinkInterval = 1000,
            };
            _root.AddChild(Eyes);
            TestCharacter character = new(Eyes);
            TestMind mind = new(character)
            {
                Name = "Mind"
            };
            Controller = new OrientingController
            {
                Name = "OrientingController",
                Viewpoint = FixtureViewpoint,
                NeckPivot = FixtureSkeleton.GetNode<Marker3D>("Neck/NeckPivot")
            };
            mind.AddChild(Controller);
            _root.AddChild(mind);
            _ik = characterIK ?? new CharacterIK();
            _ik.Name = "CharacterIK";
            _ik.Viewpoint = FixtureViewpoint;
            _ik.PhysicalRig = _physical;
            _ik.HeadTargetIntentProvider = Controller;
            _ik.HeadIKTarget = _root.GetNode<CharacterBody3D>("IKTargets/Head");
            _ik.HeadIKSolveTarget = _root.GetNode<Marker3D>("IKTargets/HeadSolve");
            _ik.RightHandIKTarget = _root.GetNode<AnimatableBody3D>("IKTargets/RightHand");
            _ik.LeftHandIKTarget = _root.GetNode<AnimatableBody3D>("IKTargets/LeftHand");
            _ik.HeadModifierGroup = [ FixtureSkeleton.GetNode<SkeletonModifier3D>("NeckSpineIK"),
                FixtureSkeleton.GetNode<SkeletonModifier3D>("HeadCopyRotation"),
                FixtureSkeleton.GetNode<SkeletonModifier3D>("NeckTwistDisperser") ];
            _root.AddChild(_ik);
            FixtureSkeleton.SkeletonUpdated += CaptureSolvedJoints;
            _root.AddChild(LoadPackedScene("res://assets/testing/test_environment.tscn").Instantiate());
            _root.AddChild(new Camera3D
            {
                Name = "TrackingCamera",
                Position = new Vector3(0, 1.48f, -0.8f),
                Rotation = new Vector3(0, Mathf.Pi, 0),
                Current = true
            });
        }

        public EyesBehaviour Eyes
        {
            get;
        }

        /// <summary>The real orienting controller under test (diagnostics for fold-domain facts).</summary>
        public OrientingController Controller
        {
            get;
        }

        /// <summary>The fixture skeleton (authored-rest oracles for fold-domain facts).</summary>
        public Skeleton3D FixtureSkeleton
        {
            get;
        }

        /// <summary>The chain's Head bone index (authored-rest oracles for fold-domain facts).</summary>
        public int HeadBoneIndex => _indices[0];

        /// <summary>The viewpoint marker (authored-rest oracles for fold-domain facts).</summary>
        public Marker3D FixtureViewpoint
        {
            get;
        }

        /// <summary>Authored horizontal comfort cone, degrees — the geometric residual a saturation park holds.</summary>
        public float ComfortConeHorizontalDegrees => Controller.ComfortConeHorizontalDegrees;

        /// <summary>Authored centring delay, seconds — saturation-park eligibility after assignment.</summary>
        public float CentringDelaySeconds => Controller.CentringDelaySeconds;
        public List<Sample> Samples { get; } = [];
        public static double Now => Time.GetTicksUsec() / 1e6;
        private bool _manual;
        private double _simulationTime;
        private const double SimulationDelta = 1d / 90d;
        private double Clock => _manual ? _simulationTime : Now;
        private Action<double>? _manualEyes;
        private readonly List<string> _manualStages = [];
        private string? _manualEvidenceDirectory;
        public bool PerturbHostPacing
        {
            get;
            set;
        }

        public float CheckHeadInfluence(string phase, bool requireFull = false, bool requireZero = false)
        {
            float influence = Controller.GetTargetIntent().DesiredInfluence;
            foreach (SkeletonModifier3D modifier in _ik.HeadModifierGroup)
            {
                Check(Mathf.IsEqualApprox(modifier.Influence, influence),
                    $"{phase} {modifier.Name} applied {modifier.Influence:R} equals NPC intent {influence:R}");
                Check(modifier.Active == (influence > 0),
                    $"{phase} {modifier.Name} active={modifier.Active} iff influence>0");
            }

            if (requireFull)
            {
                Check(influence == 1, $"full influence control {influence:R}=1");
            }
            if (requireZero)
            {
                Check(influence == 0, $"zero influence control {influence:R}=0");
            }
            return influence;
        }

        public void CheckFrozenUpstream()
        {
            OrientingUpstreamSnapshot upstream = Controller.LastUpstreamSnapshot
                ?? throw new InvalidOperationException("Partial NPC influence requires real upstream capture.");
            Check(Degrees((-upstream.UpstreamFaceBasisWorld.Z).AngleTo(-_neutralFace.Z)) < 0.05,
                "partial influence does not recursively alter frozen upstream forward <0.05°");
        }

        public HoldObservation ObserveHold(string phase)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var policy = (OrientingPolicy)(typeof(OrientingController).GetField("_policy", flags)!
                .GetValue(Controller) ?? throw new InvalidOperationException("The real controller has no policy."));
            double Read(string field)
            {
                return (double)typeof(OrientingPolicy).GetField(field, flags)!.GetValue(policy)!;
            }
            string mode = typeof(OrientingPolicy).GetField("_mode", flags)!.GetValue(policy)!.ToString()!;
            OrientingAim output = Controller.LastPublishedAim;
            IKTargetPipelineResult pipeline = _ik.HeadTargetPipelineDebugState;
            Sample sample = Samples[^1];
            Node3D? target = Eyes.LookTarget;
            if (target is not null && target != _observedHoldTarget)
            {
                _previousHoldTarget = _observedHoldTarget;
                _observedHoldTarget = target;
            }
            return new HoldObservation(phase, Now, Engine.GetProcessFrames(), mode,
                Read("_heldHorizontalRadians") * 180 / Math.PI, Read("_heldVerticalRadians") * 180 / Math.PI,
                output.HorizontalRadians * 180 / Math.PI, output.VerticalRadians * 180 / Math.PI,
                sample.Yaw, sample.Pitch, sample.AnchorYaw, target?.GetInstanceId() ?? 0,
                target is null ? [] : Vector(target.GlobalPosition),
                _previousHoldTarget?.GetInstanceId() ?? 0,
                _previousHoldTarget is null ? [] : Vector(_previousHoldTarget.GlobalPosition),
                Vector(Controller.GetTargetIntent().WorldTransform.Origin), Vector(pipeline.RequestedTarget.Origin),
                Vector(pipeline.RealisedTarget.Origin), Vector((FixtureSkeleton.GlobalTransform
                    * FixtureSkeleton.GetBoneGlobalPose(_indices[0])).Origin), Vector(FixtureViewpoint.GlobalPosition));
        }

        public static async Task<Fixture> CreateAsync(string name, CharacterIK? characterIK = null, bool manual = false)
        {
            Fixture rig = new(name, characterIK ?? (manual ? new ManualTrackingCharacterIK() : null))
            {
                _manual = manual
            };
            Engine.MaxFps = manual ? 0 : 90;
            // Keep project physics scheduling; 90fps is only the rendering cap, not a measured cadence claim.
            rig._tree.Root.AddChild(rig._root);
            if (manual)
            {
                Assert.Equal(Skeleton3D.ModifierCallbackModeProcessEnum.Idle,
                    rig.FixtureSkeleton.ModifierCallbackModeProcess);
                rig._manualEvidenceDirectory = ProjectSettings.GlobalizePath(
                    "res://temp/ai-009-design-review/deterministic-regression-repair/"
                    + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ") + "-" + name);
                rig._manualEyes = typeof(EyesBehaviour).GetMethod("ApplyLateGazeComposition",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action<double>>(rig.Eyes);
                ((ManualTrackingCharacterIK)rig._ik).Observe = rig._manualStages.Add;
                foreach (SkeletonModifier3D modifier in rig._ik.HeadModifierGroup)
                {
                    modifier.ModificationProcessed += () => rig._manualStages.Add(modifier.Name);
                }
                rig.FixtureSkeleton.SkeletonUpdated += () => rig._manualStages.Add("final");
                rig.FixtureSkeleton.ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Manual;
                rig.Eyes.SetProcess(false);
                rig.Eyes.SetPhysicsProcess(false);
                rig.Controller.SetProcess(false);
                AnimationPlayer upstream = rig._root.GetNode<AnimationPlayer>("UpstreamNeutral");
                upstream.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
                upstream.Play("Neutral");
                upstream.Advance(0);
                // CharacterIK attaches its begin/provider runtime stages lazily from _PhysicsProcess
                // (TryInitialiseRuntimeBindings; the _Ready path only runs outside the tree), so a
                // process-frame-only wait cannot guarantee attachment before the first manual flush —
                // physics ticks are wall-clock paced and may not land within those frames. Guarantee
                // one physics tick, then keep the process-frame wait for the deferred-call flush that
                // follows tree entry.
                await WaitForPhysicsFramesAsync(rig._tree, 1);
                await WaitForFramesAsync(rig._tree, 2);
            }
            else
            {
                await WaitForPhysicsFramesAsync(rig._tree, 3);
            }
            await rig.ObserveAsync("initial_neutral", 1.6);
            rig._neutralFace = rig.FixtureViewpoint.GlobalBasis.Orthonormalized();
            rig._neutralOrigin = rig.FixtureViewpoint.GlobalPosition;
            for (int i = 0; i < _chain.Length; i++)
            {
                rig._neutralJoints[i] = rig._solved[i];
                int parent = rig.FixtureSkeleton.GetBoneParent(rig._indices[i]);
                rig._neutralParents[i] = rig.FixtureSkeleton.GlobalBasis * rig.FixtureSkeleton.GetBoneGlobalRest(parent).Basis;
            }
            rig.AssertBoundary();
            return rig;
        }

        public async Task WarmUpAsync()
        {
            await CentreAsync(Anchor(0, 0, 1.4f));
            await ObserveAsync("warmup_hold", 1.6);
            await ReleaseAsync();
            _neutralFace = FixtureViewpoint.GlobalBasis.Orthonormalized();
            _neutralOrigin = FixtureViewpoint.GlobalPosition;
        }

        public StaticVisualCue Anchor(float yaw, float pitch, float distance)
        {
            float y = Mathf.DegToRad(yaw), p = Mathf.DegToRad(pitch);
            Vector3 direction = new(-Mathf.Sin(y) * Mathf.Cos(p), Mathf.Sin(p), -Mathf.Cos(y) * Mathf.Cos(p));
            StaticVisualCue anchor = new()
            {
                Name = "Anchor"
            };
            _root.AddChild(anchor);
            anchor.GlobalPosition = _neutralOrigin + (_neutralFace * direction * distance);
            return anchor;
        }

        public async Task CentreAsync(StaticVisualCue anchor)
        {
            Eyes.SetLookTarget(anchor);
            await CentreCurrentAsync();
        }

        public async Task CentreCurrentAsync()
        {
            double start = Clock;
            do
            {
                await FrameAsync("centring");
                if (Samples[^1].Residual < 2)
                {
                    return;
                }
            } while (Clock - start < 8);
            Check(false, $"SETUP current anchor never centres <2°: {Samples[^1].Residual:F4}°");
        }

        public async Task ReleaseAsync()
        {
            Eyes.ClearLookTarget();
            double start = Clock;
            do
            {
                await FrameAsync("release");
            } while (Clock - start < 1.6);
            Sample end = Samples[^1];
            Check(end.Influence < 0.01f, $"release influence {end.Influence:F5} <0.01");
            // Compare actual attachment forward with declared upstream rest, not the last aimed output.
            Basis restFace = (FixtureSkeleton.GlobalBasis * FixtureSkeleton.GetBoneGlobalRest(_indices[0]).Basis * FixtureViewpoint.Basis).Orthonormalized();
            Check(Degrees((-restFace.Z).AngleTo(-FixtureViewpoint.GlobalBasis.Z)) < 0.3,
                $"release actual face restores upstream rest: {Degrees((-restFace.Z).AngleTo(-FixtureViewpoint.GlobalBasis.Z)):F4}° <0.3°");
        }

        public async Task ObserveAsync(string phase, double duration)
        {
            double start = Clock;
            do
            {
                await FrameAsync(phase);
            } while (Clock - start < duration);
        }

        public async Task FrameAsync(string phase)
        {
            if (_manual)
            {
                if (PerturbHostPacing && Samples.Count % 47 == 0)
                {
                    OrientingAim before = Controller.LastPublishedAim;
                    await WaitForFramesAsync(_tree, 2);
                    Assert.Equal(before, Controller.LastPublishedAim);
                }
                _manualStages.Clear();
                int passes = _passes;
                ulong previous = Controller.LastUpstreamSnapshot?.EvaluationSequence ?? 0;
                _root.GetNode<AnimationPlayer>("UpstreamNeutral").Advance(SimulationDelta);
                FixtureSkeleton.Advance(SimulationDelta);
                // Flush the same native skeleton notification that deferred Advance dispatches.
                // This runs Begin, CCDIK, copy, twist and final SkeletonUpdated without host pacing.
                FixtureSkeleton.Notification((int)Skeleton3D.NotificationUpdateSkeleton);
                string[] expectedStages = Controller.LastPublishedAim.Influence > 0
                    ? ["begin", "provider", "NeckSpineIK", "HeadCopyRotation", "NeckTwistDisperser", "final"]
                    : ["begin", "provider", "final"];
                Assert.Equal(expectedStages, _manualStages);
                Assert.Equal(passes + 1, _passes);
                Assert.Equal(previous + 1, Controller.LastUpstreamSnapshot!.EvaluationSequence);
                _manualEyes!(SimulationDelta);
                _simulationTime += SimulationDelta;
            }
            else
            {
                _ = await _tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            }
            Basis face = FixtureViewpoint.GlobalBasis.Orthonormalized();
            Check(Degrees((-_solvedFace.Z).AngleTo(-face.Z)) < 0.05,
                "measurement final skeletal face and rendered attachment forward agree <0.05°");
            Vector3 relativeForward = _neutralFace.Inverse() * -face.Z;
            Vector3 expectedRight = Vector3.Up.Cross(face.Z).Normalized();
            double roll = Degrees(expectedRight.SignedAngleTo(face.X, -face.Z));
            // Remove each eye's authored toe-out, then transport the actual transform delta into world face space.
            Basis parent = ((Node3D)_eye.GetParent()).GlobalBasis.Orthonormalized();
            Basis gazeDelta = parent * _eye.Basis.Orthonormalized() * _eyeNeutral.Orthonormalized().Inverse() * parent.Inverse();
            Vector3 gaze = gazeDelta * -face.Z;
            Vector3 eyeForward = face.Inverse() * gaze;
            Node3D? anchor = Eyes.LookTarget;
            Vector3 anchorDirection = anchor is null ? -face.Z : (anchor.GlobalPosition - FixtureViewpoint.GlobalPosition).Normalized();
            Vector3 anchorInFace = face.Inverse() * anchorDirection;
            double[] contributions = new double[5];
            for (int i = 0; i < 5; i++)
            {
                Basis delta = _neutralParents[i] * (_solved[i] * _neutralJoints[i].Inverse()) * _neutralParents[i].Inverse();
                contributions[i] = Pitch(_neutralFace.Inverse() * delta * -_neutralFace.Z);
            }

            // Direct-fixation oracle: independently expected constrained directions for both eyes from
            // the shared solved eye origin and immutable authored optical references captured before
            // any gaze (no production gaze helper, no live calibration).
            EyeObservation? leftEye = null;
            EyeObservation? rightEye = null;
            if (anchor is not null)
            {
                Quaternion originRotation = face.GetRotationQuaternion();
                Quaternion constrained = DirectFixationGeometry.ConstrainedGazeRotation(
                    originRotation.Inverse() * (anchor.GlobalPosition - FixtureViewpoint.GlobalPosition));
                leftEye = ObserveEye(_eye, _eyeNeutral, originRotation, constrained);
                rightEye = ObserveEye(_rightEye, _rightEyeNeutral, originRotation, constrained);
                if (DiagnosticRightEyeExpectedFaultDegrees != 0f && rightEye is { } faulty)
                {
                    rightEye = ApplyDiagnosticReferenceFault(faulty, DiagnosticRightEyeExpectedFaultDegrees);
                }
            }

            double time = Clock;
            long sequence = ++_eventSequence;
            OrientingAim command = Controller.LastPublishedAim;
            Samples.Add(new Sample(phase, time, sequence, Engine.GetProcessFrames(), Yaw(relativeForward), Pitch(relativeForward), roll,
                Yaw(eyeForward), Yaw(anchorInFace), Yaw(_neutralFace.Inverse() * gaze),
                Degrees((-face.Z).AngleTo(anchorDirection)), contributions[0], contributions[1], contributions[2],
                contributions[3], contributions[4], Controller.GetTargetIntent().DesiredInfluence,
                Degrees(gaze.AngleTo(anchorDirection)), Yaw(_neutralFace.Inverse() * anchorDirection),
                FixtureViewpoint.GlobalPosition, leftEye, rightEye,
                Mathf.RadToDeg((float)command.HorizontalRadians), Mathf.RadToDeg((float)command.VerticalRadians)));
            if (_animatedEvidenceDirectory is not null && _nativePlayer is not null)
            {
                double clipTime = _nativePlayer.CurrentAnimationPosition;
                if (clipTime < _previousClipTime)
                {
                    _nativeLoopCount++;
                }
                _previousClipTime = clipTime;
                OrientingUpstreamSnapshot upstream = Controller.LastUpstreamSnapshot
                    ?? throw new InvalidOperationException("Native animation requires a real engine upstream snapshot.");
                Vector3 upstreamForward = -upstream.UpstreamFaceBasisWorld.Z;
                _firstNativeUpstreamForward ??= upstreamForward;
                _maxNativeUpstreamMotionDegrees = Math.Max(_maxNativeUpstreamMotionDegrees,
                    Degrees(_firstNativeUpstreamForward.Value.AngleTo(upstreamForward)));
                Camera3D camera = FixtureSkeleton.GetNode<Camera3D>("Head/TrackingCamera");
                _animatedTrace.Add(JsonSerializer.Serialize(new
                {
                    Phase = phase,
                    Time = time,
                    Sequence = sequence,
                    Frame = Engine.GetProcessFrames(),
                    PhysicsFrame = Engine.GetPhysicsFrames(),
                    ClipTime = clipTime,
                    Loops = _nativeLoopCount,
                    UpstreamFace = Matrix(new Transform3D(upstream.UpstreamFaceBasisWorld, upstream.HeadOriginWorld)),
                    UpstreamEvaluationSequence = upstream.EvaluationSequence,
                    HeadBone = Matrix(FixtureSkeleton.GetBoneGlobalPose(_indices[0])),
                    Viewpoint = Matrix(FixtureViewpoint.GlobalTransform),
                    Camera = Matrix(camera.GlobalTransform),
                    LeftLocal = Matrix(_eye.Transform),
                    RightLocal = Matrix(_rightEye.Transform),
                    LeftGlobal = Matrix(_eye.GlobalTransform),
                    RightGlobal = Matrix(_rightEye.GlobalTransform),
                    Target = anchor is null ? null : Vector(anchor.GlobalPosition),
                    TargetIdentity = anchor?.GetInstanceId(),
                    LeftActual = leftEye is null ? null : Vector(leftEye.Actual),
                    LeftExpected = leftEye is null ? null : Vector(leftEye.Expected),
                    RightActual = rightEye is null ? null : Vector(rightEye.Actual),
                    RightExpected = rightEye is null ? null : Vector(rightEye.Expected),
                    RequestedHorizontal = Eyes.GetHorizontalLookSeekTime(),
                    RequestedVertical = Eyes.GetVerticalLookSeekTime()
                }));
            }
            if (ActiveFixationWindow is { } fixationWindow && leftEye is { } left && rightEye is { } right)
            {
                fixationWindow.AddPresentation(new FixationPresentation(
                    time,
                    sequence,
                    new EyeFixationSample(left.Actual, left.Expected),
                    new EyeFixationSample(right.Actual, right.Expected)));
            }
        }

        /// <summary>
        /// Assigns the look target at the controlled point between frames: the caller invokes this right
        /// after a post-draw observation returned and before awaiting any further frame, so the next
        /// presentation is deterministically eligible frame 1. Records the exact assignment timestamp,
        /// monotonic sequence and pre-assignment presented directions, and opens the direct-fixation
        /// window that every later observation feeds.
        /// </summary>
        public FixationAcquisitionWindow AssignLookTargetControlled(StaticVisualCue anchor)
        {
            Eyes.SetLookTarget(anchor);
            double time = Clock;
            long sequence = ++_eventSequence;
            Sample pre = Samples[^1];
            if (pre.LeftEye is null || pre.RightEye is null)
            {
                throw new InvalidOperationException(
                    "The controlled assignment requires a target-backed pre-assignment observation.");
            }

            Assignment = (time, sequence);
            return ActiveFixationWindow = new FixationAcquisitionWindow(
                time,
                sequence,
                pre.LeftEye.Actual,
                pre.RightEye.Actual);
        }

        /// <summary>
        /// Measurement-validity baseline: the independent oracle must read both eyes aligned on the last
        /// pre-assignment observation (a settled, centred straight anchor) before any acceptance window opens.
        /// </summary>
        public void CheckPreAssignmentAlignment()
        {
            Sample pre = Samples[^1];
            if (pre.LeftEye is not { } left || pre.RightEye is not { } right)
            {
                Check(false, "pre-assignment reference requires an observed target-backed eye sample");
                return;
            }

            Check(left.ErrorDegrees <= 0.5,
                $"oracle baseline: left eye {left.ErrorDegrees:F4}° <=0.5° against the straight anchor");
            Check(right.ErrorDegrees <= 0.5,
                $"oracle baseline: right eye {right.ErrorDegrees:F4}° <=0.5° against the straight anchor");
        }

        private static EyeObservation ObserveEye(
            Node3D eye,
            Basis authoredNeutral,
            Quaternion originRotation,
            Quaternion constrained)
        {
            Quaternion parentRotation = ((Node3D)eye.GetParent()).GlobalBasis.Orthonormalized().GetRotationQuaternion();
            Vector3 neutralOptical = DirectFixationGeometry.NeutralOpticalDirection(
                parentRotation,
                authoredNeutral.Orthonormalized().GetRotationQuaternion(),
                DirectFixationGeometry.OpticalForward);
            Vector3 expected = DirectFixationGeometry.ExpectedEyeDirection(originRotation, constrained, neutralOptical);
            Vector3 actual = eye.GlobalBasis.Orthonormalized().GetRotationQuaternion() * DirectFixationGeometry.OpticalForward;
            return new EyeObservation(actual, expected, DirectFixationGeometry.AngleBetweenDegrees(actual, expected));
        }

        /// <summary>
        /// DIAGNOSTIC ONLY (TEST-3): rotates one eye's independently expected reference by a known
        /// angle about a perpendicular axis, so the per-eye oracle must read red by exactly that angle.
        /// </summary>
        private static EyeObservation ApplyDiagnosticReferenceFault(EyeObservation observation, float faultDegrees)
        {
            Vector3 axis = observation.Expected.Cross(Vector3.Up);
            axis = axis.LengthSquared() < 1e-10f ? Vector3.Right : axis.Normalized();
            Vector3 faulted = observation.Expected.Rotated(axis, Mathf.DegToRad(faultDegrees));
            return observation with
            {
                Expected = faulted,
                ErrorDegrees = DirectFixationGeometry.AngleBetweenDegrees(observation.Actual, faulted),
            };
        }

        private void CaptureSolvedJoints()
        {
            _passes++;
            _solvedFace = (FixtureSkeleton.GlobalBasis * FixtureSkeleton.GetBoneGlobalPose(_indices[0]).Basis * FixtureViewpoint.Basis).Orthonormalized();
            for (int i = 0; i < 5; i++)
            {
                int bone = _indices[i];
                Basis parent = FixtureSkeleton.GetBoneGlobalPose(FixtureSkeleton.GetBoneParent(bone)).Basis.Orthonormalized();
                _solved[i] = parent.Inverse() * FixtureSkeleton.GetBoneGlobalPose(bone).Basis.Orthonormalized();
            }
        }

        private void AssertBoundary()
        {
            _ = Assert.Single(_root.FindChildren("*", "Skeleton3D", true, false));
            Assert.Contains(_root.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>(), m => m.Skin is not null);
            Assert.Equal(0, _physical.GeneratedProxyCount);
            Assert.False(_physical.Enabled);
            Assert.Null(_ik.LeftHandIKTargetIntentProvider);
            Assert.Null(_ik.RightHandIKTargetIntentProvider);
            Assert.Empty(_ik.LeftHandModifierGroup);
            Assert.Empty(_ik.RightHandModifierGroup);
            Assert.Empty(_ik.LeftFootModifierGroup);
            Assert.Empty(_ik.RightFootModifierGroup);
            Assert.DoesNotContain(_root.FindChildren("*", "Node", true, false), n =>
                n.Name.ToString().Contains("Installer", StringComparison.Ordinal) || n is NavigationAgent3D);
            foreach (SkeletonModifier3D modifier in FixtureSkeleton.GetChildren().OfType<SkeletonModifier3D>())
            {
                Assert.True(_ik.HeadModifierGroup.Contains(modifier) || modifier.GetType().DeclaringType == typeof(CharacterIK),
                    $"Unexpected modifier {modifier.Name}/{modifier.GetType()}");
            }

            Assert.True(_ik.HeadModifierGroup[0].GetIndex() < _ik.HeadModifierGroup[1].GetIndex()
                && _ik.HeadModifierGroup[1].GetIndex() < _ik.HeadModifierGroup[2].GetIndex());
            Assert.True(_passes > 0, "Actual engine modifier pass must have executed.");
        }

        public void Check(bool pass, string detail)
        {
            if (!pass)
            {
                _failedCount++;
            }

            if (_checks.Count < 10000)
            {
                _checks.Add($"{(pass ? "PASS" : "FAIL")} {detail}");
            }

            if (!pass && _failures.Count < 20)
            {
                _failures.Add(detail);
            }
        }

        public void Finish()
        {
            AssertBoundary();
            WriteEvidence();
            Assert.True(_failedCount == 0, $"{_failedCount} failed checks. " + string.Join("; ", _failures));
        }

        private void WriteEvidence()
        {
            string directory = _manualEvidenceDirectory ?? _animatedEvidenceDirectory ?? ProjectSettings.GlobalizePath("res://temp/ai-009-isolated");
            _ = Directory.CreateDirectory(directory);
            if (_manual)
            {
                File.WriteAllText(Path.Combine(directory, "simulation.json"), JsonSerializer.Serialize(new
                {
                    DeltaSeconds = SimulationDelta,
                    Steps = Samples.Count,
                    SimulatedSeconds = _simulationTime,
                    PerturbHostPacing,
                    HostYieldEverySteps = PerturbHostPacing ? 47 : 0,
                    NativeStages = new[] { "begin", "provider", "NeckSpineIK", "HeadCopyRotation", "NeckTwistDisperser", "final" },
                    Observation = "Final skeletal and optical state; no rendered-image claim"
                }));
            }
            using StreamWriter writer = new(Path.Combine(directory, $"{_name}.csv"));
            writer.WriteLine(
                "phase,time_s,sequence,frame,face_yaw,face_pitch,roll,eye_yaw,anchor_yaw,world_gaze_yaw,residual,Head,Neck,UpperChest,Chest,Spine,influence,gaze_residual,anchor_bearing_yaw,vp_x,vp_y,vp_z,left_eye_err,right_eye_err,left_actual_x,left_actual_y,left_actual_z,left_expected_x,left_expected_y,left_expected_z,right_actual_x,right_actual_y,right_actual_z,right_expected_x,right_expected_y,right_expected_z");
            foreach (Sample s in Samples)
            {
                writer.WriteLine(FormattableString.Invariant(
                    $"{s.Phase},{s.Time:F6},{s.Sequence},{s.Frame},{s.Yaw:F6},{s.Pitch:F6},{s.Roll:F6},{s.EyeYaw:F6},{s.AnchorYaw:F6},{s.GazeYaw:F6},{s.Residual:F6},{s.Head:F6},{s.Neck:F6},{s.UpperChest:F6},{s.Chest:F6},{s.Spine:F6},{s.Influence:F6},{s.GazeResidual:F6},{s.AnchorBearingYaw:F6},{s.ViewpointPosition.X:F6},{s.ViewpointPosition.Y:F6},{s.ViewpointPosition.Z:F6},{EyeColumn(s.LeftEye, o => o.ErrorDegrees)},{EyeColumn(s.RightEye, o => o.ErrorDegrees)},{EyeColumn(s.LeftEye, o => o.Actual.X)},{EyeColumn(s.LeftEye, o => o.Actual.Y)},{EyeColumn(s.LeftEye, o => o.Actual.Z)},{EyeColumn(s.LeftEye, o => o.Expected.X)},{EyeColumn(s.LeftEye, o => o.Expected.Y)},{EyeColumn(s.LeftEye, o => o.Expected.Z)},{EyeColumn(s.RightEye, o => o.Actual.X)},{EyeColumn(s.RightEye, o => o.Actual.Y)},{EyeColumn(s.RightEye, o => o.Actual.Z)},{EyeColumn(s.RightEye, o => o.Expected.X)},{EyeColumn(s.RightEye, o => o.Expected.Y)},{EyeColumn(s.RightEye, o => o.Expected.Z)}"));
            }

            File.WriteAllLines(Path.Combine(directory, $"{_name}-checks.txt"), _checks);
            if (_animatedEvidenceDirectory is not null)
            {
                File.WriteAllLines(Path.Combine(directory, "presentations.jsonl"), _animatedTrace);
            }
            if (Assignment is { } assignment)
            {
                File.WriteAllLines(
                    Path.Combine(directory, $"{_name}-assignment.txt"),
                    [$"assignment_time_s={assignment.Time:F6}", $"assignment_sequence={assignment.Sequence}"]);
            }
        }

        private static string EyeColumn(EyeObservation? observation, Func<EyeObservation, double> value)
            => observation is null ? "NaN" : FormattableString.Invariant($"{value(observation):F6}");

        public async ValueTask DisposeAsync()
        {
            ulong rootIdentity = _root.GetInstanceId();
            Exception? failure = null;
            Attempt(WriteEvidence);
            Attempt(() => FixtureSkeleton.SkeletonUpdated -= CaptureSolvedJoints);
            Attempt(_root.QueueFree);
            Attempt(() => Engine.MaxFps = _oldFps);
            Attempt(() => Engine.PhysicsTicksPerSecond = _oldPhysics);
            try
            {
                await WaitForFramesAsync(_tree, 2);
            }
            catch (Exception error)
            {
                failure ??= error;
            }
            if (failure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }

            void Attempt(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception error)
                {
                    failure ??= error;
                }
            }
        }

        private static double Degrees(float radians) => Mathf.RadToDeg(radians);
        private static double Pitch(Vector3 forward) => Degrees(Mathf.Atan2(forward.Y, Mathf.Sqrt((forward.X * forward.X) + (forward.Z * forward.Z))));
        private static double Yaw(Vector3 forward) => Degrees(Mathf.Atan2(-forward.X, -forward.Z));

        private static AnimationTree CreateEyeTree(Node3D root)
        {
            AnimationPlayer player = new()
            {
                Name = "EyeAnimationPlayer",
                RootNode = new NodePath("../Female")
            };
            _ = player.AddAnimationLibrary("eyes", ResourceLoader.Load<AnimationLibrary>(
                "res://assets/characters/reference/female/animations/eyes/eyes.tres"));
            AnimationLibrary neutralLibrary = new();
            _ = neutralLibrary.AddAnimation("Neutral", new Animation { Length = 1, LoopMode = Animation.LoopModeEnum.Linear });
            _ = player.AddAnimationLibrary("fixture", neutralLibrary);
            root.AddChild(player);
            AnimationNodeBlendTree blend = new();
            blend.AddNode("Neutral", new AnimationNodeAnimation { Animation = "fixture/Neutral" });
            blend.AddNode(EyesAnimationTreePaths.HorizontalLookAnimationNode,
                new AnimationNodeAnimation { Animation = EyesAnimationTreePaths.HorizontalLookAnimationName });
            blend.AddNode(EyesAnimationTreePaths.VerticalLookAnimationNode,
                new AnimationNodeAnimation { Animation = EyesAnimationTreePaths.VerticalLookAnimationName });
            blend.AddNode(EyesAnimationTreePaths.HorizontalLookSeekNode, new AnimationNodeTimeSeek());
            blend.AddNode(EyesAnimationTreePaths.VerticalLookSeekNode, new AnimationNodeTimeSeek());
            blend.AddNode(EyesAnimationTreePaths.HorizontalLookBlendNode, new AnimationNodeBlend2());
            blend.AddNode(EyesAnimationTreePaths.VerticalLookBlendNode, new AnimationNodeBlend2());
            blend.AddNode(EyesAnimationTreePaths.BlinkAnimationNode, new AnimationNodeAnimation { Animation = EyesAnimationTreePaths.BlinkAnimationName });
            blend.AddNode(EyesAnimationTreePaths.BlinkTimeScaleNode, new AnimationNodeTimeScale());
            blend.AddNode(EyesAnimationTreePaths.BlinkOneShotNode, new AnimationNodeOneShot());
            blend.ConnectNode(EyesAnimationTreePaths.HorizontalLookSeekNode, 0, EyesAnimationTreePaths.HorizontalLookAnimationNode);
            blend.ConnectNode(EyesAnimationTreePaths.VerticalLookSeekNode, 0, EyesAnimationTreePaths.VerticalLookAnimationNode);
            blend.ConnectNode(EyesAnimationTreePaths.HorizontalLookBlendNode, 0, "Neutral");
            blend.ConnectNode(EyesAnimationTreePaths.HorizontalLookBlendNode, 1, EyesAnimationTreePaths.HorizontalLookSeekNode);
            blend.ConnectNode(EyesAnimationTreePaths.VerticalLookBlendNode, 0, EyesAnimationTreePaths.HorizontalLookBlendNode);
            blend.ConnectNode(EyesAnimationTreePaths.VerticalLookBlendNode, 1, EyesAnimationTreePaths.VerticalLookSeekNode);
            blend.ConnectNode(EyesAnimationTreePaths.BlinkTimeScaleNode, 0, EyesAnimationTreePaths.BlinkAnimationNode);
            blend.ConnectNode(EyesAnimationTreePaths.BlinkOneShotNode, 0, EyesAnimationTreePaths.VerticalLookBlendNode);
            blend.ConnectNode(EyesAnimationTreePaths.BlinkOneShotNode, 1, EyesAnimationTreePaths.BlinkTimeScaleNode);
            blend.ConnectNode("output", 0, EyesAnimationTreePaths.BlinkOneShotNode);
            AnimationTree tree = new()
            {
                Name = "EyeAnimationTree",
                TreeRoot = blend,
                AnimPlayer = new NodePath("../EyeAnimationPlayer"),
                RootNode = new NodePath("../Female"),
                Active = true
            };
            root.AddChild(tree);
            return tree;
        }
    }

    private sealed partial class ManualTrackingCharacterIK : CharacterIK
    {
        public Action<string>? Observe
        {
            get;
            set;
        }
        protected override void BeforeProviderTargetProcessing() => Observe?.Invoke("begin");
        protected override void AfterProviderTargetProcessing(Skeleton3D skeleton, double delta) => Observe?.Invoke("provider");
    }

    private sealed partial class TestMind(ICharacter owner) : MindBase
    {
        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    private sealed class TestCharacter(EyesBehaviour eyes) : ICharacter, IComponentProjectionNotifier
    {
        public string Id { get; set; } = "isolated-female";
        public IReadOnlyList<IComponent> Components { get; } = [eyes];
        public bool HasComponentProjection => true;
        public IReadOnlyList<VisualCue> VisualCues { get; } = [];
        public Transform3D GlobalTransform => Transform3D.Identity;
        public event Action? ComponentsRefreshed { add { } remove { } }
    }
}
