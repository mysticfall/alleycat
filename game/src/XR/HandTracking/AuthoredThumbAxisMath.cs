using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Pure value maths for the authored-animation thumb axis model: Reset→flexion authored-axis derivation,
/// Reset palm-plane construction, the deterministic metacarpal bend/splay frame with its fail-closed gates,
/// and the bilateral mirror contract (XR-002 TR26, TR28).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Authored axes (XR-002 TR26).</strong> For each thumb joint <c>j</c>, binding reads the absolute
/// parent-local Reset key <c>R_j</c> and flexion key <c>F_j</c> and derives
/// <c>F'_j = hemisphere_align(F_j, R_j)</c>, <c>A_j = normalise(inverse(R_j) × F'_j)</c>,
/// <c>alpha_j = 2·atan2(|A_j.xyz|, A_j.w)</c> (at least 2°), and <c>a_j = normalise(A_j.xyz)</c>. The
/// multiplication order is normative: <c>inverse(R) × F'</c> composes the authored delta in the bone-local
/// right factor, matching <c>D_j = N_j × rotation(a_j, theta_j)</c>; the reversed product is a different
/// rotation and must not appear. <c>alpha_j</c> is diagnostics and provenance only — never gain.
/// </para>
/// <para>
/// <strong>Frames (XR-002 TR28.1).</strong> <c>S</c> is the skeleton-local global space,
/// <c>P_b</c> the absolute parent-local pose frame, <c>R_b</c> the Reset bone frame, and <c>H</c> the Reset
/// hand-local frame. Every authored metacarpal frame vector <c>(l, b, h)</c> lives in the Reset metacarpal
/// right-local factor — the coordinate system on the right of <c>N_meta</c> in
/// <c>D_meta = N_meta × R_meta</c> — which is also the frame the imported rest origin
/// <c>o_thumb_proximal</c> is expressed in, because pose translations compose in the parent's pose frame.
/// </para>
/// <para>
/// <strong>Palm plane (XR-002 TR28.3).</strong> In the Reset hand-local frame <c>H</c> (hand origin at 0):
/// <c>C</c> is the mean of the four non-thumb proximal root positions, <c>P</c> the wrist/hand midpoint,
/// <c>u = normalise(C − P)</c>, <c>t</c> the index-to-little span projected perpendicular to <c>u</c>, and
/// <c>n_palm,H = sigma_side × normalise(t × u)</c> with the normative cross order <c>t × u</c> and
/// <c>sigma_Left = −1</c>, <c>sigma_Right = +1</c> (σ_R = −σ_L). The constants are calibrated on the
/// reference female — capture-era rest geometry plus Reset keys, whose hand-local chirality places the
/// wrist/hand-parent FK origin at −Y (<c>o_hand = +Y·0.2050707</c> in the <c>LeftLowerArm</c> frame) and
/// the four proximal roots at +Y — so the normal points palmward: <c>n_palm</c> agrees with the
/// hardware-accepted non-thumb per-hand frame palmward axis within ≈19.5° with exact bilateral mirror
/// residuals of 0.0°. The palmward sign is never selected from flexion geometry, thumb-to-index geometry,
/// world axes, or previous frames.
/// <c>n_palm,meta = inverse(q_meta^R) × n_palm,H</c> expresses it in the metacarpal parent-local Reset
/// frame.
/// </para>
/// <para>
/// <strong>Metacarpal frame (XR-002 TR28.4).</strong> In Reset metacarpal local space:
/// <c>l = normalise(o_thumb_proximal)</c> (equivalently
/// <c>normalise(inverse(Q_meta^R) × (p_prox^R − p_meta^R))</c>), <c>h_raw = a_meta − l·dot(a_meta, l)</c>,
/// <c>rho = |h_raw|</c> (at least <c>sin 35°</c>), <c>h = h_raw / rho</c>, and <c>b = h × l</c> with no
/// per-side flip — the palm and soft-fist gates validate its sign. This is not the rejected thumb-to-index
/// frame, not a segment-centre/curvature frame, not a shared geometry hinge, not canonical <c>+X</c>, and
/// not a synthetic-tip frame.
/// </para>
/// <para>
/// <strong>Mirror contract (XR-002 TR28.6).</strong> In skeleton space the polar mirror is
/// <c>M = diag(−1, +1, +1)</c> for the FK positions and the polar vectors <c>l</c>, <c>b</c>, and
/// <c>n</c>; the axial vector mirrors as <c>h_left_expected = −M·h_right</c>; the local authored axes
/// mirror as <c>a_left_expected = J·a_right</c> with <c>J = diag(+1, −1, −1)</c>. Residuals must stay
/// within 0.1° angular, 1e-4 component norm, and 0.1° reference-angle difference. No per-side sign rescue
/// is permitted anywhere.
/// </para>
/// <para>
/// All helpers are fail-closed value-only maths with no Godot node dependency so they stay unit-testable,
/// and nothing here allocates in the steady-state hot path.
/// </para>
/// </remarks>
public static class AuthoredThumbAxisMath
{
    /// <summary>Required minimum authored reference angle in degrees (XR-002 TR26, TR28.5).</summary>
    public const float MinimumAuthoredAngleDegrees = 2.0f;

    /// <summary>Required minimum projected authored-axis component <c>rho</c>: <c>sin 35°</c> (XR-002 TR28.4-28.5).</summary>
    public const float MinimumRho = 0.573576436f;

    /// <summary>
    /// Required minimum normalised palmward alignment <c>dot(b, normalise(n_perp))</c> (XR-002 TR28.5), where
    /// <c>n_perp = n_palm,meta − l·dot(n_palm,meta, l)</c>. The raw <c>dot(b, n_palm,meta)</c> is
    /// anatomy-capped at <c>sin theta(l, n_palm,meta)</c> because <c>b ⊥ l</c> by construction (reference
    /// female: <c>theta ≈ 47.8°</c>, raw ceiling ≈ 0.7407, normalised dot ≈ 0.99958 on both hands).
    /// </summary>
    public const float MinimumPalmAlignmentDot = 0.8f;

    /// <summary>
    /// Required minimum palm-normal perpendicular component: <c>|n_perp| ≥ sin 20°</c> (XR-002 TR28.5). When
    /// the thumb longitudinal lies nearly along the palm normal the projected palmward direction is
    /// ill-defined and the gate fails closed.
    /// </summary>
    public const float MinimumPalmNormalPerpendicularComponent = 0.342020143f;

    /// <summary>Required minimum movement alignment: <c>cos 35°</c> (XR-002 TR28.5).</summary>
    public const float MinimumMovementAlignmentDot = 0.819152044f;

    /// <summary>Required minimum soft-fist swing <c>beta</c> in degrees (XR-002 TR28.5).</summary>
    public const float MinimumSoftFistSwingDegrees = 2.0f;

    /// <summary>Required minimum palm projected span divided by the mean proximal length (XR-002 TR28.5).</summary>
    public const float MinimumPalmSpanRatio = 0.5f;

    /// <summary>Maximum bilateral mirror angular residual in degrees (XR-002 TR28.6).</summary>
    public const float MaximumMirrorAngularDegrees = 0.1f;

    /// <summary>Maximum bilateral mirror component norm residual (XR-002 TR28.6).</summary>
    public const float MaximumMirrorComponentNorm = 1e-4f;

    /// <summary>Maximum bilateral mirror reference-angle difference in degrees (XR-002 TR28.6).</summary>
    public const float MaximumMirrorAngleDifferenceDegrees = 0.1f;

    /// <summary>Quaternion unit-length tolerance: <c>|length² − 1| ≤ 0.001</c> (XR-002 TR25, TR28.5).</summary>
    public const float QuaternionUnitTolerance = 0.001f;

    private const float DirectionLengthSquaredEpsilon = 1e-10f;
    private const float BasisTolerance = 1e-4f;

    /// <summary>
    /// Returns <paramref name="value" /> or its negation — whichever has a non-negative dot product with
    /// <paramref name="reference" /> — resolving the quaternion double cover before quotienting (XR-002 TR26).
    /// </summary>
    public static Quaternion HemisphereAlign(Quaternion value, Quaternion reference)
        => value.Normalized().Dot(reference.Normalized()) < 0.0f
            ? new Quaternion(-value.X, -value.Y, -value.Z, -value.W).Normalized()
            : value.Normalized();

    /// <summary>
    /// Derives one joint's authored destination axis from the absolute parent-local Reset and flexion keys:
    /// <c>A_j = normalise(inverse(R_j) × hemisphere_align(F_j, R_j))</c>, with the reference angle and the
    /// hemisphere-flip decision retained as diagnostics (XR-002 TR26).
    /// </summary>
    /// <remarks>
    /// Fails closed when either quaternion is non-finite or fails <c>|length² − 1| ≤ 0.001</c>, or when the
    /// authored angle is below the required minimum.
    /// </remarks>
    public static bool TryDeriveAuthoredAxis(
        Quaternion resetRotation,
        Quaternion flexionRotation,
        string jointLabel,
        out AuthoredThumbAxis axis,
        out string error)
    {
        if (!IsUnitRotation(resetRotation, $"{jointLabel} Reset key", out error)
            || !IsUnitRotation(flexionRotation, $"{jointLabel} flexion key", out error))
        {
            axis = default;
            return false;
        }

        Quaternion flexionAligned = HemisphereAlign(flexionRotation, resetRotation);
        bool hemisphereFlipped = flexionRotation.Normalized().Dot(resetRotation.Normalized()) < 0.0f;
        Quaternion authored = CanonicalNormalise(resetRotation.Inverse() * flexionAligned);
        if (!IsFinite(authored))
        {
            error = $"{jointLabel}: the hemisphere-aligned Reset⁻¹ × Flexion quotient is degenerate.";
            axis = default;
            return false;
        }

        Vector3 axisPart = new(authored.X, authored.Y, authored.Z);
        float referenceAngleDegrees = Mathf.RadToDeg(2.0f * Mathf.Atan2(axisPart.Length(), authored.W));
        if (referenceAngleDegrees < MinimumAuthoredAngleDegrees)
        {
            error = $"{jointLabel}: authored reference angle {referenceAngleDegrees:F3} degrees is below " +
                $"the required {MinimumAuthoredAngleDegrees} degrees.";
            axis = default;
            return false;
        }

        axis = new AuthoredThumbAxis(
            axisPart.Normalized(),
            referenceAngleDegrees,
            hemisphereFlipped,
            authored);
        error = string.Empty;
        return true;
    }

    /// <summary>Derives the Reset hand-local palm plane and its metacarpal-local normal.</summary>
    public static bool TryDeriveResetPalmPlane(
        in AuthoredThumbResetGeometry geometry,
        LimbSide side,
        out AuthoredThumbPalmPlane palm,
        out string error)
    {
        palm = default;
        string sideLabel = GetSidePrefix(side);
        if (!IsUnitRotation(geometry.HandResetGlobal, $"{sideLabel} Reset hand global rotation", out error)
            || !IsUnitRotation(geometry.MetacarpalResetLocal, $"{sideLabel} Reset metacarpal key", out error))
        {
            return false;
        }

        Basis inverseHandBasis = new(geometry.HandResetGlobal.Inverse());
        Vector3 wrist = inverseHandBasis * (geometry.SkeletonWristPosition - geometry.SkeletonHandPosition);
        Vector3 index = inverseHandBasis * (geometry.SkeletonIndexProximalPosition - geometry.SkeletonHandPosition);
        Vector3 middle = inverseHandBasis * (geometry.SkeletonMiddleProximalPosition - geometry.SkeletonHandPosition);
        Vector3 ring = inverseHandBasis * (geometry.SkeletonRingProximalPosition - geometry.SkeletonHandPosition);
        Vector3 little = inverseHandBasis * (geometry.SkeletonLittleProximalPosition - geometry.SkeletonHandPosition);
        if (!IsFinite(wrist) || !IsFinite(index) || !IsFinite(middle) || !IsFinite(ring) || !IsFinite(little))
        {
            error = $"{sideLabel} hand: the Reset palm-plane positions are non-finite in the hand-local frame.";
            return false;
        }

        Vector3 centroid = (index + middle + ring + little) / 4.0f;
        Vector3 palmReference = wrist * 0.5f;
        Vector3 longitudinal = centroid - palmReference;
        if (!IsFinite(longitudinal) || longitudinal.LengthSquared() <= DirectionLengthSquaredEpsilon)
        {
            error = $"{sideLabel} hand: the Reset palm longitudinal direction is degenerate.";
            return false;
        }

        longitudinal = longitudinal.Normalized();

        Vector3 span = index - little;
        Vector3 spanAxis = span - (longitudinal * longitudinal.Dot(span));
        float projectedSpanLength = spanAxis.Length();
        if (!IsFinite(spanAxis) || projectedSpanLength <= DirectionLengthSquaredEpsilon)
        {
            error = $"{sideLabel} hand: the Reset palm projected span direction is degenerate.";
            return false;
        }

        spanAxis = spanAxis.Normalized();

        // Normative cross order t × u (XR-002 TR28.3); |t × u| = |t| because t ⊥ u by construction.
        Vector3 palmNormal = spanAxis.Cross(longitudinal);
        if (!IsFinite(palmNormal) || palmNormal.LengthSquared() <= DirectionLengthSquaredEpsilon)
        {
            error = $"{sideLabel} hand: the Reset palm normal is degenerate.";
            return false;
        }

        palmNormal = palmNormal.Normalized();

        // sigma_Left = -1, sigma_Right = +1 (XR-002 TR28.3): the only side-dependent constant, applied to the
        // normative t × u cross product and never selected from geometry. Calibrated on the reference
        // female's real hand-local chirality (wrist/hand-parent FK origin at -Y, proximal roots at +Y).
        palmNormal *= side == LimbSide.Left ? -1.0f : 1.0f;
        Vector3 palmNormalInMetacarpalLocal =
            new Basis(geometry.MetacarpalResetLocal.Inverse()) * palmNormal;
        // Span gate normaliser: the mean of the four hand-origin→proximal-root distances in H (XR-002 TR28.5).
        float meanProximalLength = (index.Length() + middle.Length() + ring.Length() + little.Length()) / 4.0f;
        float spanRatio = meanProximalLength > 0.0f ? projectedSpanLength / meanProximalLength : 0.0f;
        if (spanRatio < MinimumPalmSpanRatio)
        {
            error = $"{sideLabel} hand: Reset palm projected span ratio {spanRatio:F3} is below the required " +
                $"{MinimumPalmSpanRatio}.";
            return false;
        }

        palm = new AuthoredThumbPalmPlane(
            centroid,
            palmReference,
            longitudinal,
            spanAxis,
            palmNormal,
            palmNormalInMetacarpalLocal,
            spanRatio);
        error = string.Empty;
        return true;
    }

    /// <summary>Derives the fail-closed, roll-free Reset-local metacarpal bend/splay frame.</summary>
    public static bool TryDeriveMetacarpalFrame(
        in AuthoredThumbResetGeometry geometry,
        in AuthoredThumbAxis metacarpalAxis,
        in AuthoredThumbPalmPlane palm,
        LimbSide side,
        out AuthoredThumbMetacarpalFrame frame,
        out string error)
    {
        frame = default;
        string sideLabel = GetSidePrefix(side);
        Vector3 longitudinal = geometry.ThumbProximalRestOrigin;
        if (!IsFinite(longitudinal) || longitudinal.LengthSquared() <= DirectionLengthSquaredEpsilon)
        {
            error = $"{sideLabel} thumb metacarpal: the thumb-proximal rest origin direction is degenerate.";
            return false;
        }

        longitudinal = longitudinal.Normalized();

        Vector3 splayRaw = metacarpalAxis.Axis - (longitudinal * longitudinal.Dot(metacarpalAxis.Axis));
        float rho = splayRaw.Length();
        if (!IsFinite(splayRaw) || rho < MinimumRho)
        {
            error = $"{sideLabel} thumb metacarpal: authored-axis projected component rho {rho:F3} is below " +
                $"the required {MinimumRho} (sin 35 degrees).";
            return false;
        }

        Vector3 splay = splayRaw / rho;
        Vector3 bend = splay.Cross(longitudinal);

        Vector3 softFistDirection = new Basis(metacarpalAxis.AuthoredRotation) * longitudinal;
        if (!IsFinite(softFistDirection) || softFistDirection.LengthSquared() <= DirectionLengthSquaredEpsilon)
        {
            error = $"{sideLabel} thumb metacarpal: the soft-fist swing direction is degenerate.";
            return false;
        }

        softFistDirection = softFistDirection.Normalized();

        // Normalised palmward gate (XR-002 TR28.5): n_perp is the palm normal projected perpendicular to l,
        // because b ⊥ l by construction caps the raw dot(b, n_palm,meta) at sin theta(l, n_palm,meta)
        // (reference female: theta ≈ 47.8°, raw ceiling 0.7407). The sin 20° floor fails closed when the
        // longitudinal lies nearly along the palm normal and the projected palmward direction is ill-defined.
        Vector3 palmNormal = palm.PalmNormalInMetacarpalLocal;
        float longitudinalPalmDot = longitudinal.Dot(palmNormal);
        Vector3 palmNormalPerpendicular = palmNormal - (longitudinal * longitudinalPalmDot);
        float palmNormalPerpendicularLength = palmNormalPerpendicular.Length();
        float longitudinalPalmAngleDegrees =
            Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(longitudinalPalmDot, -1.0f, 1.0f)));
        float rawPalmAlignmentDot = bend.Dot(palmNormal);
        if (palmNormalPerpendicularLength < MinimumPalmNormalPerpendicularComponent)
        {
            error = $"{sideLabel} thumb metacarpal: palm-normal perpendicular component |n_perp| " +
                $"{palmNormalPerpendicularLength:F5} is below the required sin 20 degrees " +
                $"{MinimumPalmNormalPerpendicularComponent:F5}; theta(l, n_palm) " +
                $"{longitudinalPalmAngleDegrees:F3} degrees places the thumb longitudinal nearly along the " +
                "palm normal, so the projected palmward direction is ill-defined and the gate fails closed.";
            return false;
        }

        float palmAlignmentDot = bend.Dot(palmNormalPerpendicular / palmNormalPerpendicularLength);
        if (palmAlignmentDot < MinimumPalmAlignmentDot)
        {
            error = $"{sideLabel} thumb metacarpal: normalised palm alignment dot(b, normalise(n_perp)) " +
                $"{palmAlignmentDot:F5} is below the required {MinimumPalmAlignmentDot}; |n_perp| " +
                $"{palmNormalPerpendicularLength:F5}, theta(l, n_palm) {longitudinalPalmAngleDegrees:F3} " +
                $"degrees, raw dot(b, n_palm) {rawPalmAlignmentDot:F5} (anatomy-capped at sin theta because " +
                "b is perpendicular to l).";
            return false;
        }

        float softFistSwingDegrees = Mathf.RadToDeg(
            Mathf.Acos(Mathf.Clamp(longitudinal.Dot(softFistDirection), -1.0f, 1.0f)));
        if (softFistSwingDegrees < MinimumSoftFistSwingDegrees)
        {
            error = $"{sideLabel} thumb metacarpal: soft-fist swing beta {softFistSwingDegrees:F3} degrees is " +
                $"below the required {MinimumSoftFistSwingDegrees} degrees.";
            return false;
        }

        Vector3 softFistPerpendicular =
            (softFistDirection - (longitudinal * longitudinal.Dot(softFistDirection))).Normalized();
        float movementAlignmentDot = softFistPerpendicular.Dot(bend);
        if (movementAlignmentDot < MinimumMovementAlignmentDot || softFistDirection.Dot(bend) <= 0.0f)
        {
            error = $"{sideLabel} thumb metacarpal: movement alignment {movementAlignmentDot:F3} fails the " +
                $"required {MinimumMovementAlignmentDot} (cos 35 degrees) or the soft-fist direction is not " +
                "bend-positive; no per-side sign rescue is permitted.";
            return false;
        }

        if (!IsFrameValid(longitudinal, bend, splay, out string frameError))
        {
            error = $"{sideLabel} thumb metacarpal: {frameError}";
            return false;
        }

        frame = new AuthoredThumbMetacarpalFrame(
            longitudinal,
            bend,
            splay,
            rho,
            softFistSwingDegrees,
            movementAlignmentDot,
            palmAlignmentDot,
            softFistDirection,
            Mathf.RadToDeg(longitudinal.AngleTo(metacarpalAxis.Axis)),
            rawPalmAlignmentDot,
            palmNormalPerpendicularLength,
            longitudinalPalmAngleDegrees);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Validates the bilateral mirror contract (XR-002 TR28.6): skeleton-space polar mirror
    /// <c>M = diag(−1, +1, +1)</c> for the Reset positions and the polar vectors <c>l</c>, <c>b</c>, and
    /// <c>n</c>; the axial <c>h_left_expected = −M·h_right</c>; and the local authored axes
    /// <c>a_left_expected = J·a_right</c> with <c>J = diag(+1, −1, −1)</c>.
    /// </summary>
    /// <remarks>
    /// Residuals must stay within <see cref="MaximumMirrorAngularDegrees" /> angular,
    /// <see cref="MaximumMirrorComponentNorm" /> component norm, and
    /// <see cref="MaximumMirrorAngleDifferenceDegrees" /> reference-angle difference. No per-side sign
    /// rescue is applied before measuring.
    /// </remarks>
    public static bool TryValidateBilateralMirror(
        in AuthoredThumbResetGeometry leftGeometry,
        in AuthoredThumbAxis leftMetacarpalAxis,
        in AuthoredThumbAxis leftProximalAxis,
        in AuthoredThumbAxis leftDistalAxis,
        in AuthoredThumbMetacarpalFrame leftFrame,
        in AuthoredThumbPalmPlane leftPalm,
        in AuthoredThumbResetGeometry rightGeometry,
        in AuthoredThumbAxis rightMetacarpalAxis,
        in AuthoredThumbAxis rightProximalAxis,
        in AuthoredThumbAxis rightDistalAxis,
        in AuthoredThumbMetacarpalFrame rightFrame,
        in AuthoredThumbPalmPlane rightPalm,
        out AuthoredThumbMirrorResiduals residuals,
        out string error)
    {
        if (!IsUnitRotation(
                leftGeometry.MetacarpalResetGlobal,
                $"{GetSidePrefix(LimbSide.Left)} Reset metacarpal global rotation",
                out error)
            || !IsUnitRotation(
                rightGeometry.MetacarpalResetGlobal,
                $"{GetSidePrefix(LimbSide.Right)} Reset metacarpal global rotation",
                out error))
        {
            residuals = default;
            return false;
        }

        // Skeleton-space projections: Q_b^R · v_local (XR-002 TR28.1, TR28.6).
        Basis leftMetacarpalGlobal = new(leftGeometry.MetacarpalResetGlobal);
        Basis rightMetacarpalGlobal = new(rightGeometry.MetacarpalResetGlobal);
        Basis leftHandGlobal = new(leftGeometry.HandResetGlobal);
        Basis rightHandGlobal = new(rightGeometry.HandResetGlobal);

        bool valid;
        float maximumAngular;
        float maximumComponent;

        float longitudinalAngular = MirrorPolarResidual(
            leftMetacarpalGlobal * leftFrame.Longitudinal,
            rightMetacarpalGlobal * rightFrame.Longitudinal,
            out float longitudinalComponent);
        float bendAngular = MirrorPolarResidual(
            leftMetacarpalGlobal * leftFrame.Bend,
            rightMetacarpalGlobal * rightFrame.Bend,
            out float bendComponent);
        float palmNormalAngular = MirrorPolarResidual(
            leftHandGlobal * leftPalm.PalmNormalInHand,
            rightHandGlobal * rightPalm.PalmNormalInHand,
            out float palmNormalComponent);
        float splayAngular = MirrorAxialResidual(
            leftMetacarpalGlobal * leftFrame.Splay,
            rightMetacarpalGlobal * rightFrame.Splay,
            out float splayComponent);

        float metacarpalAxisAngular = MirrorLocalAxisResidual(
            leftMetacarpalAxis.Axis,
            rightMetacarpalAxis.Axis,
            out float metacarpalAxisComponent);
        float proximalAxisAngular = MirrorLocalAxisResidual(
            leftProximalAxis.Axis,
            rightProximalAxis.Axis,
            out float proximalAxisComponent);
        float distalAxisAngular = MirrorLocalAxisResidual(
            leftDistalAxis.Axis,
            rightDistalAxis.Axis,
            out float distalAxisComponent);

        float positionAngular;
        float positionComponent;
        {
            Vector3[] leftPositions =
            [
                leftGeometry.SkeletonWristPosition,
                leftGeometry.SkeletonHandPosition,
                leftGeometry.SkeletonIndexProximalPosition,
                leftGeometry.SkeletonMiddleProximalPosition,
                leftGeometry.SkeletonRingProximalPosition,
                leftGeometry.SkeletonLittleProximalPosition,
            ];
            Vector3[] rightPositions =
            [
                rightGeometry.SkeletonWristPosition,
                rightGeometry.SkeletonHandPosition,
                rightGeometry.SkeletonIndexProximalPosition,
                rightGeometry.SkeletonMiddleProximalPosition,
                rightGeometry.SkeletonRingProximalPosition,
                rightGeometry.SkeletonLittleProximalPosition,
            ];
            positionAngular = 0.0f;
            positionComponent = 0.0f;
            for (int index = 0; index < leftPositions.Length; index++)
            {
                positionAngular = Mathf.Max(positionAngular, MirrorPolarResidual(
                    leftPositions[index],
                    rightPositions[index],
                    out float component));
                positionComponent = Mathf.Max(positionComponent, component);
            }
        }

        maximumAngular = Mathf.Max(
            Mathf.Max(
                Mathf.Max(longitudinalAngular, bendAngular),
                Mathf.Max(palmNormalAngular, splayAngular)),
            Mathf.Max(
                Mathf.Max(metacarpalAxisAngular, Mathf.Max(proximalAxisAngular, distalAxisAngular)),
                positionAngular));
        maximumComponent = Mathf.Max(
            Mathf.Max(
                Mathf.Max(longitudinalComponent, bendComponent),
                Mathf.Max(palmNormalComponent, splayComponent)),
            Mathf.Max(
                Mathf.Max(metacarpalAxisComponent, Mathf.Max(proximalAxisComponent, distalAxisComponent)),
                positionComponent));

        float metacarpalAngleDifference = Mathf.Abs(
            leftMetacarpalAxis.ReferenceAngleDegrees - rightMetacarpalAxis.ReferenceAngleDegrees);
        float proximalAngleDifference = Mathf.Abs(
            leftProximalAxis.ReferenceAngleDegrees - rightProximalAxis.ReferenceAngleDegrees);
        float distalAngleDifference = Mathf.Abs(
            leftDistalAxis.ReferenceAngleDegrees - rightDistalAxis.ReferenceAngleDegrees);

        valid = maximumAngular <= MaximumMirrorAngularDegrees
            && maximumComponent <= MaximumMirrorComponentNorm
            && metacarpalAngleDifference <= MaximumMirrorAngleDifferenceDegrees
            && proximalAngleDifference <= MaximumMirrorAngleDifferenceDegrees
            && distalAngleDifference <= MaximumMirrorAngleDifferenceDegrees;

        residuals = new AuthoredThumbMirrorResiduals(
            longitudinalAngular,
            bendAngular,
            palmNormalAngular,
            splayAngular,
            metacarpalAxisAngular,
            proximalAxisAngular,
            distalAxisAngular,
            positionAngular,
            maximumAngular,
            longitudinalComponent,
            bendComponent,
            palmNormalComponent,
            splayComponent,
            metacarpalAxisComponent,
            proximalAxisComponent,
            distalAxisComponent,
            positionComponent,
            maximumComponent,
            metacarpalAngleDifference,
            proximalAngleDifference,
            distalAngleDifference);
        error = valid
            ? string.Empty
            : "Bilateral authored-thumb mirror validation failed: maximum angular residual " +
                $"{maximumAngular:F5} degrees (limit {MaximumMirrorAngularDegrees}), maximum component norm " +
                $"{maximumComponent:E3} (limit {MaximumMirrorComponentNorm}), reference-angle differences " +
                $"meta {metacarpalAxisAngular:F5}/prox {proximalAxisAngular:F5}/dist {distalAxisAngular:F5} " +
                $"degrees (limit {MaximumMirrorAngleDifferenceDegrees}).";
        return valid;
    }

    /// <summary>Expresses a skeleton-space direction in a Reset bone frame: <c>inverse(frame) × direction</c>.</summary>
    public static Vector3 ExpressInResetFrame(Quaternion resetRotation, Vector3 direction)
        => new Basis(resetRotation.Inverse()) * direction;

    private static float MirrorPolarResidual(Vector3 left, Vector3 right, out float componentNorm)
    {
        Vector3 expected = MirrorPolar(right);
        componentNorm = (left - expected).Length();
        return IsFinite(left) && IsFinite(expected)
            && left.LengthSquared() > 0.0f && expected.LengthSquared() > 0.0f
            ? Mathf.RadToDeg(left.Normalized().AngleTo(expected))
            : 180.0f;
    }

    private static float MirrorAxialResidual(Vector3 left, Vector3 right, out float componentNorm)
    {
        Vector3 expected = -MirrorPolar(right);
        componentNorm = (left - expected).Length();
        return IsFinite(left) && IsFinite(expected)
            && left.LengthSquared() > 0.0f && expected.LengthSquared() > 0.0f
            ? Mathf.RadToDeg(left.Normalized().AngleTo(expected))
            : 180.0f;
    }

    private static float MirrorLocalAxisResidual(Vector3 left, Vector3 right, out float componentNorm)
    {
        Vector3 expected = MirrorLocal(right);
        componentNorm = (left - expected).Length();
        return IsFinite(left) && IsFinite(expected)
            && left.LengthSquared() > 0.0f && expected.LengthSquared() > 0.0f
            ? Mathf.RadToDeg(left.Normalized().AngleTo(expected))
            : 180.0f;
    }

    /// <summary>Skeleton-space polar mirror <c>M = diag(−1, +1, +1)</c> (XR-002 TR28.6).</summary>
    public static Vector3 MirrorPolar(Vector3 value) => new(-value.X, value.Y, value.Z);

    /// <summary>Local authored-axis mirror <c>J = diag(+1, −1, −1)</c> (XR-002 TR26, TR28.6).</summary>
    public static Vector3 MirrorLocal(Vector3 value) => new(value.X, -value.Y, -value.Z);

    private static bool IsUnitRotation(Quaternion value, string label, out string error)
    {
        if (!IsFinite(value) || value.LengthSquared() <= 0.0f)
        {
            error = $"The {label} quaternion is non-finite or near-zero: {value}.";
            return false;
        }

        if (Mathf.Abs(value.LengthSquared() - 1.0f) > QuaternionUnitTolerance)
        {
            error = $"The {label} quaternion fails |length² − 1| ≤ {QuaternionUnitTolerance}: {value}.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsFrameValid(Vector3 longitudinal, Vector3 bend, Vector3 splay, out string error)
    {
        if (Mathf.Abs(longitudinal.LengthSquared() - 1.0f) > BasisTolerance
            || Mathf.Abs(bend.LengthSquared() - 1.0f) > BasisTolerance
            || Mathf.Abs(splay.LengthSquared() - 1.0f) > BasisTolerance)
        {
            error = "the (l, b, h) basis is not unit length.";
            return false;
        }

        if (Mathf.Abs(longitudinal.Dot(bend)) > BasisTolerance
            || Mathf.Abs(longitudinal.Dot(splay)) > BasisTolerance
            || Mathf.Abs(bend.Dot(splay)) > BasisTolerance)
        {
            error = "the (l, b, h) basis is not pairwise orthogonal.";
            return false;
        }

        // Right-handed with b = h × l: l × b = h, b × h = l, h × l = b (XR-002 TR28.4).
        if (longitudinal.DistanceTo(bend.Cross(splay)) > BasisTolerance
            || bend.DistanceTo(splay.Cross(longitudinal)) > BasisTolerance
            || splay.DistanceTo(longitudinal.Cross(bend)) > BasisTolerance)
        {
            error = "the (l, b, h) basis is not right-handed with b = h × l.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static Quaternion CanonicalNormalise(Quaternion value)
    {
        Quaternion normalised = value.Normalized();
        return normalised.W < 0.0f
            ? new Quaternion(-normalised.X, -normalised.Y, -normalised.Z, -normalised.W)
            : normalised;
    }

    private static string GetSidePrefix(LimbSide side)
        => side == LimbSide.Left ? "Left" : "Right";

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>
/// One thumb joint's authored destination axis with its provenance: the hemisphere-aligned authored rotation
/// <c>A_j</c>, the normalised axis <c>a_j</c>, the reference angle, and the hemisphere-flip decision
/// (XR-002 TR26). The angle is diagnostics and provenance only — never gain.
/// </summary>
public readonly record struct AuthoredThumbAxis(
    Vector3 Axis,
    float ReferenceAngleDegrees,
    bool HemisphereFlipped,
    Quaternion AuthoredRotation);

/// <summary>
/// The Reset forward-kinematics inputs consumed by the authored thumb model (XR-002 TR28.2-28.4): the Reset
/// hand-global and metacarpal rotations, the skeleton-space Reset positions of the wrist, hand, and four
/// non-thumb proximal roots, and the imported thumb-proximal rest origin that supplies <c>l</c>.
/// </summary>
/// <remarks>
/// Positions are root-anchored skeleton-space Reset values produced by the sampler's forward kinematics;
/// hand-local projection happens in <see cref="AuthoredThumbAxisMath" />. The thumb-proximal rest origin is
/// the imported <c>Skeleton3D.GetBoneRest().origin</c> read directly, equivalent to
/// <c>inverse(Q_meta^R) × (p_prox^R − p_meta^R)</c> under the Reset forward kinematics.
/// </remarks>
public readonly record struct AuthoredThumbResetGeometry(
    Quaternion HandResetGlobal,
    Quaternion MetacarpalResetLocal,
    Quaternion MetacarpalResetGlobal,
    Vector3 ThumbProximalRestOrigin,
    Vector3 SkeletonWristPosition,
    Vector3 SkeletonHandPosition,
    Vector3 SkeletonIndexProximalPosition,
    Vector3 SkeletonMiddleProximalPosition,
    Vector3 SkeletonRingProximalPosition,
    Vector3 SkeletonLittleProximalPosition);

/// <summary>
/// The Reset hand-local palm plane (XR-002 TR28.3): the proximal-root centroid, the wrist/hand midpoint
/// reference, the longitudinal <c>u</c> and projected span <c>t</c> axes, the side-signed palm normal in
/// hand-local and metacarpal-local Reset frames, and the projected-span ratio against its pinned normaliser.
/// </summary>
public readonly record struct AuthoredThumbPalmPlane(
    Vector3 CentroidInHand,
    Vector3 PalmReferenceInHand,
    Vector3 Longitudinal,
    Vector3 SpanAxis,
    Vector3 PalmNormalInHand,
    Vector3 PalmNormalInMetacarpalLocal,
    float SpanRatio);

/// <summary>
/// The runtime metacarpal correspondence frame (XR-002 TR28.7): the binding palm plane's
/// <c>(u, t, n_palm,H)</c> axes in the Reset hand-local frame, paired with the source wrist axes
/// <c>(+Y, +X, +Z)</c> through the measured side-dependent signs — Left <c>(t: −1, n: +1)</c>,
/// Right <c>(t: +1, n: +1)</c>, both with <c>det = +1</c> — that define
/// <c>C_side = B_dest × B_source⁻¹</c>. Published by the bilateral binding from the same palm-plane
/// construction the TR28.5 gates validated; the runtime anchored hand-frame transfer consumes it directly.
/// </summary>
public readonly record struct AuthoredThumbCorrespondenceFrame(
    Vector3 Longitudinal,
    Vector3 SpanAxis,
    Vector3 PalmNormal);

/// <summary>
/// The deterministic roll-free metacarpal bend/splay frame in Reset metacarpal local space (XR-002 TR28.4):
/// longitudinal <c>l</c>, bend <c>b = h × l</c>, and splay <c>h</c>, with the gate evidence — <c>rho</c>, the
/// soft-fist swing <c>beta</c>, the movement and normalised palm alignment dots, the soft-fist direction,
/// the axis/longitudinal separation, and the palm-gate forensics (XR-002 TR28.5): the raw
/// <c>dot(b, n_palm,meta)</c> with its <c>sin theta(l, n_palm,meta)</c> anatomy cap, the perpendicular
/// component <c>|n_perp|</c>, and <c>theta(l, n_palm,meta)</c> itself.
/// </summary>
public readonly record struct AuthoredThumbMetacarpalFrame(
    Vector3 Longitudinal,
    Vector3 Bend,
    Vector3 Splay,
    float Rho,
    float SoftFistSwingDegrees,
    float MovementAlignmentDot,
    float PalmAlignmentDot,
    Vector3 SoftFistDirection,
    float AxisLongitudinalSeparationDegrees,
    float RawPalmAlignmentDot,
    float PalmNormalPerpendicularLength,
    float LongitudinalPalmAngleDegrees);

/// <summary>
/// Bilateral mirror residuals of the authored thumb model (XR-002 TR28.6): per-vector angular and component
/// norms in degrees/unit space, the maximums, and the per-joint reference-angle differences.
/// </summary>
public readonly record struct AuthoredThumbMirrorResiduals(
    float LongitudinalAngularDegrees,
    float BendAngularDegrees,
    float PalmNormalAngularDegrees,
    float SplayAxialAngularDegrees,
    float MetacarpalAxisAngularDegrees,
    float ProximalAxisAngularDegrees,
    float DistalAxisAngularDegrees,
    float PositionAngularDegrees,
    float MaximumAngularDegrees,
    float LongitudinalComponentNorm,
    float BendComponentNorm,
    float PalmNormalComponentNorm,
    float SplayAxialComponentNorm,
    float MetacarpalAxisComponentNorm,
    float ProximalAxisComponentNorm,
    float DistalAxisComponentNorm,
    float PositionComponentNorm,
    float MaximumComponentNorm,
    float MetacarpalAngleDifferenceDegrees,
    float ProximalAngleDifferenceDegrees,
    float DistalAngleDifferenceDegrees);
