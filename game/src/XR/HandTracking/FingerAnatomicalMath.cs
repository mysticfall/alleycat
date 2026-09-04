using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Pure constrained anatomical maths for optical finger retargeting: delivered source-frame constants,
/// signed hinge-angle extraction, roll-free proximal direction transfer, per-hand semantic frame derivation with
/// fail-closed consensus gates, the authored-animation Stage 1 thumb mapping model, and transactional bilateral
/// binding (XR-002 TR17-TR29, TR43).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Source frame (XR-002 TR17).</strong> The delivered hand-joint convention is evaluated in the neutral
/// child frame produced by <c>Delta = S0⁻¹ × S</c>: longitudinal <c>l_s = +Y</c> (towards the fingertip), palmward
/// <c>b_s = +Z</c>, and flexion hinge <c>h_s = +X</c> with physical flexion a positive twist about it, satisfying
/// <c>h_s × l_s = b_s</c>. This is the delivered WiVRn/Godot convention, rather than the raw-OpenXR
/// <c>(−Z, −Y, −X)</c> convention. It applies unchanged to both hands; no side-specific source inversion is
/// introduced.
/// </para>
/// <para>
/// <strong>Destination frame (XR-002 TR18-TR19).</strong> One shared right-handed frame <c>(L, B, H)</c> per hand is
/// derived at binding time from global rest geometry plus the accepted chain-neutral swings. Positive rotation about
/// <c>H</c> moves <c>L</c> palmward towards <c>B</c>, mirroring the source convention so positive hinge flexion
/// transfers with a consistent sign on both hands.
/// </para>
/// <para>
/// <strong>Thumb model (XR-002 TR24-TR29, Stage 1, authored-animation).</strong> The thumb source frame is the same
/// delivered convention (TR24): local +Y towards the tip, local +X the flexion hinge with physical flexion a
/// positive twist, no side-specific inversion. Thumb mapping is the authored-animation reference model (TR25-TR29):
/// binding samples two immutable Blender-authored single-frame pose animations directly as resources — a
/// neutral Reset reference and a soft-fist flexion reference — and derives six independent authored destination
/// axes <c>a_j</c> (TR26), a deterministic Reset palm plane, and a roll-free metacarpal bend/splay frame
/// <c>(l, b, h)</c> in the Reset metacarpal right-local factor (TR28) through
/// <see cref="AuthoredThumbAxisMath" />. At runtime the metacarpal receives the anchored hand-frame
/// correspondence transfer (TR28.7): <c>d_w = S_meta × (+Y)</c> from the live wrist→metacarpal relation,
/// <c>d_h = C_side × d_w</c> through the measured side-dependent pairing of the source wrist axes with the
/// binding palm plane, <c>d_0 = Q0 × (N_meta⁻¹ × d_h)</c> anchored by the pinned per-side neutral anchor, and
/// <c>D_meta = N_meta × rotation(axis(R), k_eff · angle(R))</c> with <c>R = shortest_arc(l, d_0)</c> and the
/// profile-scoped directional envelope — discarding longitudinal roll and the metacarpal's axial opposition roll — and
/// the proximal and distal each receive their own signed hinge flexion
/// <c>D_j = N_j × rotation(a_j, theta_j)</c> about their independent authored axes (TR27-TR28.7). The authored
/// axes and child directions define the model; canonical-local, custom rest-geometry, segment-centre, and
/// curvature frames are not part of it. The thumb neutrals are the
/// sampled Reset keys — the authored definition of the desired visual thumb neutral — never the TR43
/// chain-neutral swings and never the imported rest locals (XR-002 TR29); on the reference female the Reset
/// thumb keys are numerically equal to the imported local rest rotations, so the contract-source change has no
/// behavioural delta there. The imported thumb rest rotations remain finite binding-qualification inputs
/// only, and thumb rest bases tolerate
/// non-uniform import scale: every consumed thumb rest rotation comes from
/// <see cref="ThumbRestBasisMath" />'s polar-decomposition extraction with its documented scale-ratio tolerance
/// and positive-determinant guard, while shear beyond tolerance or reflection still fails closed (TR25). Stage 1
/// deliberately discards the metacarpal's axial opposition roll; the full <c>N × Delta</c> transfer is not part
/// of the production mapping.
/// </para>
/// <para>
/// All frame and binding derivation is fail-closed: there is deliberately no arbitrary-basis, world-axis,
/// previous-frame, animation-derived, or synthetic-tip fallback (XR-002 TR19, TR25). Every helper is value-only
/// maths with no Godot node dependency so it stays unit-testable, and no method allocates in the steady-state hot
/// path.
/// </para>
/// </remarks>
public static class FingerAnatomicalMath
{
    private const int IndexChain = 0;
    private const int MiddleChain = 1;
    private const int LittleChain = 3;

    // Per-side thumb calibration-record offsets in canonical destination order (XR-002 TR12, TR44).
    private const int ThumbMetacarpalRecord = 0;
    private const int ThumbProximalRecord = 1;
    private const int ThumbDistalRecord = 2;
    private const int ThumbRecordCount = 3;

    private const float DirectionLengthSquaredEpsilon = 1e-10f;
    private const float ParallelDotTolerance = 1e-6f;
    private const float BasisTolerance = 1e-4f;
    private const float DegenerateTwistEpsilon = 1e-5f;
    private const float CurvatureSignEpsilon = 1e-6f;

    /// <summary>Required projected root span divided by the mean proximal segment length (XR-002 TR19).</summary>
    public const float MinimumSpanRatio = 0.5f;

    /// <summary>Required minimum contributing natural bend in degrees (XR-002 TR19).</summary>
    public const float MinimumNaturalBendDegrees = 2.0f;

    /// <summary>Required equal-weight curvature resultant concentration (XR-002 TR19).</summary>
    public const float MinimumConcentration = 0.8f;

    /// <summary>Maximum angle between any curvature direction and the resultant, in degrees (XR-002 TR19).</summary>
    public const float MaximumCurvatureDisagreementDegrees = 35.0f;

    /// <summary>
    /// Non-thumb source longitudinal direction <c>l_s = +Y</c> (towards the fingertip) (XR-002 TR17).
    /// </summary>
    /// <remarks>This fixed delivered convention is distinct from raw OpenXR joint-frame assumptions.</remarks>
    public static readonly Vector3 SourceLongitudinal = Vector3.Up;

    /// <summary>
    /// Non-thumb source palmward direction <c>b_s = +Z</c> (XR-002 TR17).
    /// </summary>
    /// <remarks>This fixed delivered convention is distinct from raw OpenXR joint-frame assumptions.</remarks>
    public static readonly Vector3 SourcePalmward = Vector3.Back;

    /// <summary>
    /// Non-thumb source flexion hinge <c>h_s = +X</c>, with physical flexion a positive twist about it
    /// (XR-002 TR17).
    /// </summary>
    /// <remarks>
    /// The WiVRn/Godot delivery layer presents joint frames whose algebra differs from the raw-OpenXR
    /// <c>(−Z, −Y, −X)</c> triple; physical flexion has the positive sign about local +X.
    /// </remarks>
    public static readonly Vector3 SourceHinge = Vector3.Right;

    /// <summary>
    /// Extracts the signed hinge flexion angle from a hemisphere-stabilised unit delta about the shared source
    /// hinge <c>h_s</c> (XR-002 TR21).
    /// </summary>
    /// <remarks>
    /// For unit <c>Delta = (v, w)</c>: <c>p = dot(v, h_s)</c>, <c>m = sqrt(w² + p²)</c>, and
    /// <c>theta = 2 · atan2(p/m, w/m)</c> in <c>(−π, π]</c>. The caller must normalise and hemisphere-align the delta
    /// to identity (<c>w ≥ 0</c>) so the half-angle is unambiguous. A degenerate <c>m</c> — a π rotation about an
    /// axis perpendicular to the hinge — fails so only that destination freezes; off-hinge swing and longitudinal
    /// roll are discarded exactly because they never enter <c>p</c> or <c>w</c>.
    /// </remarks>
    public static bool TryExtractHingeAngle(Quaternion delta, out float thetaRadians)
    {
        Quaternion rotation = delta.Normalized();
        if (!IsFinite(rotation))
        {
            thetaRadians = 0.0f;
            return false;
        }

        Vector3 axis = new(rotation.X, rotation.Y, rotation.Z);
        float p = axis.Dot(SourceHinge);
        float m = Mathf.Sqrt((rotation.W * rotation.W) + (p * p));
        if (m <= DegenerateTwistEpsilon)
        {
            thetaRadians = 0.0f;
            return false;
        }

        thetaRadians = 2.0f * Mathf.Atan2(p / m, rotation.W / m);
        return true;
    }

    /// <summary>
    /// Transfers the tracked longitudinal direction into the destination proximal's desired-local frame as one
    /// roll-free directional swing (XR-002 TR22).
    /// </summary>
    /// <remarks>
    /// With <c>d_s = Delta × l_s</c> — which discards source roll about the tracked longitudinal direction — and
    /// components <c>x = dot(d_s, l_s)</c>, <c>y = dot(d_s, b_s)</c>, <c>z = dot(d_s, h_s)</c>, the mapped direction
    /// is <c>d_d = normalise(x·l_d + y·b_d + z·h_d)</c> and the returned swing is <c>shortest_arc(l_d, d_d)</c>.
    /// Parallel directions produce the identity deterministically; an antiparallel or degenerate direction fails so
    /// only that proximal freezes — no roll axis is invented.
    /// </remarks>
    public static bool TryTransferProximalSwing(
        Quaternion delta,
        in FingerAnatomicalFrame localProximalFrame,
        out Quaternion swing)
    {
        Vector3 tracked = new Basis(delta.Normalized()) * SourceLongitudinal;
        if (!IsFinite(tracked))
        {
            swing = Quaternion.Identity;
            return false;
        }

        float x = tracked.Dot(SourceLongitudinal);
        float y = tracked.Dot(SourcePalmward);
        float z = tracked.Dot(SourceHinge);

        Vector3 mapped = (localProximalFrame.Longitudinal * x)
            + (localProximalFrame.Palmward * y)
            + (localProximalFrame.Hinge * z);
        if (!IsFinite(mapped) || mapped.LengthSquared() <= DirectionLengthSquaredEpsilon)
        {
            swing = Quaternion.Identity;
            return false;
        }

        return TryShortestArc(localProximalFrame.Longitudinal, mapped.Normalized(), out swing);
    }

    /// <summary>
    /// Deterministic shortest-arc rotation between two directions (XR-002 TR22). Parallel directions produce the
    /// identity; antiparallel or degenerate directions fail rather than inventing an axis.
    /// </summary>
    public static bool TryShortestArc(Vector3 from, Vector3 to, out Quaternion rotation)
    {
        if (!IsFinite(from) || !IsFinite(to)
            || from.LengthSquared() <= DirectionLengthSquaredEpsilon
            || to.LengthSquared() <= DirectionLengthSquaredEpsilon)
        {
            rotation = Quaternion.Identity;
            return false;
        }

        Vector3 source = from.Normalized();
        Vector3 target = to.Normalized();
        float dot = Mathf.Clamp(source.Dot(target), -1.0f, 1.0f);
        if (dot >= 1.0f - ParallelDotTolerance)
        {
            rotation = Quaternion.Identity;
            return true;
        }

        if (dot <= -1.0f + ParallelDotTolerance)
        {
            rotation = Quaternion.Identity;
            return false;
        }

        Vector3 cross = source.Cross(target);
        rotation = new Quaternion(cross.X, cross.Y, cross.Z, 1.0f + dot).Normalized();
        return true;
    }

    /// <summary>
    /// Maps an intermediate or distal destination from its neutral delta: signed hinge flexion about the shared
    /// per-hand hinge expressed in the bone's own desired-local frame (XR-002 TR21).
    /// </summary>
    public static bool TryMapHingeDestination(
        Quaternion delta,
        Vector3 localHingeAxis,
        Quaternion destinationNeutral,
        out Quaternion rotation)
    {
        if (!TryExtractHingeAngle(delta, out float theta))
        {
            rotation = Quaternion.Identity;
            return false;
        }

        rotation = destinationNeutral.Normalized() * new Quaternion(localHingeAxis.Normalized(), theta);
        return true;
    }

    /// <summary>
    /// Maps a proximal destination from its neutral delta: one roll-free directional swing applied after the
    /// rest-derived effective neutral (XR-002 TR22).
    /// </summary>
    public static bool TryMapProximalDestination(
        Quaternion delta,
        in FingerAnatomicalFrame localProximalFrame,
        Quaternion destinationNeutral,
        out Quaternion rotation)
    {
        if (!TryTransferProximalSwing(delta, localProximalFrame, out Quaternion swing))
        {
            rotation = Quaternion.Identity;
            return false;
        }

        rotation = destinationNeutral.Normalized() * swing;
        return true;
    }

    /// <summary>
    /// Maps the thumb metacarpal from the live wrist→metacarpal source relation through the anchored
    /// hand-frame correspondence with the calibrated metacarpal swing gain (XR-002 TR28.7):
    /// <c>d_w = S_meta × (+Y)</c>, <c>d_h = C_side × d_w</c>, <c>d_0 = Q0 × (N_meta⁻¹ × d_h)</c>,
    /// <c>R = shortest_arc(l, d_0)</c>, <c>R' = rotation(axis(R), k_eff · angle(R))</c>, and
    /// <c>D_meta = N_meta × R'</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The source swing direction reads the live relation directly — <c>d_w = S_meta × (+Y)</c> with
    /// <c>S_meta = inverse(Q_wrist) × Q_metacarpal</c> (XR-002 TR20) — so the mapping is S0-drift-invariant
    /// by construction: the source neutral never enters, and any constant neutral offset is absorbed by the
    /// pinned anchor <c>Q0</c>. <c>C_side</c> pairs the source wrist axes with the destination binding hand
    /// frame <c>(u_d, t_d, n_d)</c> of Requirement 28.3 through the measured side-dependent signs — source
    /// <c>+Y</c> ↔ <c>u</c>, source <c>+Z</c> ↔ <c>n_palm,H</c>, source <c>+X</c> ↔ <c>sigma_t · t</c> with
    /// Left <c>(t: −1, n: +1)</c>, Right <c>(t: +1, n: +1)</c>, <c>det = +1</c> on both sides — supplied as
    /// <paramref name="correspondenceFrame" /> by the binding's validated palm plane.
    /// <c>Q0</c>, <c>K_meta</c>, and the response thresholds are pinned profile records (XR-002 TR45).
    /// </para>
    /// <para>
    /// The swing axis <c>l × d_0</c> is perpendicular to both vectors, so the aim's component along
    /// <c>l</c> never enters axis selection, but it does affect the full-dot swing angle. The perpendicular
    /// projection is realised by the cross-product axis, and the angle is <c>angle(l, d_0)</c> exactly as the
    /// offline replay computes it. The <c>N × R'</c> composition order is normative; <c>R' × N</c>,
    /// <c>N_meta × Delta</c>, and conjugation through <c>N_meta</c> are incorrect and must not appear. The
    /// deterministic parallel branch (the anchored aim lying on <c>l</c> — the calibrated neutral) returns
    /// the cached <c>N_meta</c> verbatim, preserving the Requirement 29 identity contract; an antiparallel
    /// aim fails that destination only and freezes per the Requirement 29 ladder; and a right-composed
    /// source <c>+Y</c> roll leaves <c>d_w</c> and the output unchanged because
    /// <c>rotation(+Y, phi) × (+Y) = (+Y)</c>.
    /// </para>
    /// </remarks>
    public static bool TryMapThumbMetacarpalSwing(
        Quaternion sourceRelation,
        in AuthoredThumbCorrespondenceFrame correspondenceFrame,
        LimbSide side,
        in FingerAnatomicalFrame metacarpalFrame,
        Quaternion destinationNeutral,
        in ResolvedThumbMetacarpalCalibration calibration,
        out Quaternion rotation)
    {
        Vector3 sourceSwing = new Basis(sourceRelation.Normalized()) * SourceLongitudinal;
        if (!IsFinite(sourceSwing))
        {
            rotation = Quaternion.Identity;
            return false;
        }

        // C_side pairing (XR-002 TR28.7): the measured side-dependent signs pair the source wrist axes
        // (+X/+Y/+Z) with the destination binding hand frame. The palm normal already carries sigma_side
        // (TR28.3), so its pairing sign is +1 on both sides.
        float spanSign = side == LimbSide.Left ? -1.0f : 1.0f;
        Vector3 correspondence = (correspondenceFrame.SpanAxis * (spanSign * sourceSwing.X))
            + (correspondenceFrame.Longitudinal * sourceSwing.Y)
            + (correspondenceFrame.PalmNormal * sourceSwing.Z);
        if (!IsFinite(correspondence) || correspondence.LengthSquared() <= DirectionLengthSquaredEpsilon)
        {
            rotation = Quaternion.Identity;
            return false;
        }

        Quaternion neutral = destinationNeutral.Normalized();
        Quaternion anchor = calibration.NeutralAnchor.Normalized();
        Vector3 aim = new Basis(anchor)
            * (new Basis(neutral.Inverse()) * correspondence);
        if (!IsFinite(aim) || aim.LengthSquared() <= DirectionLengthSquaredEpsilon)
        {
            rotation = Quaternion.Identity;
            return false;
        }

        Vector3 unitAim = aim.Normalized();
        Vector3 longitudinal = metacarpalFrame.Longitudinal;
        float dot = Mathf.Clamp(longitudinal.Dot(unitAim), -1.0f, 1.0f);
        if (dot >= 1.0f - ParallelDotTolerance)
        {
            // Deterministic parallel branch: the anchored aim lies on l — the calibrated neutral — so the
            // swing is the identity and D_meta = N_meta verbatim (XR-002 TR28.7, Requirement 29).
            rotation = neutral;
            return true;
        }

        if (dot <= -1.0f + ParallelDotTolerance)
        {
            // Antiparallel: fail this destination only — no roll axis is invented (XR-002 TR28.7).
            rotation = Quaternion.Identity;
            return false;
        }

        // R = shortest_arc(l, d): the axis l × d discards the aim's l-component by construction, and the
        // angle is angle(l, d). R' rescales the angle by the pinned metacarpal swing gain about the same
        // axis; no clamp is applied anywhere.
        Vector3 swingAxis = longitudinal.Cross(unitAim).Normalized();
        float swingRadians = Mathf.Acos(dot);
        float bendAxis = swingAxis.Dot(metacarpalFrame.Palmward);
        float hingeAxis = swingAxis.Dot(metacarpalFrame.Hinge);
        float mirroredBend = side == LimbSide.Left ? -bendAxis : bendAxis;
        float hingeGate = Smoothstep(
            hingeAxis,
            calibration.HingeGateStart,
            calibration.HingeGateEnd);
        float bendGate = Smoothstep(
            mirroredBend,
            calibration.BendGateStart,
            calibration.BendGateEnd);
        float combinedGate = hingeGate * bendGate;
        float effectiveGain = 1.0f + ((calibration.SwingGain - 1.0f) * combinedGate);
        float gainedRadians = effectiveGain * swingRadians;
        Quaternion appliedSwing = new(swingAxis, gainedRadians);
        rotation = neutral * appliedSwing;
        return true;
    }

    /// <summary>Compatibility overload for value-only fixtures using the authored profile defaults.</summary>
    public static bool TryMapThumbMetacarpalSwing(
        Quaternion sourceRelation,
        in AuthoredThumbCorrespondenceFrame correspondenceFrame,
        LimbSide side,
        in FingerAnatomicalFrame metacarpalFrame,
        Quaternion destinationNeutral,
        Quaternion neutralAnchor,
        float metacarpalSwingGain,
        out Quaternion rotation)
    {
        var calibration = new ResolvedThumbMetacarpalCalibration(
            neutralAnchor,
            metacarpalSwingGain,
            OpticalFingerTrackingCalibrationProfile.DefaultMetacarpalHingeGateStart,
            OpticalFingerTrackingCalibrationProfile.DefaultMetacarpalHingeGateEnd,
            OpticalFingerTrackingCalibrationProfile.DefaultMetacarpalBendGateStart,
            OpticalFingerTrackingCalibrationProfile.DefaultMetacarpalBendGateEnd);
        return TryMapThumbMetacarpalSwing(
            sourceRelation,
            correspondenceFrame,
            side,
            metacarpalFrame,
            destinationNeutral,
            calibration,
            out rotation);
    }

    /// <summary>C1-continuous threshold response used by the thumb-metacarpal directional envelope.</summary>
    public static float Smoothstep(float value, float start, float end)
    {
        if (value <= start)
        {
            return 0.0f;
        }

        if (value >= end)
        {
            return 1.0f;
        }

        float u = (value - start) / (end - start);
        return u * u * (3.0f - (2.0f * u));
    }

    /// <summary>
    /// Expresses a global-frame direction in a desired global bone orientation's local frame:
    /// <c>inverse(Q') × direction</c>. Used for the local proximal frame and the per-bone local hinge axes
    /// (XR-002 TR18, TR21-TR22).
    /// </summary>
    public static Vector3 ExpressInLocalFrame(Quaternion desiredGlobalRotation, Vector3 globalDirection)
        => new Basis(desiredGlobalRotation.Inverse()) * globalDirection;

    /// <summary>
    /// Derives one shared per-hand anatomical frame from the four chains' global rest geometry and their desired
    /// (swung) global orientations, applying every fail-closed consensus gate (XR-002 TR18-TR19).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>L</c> is the desired middle proximal direction; <c>R</c> is the desired index proximal root minus the
    /// little proximal root; <c>R_perp = R − L·(L·R)</c>; <c>H0 = normalise(R_perp)</c>. Each swung chain's
    /// intermediate segment direction is projected perpendicular to <c>L</c> and normalised, and the four
    /// curvature directions form the equal-weight resultant <c>B_curv</c>. <c>H = ±H0</c> is chosen so
    /// <c>(H × L)·B_curv &gt; 0</c>, then <c>B = H × L</c>, producing the right-handed frame
    /// <c>L × B = H</c>, <c>B × H = L</c>, <c>H × L = B</c>.
    /// </para>
    /// <para>
    /// The natural bend of a chain is the angle between its swung proximal and swung intermediate segment
    /// directions. Binding fails closed unless all inputs are finite; the projected span ratio is at least
    /// <see cref="MinimumSpanRatio" />; every contributing bend is at least
    /// <see cref="MinimumNaturalBendDegrees" />; all four curvature directions are available; the resultant
    /// concentration is at least <see cref="MinimumConcentration" />; every curvature direction lies within
    /// <see cref="MaximumCurvatureDisagreementDegrees" /> of the resultant; and the final frame is unit,
    /// orthogonal, and right-handed within the existing numerical tolerances. A rejected hand never publishes
    /// partial state and never falls back to arbitrary, world-axis, animation-derived, or synthetic-tip axes.
    /// </para>
    /// </remarks>
    public static bool TryDeriveHandFrame(
        ReadOnlySpan<FingerRestNeutralChain> chains,
        ReadOnlySpan<Quaternion> desiredGlobalRotations,
        out FingerAnatomicalFrame frame,
        out string error)
    {
        frame = default;
        if (chains.Length != FingerRestNeutralMath.ChainCount
            || desiredGlobalRotations.Length < FingerRestNeutralMath.NeutralCount)
        {
            error = $"Expected exactly {FingerRestNeutralMath.ChainCount} chains and at least " +
                $"{FingerRestNeutralMath.NeutralCount} desired global rotations.";
            return false;
        }

        Span<Vector3> proximalSegments = stackalloc Vector3[FingerRestNeutralMath.ChainCount];
        Span<Vector3> swungIntermediateSegments = stackalloc Vector3[FingerRestNeutralMath.ChainCount];
        Span<Quaternion> swings = stackalloc Quaternion[FingerRestNeutralMath.ChainCount];
        Span<Vector3> curvatureDirections = stackalloc Vector3[FingerRestNeutralMath.ChainCount];

        // Gate 1: finiteness of every contributing origin, segment, and desired orientation.
        for (int chainIndex = 0; chainIndex < FingerRestNeutralMath.ChainCount; chainIndex++)
        {
            FingerRestNeutralChain chain = chains[chainIndex];
            Vector3 proximalSegment = chain.IntermediateGlobalRest.Origin - chain.ProximalGlobalRest.Origin;
            Vector3 intermediateSegment = chain.DistalGlobalRest.Origin - chain.IntermediateGlobalRest.Origin;
            Quaternion desiredProximal = desiredGlobalRotations[chainIndex * FingerRestNeutralMath.BonesPerChain];
            if (!IsFinite(proximalSegment) || !IsFinite(intermediateSegment) || !IsFinite(desiredProximal)
                || proximalSegment.LengthSquared() <= DirectionLengthSquaredEpsilon
                || intermediateSegment.LengthSquared() <= DirectionLengthSquaredEpsilon)
            {
                error = $"Chain {chainIndex} frame geometry or desired orientation is degenerate or non-finite.";
                return false;
            }

            proximalSegments[chainIndex] = proximalSegment;

            // The swing is recovered from the desired and rest proximal global orientations (XR-002 TR37 step 3).
            Quaternion restProximal = chain.ProximalGlobalRest.Basis.Orthonormalized().GetRotationQuaternion();
            Quaternion swing = CanonicalNormalise(desiredProximal.Normalized() * restProximal.Inverse());
            Vector3 swungProximal = new Basis(swing) * proximalSegment;
            Vector3 swungIntermediate = new Basis(swing) * intermediateSegment;
            if (!IsFinite(swungProximal) || !IsFinite(swungIntermediate))
            {
                error = $"Chain {chainIndex} swung segment direction is non-finite.";
                return false;
            }

            swings[chainIndex] = swing;
            swungIntermediateSegments[chainIndex] = swungIntermediate;
        }

        // The middle chain receives the identity swing (XR-002 TR43), so its rest proximal segment is the desired
        // middle proximal direction L.
        Vector3 longitudinal = proximalSegments[MiddleChain].Normalized();
        if (!IsFinite(longitudinal))
        {
            error = "The desired middle proximal direction is non-finite.";
            return false;
        }

        // Gate 2: the projected root span divided by the mean proximal segment length.
        Vector3 rootSpan = chains[IndexChain].ProximalGlobalRest.Origin - chains[LittleChain].ProximalGlobalRest.Origin;
        if (!IsFinite(rootSpan))
        {
            error = "The index/little proximal root span is non-finite.";
            return false;
        }

        float meanProximalLength = 0.0f;
        for (int chainIndex = 0; chainIndex < FingerRestNeutralMath.ChainCount; chainIndex++)
        {
            meanProximalLength += proximalSegments[chainIndex].Length();
        }

        meanProximalLength /= FingerRestNeutralMath.ChainCount;

        Vector3 projectedRootSpan = rootSpan - (longitudinal * longitudinal.Dot(rootSpan));
        float spanRatio = meanProximalLength > 0.0f ? projectedRootSpan.Length() / meanProximalLength : 0.0f;
        if (spanRatio < MinimumSpanRatio)
        {
            error = $"Projected root span ratio {spanRatio:F3} is below the required {MinimumSpanRatio}.";
            return false;
        }

        Vector3 rootAxis = projectedRootSpan.Normalized();
        if (!IsFinite(rootAxis))
        {
            error = "The projected root span direction is degenerate.";
            return false;
        }

        // Gate 3: every contributing natural bend — the angle between the swung proximal and swung intermediate
        // segment directions — is at least the required minimum.
        Span<float> naturalBendDegrees = stackalloc float[FingerRestNeutralMath.ChainCount];
        for (int chainIndex = 0; chainIndex < FingerRestNeutralMath.ChainCount; chainIndex++)
        {
            Quaternion swing = swings[chainIndex];
            Vector3 swungProximal = new Basis(swing) * proximalSegments[chainIndex];
            naturalBendDegrees[chainIndex] = Mathf.RadToDeg(
                swungProximal.Normalized().AngleTo(swungIntermediateSegments[chainIndex].Normalized()));
            if (naturalBendDegrees[chainIndex] < MinimumNaturalBendDegrees)
            {
                error = $"Chain {chainIndex} natural bend {naturalBendDegrees[chainIndex]:F3} degrees is below " +
                    $"the required {MinimumNaturalBendDegrees} degrees.";
                return false;
            }
        }

        // Gate 4: all four curvature directions are available.
        for (int chainIndex = 0; chainIndex < FingerRestNeutralMath.ChainCount; chainIndex++)
        {
            Vector3 swungIntermediate = swungIntermediateSegments[chainIndex];
            Vector3 curvature = swungIntermediate - (longitudinal * longitudinal.Dot(swungIntermediate));
            if (!IsFinite(curvature) || curvature.LengthSquared() <= DirectionLengthSquaredEpsilon)
            {
                error = $"Chain {chainIndex} has no available curvature direction perpendicular to L.";
                return false;
            }

            curvatureDirections[chainIndex] = curvature.Normalized();
        }

        Vector3 resultant = Vector3.Zero;
        for (int chainIndex = 0; chainIndex < FingerRestNeutralMath.ChainCount; chainIndex++)
        {
            resultant += curvatureDirections[chainIndex];
        }

        resultant /= FingerRestNeutralMath.ChainCount;

        // Gate 5: resultant concentration — the magnitude of the equal-weight mean before normalisation.
        float concentration = resultant.Length();
        if (!float.IsFinite(concentration) || concentration < MinimumConcentration)
        {
            error = $"Curvature resultant concentration {concentration:F3} is below the required {MinimumConcentration}.";
            return false;
        }

        // Gate 6: every curvature direction lies within the maximum disagreement of the resultant.
        float maximumDisagreementDegrees = 0.0f;
        for (int chainIndex = 0; chainIndex < FingerRestNeutralMath.ChainCount; chainIndex++)
        {
            float disagreement = Mathf.RadToDeg(curvatureDirections[chainIndex].AngleTo(resultant));
            maximumDisagreementDegrees = Mathf.Max(maximumDisagreementDegrees, disagreement);
            if (disagreement > MaximumCurvatureDisagreementDegrees)
            {
                error = $"Chain {chainIndex} curvature disagreement {disagreement:F3} degrees exceeds the maximum " +
                    $"{MaximumCurvatureDisagreementDegrees} degrees.";
                return false;
            }
        }

        // H sign choice: H = ±H0 so (H × L)·B_curv > 0, then B = H × L (XR-002 TR18).
        Vector3 palmwardReference = rootAxis.Cross(longitudinal);
        float curvatureAlignment = palmwardReference.Dot(resultant);
        if (Mathf.Abs(curvatureAlignment) <= CurvatureSignEpsilon)
        {
            error = "The curvature resultant is degenerate relative to the projected root span axis.";
            return false;
        }

        Vector3 hinge = curvatureAlignment > 0.0f ? rootAxis : -rootAxis;
        Vector3 palmward = hinge.Cross(longitudinal);

        // Gate 7: the final frame is unit, orthogonal, and right-handed within the existing tolerances.
        if (!IsFrameValid(longitudinal, palmward, hinge, out string frameError))
        {
            error = frameError;
            return false;
        }

        frame = new FingerAnatomicalFrame(longitudinal, palmward, hinge);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Transactional bilateral binding (XR-002 TR18, TR25-TR26, TR28): derives and publishes the effective
    /// neutrals, desired global orientations, per-hand frames, local proximal frames, local hinge axes, the
    /// six authored thumb axes, both metacarpal frames, and both metacarpal correspondence frames — 30
    /// records per skeleton, thumb records first per side — only after BOTH hands pass every neutral,
    /// hand-frame, thumb-binding, authored-axis, and bilateral-mirror gate. Any hand's failure clears every
    /// output with no partial publication: non-thumb state never becomes usable through a separate earlier
    /// publication, and a thumb failure voids the whole binding rather than a thumb-only subset.
    /// </summary>
    public static bool TryDeriveBilateralBinding(
        in FingerHandRestGeometry leftHand,
        in FingerHandRestGeometry rightHand,
        in ThumbRestGeometry leftThumb,
        in ThumbRestGeometry rightThumb,
        in AuthoredThumbSideReferences leftReferences,
        in AuthoredThumbSideReferences rightReferences,
        Span<Quaternion> destinationNeutrals,
        Span<Quaternion> desiredGlobalRotations,
        Span<Vector3> localHingeAxes,
        Span<FingerAnatomicalFrame> localProximalFrames,
        Span<FingerAnatomicalFrame> handFrames,
        Span<AuthoredThumbCorrespondenceFrame> correspondenceFrames,
        out string error)
    {
        int recordCount = OpticalFingerTrackingCalibrationProfile.RecordCount;
        if (destinationNeutrals.Length < recordCount
            || desiredGlobalRotations.Length < recordCount
            || localHingeAxes.Length < recordCount
            || localProximalFrames.Length < recordCount
            || handFrames.Length < 2
            || correspondenceFrames.Length < 2)
        {
            error = "Bilateral binding requires complete caller-owned output buffers.";
            return false;
        }

        ClearBilateralOutputs(destinationNeutrals, desiredGlobalRotations, localHingeAxes, localProximalFrames,
            handFrames, correspondenceFrames);

        bool success = TryDeriveSideBinding(
            leftHand,
            leftThumb,
            leftReferences,
            LimbSide.Left,
            destinationNeutrals,
            desiredGlobalRotations,
            localHingeAxes,
            localProximalFrames,
            handFrames,
            correspondenceFrames,
            out AuthoredThumbAxis leftMetacarpalAxis,
            out AuthoredThumbAxis leftProximalAxis,
            out AuthoredThumbAxis leftDistalAxis,
            out AuthoredThumbPalmPlane leftPalm,
            out AuthoredThumbMetacarpalFrame leftFrame,
            out error);
        if (success)
        {
            success = TryDeriveSideBinding(
                rightHand,
                rightThumb,
                rightReferences,
                LimbSide.Right,
                destinationNeutrals,
                desiredGlobalRotations,
                localHingeAxes,
                localProximalFrames,
                handFrames,
                correspondenceFrames,
                out AuthoredThumbAxis rightMetacarpalAxis,
                out AuthoredThumbAxis rightProximalAxis,
                out AuthoredThumbAxis rightDistalAxis,
                out AuthoredThumbPalmPlane rightPalm,
                out AuthoredThumbMetacarpalFrame rightFrame,
                out error);
            if (success)
            {
                // Bilateral mirror contract (XR-002 TR28.6): measured after hemisphere alignment, with no per-side
                // sign rescue.
                success = AuthoredThumbAxisMath.TryValidateBilateralMirror(
                    leftReferences.ResetGeometry,
                    leftMetacarpalAxis,
                    leftProximalAxis,
                    leftDistalAxis,
                    leftFrame,
                    leftPalm,
                    rightReferences.ResetGeometry,
                    rightMetacarpalAxis,
                    rightProximalAxis,
                    rightDistalAxis,
                    rightFrame,
                    rightPalm,
                    out _,
                    out error);
            }
        }

        if (!success)
        {
            // Transactional: no partially valid hand may leak usable values to callers (XR-002 TR18, TR25-TR26).
            ClearBilateralOutputs(destinationNeutrals, desiredGlobalRotations, localHingeAxes, localProximalFrames,
                handFrames, correspondenceFrames);
            return false;
        }

        return true;
    }

    private static void ClearBilateralOutputs(
        Span<Quaternion> destinationNeutrals,
        Span<Quaternion> desiredGlobalRotations,
        Span<Vector3> localHingeAxes,
        Span<FingerAnatomicalFrame> localProximalFrames,
        Span<FingerAnatomicalFrame> handFrames,
        Span<AuthoredThumbCorrespondenceFrame> correspondenceFrames)
    {
        int recordCount = OpticalFingerTrackingCalibrationProfile.RecordCount;
        destinationNeutrals[..recordCount].Clear();
        desiredGlobalRotations[..recordCount].Clear();
        localHingeAxes[..recordCount].Clear();
        localProximalFrames[..recordCount].Clear();
        handFrames[..2].Clear();
        correspondenceFrames[..2].Clear();
    }

    private static bool TryDeriveSideBinding(
        in FingerHandRestGeometry hand,
        in ThumbRestGeometry thumb,
        in AuthoredThumbSideReferences references,
        LimbSide side,
        Span<Quaternion> destinationNeutrals,
        Span<Quaternion> desiredGlobalRotations,
        Span<Vector3> localHingeAxes,
        Span<FingerAnatomicalFrame> localProximalFrames,
        Span<FingerAnatomicalFrame> handFrames,
        Span<AuthoredThumbCorrespondenceFrame> correspondenceFrames,
        out AuthoredThumbAxis metacarpalAxis,
        out AuthoredThumbAxis proximalAxis,
        out AuthoredThumbAxis distalAxis,
        out AuthoredThumbPalmPlane palm,
        out AuthoredThumbMetacarpalFrame metacarpalFrame,
        out string error)
    {
        metacarpalAxis = default;
        proximalAxis = default;
        distalAxis = default;
        palm = default;
        metacarpalFrame = default;
        string sideName = side == LimbSide.Left ? "Left" : "Right";
        int sideOffset = side == LimbSide.Left ? 0 : OpticalFingerTrackingCalibrationProfile.RecordsPerSide;
        Span<Quaternion> sideNeutrals = stackalloc Quaternion[FingerRestNeutralMath.NeutralCount];
        Span<Quaternion> sideDesiredGlobals = stackalloc Quaternion[FingerRestNeutralMath.NeutralCount];

        if (!FingerRestNeutralMath.TryDerive(
                hand.HandBoneIndex,
                hand.HandGlobalRest,
                hand.Chains,
                sideNeutrals,
                sideDesiredGlobals,
                out error))
        {
            error = $"{sideName} hand: {error}";
            return false;
        }

        if (!TryDeriveHandFrame(hand.Chains, sideDesiredGlobals, out FingerAnatomicalFrame frame, out error))
        {
            error = $"{sideName} hand: {error}";
            return false;
        }

        // Publish the non-thumb records at sideOffset + ThumbRecordCount in chain-major order (XR-002 TR43-TR44).
        for (int index = 0; index < FingerRestNeutralMath.NeutralCount; index++)
        {
            // Every non-thumb bone flexes about the same global H expressed in its own desired local frame; the
            // tipless distal inherits the same H and no synthetic tip direction exists (XR-002 TR21, A6).
            int recordIndex = sideOffset + ThumbRecordCount + index;
            destinationNeutrals[recordIndex] = sideNeutrals[index];
            desiredGlobalRotations[recordIndex] = sideDesiredGlobals[index];
            localHingeAxes[recordIndex] = ExpressInLocalFrame(sideDesiredGlobals[index], frame.Hinge);
        }

        for (int chainIndex = 0; chainIndex < FingerRestNeutralMath.ChainCount; chainIndex++)
        {
            int proximalIndex = chainIndex * FingerRestNeutralMath.BonesPerChain;
            localProximalFrames[sideOffset + ThumbRecordCount + proximalIndex] = new FingerAnatomicalFrame(
                ExpressInLocalFrame(sideDesiredGlobals[proximalIndex], frame.Longitudinal),
                ExpressInLocalFrame(sideDesiredGlobals[proximalIndex], frame.Palmward),
                ExpressInLocalFrame(sideDesiredGlobals[proximalIndex], frame.Hinge));
        }

        int sideIndex = sideOffset / OpticalFingerTrackingCalibrationProfile.RecordsPerSide;
        handFrames[sideIndex] = frame;

        if (!TryDeriveThumbSideBinding(
                thumb,
                references,
                hand.HandBoneIndex,
                side,
                sideOffset,
                destinationNeutrals,
                desiredGlobalRotations,
                localHingeAxes,
                localProximalFrames,
                correspondenceFrames,
                out metacarpalAxis,
                out proximalAxis,
                out distalAxis,
                out palm,
                out metacarpalFrame,
                out error))
        {
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Derives and publishes one hand's thumb records (XR-002 TR25-TR29): topology and finite-rest-rotation
    /// validation, the sampled immutable Reset local keys as the thumb neutrals (XR-002 TR29 — never the
    /// imported rest locals, which remain finite binding-qualification inputs and import-forensic evidence)
    /// and the authored imported global rests as import-forensic desired globals, the six authored axes from
    /// the sampled reference keys, the Reset palm plane, and the deterministic metacarpal frame with every
    /// gate — publishing the authored proximal/distal axes as the thumb local hinge caches, the metacarpal
    /// <c>(l, b, h)</c> frame as the thumb metacarpal's local swing frame, and the validated palm plane's
    /// <c>(u, t, n_palm,H)</c> axes as the runtime metacarpal correspondence frame (XR-002 TR28.7). The
    /// runtime mapping expresses every output about these authored axes; no segment-centre derivation,
    /// shared geometry hinge, or synthetic tip exists.
    /// </summary>
    private static bool TryDeriveThumbSideBinding(
        in ThumbRestGeometry thumb,
        in AuthoredThumbSideReferences references,
        int handBoneIndex,
        LimbSide side,
        int sideOffset,
        Span<Quaternion> destinationNeutrals,
        Span<Quaternion> desiredGlobalRotations,
        Span<Vector3> localHingeAxes,
        Span<FingerAnatomicalFrame> localProximalFrames,
        Span<AuthoredThumbCorrespondenceFrame> correspondenceFrames,
        out AuthoredThumbAxis metacarpalAxis,
        out AuthoredThumbAxis proximalAxis,
        out AuthoredThumbAxis distalAxis,
        out AuthoredThumbPalmPlane palm,
        out AuthoredThumbMetacarpalFrame metacarpalFrame,
        out string error)
    {
        metacarpalAxis = default;
        proximalAxis = default;
        distalAxis = default;
        palm = default;
        metacarpalFrame = default;
        string sideName = side == LimbSide.Left ? "Left" : "Right";
        if (thumb.MetacarpalBoneIndex < 0 || thumb.ProximalBoneIndex < 0 || thumb.DistalBoneIndex < 0)
        {
            error = $"{sideName} hand: the thumb binding has a missing destination bone.";
            return false;
        }

        if (thumb.MetacarpalBoneIndex == thumb.ProximalBoneIndex
            || thumb.MetacarpalBoneIndex == thumb.DistalBoneIndex
            || thumb.ProximalBoneIndex == thumb.DistalBoneIndex
            || thumb.MetacarpalParentBoneIndex != handBoneIndex
            || thumb.ProximalParentBoneIndex != thumb.MetacarpalBoneIndex
            || thumb.DistalParentBoneIndex != thumb.ProximalBoneIndex)
        {
            error = $"{sideName} hand: the thumb chain has an unsupported parent topology.";
            return false;
        }

        // Thumb rest bases tolerate non-uniform import scale within the documented polar-decomposition bounds,
        // unlike the strict uniform-scale non-thumb validation (XR-002 TR25). These six extractions are the
        // thumb finite-rest-rotation gates. The three rest-local values are binding-qualification inputs only
        // (XR-002 TR25, TR29): N is the sampled Reset key, so the extracted rest locals are deliberately
        // discarded after binding qualification.
        if (!ThumbRestBasisMath.TryExtractRotation(
                thumb.MetacarpalGlobalRest.Basis,
                $"{sideName} thumb metacarpal global rest",
                out Quaternion metacarpalGlobal,
                out error)
            || !ThumbRestBasisMath.TryExtractRotation(
                thumb.ProximalGlobalRest.Basis,
                $"{sideName} thumb proximal global rest",
                out Quaternion proximalGlobal,
                out error)
            || !ThumbRestBasisMath.TryExtractRotation(
                thumb.DistalGlobalRest.Basis,
                $"{sideName} thumb distal global rest",
                out Quaternion distalGlobal,
                out error)
            || !ThumbRestBasisMath.TryExtractRotation(
                thumb.MetacarpalRestLocalBasis,
                $"{sideName} thumb metacarpal rest local",
                out _,
                out error)
            || !ThumbRestBasisMath.TryExtractRotation(
                thumb.ProximalRestLocalBasis,
                $"{sideName} thumb proximal rest local",
                out _,
                out error)
            || !ThumbRestBasisMath.TryExtractRotation(
                thumb.DistalRestLocalBasis,
                $"{sideName} thumb distal rest local",
                out _,
                out error))
        {
            error = $"{sideName} hand: {error}";
            return false;
        }

        // Authored axes from the sampled immutable reference keys (XR-002 TR26): three independent per-joint
        // derivations with the reference-angle gate, never gain.
        if (!AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
                references.Metacarpal.Reset,
                references.Metacarpal.Flexion,
                $"{sideName} thumb metacarpal",
                out metacarpalAxis,
                out error)
            || !AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
                references.Proximal.Reset,
                references.Proximal.Flexion,
                $"{sideName} thumb proximal",
                out proximalAxis,
                out error)
            || !AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
                references.Distal.Reset,
                references.Distal.Flexion,
                $"{sideName} thumb distal",
                out distalAxis,
                out error)
            || !AuthoredThumbAxisMath.TryDeriveResetPalmPlane(
                references.ResetGeometry,
                side,
                out palm,
                out error)
            || !AuthoredThumbAxisMath.TryDeriveMetacarpalFrame(
                references.ResetGeometry,
                metacarpalAxis,
                palm,
                side,
                out metacarpalFrame,
                out error))
        {
            error = $"{sideName} hand: {error}";
            return false;
        }

        // Publish the thumb records (XR-002 TR26-TR28.7, TR29): the normalised sampled Reset local keys as N —
        // never the imported rest locals and never TR43 chain-neutral swings, so identity Delta lands on the
        // authored Reset neutral by contract rather than incidentally — the authored imported global rests as
        // import-forensic desired globals, the independent authored proximal/distal axes as the local hinge
        // caches, and the metacarpal (l, b, h) frame as the metacarpal's local swing frame. The validated
        // palm plane's (u, t, n_palm,H) axes publish as the metacarpal correspondence frame for the runtime
        // anchored hand-frame transfer (XR-002 TR28.7).
        int metacarpalRecord = sideOffset + ThumbMetacarpalRecord;
        destinationNeutrals[metacarpalRecord] = references.Metacarpal.Reset.Normalized();
        desiredGlobalRotations[metacarpalRecord] = CanonicalNormalise(metacarpalGlobal);
        localProximalFrames[metacarpalRecord] = new FingerAnatomicalFrame(
            metacarpalFrame.Longitudinal,
            metacarpalFrame.Bend,
            metacarpalFrame.Splay);
        correspondenceFrames[(int)side] = new AuthoredThumbCorrespondenceFrame(
            palm.Longitudinal,
            palm.SpanAxis,
            palm.PalmNormalInHand);

        int proximalRecord = sideOffset + ThumbProximalRecord;
        destinationNeutrals[proximalRecord] = references.Proximal.Reset.Normalized();
        desiredGlobalRotations[proximalRecord] = CanonicalNormalise(proximalGlobal);
        localHingeAxes[proximalRecord] = proximalAxis.Axis;

        int distalRecord = sideOffset + ThumbDistalRecord;
        destinationNeutrals[distalRecord] = references.Distal.Reset.Normalized();
        desiredGlobalRotations[distalRecord] = CanonicalNormalise(distalGlobal);
        localHingeAxes[distalRecord] = distalAxis.Axis;

        return true;
    }

    private static bool IsFrameValid(Vector3 longitudinal, Vector3 palmward, Vector3 hinge, out string error)
    {
        if (Mathf.Abs(longitudinal.LengthSquared() - 1.0f) > BasisTolerance
            || Mathf.Abs(palmward.LengthSquared() - 1.0f) > BasisTolerance
            || Mathf.Abs(hinge.LengthSquared() - 1.0f) > BasisTolerance)
        {
            error = "The derived hand frame axes are not unit length.";
            return false;
        }

        if (Mathf.Abs(longitudinal.Dot(palmward)) > BasisTolerance
            || Mathf.Abs(longitudinal.Dot(hinge)) > BasisTolerance
            || Mathf.Abs(palmward.Dot(hinge)) > BasisTolerance)
        {
            error = "The derived hand frame axes are not orthogonal.";
            return false;
        }

        if (palmward.DistanceTo(hinge.Cross(longitudinal)) > BasisTolerance
            || hinge.DistanceTo(longitudinal.Cross(palmward)) > BasisTolerance
            || longitudinal.DistanceTo(palmward.Cross(hinge)) > BasisTolerance)
        {
            error = "The derived hand frame is not right-handed.";
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

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>
/// A right-handed anatomical frame <c>(L, B, H)</c>: longitudinal, palmward, and flexion-hinge axes satisfying
/// <c>L × B = H</c>, <c>B × H = L</c>, <c>H × L = B</c> (XR-002 TR18). Used both for the per-hand global frame,
/// for the same frame expressed in a proximal bone's desired-local orientation, and for the authored thumb
/// metacarpal's Reset-local bend/splay frame <c>(l, b, h)</c> (XR-002 TR28.4, TR28.7).
/// </summary>
public readonly record struct FingerAnatomicalFrame(Vector3 Longitudinal, Vector3 Palmward, Vector3 Hinge);

/// <summary>Value-only binding inputs for one hand's non-thumb chains (XR-002 TR18, TR43).</summary>
public readonly record struct FingerHandRestGeometry(
    int HandBoneIndex,
    Transform3D HandGlobalRest,
    FingerRestNeutralChain[] Chains);

/// <summary>
/// Value-only binding inputs for one hand's thumb chain: skeleton-local global-rest geometry of the three thumb
/// bones, the thumb bones' parent topology, and the authored imported rest local bases that qualify the
/// binding — finite, scale-tolerantly extracted — without supplying the thumb neutrals, which are the sampled
/// Reset keys (XR-002 TR25-TR27, TR29).
/// </summary>
/// <remarks>
/// Thumb rest bases may carry non-uniform import scale; every consumed rotation — the three global rests and
/// the three authored local rests — is extracted by <see cref="ThumbRestBasisMath" />'s tolerant polar
/// decomposition, so all six derive from the same extraction (XR-002 TR25-TR27). The neutral itself comes from
/// the sampled Reset key (XR-002 TR29), so <c>Delta = identity</c> reproduces the Reset key exactly
/// independent of what the imported rest contains. Rest origins are carried only as import-forensic evidence
/// during binding qualification; the authored-animation thumb mapping (XR-002 TR26-TR28.7) never consumes
/// segment-centre geometry, so no index-proximal opposition reference exists.
/// </remarks>
public readonly record struct ThumbRestGeometry(
    int MetacarpalBoneIndex,
    int MetacarpalParentBoneIndex,
    Transform3D MetacarpalGlobalRest,
    Basis MetacarpalRestLocalBasis,
    int ProximalBoneIndex,
    int ProximalParentBoneIndex,
    Transform3D ProximalGlobalRest,
    Basis ProximalRestLocalBasis,
    int DistalBoneIndex,
    int DistalParentBoneIndex,
    Transform3D DistalGlobalRest,
    Basis DistalRestLocalBasis);
