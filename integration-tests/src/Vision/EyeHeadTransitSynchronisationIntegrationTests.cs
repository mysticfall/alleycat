using AlleyCat.Testing;
using AlleyCat.Vision;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Vision;

/// <summary>
/// Eye–head transit synchronisation regression for VISION-001 UR 18 / TR 50–52 on the reference female:
/// during a 40° head-turn transit the rendered gaze must stay locked on the assigned anchor — the
/// signed gaze error (gaze yaw minus the anchor bearing yaw, both measured from the current face
/// origin) must never deepen against the head motion beyond the evidence-frozen thresholds — and both
/// eyes must acquire and maintain the independently derived constrained fixation direction within the
/// frozen direct-fixation bounds.
/// </summary>
/// <remarks>
/// <para>
/// The opposed-excursion criterion was redefined to gaze-error semantics by explicit user approval
/// (acceptance-decisions checkpoint, 2026-10-12), matching the isolated tracking fixture. The numeric
/// bounds are unchanged.
/// </para>
/// <para>
/// Migrated to the approved simplified direct-fixation arrangement (2026-10-13): the target is
/// assigned at ONE controlled point between frames — immediately after the previous frame's post-draw
/// observation returned, before the next frame's processing — so the next presentation is
/// deterministically eligible frame 1. Expected directions are derived independently from the
/// Shared-Direction Geometry (shared solved eye origin, componentwise 35°/25° clamps, immutable
/// authored optical axes); live neutral calibration is removed from acceptance. Saccades are
/// explicitly disabled by the photobooth driver's deterministic presentation configuration. The
/// residual ±1-frame state/image observability ambiguity is declared in evidence, not engineered away.
/// </para>
/// <para>
/// This is a windowed test on purpose: the behaviour under test is the per-frame pipeline ordering
/// between the eye-gaze composition and the skeleton-modifier solve, which needs the real frame loop
/// with an active renderer.
/// </para>
/// <para>
/// All acceptance timing bounds are asserted in the frame domain — presented frames from the
/// assignment at the authored 90 fps cadence — because the transit is frame-quantised: slower
/// renderer pacing (for example software rendering under host load) inflates wall time without
/// changing per-frame behaviour, so host wall time must not gate a frozen seconds deadline.
/// </para>
/// </remarks>
public sealed class EyeHeadTransitSynchronisationIntegrationTests
{
    private const string PhotoboothScenePath = "res://tests/ai009_head_orientation/ai009_orienting_photobooth.tscn";
    private const string PhotoboothMindScriptPath = "res://tests/ai009_head_orientation/Ai009OrientingPhotoboothMind.cs";
    private const string ShortNpc = "ShortNpc";

    // Frozen verification thresholds (Phase 1 diagnostic baseline, 2026-10-12).
    private const int MaxEyeResponseFrames = 2;
    private const float MaxEyeOvershootDegrees = 4f;
    private const float MaxCounterRotationExcursionDegrees = 0.5f;
    private const float MaxCounterRotationDurationMilliseconds = 150f;
    private const float MaxCentringSeconds = 0.8f;

    // The frozen timing bounds expressed in the frame domain at the authored cadence: the transit
    // is frame-quantised (one presented frame per sample), so acceptance measures presented frames —
    // 72 frames = 0.8 s, 36 frames = 0.4 s, 13.5 frames = 150 ms at 90 fps — never host wall time.
    private const double MaxCentringFrames = MaxCentringSeconds * TargetFps;
    private const double MaxCounterRotationDurationFrames =
        MaxCounterRotationDurationMilliseconds / 1000 * TargetFps;
    private const double DirectFixationCompletionDeadlineFrames =
        DirectFixationAcceptance.CompletionDeadlineSeconds * TargetFps;

    private const int TargetFps = 90;
    private const int WarmUpTimeoutMilliseconds = 8000;
    private const int SampleFrameCount = 140;
    private const double SettledHoldSeconds = 1.5;
    private const double CentringTimeoutSeconds = 2.5;
    private const float HeadMotionEpsilonDegrees = 0.05f;
    private const float OpposedMotionEpsilonDegrees = 0.02f;
    private const float CentredResidualDegrees = 2f;

    /// <summary>
    /// During the 40° transit the eyes respond within two frames, never overshoot the true anchor
    /// residual by more than four degrees, keep the signed gaze error against the anchor bearing from
    /// deepening against the head motion beyond half a degree of excursion or 150 ms, centre the turn
    /// within 0.8 s (72 presented frames at the authored cadence) of the actual assignment, and —
    /// through the settled hold — both eyes acquire the independently derived constrained direction
    /// within 0.4 s (36 presented frames at the authored cadence) of the assignment and keep ≤0.5°
    /// alignment afterwards.
    /// </summary>
    [Fact]
    public async Task DuringHeadTransit_EyesStayWithTheHeadWithinFrozenThresholds()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 1);

        int previousMaxFps = Engine.MaxFps;
        Node photobooth = LoadPackedScene(PhotoboothScenePath).Instantiate();
        try
        {
            Engine.MaxFps = TargetFps;
            InstallInertPhotoboothMinds(photobooth);
            sceneTree.Root.AddChild(photobooth);
            await WaitForFramesAsync(sceneTree, 3);

            Ai009OrientingPhotoboothDriver driver =
                photobooth.GetNode<Ai009OrientingPhotoboothDriver>("Ai009OrientingPhotoboothDriver");
            driver.Activate();
            await WaitForFramesAsync(sceneTree, 3);

            Node3D viewpoint = photobooth.GetNode<Node3D>("Subject/ShortNpc/Female/GeneralSkeleton/Head/Viewpoint");
            Node3D leftEye = photobooth.GetNode<Node3D>("Subject/ShortNpc/Female/GeneralSkeleton/Head/Eyes/LeftEye");
            Node3D rightEye = photobooth.GetNode<Node3D>("Subject/ShortNpc/Female/GeneralSkeleton/Head/Eyes/RightEye");
            Node3D straightAnchor = photobooth.GetNode<Node3D>("Subject/ShortStraightAnchor");
            Node3D transitAnchor = photobooth.GetNode<Node3D>("Subject/ShortTransitAnchor");

            // Engagement warm-up cycle — centre once on a provisional straight anchor, release, and let
            // the head settle — mirroring the diagnostic's warm-up, which escapes the crouched cold-start
            // rest before any anchor geometry is authored.
            straightAnchor.GlobalPosition =
                viewpoint.GlobalPosition + (viewpoint.GlobalBasis * new Vector3(0f, 0f, -1.4f));
            await AssignAndWaitUntilCentredAsync(sceneTree, driver, straightAnchor);
            await WaitForFramesAsync(sceneTree, 144);
            driver.ClearLookTarget(ShortNpc);
            await WaitUntilHeadSettledAsync(sceneTree, driver);

            // Author the scenario anchors relative to the released, settled rest viewpoint — the same
            // placement basis as the diagnostic's anchor table — then re-centre on the straight anchor
            // before the transit, exactly as the eye-transit scenario does.
            straightAnchor.GlobalPosition =
                viewpoint.GlobalPosition + (viewpoint.GlobalBasis * new Vector3(0f, 0f, -1.4f));
            float yaw = Mathf.DegToRad(40f);
            Vector3 localDirection = new(-Mathf.Sin(yaw), 0f, -Mathf.Cos(yaw));
            transitAnchor.GlobalPosition =
                viewpoint.GlobalPosition + (viewpoint.GlobalBasis * localDirection * 1.2f);

            await AssignAndWaitUntilCentredAsync(sceneTree, driver, straightAnchor);
            await WaitForFramesAsync(sceneTree, 90);

            // Immutable authored eye neutrals loaded through the shared fail-closed reference path —
            // the same authored local transforms the production transform backend captures as its
            // neutral. Live neutral calibration (the driver's activation-time capture) is diagnostic
            // only and is not part of acceptance.
            Basis leftEyeNeutral = DirectFixationReferences.LoadAuthoredEyeNeutral(
                DirectFixationReferences.FemaleTemplatePath, "GeneralSkeleton/Head/Eyes/LeftEye");
            Basis rightEyeNeutral = DirectFixationReferences.LoadAuthoredEyeNeutral(
                DirectFixationReferences.FemaleTemplatePath, "GeneralSkeleton/Head/Eyes/RightEye");
            TransitSampler sampler = new(
                driver, ShortNpc, viewpoint, leftEye, rightEye, leftEyeNeutral, rightEyeNeutral);

            // Pre-assignment post-draw observation on the straight anchor. The await returns inside the
            // post-draw window, and the assignment below runs before any further frame processing — the
            // controlled point that makes the next presentation deterministically eligible frame 1.
            TransitSample preAssignment = await sampler.ObserveAsync(sceneTree, "pre", straightAnchor);
            Assert.True(
                Mathf.IsFinite(preAssignment.GazeWorldYaw) && Mathf.IsFinite(preAssignment.AnchorBearingYaw),
                "The world-gaze signal requires transform-driven eye nodes and a live anchor.");
            Assert.True(
                preAssignment.LeftEye.ErrorDegrees <= DirectFixationAcceptance.AlignmentToleranceDegrees
                    && preAssignment.RightEye.ErrorDegrees <= DirectFixationAcceptance.AlignmentToleranceDegrees,
                $"Oracle baseline on the centred straight anchor must read both eyes aligned, found "
                + $"{preAssignment.LeftEye.ErrorDegrees:F4}°/{preAssignment.RightEye.ErrorDegrees:F4}°.");

            driver.AssignLookTarget(ShortNpc, (VisualCue)transitAnchor);
            ulong assignmentTicksMs = Time.GetTicksMsec();
            long assignmentSequence = sampler.NextSequence();
            double assignmentTimeSeconds = assignmentTicksMs / 1000d;
            FixationAcquisitionWindow window = new(
                assignmentTimeSeconds,
                assignmentSequence,
                preAssignment.LeftEye.Actual,
                preAssignment.RightEye.Actual);

            List<TransitSample> samples = [];
            for (int frame = 0; frame < SampleFrameCount; frame++)
            {
                TransitSample sample = await sampler.ObserveAsync(sceneTree, "transit", transitAnchor);
                samples.Add(sample);
                window.AddPresentation(sample.Presentation);
            }

            // Complete the window: hold sampling through centring and the settled hold so acquisition,
            // transit, centring and settled behaviour are all observed without skipping failures.
            double centringStart = FixtureNowSeconds();
            while (FixtureNowSeconds() - centringStart < CentringTimeoutSeconds)
            {
                TransitSample sample = await sampler.ObserveAsync(sceneTree, "centring", transitAnchor);
                samples.Add(sample);
                window.AddPresentation(sample.Presentation);
                if (sample.Residual <= CentredResidualDegrees)
                {
                    break;
                }
            }

            double settledStart = FixtureNowSeconds();
            while (FixtureNowSeconds() - settledStart < SettledHoldSeconds)
            {
                TransitSample sample = await sampler.ObserveAsync(sceneTree, "settled", transitAnchor);
                samples.Add(sample);
                window.AddPresentation(sample.Presentation);
            }

            WriteTransitSamplesArtefact(preAssignment, samples, assignmentTimeSeconds, assignmentSequence);

            int responseFrames = AssertEyeResponseWithinFrozenLatency(preAssignment, samples);
            AssertEyeOvershootWithinFrozenBound(samples);
            AssertGazeErrorNeverDeepensAgainstHeadMotion(samples, responseFrames);
            AssertTurnCentresWithinFrozenDeadline(samples, assignmentSequence);
            AssertDirectFixationWindow(window);
        }
        finally
        {
            Engine.MaxFps = previousMaxFps;
            photobooth.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    private static double FixtureNowSeconds() => Time.GetTicksMsec() / 1000d;

    private static void InstallInertPhotoboothMinds(Node photobooth)
    {
        // Swap the production agentic Minds for the inert test Mind before tree entry so no AI
        // provider work runs; the template Mind children (AI-007 selector, AI-009 controller) are
        // untouched, exactly as the Phase 1 diagnostic does.
        Script mindScript = ResourceLoader.Load<Script>(PhotoboothMindScriptPath);
        foreach (string npcName in new[] { ShortNpc, "TallNpc" })
        {
            photobooth.GetNode($"Subject/{npcName}/Mind").SetScript(mindScript);
        }
    }

    /// <summary>
    /// Reads one eye node's immutable authored neutral local rotation from the female template's scene
    /// state through the shared fail-closed loader (<see cref="DirectFixationReferences"/>).
    /// </summary>
    private static async Task AssignAndWaitUntilCentredAsync(
        SceneTree sceneTree,
        Ai009OrientingPhotoboothDriver driver,
        Node3D anchor)
    {
        driver.AssignLookTarget(ShortNpc, (VisualCue)anchor);
        ulong startTicks = Time.GetTicksMsec();
        while (Time.GetTicksMsec() - startTicks < WarmUpTimeoutMilliseconds)
        {
            await WaitForFramesAsync(sceneTree, 6);
            if (driver.GetHeadToAnchorAngleDegrees(ShortNpc) < 2f)
            {
                return;
            }
        }

        Assert.True(
            driver.GetHeadToAnchorAngleDegrees(ShortNpc) < 4f,
            "Warm-up centring did not converge; the transit scenario premise is invalid.");
    }

    private static async Task WaitUntilHeadSettledAsync(
        SceneTree sceneTree,
        Ai009OrientingPhotoboothDriver driver)
    {
        // Two consecutive reads 0.25 s apart within 0.3° mean the released head has settled, matching
        // the diagnostic's settle oracle, so the authored anchors are placed against a stable rest.
        float lastYaw = driver.GetHeadYawDegrees(ShortNpc);
        float lastPitch = driver.GetHeadPitchDegrees(ShortNpc);
        int stableReads = 0;
        ulong startTicks = Time.GetTicksMsec();
        while (Time.GetTicksMsec() - startTicks < WarmUpTimeoutMilliseconds && stableReads < 2)
        {
            await WaitForFramesAsync(sceneTree, 22);
            float yaw = driver.GetHeadYawDegrees(ShortNpc);
            float pitch = driver.GetHeadPitchDegrees(ShortNpc);
            stableReads = Mathf.Abs(yaw - lastYaw) < 0.3f && Mathf.Abs(pitch - lastPitch) < 0.3f
                ? stableReads + 1
                : 0;
            lastYaw = yaw;
            lastPitch = pitch;
        }

        Assert.True(
            stableReads >= 2,
            "The released head did not settle after the warm-up cycle; the scenario premise is invalid.");
    }

    private static int AssertEyeResponseWithinFrozenLatency(TransitSample preAssignment, IReadOnlyList<TransitSample> samples)
    {
        // Eligible presentations are numbered from 1 (the first presentation after the controlled
        // assignment); the response is measured against the pre-assignment presented eye yaw.
        int responseFrames = -1;
        for (int index = 0; index < samples.Count; index++)
        {
            if (Mathf.Abs(samples[index].EyeYaw - preAssignment.EyeYaw) > 0.5f)
            {
                responseFrames = index + 1;
                break;
            }
        }

        Assert.True(
            responseFrames is >= 1 and <= MaxEyeResponseFrames,
            $"The measured eye must respond to the reassignment within {MaxEyeResponseFrames} frames, "
            + $"but the first response came at frame {responseFrames}.");

        return responseFrames;
    }

    private static void AssertEyeOvershootWithinFrozenBound(IReadOnlyList<TransitSample> samples)
    {
        float overshoot = float.MinValue;
        foreach (TransitSample sample in samples)
        {
            overshoot = MathF.Max(overshoot, sample.EyeYaw - sample.AnchorYaw);
        }

        Assert.True(
            overshoot <= MaxEyeOvershootDegrees,
            $"The measured eye-in-head must stay within {MaxEyeOvershootDegrees}° of the true anchor "
            + $"residual, but overshot by {overshoot:F2}°.");
    }

    /// <summary>
    /// User-approved opposed gaze-error criterion (2026-10-12): the signed gaze error — the
    /// neutral-stripped composed world gaze yaw minus the anchor bearing yaw from the current face
    /// origin — must not deepen against the head motion while the head advances, scanning after the
    /// approved response ramp. The neutral stripping uses the immutable authored eye neutral, not live
    /// calibration. Bounds are unchanged; the continuous-run duration is measured in presented frames
    /// at the authored cadence (13.5 frames = 150 ms at 90 fps).
    /// </summary>
    private static void AssertGazeErrorNeverDeepensAgainstHeadMotion(
        IReadOnlyList<TransitSample> samples,
        int responseFrames)
    {
        float motionDirection = MathF.Sign(samples[^1].HeadYaw - samples[0].HeadYaw);
        Assert.True(Mathf.Abs(motionDirection) > 0.5f, "The head did not turn during the sampled transit.");

        float opposedExcursion = 0f;
        long longestOpposedFrames = 0;
        long runStartSequence = 0;
        bool inOpposedRun = false;

        for (int index = Math.Max(responseFrames, 1); index < samples.Count; index++)
        {
            TransitSample previous = samples[index - 1];
            TransitSample current = samples[index];
            float headAdvance = (current.HeadYaw - previous.HeadYaw) * motionDirection;
            float previousError = previous.GazeWorldYaw - previous.AnchorBearingYaw;
            float error = current.GazeWorldYaw - current.AnchorBearingYaw;
            bool headMoving = headAdvance > HeadMotionEpsilonDegrees;
            bool errorDeepening = (error - previousError) * motionDirection < -OpposedMotionEpsilonDegrees;
            if (headMoving && errorDeepening)
            {
                opposedExcursion += (previousError - error) * motionDirection;
                if (!inOpposedRun)
                {
                    inOpposedRun = true;
                    runStartSequence = previous.Sequence;
                }

                longestOpposedFrames = Math.Max(longestOpposedFrames, current.Sequence - runStartSequence);
            }
            else
            {
                inOpposedRun = false;
            }
        }

        Assert.True(
            opposedExcursion <= MaxCounterRotationExcursionDegrees,
            $"The gaze error deepened {opposedExcursion:F2}° against the head motion during the "
            + $"transit, beyond the frozen {MaxCounterRotationExcursionDegrees}° excursion bound.");
        Assert.True(
            longestOpposedFrames <= MaxCounterRotationDurationFrames,
            $"The gaze error deepened against the head motion for {longestOpposedFrames} presented frames "
            + $"(~{longestOpposedFrames / (double)TargetFps * 1000d:F0} ms at the authored {TargetFps} fps "
            + $"cadence) continuously, beyond the frozen {MaxCounterRotationDurationMilliseconds} ms "
            + $"({MaxCounterRotationDurationFrames:F1} frames) bound.");
    }

    /// <summary>
    /// The 40° turn must centre within the frozen 0.8 s deadline measured from the ACTUAL assignment,
    /// never from the first post-assignment sample. The deadline is asserted as presented frames from
    /// the assignment at the authored cadence (72 frames = 0.8 s at 90 fps), so slower renderer
    /// pacing under host load cannot inflate it while per-frame behaviour is unchanged.
    /// </summary>
    private static void AssertTurnCentresWithinFrozenDeadline(
        IReadOnlyList<TransitSample> samples,
        long assignmentSequence)
    {
        for (int index = 0; index < samples.Count; index++)
        {
            if (samples[index].Residual <= CentredResidualDegrees)
            {
                long centredFrames = samples[index].Sequence - assignmentSequence;
                Assert.True(
                    centredFrames <= MaxCentringFrames,
                    $"The 40° turn reached {CentredResidualDegrees}° residual after {centredFrames} presented "
                    + $"frames (~{centredFrames / (double)TargetFps:F3} s at the authored {TargetFps} fps "
                    + $"cadence) from assignment, beyond the frozen {MaxCentringSeconds} s "
                    + $"({MaxCentringFrames:F0} frames) deadline.");
                return;
            }
        }

        Assert.Fail(
            $"The 40° turn never reached {CentredResidualDegrees}° residual within the sampled window.");
    }

    /// <summary>
    /// Direct-fixation oracle verdicts over the complete window: onset ≤2 eligible presented frames,
    /// acquisition completion ≤0.4 s (36 presented frames at the authored cadence) from actual
    /// assignment for BOTH eyes against their independently derived constrained directions, then
    /// maintained ≤0.5° alignment through the settled hold. The completion deadline is asserted on
    /// the oracle's eligible-frame count so renderer pacing under host load cannot inflate it.
    /// </summary>
    private static void AssertDirectFixationWindow(FixationAcquisitionWindow window)
    {
        FixationWindowResult result = window.Evaluate();

        _ = Assert.NotNull(result.OnsetFrame);
        Assert.True(
            result.IsOnsetWithinBound,
            $"Fixation onset must occur within {DirectFixationAcceptance.MaxOnsetEligibleFrames} eligible "
            + $"presented frames, but occurred at frame {result.OnsetFrame}.");
        _ = Assert.NotNull(result.CompletionFrame);
        Assert.True(
            result.CompletionFrame <= DirectFixationCompletionDeadlineFrames,
            $"Both eyes must acquire within {DirectFixationAcceptance.CompletionDeadlineSeconds} s "
            + $"({DirectFixationCompletionDeadlineFrames:F0} presented frames at the authored {TargetFps} fps "
            + $"cadence) of the actual assignment, but completed at eligible frame {result.CompletionFrame}.");
        Assert.True(
            result.IsFixationMaintained,
            $"Both eyes must maintain ≤{DirectFixationAcceptance.AlignmentToleranceDegrees}° alignment after "
            + $"acquisition, but the worst post-acquisition error was {result.MaxPostCompletionErrorDegrees:F4}°.");
    }

    private static void WriteTransitSamplesArtefact(
        TransitSample preAssignment,
        IReadOnlyList<TransitSample> samples,
        double assignmentTimeSeconds,
        long assignmentSequence)
    {
        // Diagnostic evidence artefact under game/temp (test-framework convention); the test never
        // depends on it.
        string directory = "temp/ai-009-attention-head-orientation/diag";
        try
        {
            _ = Directory.CreateDirectory(directory);
            using StreamWriter writer = new($"{directory}/test_transit_samples.csv", append: false);
            writer.WriteLine(
                "phase,time_s,sequence,ticks_ms,head_yaw,head_pitch,eye_yaw,anchor_yaw,residual,"
                + "gaze_world_yaw,anchor_bearing_yaw,gaze_error,left_eye_err,right_eye_err,"
                + "left_actual_x,left_actual_y,left_actual_z,left_expected_x,left_expected_y,left_expected_z,"
                + "right_actual_x,right_actual_y,right_actual_z,right_expected_x,right_expected_y,right_expected_z");
            void WriteSample(TransitSample sample)
            {
                writer.WriteLine(
                    $"{sample.Phase},{sample.TimeSeconds:F6},{sample.Sequence},{sample.TicksMilliseconds},"
                    + $"{sample.HeadYaw:F3},{sample.HeadPitch:F3},{sample.EyeYaw:F3},{sample.AnchorYaw:F3},{sample.Residual:F3},"
                    + $"{sample.GazeWorldYaw:F3},{sample.AnchorBearingYaw:F3},{sample.GazeWorldYaw - sample.AnchorBearingYaw:F3},"
                    + $"{sample.LeftEye.ErrorDegrees:F4},{sample.RightEye.ErrorDegrees:F4},"
                    + $"{sample.LeftEye.Actual.X:F6},{sample.LeftEye.Actual.Y:F6},{sample.LeftEye.Actual.Z:F6},"
                    + $"{sample.LeftEye.Expected.X:F6},{sample.LeftEye.Expected.Y:F6},{sample.LeftEye.Expected.Z:F6},"
                    + $"{sample.RightEye.Actual.X:F6},{sample.RightEye.Actual.Y:F6},{sample.RightEye.Actual.Z:F6},"
                    + $"{sample.RightEye.Expected.X:F6},{sample.RightEye.Expected.Y:F6},{sample.RightEye.Expected.Z:F6}");
            }

            WriteSample(preAssignment);
            foreach (TransitSample sample in samples)
            {
                WriteSample(sample);
            }

            File.WriteAllLines(
                $"{directory}/test_transit_assignment.txt",
                [$"assignment_time_s={assignmentTimeSeconds:F6}", $"assignment_sequence={assignmentSequence}"]);
        }
        catch (IOException)
        {
            // The artefact is best-effort evidence only.
        }
    }

    /// <summary>One eye's measured and independently expected presented optical direction.</summary>
    private readonly record struct EyeObservation(Vector3 Actual, Vector3 Expected, float ErrorDegrees);

    private readonly record struct TransitSample(
        string Phase,
        double TimeSeconds,
        long Sequence,
        ulong TicksMilliseconds,
        float HeadYaw,
        float HeadPitch,
        float EyeYaw,
        float AnchorYaw,
        float Residual,
        float GazeWorldYaw,
        float AnchorBearingYaw,
        EyeObservation LeftEye,
        EyeObservation RightEye)
    {
        public FixationPresentation Presentation => new(
            TimeSeconds,
            Sequence,
            new EyeFixationSample(LeftEye.Actual, LeftEye.Expected),
            new EyeFixationSample(RightEye.Actual, RightEye.Expected));
    }

    /// <summary>
    /// Post-draw transit sampler: each observation awaits the frame's post-draw signal and records the
    /// solved head probes plus both eyes' presented directions against the independently derived
    /// constrained directions from immutable authored optical references.
    /// </summary>
    private sealed class TransitSampler(
        Ai009OrientingPhotoboothDriver driver,
        string characterId,
        Node3D viewpoint,
        Node3D leftEye,
        Node3D rightEye,
        Basis leftEyeNeutral,
        Basis rightEyeNeutral)
    {
        private long _sequence;

        public long NextSequence() => ++_sequence;

        public async Task<TransitSample> ObserveAsync(SceneTree sceneTree, string phase, Node3D anchor)
        {
            _ = await sceneTree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            return Capture(phase, anchor);
        }

        private TransitSample Capture(string phase, Node3D anchor)
        {
            long sequence = ++_sequence;
            double time = Time.GetTicksMsec() / 1000d;
            Basis view = viewpoint.GlobalBasis.Orthonormalized();
            Quaternion originRotation = view.GetRotationQuaternion();

            // Independent direct-fixation geometry: shared solved origin, target offset rotated into the
            // shared frame, componentwise clamps, per-eye authored optical mapping.
            Vector3 targetOffset = originRotation.Inverse() * (anchor.GlobalPosition - viewpoint.GlobalPosition);
            Quaternion constrained = DirectFixationGeometry.ConstrainedGazeRotation(targetOffset);
            EyeObservation left = ObserveEye(leftEye, leftEyeNeutral, originRotation, constrained);
            EyeObservation right = ObserveEye(rightEye, rightEyeNeutral, originRotation, constrained);

            // Neutral-stripped measured eye rotation against the immutable authored neutral, transported
            // through the solved head onto the head forward — the same world-gaze composition the
            // isolated tracking fixture uses (parent · local eye basis · neutral⁻¹ · parent⁻¹), with
            // the right eye driving the composed yaw probes.
            Basis parent = ((Node3D)rightEye.GetParent()).GlobalBasis.Orthonormalized();
            Basis gazeDelta = parent * rightEye.Transform.Basis.Orthonormalized()
                * rightEyeNeutral.Orthonormalized().Inverse() * parent.Inverse();
            Vector3 gazeWorld = gazeDelta * -view.Z;
            Vector3 anchorDirection = (anchor.GlobalPosition - viewpoint.GlobalPosition).Normalized();

            return new TransitSample(
                phase,
                time,
                sequence,
                Time.GetTicksMsec(),
                driver.GetHeadYawDegrees(characterId),
                driver.GetHeadPitchDegrees(characterId),
                YawDegrees(view.Inverse() * gazeWorld),
                driver.GetAnchorYawInHeadDegrees(characterId),
                driver.GetHeadToAnchorAngleDegrees(characterId),
                YawDegrees(gazeWorld),
                YawDegrees(anchorDirection),
                left,
                right);
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

        private static float YawDegrees(Vector3 direction)
            => Mathf.RadToDeg(Mathf.Atan2(-direction.X, -direction.Z));
    }
}
