using Godot;

namespace AlleyCat.IntegrationTests.Vision;

/// <summary>
/// Independent expected-direction derivation for the VISION-001 direct-fixation contract (TR51/52),
/// implementing the specification's Shared-Direction Geometry exactly: a shared solved eye origin,
/// componentwise 35°/25° clamps on angles extracted from the original direction before either is
/// clamped, pitch-then-yaw reconstruction, defined rear/vertical/coincident handling, and per-eye
/// immutable authored optical mapping.
/// </summary>
/// <remarks>
/// This oracle is deliberately independent: it never calls production gaze helpers
/// (<see cref="AlleyCat.Vision.EyesLookMath"/> and friends) and never derives a reference from live
/// gaze output, so a production geometry fault cannot cancel itself out of the measurement.
/// </remarks>
internal static class DirectFixationGeometry
{
    public const float MaxHorizontalAngleDegrees = 35f;
    public const float MaxVerticalAngleDegrees = 25f;

    /// <summary>
    /// The immutable authored optical-forward direction in eye-local coordinates. Both reference
    /// female eye nodes author their optical forward along the Godot −Z axis of the eye node; the
    /// authored toe-out rests live in the neutral rotation, not in this forward.
    /// </summary>
    public static readonly Vector3 OpticalForward = new(0f, 0f, -1f);

    /// <summary>
    /// Resolves the constrained shared-frame gaze rotation for a target offset expressed in the shared
    /// eye-origin frame (−Z forward, +X right, +Y up). Yaw and pitch are extracted from the original
    /// direction before either is clamped, clamped componentwise, and reconstructed as
    /// <c>D = Ry(−h)·Rx(v)</c> (pitch first, then yaw).
    /// </summary>
    /// <param name="targetOffsetSharedFrame">The target position minus the shared eye-origin position,
    /// rotated into the shared frame.</param>
    public static Quaternion ConstrainedGazeRotation(Vector3 targetOffsetSharedFrame)
    {
        ValidateFinite(targetOffsetSharedFrame, "target offset");
        float yaw;
        float pitch;
        if (targetOffsetSharedFrame.LengthSquared() <= Mathf.Epsilon)
        {
            // Coincident target: no direction exists, so the neutral request h = v = 0 applies.
            yaw = 0f;
            pitch = 0f;
        }
        else
        {
            float x = targetOffsetSharedFrame.X;
            float y = targetOffsetSharedFrame.Y;
            float z = targetOffsetSharedFrame.Z;
            yaw = Mathf.Atan2(x, -z);
            if (z > 0f && x == 0f)
            {
                // Exact rear axis: +180° regardless of the signed zero of x (IEEE == treats -0.0 as 0).
                yaw = Mathf.Pi;
            }
            else if (x == 0f && z == 0f)
            {
                // Exact vertical axis: the spec fixes yaw at 0 with pitch sign(y)·90°. Atan2(+0, −0)
                // reads +π under IEEE 754, which would clamp to +35°, so yaw is set explicitly.
                yaw = 0f;
            }

            // Pitch is well defined on every non-degenerate branch, including the vertical axis,
            // where Atan2(±y, +0) resolves to sign(y)·90°.
            pitch = Mathf.Atan2(y, Mathf.Sqrt((x * x) + (z * z)));
        }

        float clampedYaw = Mathf.Clamp(
            yaw,
            -Mathf.DegToRad(MaxHorizontalAngleDegrees),
            Mathf.DegToRad(MaxHorizontalAngleDegrees));
        float clampedPitch = Mathf.Clamp(
            pitch,
            -Mathf.DegToRad(MaxVerticalAngleDegrees),
            Mathf.DegToRad(MaxVerticalAngleDegrees));

        Quaternion yawRotation = new(Vector3.Up, -clampedYaw);
        Quaternion pitchRotation = new(Vector3.Right, clampedPitch);
        return yawRotation * pitchRotation;
    }

    /// <summary>
    /// Resolves the constrained shared-frame direction <c>d = D·(0, 0, −1)</c> for a target offset in
    /// the shared frame.
    /// </summary>
    public static Vector3 ConstrainedDirection(Vector3 targetOffsetSharedFrame)
        => ConstrainedGazeRotation(targetOffsetSharedFrame) * OpticalForward;

    /// <summary>
    /// Computes the neutral optical direction <c>n_i = P_i·N_i·f_i</c>: the immutable authored
    /// eye-local optical forward rotated by the authored neutral rotation into the parent frame, then
    /// by the current solved parent-to-world rotation.
    /// </summary>
    public static Vector3 NeutralOpticalDirection(
        Quaternion parentToWorldRotation,
        Quaternion authoredNeutralRotation,
        Vector3 opticalForward)
        => parentToWorldRotation * (authoredNeutralRotation * opticalForward);

    /// <summary>
    /// Computes the independently expected presented direction <c>e_i = R·D·R⁻¹·n_i</c>: the shared
    /// constrained request transported through the shared origin rotation onto the eye's authored
    /// neutral optical direction. Both eyes receive the same shared request through their authored
    /// optical mappings; authored neutral offsets such as toe-out are preserved, never corrected.
    /// </summary>
    public static Vector3 ExpectedEyeDirection(
        Quaternion sharedOriginRotation,
        Quaternion constrainedGazeRotation,
        Vector3 neutralOpticalDirection)
        => sharedOriginRotation * constrainedGazeRotation * sharedOriginRotation.Inverse()
            * neutralOpticalDirection;

    /// <summary>
    /// Angular distance in degrees between two directions: the acceptance comparison
    /// <c>acos(clamp(dot(normalise(a), normalise(e)), −1, 1))</c>.
    /// </summary>
    public static float AngleBetweenDegrees(Vector3 fromDirection, Vector3 toDirection)
    {
        ValidateFinite(fromDirection, "actual direction");
        ValidateFinite(toDirection, "expected direction");
        float dot = Mathf.Clamp(fromDirection.Normalized().Dot(toDirection.Normalized()), -1f, 1f);
        return Mathf.RadToDeg(Mathf.Acos(dot));
    }

    /// <summary>Shorthand for <see cref="AngleBetweenDegrees"/> in acceptance semantics.</summary>
    public static float AlignmentErrorDegrees(Vector3 actualDirection, Vector3 expectedDirection)
        => AngleBetweenDegrees(actualDirection, expectedDirection);

    private static void ValidateFinite(Vector3 value, string description)
    {
        if (!Mathf.IsFinite(value.X) || !Mathf.IsFinite(value.Y) || !Mathf.IsFinite(value.Z))
        {
            throw new InvalidOperationException(
                $"Non-finite {description} ({value}) invalidates direct-fixation acceptance; it can never pass it.");
        }
    }
}

/// <summary>
/// Loads and validates the immutable authored eye-neutral references shared by the direct-fixation
/// fixtures (TEST-3). Fail-closed by contract: a missing node, a node without an authored transform, a
/// wrong property type or a degenerate/non-finite basis throws with the missing input named — never a
/// silent identity or neutral fallback.
/// </summary>
internal static class DirectFixationReferences
{
    /// <summary>The female template whose scene state authors both eye-neutral transforms.</summary>
    public const string FemaleTemplatePath =
        "res://assets/characters/templates/reference_female/reference_female_base.tscn";

    /// <summary>
    /// Loads one eye's immutable authored neutral local rotation from a template's scene state. Both
    /// eyes of a fixture load through this single path so the bilateral references share one source and
    /// one validation.
    /// </summary>
    public static Basis LoadAuthoredEyeNeutral(string scenePath, string nodePathSuffix)
    {
        PackedScene template = ResourceLoader.Load<PackedScene>(scenePath)
            ?? throw new InvalidOperationException(
                $"The authored eye-reference scene '{scenePath}' failed to load; the bilateral immutable "
                + "references cannot be established.");
        SceneState state = template.GetState();
        for (int node = 0; node < state.GetNodeCount(); node++)
        {
            if (!state.GetNodePath(node).ToString().EndsWith(nodePathSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            for (int property = 0; property < state.GetNodePropertyCount(node); property++)
            {
                if (state.GetNodePropertyName(node, property) != "transform")
                {
                    continue;
                }

                Variant value = state.GetNodePropertyValue(node, property);
                return value.VariantType != Variant.Type.Transform3D
                    ? throw new InvalidOperationException(
                        $"The authored eye neutral '{nodePathSuffix}' in '{scenePath}' must serialise a "
                        + $"Transform3D transform, but the property holds {value.VariantType}.")
                    : EnsureAuthoredEyeNeutral(value.AsTransform3D().Basis, $"'{nodePathSuffix}' in '{scenePath}'");
            }

            throw new InvalidOperationException(
                $"The authored eye reference '{nodePathSuffix}' in '{scenePath}' has no authored 'transform' "
                + "property; refusing to fall back to an identity reference.");
        }

        throw new InvalidOperationException(
            $"The scene '{scenePath}' authors no node matching '{nodePathSuffix}'; the bilateral immutable "
            + "references cannot be established.");
    }

    /// <summary>
    /// Validates a captured authored eye neutral, failing closed on non-finite or degenerate input.
    /// Returns the candidate unchanged so callers keep the authored scale-and-rotation verbatim.
    /// </summary>
    public static Basis EnsureAuthoredEyeNeutral(Basis candidate, string description)
    {
        foreach (Vector3 column in new[] { candidate.X, candidate.Y, candidate.Z })
        {
            if (!Mathf.IsFinite(column.X) || !Mathf.IsFinite(column.Y) || !Mathf.IsFinite(column.Z))
            {
                throw new InvalidOperationException(
                    $"The authored eye neutral {description} contains non-finite values; acceptance cannot proceed.");
            }
        }

        float determinant = candidate.Orthonormalized().Determinant();
        return Mathf.Abs(determinant - 1f) > 1e-3f
            ? throw new InvalidOperationException(
                $"The authored eye neutral {description} is degenerate (orthonormalised determinant "
                + $"{determinant:F6}); acceptance cannot proceed.")
            : candidate;
    }

    /// <summary>
    /// Cross-validates a fixture-captured neutral against the authored scene-state value: both eyes'
    /// immutable references must describe one authored setup, so any divergence fails closed.
    /// </summary>
    public static void ValidateCapturedAgainstAuthored(Basis captured, Basis authored, string eyeName)
    {
        float divergenceDegrees = Mathf.RadToDeg(
            captured.Orthonormalized().GetRotationQuaternion()
                .AngleTo(authored.Orthonormalized().GetRotationQuaternion()));
        if (divergenceDegrees > 0.01f)
        {
            throw new InvalidOperationException(
                $"The captured authored neutral for {eyeName} diverges {divergenceDegrees:F4}° from the "
                + "template scene state; the bilateral immutable reference is broken.");
        }
    }
}

/// <summary>Frozen direct-fixation acceptance bounds (VISION-001 TR51/52 and AC70).</summary>
internal static class DirectFixationAcceptance
{
    /// <summary>Acquisition onset must occur within this many eligible presented frames.</summary>
    public const int MaxOnsetEligibleFrames = 2;

    /// <summary>Acquisition completion deadline measured from actual assignment, with no cadence grace.</summary>
    public const double CompletionDeadlineSeconds = 0.4;

    /// <summary>Maximum angular alignment error for fixation, per eye.</summary>
    public const float AlignmentToleranceDegrees = 0.5f;

    /// <summary>An eye counts as responding once its presented direction moves this far from the
    /// pre-assignment presented direction.</summary>
    public const float OnsetMovementThresholdDegrees = 0.5f;
}

/// <summary>One eye's measured and independently expected presented optical direction.</summary>
internal readonly record struct EyeFixationSample(Vector3 ActualDirection, Vector3 ExpectedDirection)
{
    public float ErrorDegrees => DirectFixationGeometry.AngleBetweenDegrees(ActualDirection, ExpectedDirection);
}

/// <summary>
/// One presentation: the rendered-scene-state sampling event's timestamp and monotonic sequence, plus
/// both eyes' measured and expected directions for that presentation.
/// </summary>
internal readonly record struct FixationPresentation(
    double TimeSeconds,
    long Sequence,
    EyeFixationSample Left,
    EyeFixationSample Right);

/// <summary>
/// The evaluated direct-fixation/acquisition window. Onset, completion and maintenance semantics follow
/// the spec's Assignment and Presentation Ordering: a presentation is eligible exactly when its sampling
/// sequence follows the assignment, equal timestamps are resolved by monotonic sequence, the completion
/// deadline runs from the actual assignment time, and no cadence grace applies.
/// </summary>
/// <param name="OnsetFrame">The 1-based eligible frame whose presented directions first move beyond
/// <see cref="DirectFixationAcceptance.OnsetMovementThresholdDegrees"/> from the pre-assignment
/// presented directions; <see langword="null"/> when neither eye ever responds.</param>
/// <param name="CompletionSeconds">Elapsed seconds from actual assignment to the first eligible
/// presentation with both eyes within tolerance; <see langword="null"/> when acquisition never completes.</param>
/// <param name="CompletionFrame">The 1-based eligible frame at which completion occurred.</param>
/// <param name="MaxPostCompletionErrorDegrees">The worst per-eye alignment error over all presentations
/// after the completion presentation.</param>
/// <param name="EligiblePresentationCount">Presentations whose sampling followed the assignment.</param>
/// <param name="PreAssignmentPresentationCount">Presentations excluded because their sampling preceded
/// the assignment, regardless of a later post-draw observation.</param>
internal sealed record FixationWindowResult(
    int? OnsetFrame,
    double? CompletionSeconds,
    int? CompletionFrame,
    float MaxPostCompletionErrorDegrees,
    int EligiblePresentationCount,
    int PreAssignmentPresentationCount)
{
    /// <summary>Whether acquisition onset met the ≤2 eligible presented frames bound.</summary>
    public bool IsOnsetWithinBound
        => OnsetFrame is >= 1 and <= DirectFixationAcceptance.MaxOnsetEligibleFrames;

    /// <summary>Whether acquisition completed within 0.4 s of actual assignment.</summary>
    public bool IsCompletionWithinDeadline
        => CompletionSeconds is not null
            && CompletionSeconds.Value <= DirectFixationAcceptance.CompletionDeadlineSeconds;

    /// <summary>Whether both eyes maintained alignment after acquisition completed.</summary>
    public bool IsFixationMaintained
        => MaxPostCompletionErrorDegrees <= DirectFixationAcceptance.AlignmentToleranceDegrees;
}

/// <summary>
/// Evaluator-level acquisition/fixation window over recorded presentations. The window records one
/// assignment (never resettable) and any number of presentations; <see cref="Evaluate"/> derives the
/// eligible frame numbering, onset, completion and maintenance verdicts. Head or target motion between
/// presentations changes the expected directions but can never move the assignment event.
/// </summary>
/// <remarks>
/// Opens a window at the actual assignment event. The pre-assignment directions are the last
/// presented directions before assignment, not a fresh live read.
/// </remarks>
internal sealed class FixationAcquisitionWindow(
    double assignmentTimeSeconds,
    long assignmentSequence,
    Vector3 preAssignmentLeftDirection,
    Vector3 preAssignmentRightDirection)
{
    private readonly double _assignmentTimeSeconds = assignmentTimeSeconds;
    private readonly long _assignmentSequence = assignmentSequence;
    private readonly Vector3 _preAssignmentLeftDirection = preAssignmentLeftDirection;
    private readonly Vector3 _preAssignmentRightDirection = preAssignmentRightDirection;
    private readonly List<FixationPresentation> _presentations = [];

    /// <summary>Records one presentation. Sequences must be monotonic across all events.</summary>
    public void AddPresentation(FixationPresentation presentation) => _presentations.Add(presentation);

    /// <summary>Evaluates onset, completion and maintenance over the recorded presentations.</summary>
    public FixationWindowResult Evaluate()
    {
        int? onsetFrame = null;
        int? completionFrame = null;
        double? completionSeconds = null;
        float maxPostCompletionError = 0f;
        int eligibleCount = 0;
        int preAssignmentCount = 0;

        foreach (FixationPresentation presentation in _presentations.OrderBy(p => p.Sequence))
        {
            if (presentation.Sequence <= _assignmentSequence)
            {
                // Pre-assignment presentations never count towards onset or completion, even when a
                // post-draw observation of them happens after the assignment.
                preAssignmentCount++;
                continue;
            }

            eligibleCount++;
            if (onsetFrame is null && (Moved(presentation.Left.ActualDirection, _preAssignmentLeftDirection)
                || Moved(presentation.Right.ActualDirection, _preAssignmentRightDirection)))
            {
                onsetFrame = eligibleCount;
            }

            if (completionFrame is null
                && presentation.Left.ErrorDegrees <= DirectFixationAcceptance.AlignmentToleranceDegrees
                && presentation.Right.ErrorDegrees <= DirectFixationAcceptance.AlignmentToleranceDegrees)
            {
                completionFrame = eligibleCount;
                completionSeconds = presentation.TimeSeconds - _assignmentTimeSeconds;
            }
            else if (completionFrame is not null)
            {
                maxPostCompletionError = MathF.Max(
                    maxPostCompletionError,
                    MathF.Max(presentation.Left.ErrorDegrees, presentation.Right.ErrorDegrees));
            }
        }

        return new FixationWindowResult(
            onsetFrame,
            completionSeconds,
            completionFrame,
            maxPostCompletionError,
            eligibleCount,
            preAssignmentCount);
    }

    private static bool Moved(Vector3 presented, Vector3 preAssignment)
        => DirectFixationGeometry.AngleBetweenDegrees(presented, preAssignment)
            > DirectFixationAcceptance.OnsetMovementThresholdDegrees;
}
