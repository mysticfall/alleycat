using Godot;

namespace AlleyCat.Mind.Attention;

/// <summary>
/// Solving mode of <see cref="GeometricGazeSaturation"/>: the objective the bounded geometric search minimises.
/// </summary>
public enum GazeSaturationMode
{
    /// <summary>
    /// Out-of-cone glance saturation: apply the least orientation change to the reference command that places the
    /// predicted target gaze direction inside the asymmetric comfort cone, within the envelope.
    /// </summary>
    Saturation,

    /// <summary>
    /// Sustained-attention centring: minimise the face-local eccentricity of the target inside the envelope, aiming
    /// the face exactly at the anchor when the envelope permits.
    /// </summary>
    Centring,
}

/// <summary>
/// Asymmetric per-axis angular bounds in radians, used for both the eye comfort cone and the head orientation
/// envelope. Positive yaw is leftward and positive pitch is upward, matching the AI-009 signed forward convention.
/// </summary>
/// <param name="YawPositiveRadians">Bound for positive (leftward) yaw; finite and within the open interval (0, π/2).</param>
/// <param name="YawNegativeRadians">Bound for negative (rightward) yaw; finite and within the open interval (0, π/2).</param>
/// <param name="PitchPositiveRadians">Bound for positive (upward) pitch; finite and within the open interval (0, π/2).</param>
/// <param name="PitchNegativeRadians">Bound for negative (downward) pitch; finite and within the open interval (0, π/2).</param>
public sealed record AngularBounds(
    double YawPositiveRadians,
    double YawNegativeRadians,
    double PitchPositiveRadians,
    double PitchNegativeRadians);

/// <summary>
/// One geometric saturation request, expressed entirely in the declared envelope frame coordinates.
/// </summary>
/// <remarks>
/// <para>
/// <b>Frames.</b> Every position and basis is expressed in the envelope frame <c>E</c>: a right-handed orthonormal
/// frame whose up axis is <c>+Y</c> and whose reference forward is <c>−Z</c>, transported by whatever root owns the
/// envelope (the character frame in the AI-009 reference design). The command is a complete roll-free orientation
/// <c>D = R_Y(yaw) · R_X(pitch)</c> relative to <c>E</c>; the envelope bounds constrain <c>(yaw, pitch)</c> directly,
/// so every emitted orientation respects the envelope and is roll-free against <c>E</c>'s up by construction — no
/// roll stripping is ever applied to a candidate.
/// </para>
/// <para>
/// <b>Comfort geometry.</b> The comfort cone is measured face-locally: the target direction
/// <c>(AnchorPosition − predictedEyeOrigin)</c> is expressed in the candidate face frame by an exact rotation
/// transpose, and yaw/pitch are extracted with the AI-009 signed forward convention (positive yaw leftward,
/// positive pitch upward, elevation form). Per-axis scalar subtraction of commanded angles is never used for
/// classification or correction — that is the HEAD-2 defect family this helper replaces.
/// </para>
/// <para>
/// <b>Near-target prediction.</b> The predicted eye origin follows the reference design's positional construction:
/// <c>O(D) = PivotPosition + (D · Uᵀ) · (UpstreamHeadOrigin − PivotPosition) + D · ViewpointOffset</c>, recomputed
/// for every candidate orientation. When a <see cref="FoldPrediction"/> frame is supplied, the head-origin term is
/// replaced by that frame's continuous resolution for EVERY candidate — the exact construction the controller
/// publishes for the same command, including its animation-anchored upstream and engagement blend, so prediction
/// and publication share one geometry with no command gate. This predicts the geometry of the requested pose; it
/// is not an assertion that a constrained skeletal solve realises it.
/// </para>
/// </remarks>
/// <param name="UpstreamFaceBasis">Upstream animation face basis <c>U</c> in envelope-frame coordinates; proper orthonormal (authored roll is permitted).</param>
/// <param name="UpstreamHeadOrigin">Upstream head origin <c>h</c> in envelope-frame coordinates.</param>
/// <param name="PivotPosition">Neck pivot position <c>p</c> in envelope-frame coordinates.</param>
/// <param name="ViewpointOffset">Authored viewpoint (eye-centre) offset in upstream face-local coordinates.</param>
/// <param name="AnchorPosition">Gaze anchor position in envelope-frame coordinates.</param>
/// <param name="ReferenceFaceBasis">Held/reference command <c>D_ref</c> in envelope-frame coordinates; must be a proper orthonormal roll-free orientation. A rolled reference is rejected rather than silently stripped.</param>
/// <param name="ComfortCone">Asymmetric face-local comfort cone bounds.</param>
/// <param name="OrientationEnvelope">Asymmetric envelope bounds on the command yaw/pitch in the envelope frame.</param>
/// <param name="Mode">Solving mode: cone saturation or sustained centring.</param>
/// <param name="FoldPrediction">Optional per-evaluation fold-family prediction frame (same evaluation's
/// animation-owned upstream geometry, envelope frame). When present, EVERY candidate's predicted head
/// origin is the frame's continuous resolution — the same single construction the controller publishes
/// for that command, with no yaw/pitch gate: the frame's engagement blend reproduces the TR 21 orbit
/// exactly on the legacy regime and the yaw-composed fold family on the downward regime. The frame's
/// upstream head/pivot/face values must be this request's own upstream geometry. A degenerate frame
/// resolution falls back to the internal pivot-orbit prediction for that candidate.</param>
public readonly record struct GazeSaturationRequest(
    Basis UpstreamFaceBasis,
    Vector3 UpstreamHeadOrigin,
    Vector3 PivotPosition,
    Vector3 ViewpointOffset,
    Vector3 AnchorPosition,
    Basis ReferenceFaceBasis,
    AngularBounds ComfortCone,
    AngularBounds OrientationEnvelope,
    GazeSaturationMode Mode,
    DownwardFoldPredictionFrame? FoldPrediction = null);

/// <summary>
/// The deterministic outcome of one geometric saturation solve.
/// </summary>
/// <param name="IsFeasible">
/// <see langword="true"/> when the returned orientation satisfies the mode objective within
/// <see cref="GeometricGazeSaturation.ConvergenceToleranceRadians"/>: the predicted gaze lies inside the closed
/// comfort cone (saturation) or the face aims at the anchor (centring). <see langword="false"/> means no such
/// orientation was reached: the returned orientation is the best-effort candidate and <paramref name="ResidualRadians"/>
/// reports its exact remaining violation.
/// </param>
/// <param name="Mode">The solving mode the result was produced for.</param>
/// <param name="FaceBasis">The roll-free result orientation in envelope-frame coordinates; always within the envelope bounds.</param>
/// <param name="YawRadians">Result command yaw in the envelope frame, radians.</param>
/// <param name="PitchRadians">Result command pitch in the envelope frame, radians.</param>
/// <param name="RotationFromReferenceRadians">Geodesic rotation angle from the reference command to the result orientation, radians.</param>
/// <param name="ResidualRadians">
/// Zero when <paramref name="IsFeasible"/>; otherwise the exact angular violation of the returned candidate:
/// the angle from its predicted face-local target direction to the nearest in-cone direction (saturation) or to the
/// face forward axis (centring), radians.
/// </param>
/// <param name="FaceLocalTargetYawRadians">Face-local yaw of the target measured at the result orientation, radians.</param>
/// <param name="FaceLocalTargetPitchRadians">Face-local pitch of the target measured at the result orientation, radians.</param>
/// <param name="PredictedEyeOrigin">Predicted eye origin of the result pose in envelope-frame coordinates.</param>
/// <param name="Iterations">Number of candidate evaluations performed; bounded by <see cref="GeometricGazeSaturation.MaxIterations"/>.</param>
public sealed record GazeSaturationResult(
    bool IsFeasible,
    GazeSaturationMode Mode,
    Basis FaceBasis,
    double YawRadians,
    double PitchRadians,
    double RotationFromReferenceRadians,
    double ResidualRadians,
    double FaceLocalTargetYawRadians,
    double FaceLocalTargetPitchRadians,
    Vector3 PredictedEyeOrigin,
    int Iterations);

/// <summary>
/// Bounded, deterministic geometric saturation helper for AI-009 head orientation (spec gate 25). Given a held or
/// reference command, a gaze anchor, an asymmetric face-local comfort cone and a roll-free orientation envelope —
/// all in the declared envelope frame — it computes the least orientation change applied to the commanded aim that
/// brings the predicted target gaze direction inside the comfort cone while respecting the envelope, or, for
/// sustained attention, minimises the face-local target eccentricity within the envelope.
/// </summary>
/// <remarks>
/// <para>
/// <b>Method.</b> The solver is a fixed-point correction search over the roll-free command pair
/// <c>(yaw, pitch)</c>. Each evaluation builds the candidate orientation analytically, recomputes the
/// parallax-predicted eye origin for that pose, expresses the anchor ray in the candidate face frame by exact
/// rotation composition, and extracts face-local yaw/pitch with the AI-009 signed forward convention. When the
/// objective is violated, the aim advances by exactly the measured excess (saturation) or the full measured angle
/// (centring) in envelope-frame coordinates, then clamps to the envelope box; yaw before pitch in a fixed order.
/// Because yaw/pitch corrections couple through the composition (a yaw step at pitched aim changes the face-local
/// pitch and vice versa), a single pass is not exact: the loop iterates the correction to its fixed point and
/// accepts the result only when the recomposed geometry verifies the objective within
/// <see cref="ConvergenceToleranceRadians"/>. This is a checked, convergent composition — not the HEAD-2 family of
/// an unchecked one-pass Euler correction plus roll stripping.
/// </para>
/// <para>
/// <b>Minimality.</b> Every step applies exactly the measured excess — no deadband, no overshoot — so a single-axis
/// violation converges to the least single-axis correction whose recomposed face-local angle sits exactly on the
/// cone boundary. The result orientation is roll-free and inside the envelope by construction, on every iteration.
/// </para>
/// <para>
/// <b>Determinism.</b> The solve is a pure function: fixed validation order, fixed correction order, IEEE-754
/// double arithmetic throughout the search, no randomness, clocks or order-dependent collections, and a hard
/// iteration bound. Candidate ranking on non-convergence is total and fixed: smaller total violation, then smaller
/// rotation from the reference, then lexicographically smaller <c>(yaw, pitch)</c>. Identical requests produce
/// bitwise-identical results.
/// </para>
/// <para>
/// <b>Infeasibility.</b> The verdict is conservative and explicit: feasibility is only reported when the final
/// candidate verifies against the exact composed geometry. When the envelope pins the aim short of the cone (or of
/// the exact aim), the monotone search stalls, stops, and reports the best candidate together with
/// <see cref="GazeSaturationResult.ResidualRadians"/> — the exact remaining angular violation of that candidate.
/// Envelope bounds are validated below π/2, so the command parameterisation stays non-degenerate; a request whose
/// search has not converged after <see cref="MaxIterations"/> evaluations resolves deterministically to the same
/// explicit best-effort verdict rather than looping or diverging silently.
/// </para>
/// <para>
/// <b>Coupled fold prediction.</b> When a fold frame is supplied, every candidate resolves through the frame's
/// continuous construction: the frame's engagement blend reproduces the pivot-orbit prediction on the legacy
/// regime and the yaw-composed animation-anchored fold family on the downward regime, continuously across the
/// engagement band — there is no gate and no construction branch in the search. Each frame resolution is a fixed
/// 33-sample bracket comparison plus one exact FK composition — never an adaptive search. A degenerate frame
/// resolution falls back to the internal pivot-orbit prediction for that candidate only.
/// </para>
/// </remarks>
public static class GeometricGazeSaturation
{
    /// <summary>Objective tolerance: a candidate verifies as feasible when its total violation is at most this, in radians.</summary>
    public const double ConvergenceToleranceRadians = 1e-7;

    /// <summary>Hard bound on candidate evaluations per solve.</summary>
    public const int MaxIterations = 128;

    private const double HalfPi = Math.PI / 2d;

    /// <summary>
    /// Tolerance for validating caller-supplied float32 bases: proper orthonormality and roll-freeness. Float32
    /// quaternion-constructed bases carry measured apparent errors up to ~1.5e-4 radians; genuine roll or shear is
    /// orders of magnitude larger.
    /// </summary>
    private const double BasisValidationToleranceRadians = 1e-3;
    private const double ImprovementToleranceRadians = 1e-12;
    private const double DegenerateDistanceSquared = 1e-18;
    private const double DegenerateDirectionMagnitude = 1e-9;

    /// <summary>
    /// Composes the roll-free command basis of a (yaw, pitch) pair in the envelope frame — the exact composition
    /// the solver itself builds candidates with, exposed so callers compose published commands identically.
    /// </summary>
    public static Basis ComposeCommandBasis(double yawRadians, double pitchRadians)
        => ColumnBasis.FromYawPitch(yawRadians, pitchRadians).ToGodotBasis();

    /// <summary>
    /// Solves one geometric saturation or centring request deterministically.
    /// </summary>
    /// <param name="request">The request, in envelope-frame coordinates; see <see cref="GazeSaturationRequest"/>.</param>
    /// <returns>The feasible orientation of least measured correction, or the explicit best-effort candidate with its residual.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Any cone or envelope bound is not finite or not within the open interval (0, π/2), or the mode is not a
    /// defined <see cref="GazeSaturationMode"/> value.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The upstream or reference basis is not proper orthonormal, the reference command is not roll-free against the
    /// envelope up axis, or the anchor coincides with a predicted eye origin (degenerate direction).
    /// </exception>
    public static GazeSaturationResult Solve(GazeSaturationRequest request)
    {
        ValidateBounds(request.ComfortCone, nameof(request), "comfort cone");
        ValidateBounds(request.OrientationEnvelope, nameof(request), "envelope");
        if (!Enum.IsDefined(request.Mode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Mode,
                "Gaze saturation mode is not a defined value.");
        }

        ColumnBasis upstream = ValidateProperOrthonormal(
            request.UpstreamFaceBasis,
            nameof(request),
            "upstream face basis");
        (double referenceYaw, double referencePitch) = ExtractRollFreeCommand(request.ReferenceFaceBasis, nameof(request));
        var referenceBasis = ColumnBasis.FromYawPitch(referenceYaw, referencePitch);
        AngularBounds envelope = request.OrientationEnvelope;

        double yaw = Math.Clamp(referenceYaw, -envelope.YawNegativeRadians, envelope.YawPositiveRadians);
        double pitch = Math.Clamp(referencePitch, -envelope.PitchNegativeRadians, envelope.PitchPositiveRadians);

        D3 anchor = ToD3(request.AnchorPosition);
        D3 pivot = ToD3(request.PivotPosition);
        D3 headOrigin = ToD3(request.UpstreamHeadOrigin);
        D3 viewpointOffset = ToD3(request.ViewpointOffset);
        D3 leverArm = Subtract(headOrigin, pivot);

        Candidate? best = null;
        int iterations = 0;

        while (iterations < MaxIterations)
        {
            iterations++;
            var candidateBasis = ColumnBasis.FromYawPitch(yaw, pitch);
            D3 predictedEyeOrigin = request.FoldPrediction is { } fold
                && fold.TryPredictHeadOrigin(yaw, pitch, out Vector3 frameOrigin, out _)
                ? Add(ToD3(frameOrigin), candidateBasis.Apply(viewpointOffset))
                : PredictEyeOrigin(candidateBasis, upstream, pivot, leverArm, viewpointOffset);
            D3 toTarget = Subtract(anchor, predictedEyeOrigin);
            double distanceSquared = LengthSquared(toTarget);
            if (distanceSquared <= DegenerateDistanceSquared)
            {
                throw new ArgumentException(
                    $"The gaze anchor coincides with the predicted eye origin at command (yaw '{yaw}', pitch '{pitch}'); " +
                    "a saturation request requires a non-degenerate target direction.",
                    nameof(request));
            }

            D3 faceLocalUnit = Scale(toTarget, 1d / Math.Sqrt(distanceSquared));
            faceLocalUnit = candidateBasis.ApplyTransposed(faceLocalUnit);
            (double targetYaw, double targetPitch) = ExtractFaceLocalAngles(faceLocalUnit);

            (double yawError, double pitchError) = request.Mode == GazeSaturationMode.Saturation
                ? (ConeExcess(targetYaw, request.ComfortCone.YawPositiveRadians, request.ComfortCone.YawNegativeRadians),
                    ConeExcess(
                        targetPitch,
                        request.ComfortCone.PitchPositiveRadians,
                        request.ComfortCone.PitchNegativeRadians))
                : (targetYaw, targetPitch);
            double totalError = Math.Sqrt((yawError * yawError) + (pitchError * pitchError));

            double rotationFromReference = RotationAngle(referenceBasis, candidateBasis);
            if (totalError <= ConvergenceToleranceRadians)
            {
                return BuildResult(
                    true,
                    request.Mode,
                    yaw,
                    pitch,
                    rotationFromReference,
                    0d,
                    targetYaw,
                    targetPitch,
                    predictedEyeOrigin,
                    iterations);
            }

            Candidate candidate = new(
                yaw,
                pitch,
                totalError,
                rotationFromReference,
                targetYaw,
                targetPitch,
                predictedEyeOrigin,
                faceLocalUnit);

            if (best is not { } currentBest)
            {
                best = candidate;
            }
            else if (candidate.TotalErrorRadians < currentBest.TotalErrorRadians - ImprovementToleranceRadians)
            {
                best = candidate;
            }
            else
            {
                // No monotone improvement: envelope pinning, a stall, or convergence-floor noise. Resolve the
                // remaining tie deterministically, then stop — the loop cannot make further verified progress.
                if (RanksBefore(candidate, currentBest))
                {
                    best = candidate;
                }

                break;
            }

            yaw = Math.Clamp(yaw + yawError, -envelope.YawNegativeRadians, envelope.YawPositiveRadians);
            pitch = Math.Clamp(pitch + pitchError, -envelope.PitchNegativeRadians, envelope.PitchPositiveRadians);
        }

        return BuildBestEffort(best.GetValueOrDefault(), request, iterations);
    }

    private static GazeSaturationResult BuildBestEffort(Candidate best, GazeSaturationRequest request, int iterations)
    {
        double residual = request.Mode == GazeSaturationMode.Saturation
            ? ConeResidualAngle(best.FaceLocalUnit, request.ComfortCone)
            : Math.Acos(Math.Clamp(-best.FaceLocalUnit.Z, -1d, 1d));
        return BuildResult(
            false,
            request.Mode,
            best.YawRadians,
            best.PitchRadians,
            best.RotationFromReferenceRadians,
            residual,
            best.TargetYawRadians,
            best.TargetPitchRadians,
            best.PredictedEyeOrigin,
            iterations);
    }

    private static GazeSaturationResult BuildResult(
        bool isFeasible,
        GazeSaturationMode mode,
        double yaw,
        double pitch,
        double rotationFromReference,
        double residual,
        double targetYaw,
        double targetPitch,
        D3 predictedEyeOrigin,
        int iterations)
    {
        var basis = ColumnBasis.FromYawPitch(yaw, pitch);
        return new GazeSaturationResult(
            isFeasible,
            mode,
            basis.ToGodotBasis(),
            yaw,
            pitch,
            rotationFromReference,
            residual,
            targetYaw,
            targetPitch,
            predictedEyeOrigin.ToGodotVector(),
            iterations);
    }

    private static D3 PredictEyeOrigin(
        ColumnBasis candidateBasis,
        ColumnBasis upstream,
        D3 pivot,
        D3 leverArm,
        D3 viewpointOffset)
    {
        // O(D) = p + (D · Uᵀ) · (h − p) + D · v, evaluated with exact rotation composition in doubles.
        D3 rotatedLever = candidateBasis.Apply(upstream.ApplyTransposed(leverArm));
        D3 rotatedOffset = candidateBasis.Apply(viewpointOffset);
        return new D3(
            pivot.X + rotatedLever.X + rotatedOffset.X,
            pivot.Y + rotatedLever.Y + rotatedOffset.Y,
            pivot.Z + rotatedLever.Z + rotatedOffset.Z);
    }

    /// <summary>Returns the signed amount by which <paramref name="angle"/> lies beyond its asymmetric bounds; zero inside.</summary>
    private static double ConeExcess(double angle, double positiveBound, double negativeBound)
        => angle > positiveBound
            ? angle - positiveBound
            : angle < -negativeBound ? angle + negativeBound : 0d;

    /// <summary>
    /// Exact angular violation of a face-local direction against the closed cone: the geodesic angle to the
    /// direction obtained by clamping its yaw/pitch into the cone bounds. Zero exactly when inside.
    /// </summary>
    private static double ConeResidualAngle(D3 faceLocalUnit, AngularBounds cone)
    {
        (double yaw, double pitch) = ExtractFaceLocalAngles(faceLocalUnit);
        double clampedYaw = Math.Clamp(yaw, -cone.YawNegativeRadians, cone.YawPositiveRadians);
        double clampedPitch = Math.Clamp(pitch, -cone.PitchNegativeRadians, cone.PitchPositiveRadians);
        D3 clampedDirection = DirectionFromYawPitch(clampedYaw, clampedPitch);
        return Math.Acos(Math.Clamp(Dot(faceLocalUnit, clampedDirection), -1d, 1d));
    }

    /// <summary>
    /// Extracts yaw/pitch with the AI-009 signed forward convention: yaw = atan2(−x, −z), pitch = atan2(y, √(x² + z²)).
    /// B5 guard: on an exact vertical ray (x = z = 0) the yaw term would read Atan2(−0, −0) = π under IEEE 754;
    /// the convention fixes yaw 0 there with pitch ±π/2, matching the spec's vertical-axis rule.
    /// </summary>
    private static (double YawRadians, double PitchRadians) ExtractFaceLocalAngles(D3 direction)
    {
        double horizontal = Math.Sqrt((direction.X * direction.X) + (direction.Z * direction.Z));
        if (horizontal <= DegenerateDirectionMagnitude)
        {
            return (0d, direction.Y >= 0d ? HalfPi : -HalfPi);
        }

        return (Math.Atan2(-direction.X, -direction.Z), Math.Atan2(direction.Y, horizontal));
    }

    /// <summary>Inverse of <see cref="ExtractFaceLocalAngles"/>: the unit direction of a yaw/pitch pair.</summary>
    private static D3 DirectionFromYawPitch(double yawRadians, double pitchRadians)
    {
        double cosPitch = Math.Cos(pitchRadians);
        return new D3(-cosPitch * Math.Sin(yawRadians), Math.Sin(pitchRadians), -cosPitch * Math.Cos(yawRadians));
    }

    /// <summary>
    /// Geodesic rotation angle between two orientations via atan2 of the antisymmetric and symmetric invariants of
    /// Aᵀ·B: numerically exact at zero (identical bases) and graceful at 180°, unlike the raw acos trace form.
    /// </summary>
    private static double RotationAngle(ColumnBasis from, ColumnBasis to)
    {
        double cosAngle = (Dot(from.X, to.X) + Dot(from.Y, to.Y) + Dot(from.Z, to.Z) - 1d) / 2d;
        double yz = Dot(from.Y, to.Z) - Dot(from.Z, to.Y);
        double zx = Dot(from.Z, to.X) - Dot(from.X, to.Z);
        double xy = Dot(from.X, to.Y) - Dot(from.Y, to.X);
        double sinAngle = Math.Sqrt((yz * yz) + (zx * zx) + (xy * xy)) / 2d;
        return Math.Atan2(sinAngle, Math.Clamp(cosAngle, -1d, 1d));
    }

    /// <summary>
    /// Extracts the roll-free command pair from a reference basis, validating properness, orthonormality and
    /// roll-freeness. A rolled reference is rejected: the helper never silently strips roll from an input command.
    /// </summary>
    private static (double YawRadians, double PitchRadians) ExtractRollFreeCommand(Basis basis, string paramName)
    {
        ColumnBasis columns = ValidateProperOrthonormal(basis, paramName, "reference face basis");
        D3 forward = new(-columns.Z.X, -columns.Z.Y, -columns.Z.Z);
        double pitch = Math.Asin(Math.Clamp(forward.Y, -1d, 1d));
        double yaw = Math.Atan2(-forward.X, -forward.Z);
        var rebuilt = ColumnBasis.FromYawPitch(yaw, pitch);
        double rollError = RotationAngle(rebuilt, columns);
        return rollError > BasisValidationToleranceRadians
            ? throw new ArgumentException(
                $"The reference face basis must be roll-free against the envelope up axis (measured roll error " +
                $"'{rollError}' radians exceeds '{BasisValidationToleranceRadians}'); refusing a rolled command " +
                "instead of silently stripping roll.",
                paramName)
            : (yaw, pitch);
    }

    private static ColumnBasis ValidateProperOrthonormal(Basis basis, string paramName, string roleName)
    {
        ColumnBasis columns = new(ToD3(basis.X), ToD3(basis.Y), ToD3(basis.Z));
        if (Math.Abs(LengthSquared(columns.X) - 1d) > BasisValidationToleranceRadians
            || Math.Abs(LengthSquared(columns.Y) - 1d) > BasisValidationToleranceRadians
            || Math.Abs(LengthSquared(columns.Z) - 1d) > BasisValidationToleranceRadians
            || Math.Abs(Dot(columns.X, columns.Y)) > BasisValidationToleranceRadians
            || Math.Abs(Dot(columns.X, columns.Z)) > BasisValidationToleranceRadians
            || Math.Abs(Dot(columns.Y, columns.Z)) > BasisValidationToleranceRadians)
        {
            throw new ArgumentException(
                $"The {roleName} must be an orthonormal basis (unit, mutually orthogonal columns); found a basis " +
                "outside the validation tolerance.",
                paramName);
        }

        double determinant = Dot(columns.X, Cross(columns.Y, columns.Z));
        return Math.Abs(determinant - 1d) > BasisValidationToleranceRadians
            ? throw new ArgumentException(
                $"The {roleName} must be a proper rotation (determinant +1); found determinant '{determinant}'.",
                paramName)
            : columns;
    }

    private static void ValidateBounds(AngularBounds bounds, string paramName, string roleName)
    {
        ValidateBound(bounds.YawPositiveRadians, paramName, roleName, nameof(AngularBounds.YawPositiveRadians));
        ValidateBound(bounds.YawNegativeRadians, paramName, roleName, nameof(AngularBounds.YawNegativeRadians));
        ValidateBound(bounds.PitchPositiveRadians, paramName, roleName, nameof(AngularBounds.PitchPositiveRadians));
        ValidateBound(bounds.PitchNegativeRadians, paramName, roleName, nameof(AngularBounds.PitchNegativeRadians));
    }

    private static void ValidateBound(double value, string paramName, string roleName, string fieldName)
    {
        if (!double.IsFinite(value) || value <= 0d || value >= HalfPi)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                $"{roleName} {fieldName} '{value}' must be finite and within the open interval (0, π/2).");
        }
    }

    /// <summary>
    /// Deterministic ranking among candidates whose total violations are equal (to within
    /// <see cref="ImprovementToleranceRadians"/>): smaller rotation from the reference command, then
    /// lexicographically smaller (yaw, pitch). Strict comparisons keep the first candidate on exact ties.
    /// </summary>
    private static bool RanksBefore(Candidate candidate, Candidate best)
        => candidate.RotationFromReferenceRadians != best.RotationFromReferenceRadians
            ? candidate.RotationFromReferenceRadians < best.RotationFromReferenceRadians
            : candidate.YawRadians != best.YawRadians
                ? candidate.YawRadians < best.YawRadians
                : candidate.PitchRadians < best.PitchRadians;

    private static D3 ToD3(Vector3 vector) => new(vector.X, vector.Y, vector.Z);

    private static double Dot(D3 left, D3 right) => (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);

    private static D3 Cross(D3 left, D3 right) => new(
        (left.Y * right.Z) - (left.Z * right.Y),
        (left.Z * right.X) - (left.X * right.Z),
        (left.X * right.Y) - (left.Y * right.X));

    private static double LengthSquared(D3 vector) => Dot(vector, vector);

    private static D3 Subtract(D3 left, D3 right)
        => new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    private static D3 Scale(D3 vector, double factor)
        => new(vector.X * factor, vector.Y * factor, vector.Z * factor);

    private static D3 Add(D3 left, D3 right)
        => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    /// <summary>One evaluated candidate of the correction search.</summary>
    /// <param name="YawRadians">Command yaw of the candidate.</param>
    /// <param name="PitchRadians">Command pitch of the candidate.</param>
    /// <param name="TotalErrorRadians">Total objective violation measured at the candidate.</param>
    /// <param name="RotationFromReferenceRadians">Geodesic rotation from the reference command.</param>
    /// <param name="TargetYawRadians">Face-local target yaw measured at the candidate.</param>
    /// <param name="TargetPitchRadians">Face-local target pitch measured at the candidate.</param>
    /// <param name="PredictedEyeOrigin">Predicted eye origin of the candidate pose.</param>
    /// <param name="FaceLocalUnit">Unit target direction in the candidate face frame.</param>
    private readonly record struct Candidate(
        double YawRadians,
        double PitchRadians,
        double TotalErrorRadians,
        double RotationFromReferenceRadians,
        double TargetYawRadians,
        double TargetPitchRadians,
        D3 PredictedEyeOrigin,
        D3 FaceLocalUnit);

    /// <summary>Double-precision orthonormal basis stored by columns; the exact-math core of the helper.</summary>
    private readonly record struct ColumnBasis(D3 X, D3 Y, D3 Z)
    {
        /// <summary>Builds the roll-free basis D = R_Y(yaw) · R_X(pitch); roll is zero by construction.</summary>
        public static ColumnBasis FromYawPitch(double yaw, double pitch)
        {
            double cosYaw = Math.Cos(yaw);
            double sinYaw = Math.Sin(yaw);
            double cosPitch = Math.Cos(pitch);
            double sinPitch = Math.Sin(pitch);
            return new ColumnBasis(
                new D3(cosYaw, 0d, -sinYaw),
                new D3(sinYaw * sinPitch, cosPitch, cosYaw * sinPitch),
                new D3(sinYaw * cosPitch, -sinPitch, cosYaw * cosPitch));
        }

        public D3 ApplyTransposed(D3 vector) => new(Dot(X, vector), Dot(Y, vector), Dot(Z, vector));

        public D3 Apply(D3 vector) => new(
            (X.X * vector.X) + (Y.X * vector.Y) + (Z.X * vector.Z),
            (X.Y * vector.X) + (Y.Y * vector.Y) + (Z.Y * vector.Z),
            (X.Z * vector.X) + (Y.Z * vector.Y) + (Z.Z * vector.Z));

        public Basis ToGodotBasis() => new(X.ToGodotVector(), Y.ToGodotVector(), Z.ToGodotVector());
    }

    /// <summary>Double-precision three-component vector.</summary>
    private readonly record struct D3(double X, double Y, double Z)
    {
        public Vector3 ToGodotVector() => new((float)X, (float)Y, (float)Z);
    }
}
