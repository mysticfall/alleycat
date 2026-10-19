using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Vision;

/// <summary>
/// Cheap synthetic evaluator-level boundary replays for the direct-fixation oracle (VISION-001 TR51/52
/// and AC70): spec example vectors, authored optical mapping, assignment/presentation ordering
/// semantics, the 0.4 s completion deadline boundary, slow accumulated drift, stable offsets and
/// acquisition-clock reset attempts. These replays feed recorded sequences into the evaluator; no
/// rendering or permutation instrumentation is involved.
/// </summary>
[Headless]
public sealed class DirectFixationOracleReplaysTests
{
    private const int VectorDecimalPlaces = 5;

    // The old (retired) target: a near-forward direction. Pre-assignment presented directions settle here.
    private static readonly Vector3 _oldTargetDirection = DirectFixationGeometry.ConstrainedDirection(new Vector3(0.03f, 0f, -0.1f));

    // The new (assigned) target: the spec's mixed-axes example, roughly 35.4° away from the old direction.
    private static readonly Vector3 _newTargetDirection = DirectFixationGeometry.ConstrainedDirection(
        new Vector3(1f, 1f, -Mathf.Sqrt(3f)));

    /// <summary>
    /// The geometry reproduces the spec's independently calculable examples: shared origin, componentwise
    /// clamps, pitch-then-yaw reconstruction and defined rear/vertical handling.
    /// </summary>
    [Theory]
    [InlineData(1f, 1f, -1.7320508f, 0.453153894f, 0.422618262f, -0.784885567f)]
    [InlineData(0.03f, 0f, -0.1f, 0.287347886f, 0f, -0.957826285f)]
    [InlineData(0f, 0f, 1f, 0.573576436f, 0f, -0.819152044f)]
    [InlineData(-0.1f, 0f, 0.1f, -0.573576436f, 0f, -0.819152044f)]
    [InlineData(0f, 1f, 0f, 0f, 0.422618262f, -0.906307787f)]
    [InlineData(0f, -1f, 0f, 0f, -0.422618262f, -0.906307787f)]
    public void ConstrainedDirection_MatchesSpecExamples(
        float x,
        float y,
        float z,
        float expectedX,
        float expectedY,
        float expectedZ)
    {
        Vector3 direction = DirectFixationGeometry.ConstrainedDirection(new Vector3(x, y, z));

        Assert.Equal((double)expectedX, direction.X, VectorDecimalPlaces);
        Assert.Equal((double)expectedY, direction.Y, VectorDecimalPlaces);
        Assert.Equal((double)expectedZ, direction.Z, VectorDecimalPlaces);
    }

    /// <summary>
    /// The coincident degenerate target resolves to the neutral direction, and reversing the rotation
    /// order does not produce the spec's mixed-axes vector.
    /// </summary>
    [Fact]
    public void ConstrainedDirection_CoincidentIsNeutralAndRotationOrderMatters()
    {
        Assert.Equal(new Vector3(0f, 0f, -1f), DirectFixationGeometry.ConstrainedDirection(Vector3.Zero));

        Quaternion clampedYaw = new(Vector3.Up, -Mathf.DegToRad(30f));
        Quaternion clampedPitch = new(Vector3.Right, Mathf.DegToRad(25f));
        Vector3 reversedOrder = clampedPitch * clampedYaw * DirectFixationGeometry.OpticalForward;

        Assert.NotEqual(_newTargetDirection, reversedOrder);
    }

    /// <summary>Non-finite inputs invalidate acceptance; they can never pass it.</summary>
    [Fact]
    public void ConstrainedDirection_NonFiniteInputInvalidatesAcceptance()
    {
        _ = Assert.Throws<InvalidOperationException>(
            () => DirectFixationGeometry.ConstrainedDirection(new Vector3(float.NaN, 0f, -1f)));
        _ = Assert.Throws<InvalidOperationException>(
            () => DirectFixationGeometry.ConstrainedDirection(new Vector3(0f, float.PositiveInfinity, -1f)));
        _ = Assert.Throws<InvalidOperationException>(
            () => DirectFixationGeometry.AngleBetweenDegrees(new Vector3(float.NaN, 0f, 0f), Vector3.Back));
    }

    /// <summary>
    /// Authored optical mapping: the spec's authored-axis example (P = identity, N = Ry(90°),
    /// f = (1, 0, 0)) still expects the shared direction, while an authored toe-out offset is preserved
    /// exactly — no per-eye correction frame and no vergence.
    /// </summary>
    [Fact]
    public void AuthoredOpticalMapping_PreservesSpecExampleAndAuthoredToeOut()
    {
        var authoredNeutral = Quaternion.FromEuler(new Vector3(0f, Mathf.DegToRad(90f), 0f));
        Vector3 authoredForward = Vector3.Right;

        Vector3 neutralOptical = DirectFixationGeometry.NeutralOpticalDirection(
            Quaternion.Identity,
            authoredNeutral,
            authoredForward);
        Assert.Equal(0d, neutralOptical.X, VectorDecimalPlaces);
        Assert.Equal(0d, neutralOptical.Y, VectorDecimalPlaces);
        Assert.Equal(-1d, neutralOptical.Z, VectorDecimalPlaces);

        Quaternion constrained = DirectFixationGeometry.ConstrainedGazeRotation(new Vector3(1f, 1f, -Mathf.Sqrt(3f)));
        Vector3 expected = DirectFixationGeometry.ExpectedEyeDirection(
            Quaternion.Identity,
            constrained,
            neutralOptical);
        Assert.Equal((double)_newTargetDirection.X, expected.X, VectorDecimalPlaces);
        Assert.Equal((double)_newTargetDirection.Y, expected.Y, VectorDecimalPlaces);
        Assert.Equal((double)_newTargetDirection.Z, expected.Z, VectorDecimalPlaces);

        // Toe-out: the authored neutral points 3.5° off the shared forward; the expected presented
        // direction must carry exactly that authored offset relative to the shared constrained direction.
        const float toeOutDegrees = 3.5f;
        var toeOutNeutral = Quaternion.FromEuler(new Vector3(0f, Mathf.DegToRad(toeOutDegrees), 0f));
        Vector3 toeOutOptical = DirectFixationGeometry.NeutralOpticalDirection(
            Quaternion.Identity,
            toeOutNeutral,
            DirectFixationGeometry.OpticalForward);
        Vector3 toeOutExpected = DirectFixationGeometry.ExpectedEyeDirection(
            Quaternion.Identity,
            constrained,
            toeOutOptical);
        Assert.Equal(
            toeOutDegrees,
            DirectFixationGeometry.AngleBetweenDegrees(toeOutExpected, _newTargetDirection),
            2);
    }

    /// <summary>
    /// Ordering replay A→G→S→B: assignment precedes the gaze evaluation and the sampling of the next
    /// presentation, so that presentation is deterministically eligible frame 1.
    /// </summary>
    [Fact]
    public void Ordering_AssignmentBeforeGazeAndSampling_CountsNextPresentationAsFrameOne()
    {
        FixationAcquisitionWindow window = new(assignmentTimeSeconds: 0d, assignmentSequence: 100,
            _oldTargetDirection, _oldTargetDirection);
        // A(seq 100) → G(seq 101) → S(seq 102) → B(seq 103): the eyes already responded at sampling time.
        window.AddPresentation(Presentation(0.0167d, 102, leftError: 20f, rightError: 20f));
        window.AddPresentation(Presentation(0.0333d, 104, leftError: 0.2f, rightError: 0.2f));
        window.AddPresentation(Presentation(0.05d, 106, leftError: 0.1f, rightError: 0.1f));

        FixationWindowResult result = window.Evaluate();

        Assert.Equal(0, result.PreAssignmentPresentationCount);
        Assert.Equal(3, result.EligiblePresentationCount);
        Assert.Equal(1, result.OnsetFrame);
        Assert.True(result.IsOnsetWithinBound);
        Assert.True(result.IsCompletionWithinDeadline);
        Assert.Equal(0.0333d, result.CompletionSeconds!.Value, 3);
        Assert.True(result.IsFixationMaintained);
    }

    /// <summary>
    /// Ordering replay G→A→S→B: the gaze evaluation ran before the assignment, so the sampled
    /// presentation carries stale gaze — it is still eligible frame 1 and counts against the ≤2-frame
    /// onset bound. One stale frame stays green; two stale frames go red.
    /// </summary>
    [Fact]
    public void Ordering_StaleGazeAfterAssignment_CountsAgainstOnsetBound()
    {
        FixationAcquisitionWindow green = new(assignmentTimeSeconds: 0.001d, assignmentSequence: 100,
            _oldTargetDirection, _oldTargetDirection);
        // G(seq 99, stale) → A(seq 100) → S(seq 102, still stale) → B(seq 103).
        green.AddPresentation(StalePresentation(0.0167d, 102));
        green.AddPresentation(Presentation(0.0333d, 104, leftError: 5f, rightError: 5f));
        green.AddPresentation(Presentation(0.05d, 106, leftError: 0.2f, rightError: 0.2f));

        FixationWindowResult greenResult = green.Evaluate();

        Assert.Equal(2, greenResult.OnsetFrame);
        Assert.True(greenResult.IsOnsetWithinBound);
        Assert.True(greenResult.IsCompletionWithinDeadline);

        FixationAcquisitionWindow red = new(assignmentTimeSeconds: 0.001d, assignmentSequence: 100,
            _oldTargetDirection, _oldTargetDirection);
        red.AddPresentation(StalePresentation(0.0167d, 102));
        red.AddPresentation(StalePresentation(0.0333d, 104));
        red.AddPresentation(Presentation(0.05d, 106, leftError: 5f, rightError: 5f));
        red.AddPresentation(Presentation(0.0667d, 108, leftError: 0.2f, rightError: 0.2f));

        FixationWindowResult redResult = red.Evaluate();

        Assert.Equal(3, redResult.OnsetFrame);
        Assert.False(redResult.IsOnsetWithinBound);
    }

    /// <summary>
    /// Ordering replay G→S→A→B: the presentation's sampling precedes the assignment, so a later post-draw
    /// observation cannot make it eligible; frame 1 is the next eligible presentation, and pre-assignment
    /// movement never consumes onset numbering.
    /// </summary>
    [Fact]
    public void Ordering_SamplingBeforeAssignment_ExcludesThatPresentation()
    {
        FixationAcquisitionWindow green = new(assignmentTimeSeconds: 0.008d, assignmentSequence: 100,
            _oldTargetDirection, _oldTargetDirection);
        // G(seq 97) → S(seq 98, pre-assignment, settled on the old target) → A(seq 100) → B(seq 101).
        green.AddPresentation(StalePresentation(0d, 98));
        green.AddPresentation(StalePresentation(0.0167d, 102));
        green.AddPresentation(Presentation(0.0333d, 104, leftError: 5f, rightError: 5f));
        green.AddPresentation(Presentation(0.05d, 106, leftError: 0.2f, rightError: 0.2f));

        FixationWindowResult greenResult = green.Evaluate();

        Assert.Equal(1, greenResult.PreAssignmentPresentationCount);
        Assert.Equal(3, greenResult.EligiblePresentationCount);
        Assert.Equal(2, greenResult.OnsetFrame);
        Assert.True(greenResult.IsOnsetWithinBound);

        // Red variant: with two stale eligible frames the onset lands on frame 3 while the
        // pre-assignment presentation stays excluded — counting it would only worsen the bound.
        FixationAcquisitionWindow red = new(assignmentTimeSeconds: 0.008d, assignmentSequence: 100,
            _oldTargetDirection, _oldTargetDirection);
        red.AddPresentation(StalePresentation(0d, 98));
        red.AddPresentation(StalePresentation(0.0167d, 102));
        red.AddPresentation(StalePresentation(0.0333d, 104));
        red.AddPresentation(Presentation(0.05d, 106, leftError: 5f, rightError: 5f));

        FixationWindowResult redResult = red.Evaluate();

        Assert.Equal(1, redResult.PreAssignmentPresentationCount);
        Assert.Equal(3, redResult.OnsetFrame);
        Assert.False(redResult.IsOnsetWithinBound);
    }

    /// <summary>
    /// The completion deadline runs from the actual assignment with no cadence grace: completion exactly
    /// at 0.4 s passes; a hair beyond it fails.
    /// </summary>
    [Fact]
    public void Deadline_CompletionIsMeasuredFromActualAssignment()
    {
        FixationAcquisitionWindow boundary = new(assignmentTimeSeconds: 0d, assignmentSequence: 0,
            _oldTargetDirection, _oldTargetDirection);
        boundary.AddPresentation(Presentation(0.1d, 1, leftError: 5f, rightError: 5f));
        boundary.AddPresentation(Presentation(0.2d, 2, leftError: 5f, rightError: 5f));
        boundary.AddPresentation(Presentation(0.3d, 3, leftError: 5f, rightError: 5f));
        boundary.AddPresentation(Presentation(0.4d, 4, leftError: 0.2f, rightError: 0.2f));

        FixationWindowResult boundaryResult = boundary.Evaluate();

        Assert.Equal(0.4d, boundaryResult.CompletionSeconds!.Value, 6);
        Assert.True(boundaryResult.IsCompletionWithinDeadline);

        FixationAcquisitionWindow beyond = new(assignmentTimeSeconds: 0d, assignmentSequence: 0,
            _oldTargetDirection, _oldTargetDirection);
        beyond.AddPresentation(Presentation(0.1d, 1, leftError: 5f, rightError: 5f));
        beyond.AddPresentation(Presentation(0.2d, 2, leftError: 5f, rightError: 5f));
        beyond.AddPresentation(Presentation(0.3d, 3, leftError: 5f, rightError: 5f));
        beyond.AddPresentation(Presentation(0.4001d, 4, leftError: 0.2f, rightError: 0.2f));

        FixationWindowResult beyondResult = beyond.Evaluate();

        Assert.Equal(0.4001d, beyondResult.CompletionSeconds!.Value, 6);
        Assert.False(beyondResult.IsCompletionWithinDeadline);
    }

    /// <summary>
    /// Slow accumulated drift beyond 0.5° must fail maintenance even though every step is small and the
    /// eyes initially acquired correctly — the retired opposed-excursion detector's blind spot. Only the
    /// right eye drifts, proving bilateral detection.
    /// </summary>
    [Fact]
    public void Drift_SlowAccumulatedRightEyeDriftBeyondTolerance_FailsMaintenance()
    {
        FixationAcquisitionWindow window = new(assignmentTimeSeconds: 0d, assignmentSequence: 0,
            _oldTargetDirection, _oldTargetDirection);
        window.AddPresentation(Presentation(0.0167d, 1, leftError: 6f, rightError: 6f));
        window.AddPresentation(Presentation(0.0333d, 2, leftError: 3f, rightError: 3f));
        window.AddPresentation(Presentation(0.05d, 3, leftError: 0.4f, rightError: 0.4f));

        double time = 0.05d;
        long sequence = 3;
        for (int frame = 1; frame <= 15; frame++)
        {
            time += 1d / 60d;
            sequence++;
            float drift = 0.05f * frame;
            window.AddPresentation(Presentation(time, sequence, leftError: 0.1f, rightError: drift));
        }

        FixationWindowResult result = window.Evaluate();

        Assert.True(result.IsCompletionWithinDeadline);
        Assert.False(result.IsFixationMaintained);
        Assert.Equal(0.75f, result.MaxPostCompletionErrorDegrees, 2);
    }

    /// <summary>A stable unacceptable offset never acquires: the completion deadline fails.</summary>
    [Fact]
    public void StableOffset_NeverAcquires_FailsCompletionDeadline()
    {
        FixationAcquisitionWindow window = new(assignmentTimeSeconds: 0d, assignmentSequence: 0,
            _oldTargetDirection, _oldTargetDirection);
        for (int frame = 1; frame <= 30; frame++)
        {
            window.AddPresentation(Presentation(frame / 60d, frame, leftError: 1f, rightError: 1f));
        }

        FixationWindowResult result = window.Evaluate();

        Assert.Null(result.CompletionSeconds);
        Assert.False(result.IsCompletionWithinDeadline);
        Assert.True(result.IsFixationMaintained, "Maintenance is vacuous without acquisition; the deadline is the red.");
    }

    /// <summary>
    /// Head/target motion between assignment and completion must not reset the acquisition clock: the
    /// deadline stays measured from the actual assignment. The green case completes at 0.35 s despite
    /// continuous expected-direction motion; the red case only passes if the clock were illegally reset
    /// at the 0.2 s motion event.
    /// </summary>
    [Fact]
    public void MotionBetweenAssignmentAndCompletion_DoesNotResetAcquisitionClock()
    {
        // The expected direction rotates 30°/s throughout (moving head/target); errors are relative to
        // the moving expectation, so the evaluator receives genuinely time-varying geometry.
        FixationAcquisitionWindow green = new(assignmentTimeSeconds: 0d, assignmentSequence: 0,
            _oldTargetDirection, _oldTargetDirection);
        for (int frame = 1; frame <= 21; frame++)
        {
            double time = frame / 60d;
            float error = time < 0.2d ? 4f : time < 0.3499d ? 3f : 0.3f;
            green.AddPresentation(Presentation(time, frame, leftError: error, rightError: error));
        }

        FixationWindowResult greenResult = green.Evaluate();

        Assert.Equal(0.35d, greenResult.CompletionSeconds!.Value, 2);
        Assert.True(greenResult.IsCompletionWithinDeadline);

        FixationAcquisitionWindow red = new(assignmentTimeSeconds: 0d, assignmentSequence: 0,
            _oldTargetDirection, _oldTargetDirection);
        for (int frame = 1; frame <= 33; frame++)
        {
            double time = frame / 60d;
            float error = time < 0.55d ? 3f : 0.3f;
            red.AddPresentation(Presentation(time, frame, leftError: error, rightError: error));
        }

        FixationWindowResult redResult = red.Evaluate();

        Assert.Equal(0.55d, redResult.CompletionSeconds!.Value, 2);
        Assert.False(redResult.IsCompletionWithinDeadline);
    }

    /// <summary>Onset numbering: an assignment whose eyes never move never reports an onset frame.</summary>
    [Fact]
    public void Onset_NeverRespondingEyes_FailTheOnsetBound()
    {
        FixationAcquisitionWindow window = new(assignmentTimeSeconds: 0d, assignmentSequence: 0,
            _oldTargetDirection, _oldTargetDirection);
        for (int frame = 1; frame <= 10; frame++)
        {
            window.AddPresentation(StalePresentation(frame / 60d, frame));
        }

        FixationWindowResult result = window.Evaluate();

        Assert.Null(result.OnsetFrame);
        Assert.False(result.IsOnsetWithinBound);
    }

    private static FixationPresentation Presentation(
        double timeSeconds,
        long sequence,
        float leftError,
        float rightError)
    {
        Vector3 expected = _newTargetDirection;
        return new FixationPresentation(
            timeSeconds,
            sequence,
            WithError(expected, leftError),
            WithError(expected, rightError));
    }

    private static FixationPresentation StalePresentation(double timeSeconds, long sequence)
        => new(
            timeSeconds,
            sequence,
            new EyeFixationSample(_oldTargetDirection, _newTargetDirection),
            new EyeFixationSample(_oldTargetDirection, _newTargetDirection));

    private static EyeFixationSample WithError(Vector3 expected, float errorDegrees)
    {
        Vector3 actual = errorDegrees == 0f
            ? expected
            : expected.Rotated(PerpendicularAxis(expected), Mathf.DegToRad(errorDegrees));
        return new EyeFixationSample(actual, expected);
    }

    private static Vector3 PerpendicularAxis(Vector3 direction)
    {
        Vector3 axis = direction.Cross(Vector3.Up);
        return axis.LengthSquared() < 1e-10f ? Vector3.Right : axis.Normalized();
    }
}
