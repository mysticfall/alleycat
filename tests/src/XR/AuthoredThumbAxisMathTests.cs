using AlleyCat.Rigging;
using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;

namespace AlleyCat.Tests.XR;

/// <summary>
/// Pure authored-animation thumb maths tests (XR-002 TR26, TR28): authored-axis derivation against the pinned
/// reference-female oracle, hemisphere handling and multiplication order, the Reset palm plane with its
/// cross-order and sigma contract, the metacarpal frame with every reachable fail-closed gate, the bilateral
/// mirror contract, and the runtime anchored hand-frame metacarpal correspondence transfer with its pinned
/// Q0 neutral anchor, K_meta swing gain, and measured side-dependent pairing signs.
/// </summary>
public sealed class AuthoredThumbAxisMathTests
{
    private const float AngularEpsilonDegrees = 0.1f;

    private static readonly Vector3 _sourceLongitudinal = Vector3.Up;
    private static readonly Vector3 _sourcePalmward = Vector3.Back;
    private static readonly Vector3 _sourceHinge = Vector3.Right;

    /// <summary>
    /// The pinned reference-female Reset/Grab keys derive exactly the six-value oracle: authored axes within
    /// 0.1 degrees and reference angles within 0.1 degrees, with no hemisphere flip (XR-002 TR26, A10).
    /// </summary>
    [Fact]
    public void TryDeriveAuthoredAxis_RealAssetKeys_ProducePinnedOracle()
    {
        Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.LeftThumbMetacarpalReset,
            AuthoredReferenceKeys.LeftThumbMetacarpalFlexion,
            "left metacarpal",
            out AuthoredThumbAxis metacarpal,
            out string metacarpalError), metacarpalError);
        Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.LeftThumbProximalReset,
            AuthoredReferenceKeys.LeftThumbProximalFlexion,
            "left proximal",
            out AuthoredThumbAxis proximal,
            out string proximalError), proximalError);
        Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.LeftThumbDistalReset,
            AuthoredReferenceKeys.LeftThumbDistalFlexion,
            "left distal",
            out AuthoredThumbAxis distal,
            out string distalError), distalError);

        AssertAxisApproximately(AuthoredThumbReferenceOracles.MetacarpalLeftAxis, metacarpal.Axis);
        AssertAxisApproximately(AuthoredThumbReferenceOracles.ProximalLeftAxis, proximal.Axis);
        AssertAxisApproximately(AuthoredThumbReferenceOracles.DistalLeftAxis, distal.Axis);
        AssertAngleWithin(
            AuthoredThumbReferenceOracles.MetacarpalAngleDegrees,
            metacarpal.ReferenceAngleDegrees,
            AngularEpsilonDegrees);
        AssertAngleWithin(
            AuthoredThumbReferenceOracles.ProximalAngleDegrees,
            proximal.ReferenceAngleDegrees,
            AngularEpsilonDegrees);
        AssertAngleWithin(
            AuthoredThumbReferenceOracles.DistalAngleDegrees,
            distal.ReferenceAngleDegrees,
            AngularEpsilonDegrees);

        // The real asset keys need no hemisphere flip (XR-002 TR25 pinned asset facts).
        Assert.False(metacarpal.HemisphereFlipped);
        Assert.False(proximal.HemisphereFlipped);
        Assert.False(distal.HemisphereFlipped);
    }

    /// <summary>
    /// The right-side asset keys mirror the left axes through J = diag(+1, -1, -1) (XR-002 TR26, A10).
    /// </summary>
    [Fact]
    public void TryDeriveAuthoredAxis_RightAssetKeys_MirrorLeftThroughJ()
    {
        Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.RightThumbMetacarpalReset,
            AuthoredReferenceKeys.RightThumbMetacarpalFlexion,
            "right metacarpal",
            out AuthoredThumbAxis metacarpal,
            out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.RightThumbProximalReset,
            AuthoredReferenceKeys.RightThumbProximalFlexion,
            "right proximal",
            out AuthoredThumbAxis proximal,
            out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.RightThumbDistalReset,
            AuthoredReferenceKeys.RightThumbDistalFlexion,
            "right distal",
            out AuthoredThumbAxis distal,
            out _));

        // a_left_expected = J · a_right with J = diag(+1, -1, -1) (XR-002 TR26, TR28.6).
        AssertAxisApproximately(
            AuthoredThumbReferenceOracles.MetacarpalLeftAxis,
            AuthoredThumbAxisMath.MirrorLocal(metacarpal.Axis));
        AssertAxisApproximately(
            AuthoredThumbReferenceOracles.ProximalLeftAxis,
            AuthoredThumbAxisMath.MirrorLocal(proximal.Axis));
        AssertAxisApproximately(
            AuthoredThumbReferenceOracles.DistalLeftAxis,
            AuthoredThumbAxisMath.MirrorLocal(distal.Axis));
    }

    /// <summary>
    /// A negated flexion key hemisphere-aligns to the identical axis and angle while recording the flip
    /// (XR-002 TR26).
    /// </summary>
    [Fact]
    public void TryDeriveAuthoredAxis_NegatedFlexion_HemisphereAlignsToIdenticalAxis()
    {
        Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.LeftThumbProximalReset,
            -AuthoredReferenceKeys.LeftThumbProximalFlexion,
            "left proximal",
            out AuthoredThumbAxis negated,
            out _));

        Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.LeftThumbProximalReset,
            AuthoredReferenceKeys.LeftThumbProximalFlexion,
            "left proximal",
            out AuthoredThumbAxis original,
            out _));

        Assert.True(negated.HemisphereFlipped);
        Assert.False(original.HemisphereFlipped);
        AssertAxisApproximately(original.Axis, negated.Axis);
        AssertAngleWithin(original.ReferenceAngleDegrees, negated.ReferenceAngleDegrees, 1e-3f);
    }

    /// <summary>
    /// The reversed product F' × inverse(R) does not reproduce the pinned axis: the normative order is
    /// A = inverse(R) × F' (XR-002 TR26, A10).
    /// </summary>
    [Fact]
    public void TryDeriveAuthoredAxis_ReversedMultiplicationOrder_ProducesDifferentAxis()
    {
        Quaternion reset = AuthoredReferenceKeys.LeftThumbMetacarpalReset.Normalized();
        Quaternion flexion = AuthoredReferenceKeys.LeftThumbMetacarpalFlexion.Normalized();

        // The reversed product F' × inverse(R) is a different rotation whose axis is NOT the pinned oracle:
        // the normative order is A = inverse(R) × F' (XR-002 TR26).
        Quaternion reversed = (flexion * reset.Inverse()).Normalized();
        Vector3 reversedAxis = new Vector3(reversed.X, reversed.Y, reversed.Z).Normalized();

        Assert.True(
            reversedAxis.AngleTo(AuthoredThumbReferenceOracles.MetacarpalLeftAxis) > Mathf.DegToRad(5.0f),
            "The reversed multiplication order must not reproduce the pinned authored axis.");
    }

    /// <summary>
    /// An authored angle below the required 2 degrees fails closed with the exact reason (XR-002 TR26).
    /// </summary>
    [Fact]
    public void TryDeriveAuthoredAxis_TooSmallReferenceAngle_FailsClosed()
    {
        Assert.False(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.LeftThumbProximalReset,
            AuthoredReferenceKeys.LeftThumbProximalReset,
            "left proximal",
            out _,
            out string identicalError));
        Assert.Contains("authored reference angle", identicalError, StringComparison.Ordinal);

        Quaternion tiny = new(new Vector3(0.3f, 0.6f, 0.74f).Normalized(), Mathf.DegToRad(1.0f));
        Assert.False(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            Quaternion.Identity,
            tiny,
            "left proximal",
            out _,
            out string tinyError));
        Assert.Contains("authored reference angle", tinyError, StringComparison.Ordinal);
    }

    /// <summary>
    /// Non-unit, non-finite, and near-zero quaternions fail closed with the joint named (XR-002 TR25.5).
    /// </summary>
    [Theory]
    [InlineData("non-unit")]
    [InlineData("non-finite")]
    [InlineData("zero")]
    public void TryDeriveAuthoredAxis_InvalidQuaternions_FailClosed(string kind)
    {
        Quaternion reset = kind switch
        {
            "non-unit" => new Quaternion(0.1f, 0.2f, 0.3f, 0.4f),
            "non-finite" => new Quaternion(float.NaN, 0.0f, 0.0f, 1.0f),
            _ => Quaternion.Identity * 0.0f,
        };

        Assert.False(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            reset,
            AuthoredReferenceKeys.LeftThumbProximalFlexion,
            "left proximal",
            out _,
            out string error));
        Assert.Contains("left proximal", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The palm plane follows the normative t × u cross order with the sigma side constant and the metacarpal
    /// local round-trip (XR-002 TR28.3). The fixture pins the real rig's hand-local chirality — the wrist
    /// anchor at −Y against the +Y proximal roots — so the normative sigma pair is the only one that yields
    /// the asserted palmward normal; inverting it flips the normal and fails here.
    /// </summary>
    [Fact]
    public void TryDeriveResetPalmPlane_SyntheticGeometry_ProducesNormativeCrossOrderAndSigma()
    {
        AuthoredThumbResetGeometry geometry = CreatePalmGeometry(mirrored: true);
        Assert.True(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(
            geometry,
            LimbSide.Left,
            out AuthoredThumbPalmPlane palm,
            out string error), error);

        // Independent recomputation in the hand-local frame (XR-002 TR28.3): the centroid is the mean of
        // all four non-thumb proximal roots.
        Basis inverseHand = new(geometry.HandResetGlobal.Inverse());
        Vector3 wrist = inverseHand * (geometry.SkeletonWristPosition - geometry.SkeletonHandPosition);
        Vector3 index = inverseHand * (geometry.SkeletonIndexProximalPosition - geometry.SkeletonHandPosition);
        Vector3 middle = inverseHand * (geometry.SkeletonMiddleProximalPosition - geometry.SkeletonHandPosition);
        Vector3 ring = inverseHand * (geometry.SkeletonRingProximalPosition - geometry.SkeletonHandPosition);
        Vector3 little = inverseHand * (geometry.SkeletonLittleProximalPosition - geometry.SkeletonHandPosition);
        Vector3 centroid = (index + middle + ring + little) / 4.0f;
        Vector3 palmReference = wrist * 0.5f;
        Vector3 u = (centroid - palmReference).Normalized();
        Vector3 span = index - little;
        Vector3 t = (span - (u * u.Dot(span))).Normalized();

        // Fixture chirality guard: the real rig keeps the wrist anchor at -Y and the roots at +Y in H, which
        // is what makes this test discriminate the sigma sign.
        Assert.True(wrist.Y < -0.1f, $"Expected the wrist anchor below the hand origin in H, got {wrist}.");
        Assert.True(centroid.Y > 0.05f, $"Expected the proximal roots above the hand origin in H, got {centroid}.");

        // Normative cross order t × u with sigma_Left = -1 (XR-002 TR28.3).
        AssertAxisApproximately(-t.Cross(u), palm.PalmNormalInHand);
        AssertAxisApproximately(u, palm.Longitudinal);
        AssertAxisApproximately(t, palm.SpanAxis);
        Assert.True(palm.SpanRatio >= AuthoredThumbAxisMath.MinimumPalmSpanRatio);

        // The metacarpal-local expression maps back to the hand-local normal through the Reset key.
        AssertAxisApproximately(
            palm.PalmNormalInHand,
            (new Basis(geometry.MetacarpalResetLocal) * palm.PalmNormalInMetacarpalLocal).Normalized());

        // sigma is the only side constant: flipping the side label on mirrored geometry flips the normal,
        // never the cross order (XR-002 TR28.3, sigma_Right = -sigma_Left = +1).
        Assert.True(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(
            CreatePalmGeometry(mirrored: false),
            LimbSide.Right,
            out AuthoredThumbPalmPlane mirroredPalm,
            out _));
        AssertAxisApproximately(
            AuthoredThumbAxisMath.MirrorPolar(palm.PalmNormalInHand),
            mirroredPalm.PalmNormalInHand);

        // Flipping the side label on identical geometry still flips only the sigma constant.
        Assert.True(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(
            geometry,
            LimbSide.Right,
            out AuthoredThumbPalmPlane flipped,
            out _));
        AssertAxisApproximately(-palm.PalmNormalInHand, flipped.PalmNormalInHand);
    }

    /// <summary>
    /// A root span collapsed below the required ratio fails the span gate with the exact reason (XR-002 TR28.5).
    /// </summary>
    [Fact]
    public void TryDeriveResetPalmPlane_CollapsedRootSpan_FailsSpanGate()
    {
        AuthoredThumbResetGeometry geometry = CreatePalmGeometry(mirrored: false, collapsedSpan: true);
        Assert.False(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(
            geometry,
            LimbSide.Left,
            out _,
            out string error));
        Assert.Contains("span ratio", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Non-finite FK positions fail the palm derivation closed (XR-002 TR28.5).
    /// </summary>
    [Fact]
    public void TryDeriveResetPalmPlane_NonFiniteGeometry_FailsClosed()
    {
        AuthoredThumbResetGeometry geometry = CreatePalmGeometry(mirrored: true) with
        {
            SkeletonIndexProximalPosition = new Vector3(float.NaN, 0.0f, 0.0f),
        };
        Assert.False(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(
            geometry,
            LimbSide.Left,
            out _,
            out string error));
        Assert.Contains("non-finite", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Valid authored inputs produce the right-handed (l, b, h) frame with b = h × l and every gate margin
    /// (XR-002 TR28.4-28.5).
    /// </summary>
    [Fact]
    public void TryDeriveMetacarpalFrame_ValidAuthoredInputs_ProduceRightHandedGatePassingFrame()
    {
        AuthoredThumbResetGeometry geometry = CreatePalmGeometry(mirrored: true);
        Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.LeftThumbMetacarpalReset,
            AuthoredReferenceKeys.LeftThumbMetacarpalFlexion,
            "left metacarpal",
            out AuthoredThumbAxis axis,
            out string axisError), axisError);
        Assert.True(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(
            geometry,
            LimbSide.Left,
            out AuthoredThumbPalmPlane palm,
            out string palmError), palmError);
        Assert.True(AuthoredThumbAxisMath.TryDeriveMetacarpalFrame(
            geometry,
            axis,
            palm,
            LimbSide.Left,
            out AuthoredThumbMetacarpalFrame frame,
            out string frameError), frameError);

        Vector3 l = geometry.ThumbProximalRestOrigin.Normalized();

        // b = h × l with no per-side flip; the frame is right-handed (XR-002 TR28.4).
        AssertAxisApproximately(l, frame.Longitudinal);
        AssertAxisApproximately(frame.Splay.Cross(frame.Longitudinal), frame.Bend);
        AssertAxisApproximately(frame.Longitudinal.Cross(frame.Bend), frame.Splay);
        AssertAxisApproximately(frame.Bend.Cross(frame.Splay), frame.Longitudinal);

        // Gate margins on the real-rig-chirality fixture (XR-002 TR28.5). The movement-alignment and
        // basis gates are construction invariants — b = (a × l)/|a × l| forces d_flex's perpendicular
        // projection onto +b — so they are asserted as holding rather than as separately reachable gates.
        Assert.True(frame.Rho >= AuthoredThumbAxisMath.MinimumRho);
        Assert.True(frame.PalmAlignmentDot >= AuthoredThumbAxisMath.MinimumPalmAlignmentDot);
        Assert.True(frame.SoftFistSwingDegrees >= AuthoredThumbAxisMath.MinimumSoftFistSwingDegrees);
        Assert.True(frame.MovementAlignmentDot >= AuthoredThumbAxisMath.MinimumMovementAlignmentDot);
        Assert.True(frame.SoftFistDirection.Dot(frame.Bend) > 0.0f);
        Assert.True(frame.AxisLongitudinalSeparationDegrees > 30.0f);

        // The fixture's thumb is ≈42° out of the palm plane like the real rig, so the normalised gate
        // passes near unity while the raw dot(b, n_palm,meta) is anatomy-capped at sin theta below the
        // 0.8 gate value: the fixture discriminates the normalised contract — a raw-dot gate, or an
        // inverted sigma pair negating both dots, fails this exact geometry (XR-002 TR28.5, A16).
        Assert.True(frame.PalmAlignmentDot > 0.99f,
            $"Expected the normalised palm dot near unity, got {frame.PalmAlignmentDot}.");
        Assert.True(
            frame.RawPalmAlignmentDot is > 0.7f and < AuthoredThumbAxisMath.MinimumPalmAlignmentDot,
            $"Expected the anatomy-capped raw dot below the raw gate, got {frame.RawPalmAlignmentDot}.");
        AssertAngleWithin(
            AuthoredThumbReferenceOracles.RealRigLongitudinalPalmAngleDegrees,
            frame.LongitudinalPalmAngleDegrees,
            0.5f);
        Assert.True(
            Mathf.Abs(Mathf.Sin(Mathf.DegToRad(frame.LongitudinalPalmAngleDegrees)) - frame.RawPalmAlignmentDot) < 0.01f,
            "The raw dot must sit on its sin theta(l, n_palm) ceiling.");
    }

    /// <summary>
    /// Mirrored sides produce mirror frames with no per-side flip: l and b mirror through M and h through -M
    /// (XR-002 TR28.4, TR28.6).
    /// </summary>
    [Fact]
    public void TryDeriveMetacarpalFrame_MirroredSides_ProduceMirrorFramesWithNoPerSideFlip()
    {
        Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.LeftThumbMetacarpalReset,
            AuthoredReferenceKeys.LeftThumbMetacarpalFlexion,
            "left metacarpal",
            out AuthoredThumbAxis leftAxis,
            out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.RightThumbMetacarpalReset,
            AuthoredReferenceKeys.RightThumbMetacarpalFlexion,
            "right metacarpal",
            out AuthoredThumbAxis rightAxis,
            out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(
            CreatePalmGeometry(mirrored: true),
            LimbSide.Left,
            out AuthoredThumbPalmPlane leftPalm,
            out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(
            CreatePalmGeometry(mirrored: false),
            LimbSide.Right,
            out AuthoredThumbPalmPlane rightPalm,
            out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveMetacarpalFrame(
            CreatePalmGeometry(mirrored: true),
            leftAxis,
            leftPalm,
            LimbSide.Left,
            out AuthoredThumbMetacarpalFrame leftFrame,
            out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveMetacarpalFrame(
            CreatePalmGeometry(mirrored: false),
            rightAxis,
            rightPalm,
            LimbSide.Right,
            out AuthoredThumbMetacarpalFrame rightFrame,
            out _));

        // The local polar vectors mirror through M = diag(-1,+1,+1) — including b, because there is no
        // per-side flip anywhere in the construction (XR-002 TR28.4, TR28.6).
        AssertAxisApproximately(AuthoredThumbAxisMath.MirrorPolar(rightFrame.Longitudinal), leftFrame.Longitudinal);
        AssertAxisApproximately(AuthoredThumbAxisMath.MirrorPolar(rightFrame.Bend), leftFrame.Bend);
        AssertAxisApproximately(-AuthoredThumbAxisMath.MirrorPolar(rightFrame.Splay), leftFrame.Splay);
        AssertAngleWithin(leftFrame.Rho, rightFrame.Rho, 1e-4f);
        AssertAngleWithin(leftFrame.SoftFistSwingDegrees, rightFrame.SoftFistSwingDegrees, 1e-3f);
    }

    /// <summary>
    /// An authored axis too close to the longitudinal fails the rho gate (XR-002 TR28.5).
    /// </summary>
    [Fact]
    public void TryDeriveMetacarpalFrame_AxisNearlyParallelToLongitudinal_FailsRhoGate()
    {
        AuthoredThumbResetGeometry geometry = CreatePalmGeometry(mirrored: true);
        Assert.True(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(geometry, LimbSide.Left, out AuthoredThumbPalmPlane palm, out _));
        Vector3 l = geometry.ThumbProximalRestOrigin.Normalized();

        // An authored axis only 10 degrees off the longitudinal projects far below sin 35 degrees.
        Vector3 nearlyParallel = ((l * Mathf.Cos(Mathf.DegToRad(10.0f)))
            + (Vector3.Right * Mathf.Sin(Mathf.DegToRad(10.0f)))).Normalized();
        AuthoredThumbAxis axis = new(
            nearlyParallel,
            30.0f,
            false,
            new Quaternion(nearlyParallel, Mathf.DegToRad(30.0f)));

        Assert.False(AuthoredThumbAxisMath.TryDeriveMetacarpalFrame(
            geometry,
            axis,
            palm,
            LimbSide.Left,
            out _,
            out string error));
        Assert.Contains("rho", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tiny authored swing whose longitudinal movement stays under 2 degrees fails the beta gate while rho
    /// passes (XR-002 TR28.5).
    /// </summary>
    [Fact]
    public void TryDeriveMetacarpalFrame_TinySwingRotation_FailsBetaGate()
    {
        AuthoredThumbResetGeometry geometry = CreatePalmGeometry(mirrored: true);
        Assert.True(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(geometry, LimbSide.Left, out AuthoredThumbPalmPlane palm, out _));
        Vector3 l = geometry.ThumbProximalRestOrigin.Normalized();

        // A 2.5-degree authored rotation about an axis just above the rho threshold moves the longitudinal
        // direction by less than the required 2-degree soft-fist swing, so beta fails while rho and the palm
        // gate pass: the axis plane is chosen so its cross product with l aligns with the palm normal.
        Vector3 splayDirection = l.Cross(palm.PalmNormalInMetacarpalLocal).Normalized();
        Vector3 nearThreshold = ((splayDirection * Mathf.Sin(Mathf.DegToRad(35.5f)))
            + (l * Mathf.Cos(Mathf.DegToRad(35.5f)))).Normalized();
        AuthoredThumbAxis axis = new(
            nearThreshold,
            2.5f,
            false,
            new Quaternion(nearThreshold, Mathf.DegToRad(2.5f)));

        Assert.True(nearThreshold.AngleTo(l) >= Mathf.DegToRad(35.0f));
        Assert.False(AuthoredThumbAxisMath.TryDeriveMetacarpalFrame(
            geometry,
            axis,
            palm,
            LimbSide.Left,
            out _,
            out string error));
        Assert.Contains("beta", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// An inverted palm normal fails the palm gate with no per-side sign rescue (XR-002 TR28.5).
    /// </summary>
    [Fact]
    public void TryDeriveMetacarpalFrame_InvertedPalmNormal_FailsPalmGateWithoutSignRescue()
    {
        AuthoredThumbResetGeometry geometry = CreatePalmGeometry(mirrored: true);
        Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.LeftThumbMetacarpalReset,
            AuthoredReferenceKeys.LeftThumbMetacarpalFlexion,
            "left metacarpal",
            out AuthoredThumbAxis axis,
            out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(geometry, LimbSide.Left, out AuthoredThumbPalmPlane palm, out _));

        AuthoredThumbPalmPlane inverted = palm with
        {
            PalmNormalInMetacarpalLocal = -palm.PalmNormalInMetacarpalLocal,
        };

        Assert.False(AuthoredThumbAxisMath.TryDeriveMetacarpalFrame(
            geometry,
            axis,
            inverted,
            LimbSide.Left,
            out _,
            out string error));
        Assert.Contains("palm alignment", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The retained real-rig regression (XR-002 TR28.2-28.5, A16): the exact forensic inputs — the real
    /// Reset/Grab keys, the retained imported rest origins, <c>o_hand = (0, 0.2050707, 0)</c> in the
    /// lower-arm frame, and <c>q_hand^R = (0, 0.7071067, 0, 0.7071067)</c> — derive the forensic metacarpal
    /// frame: normalised palm dot ≈ +0.99958 with <c>theta(l, n_palm,meta) ≈ 47.8°</c> and the raw dot on its
    /// 0.7407 <c>sin</c> ceiling, on the left and mirrored on the right, with every gate passing.
    /// Discrimination: an inverted sigma pair negates the normal (normalised dot ≈ −0.99958, gate failure),
    /// and a raw-dot gate at 0.8 rejects the anatomy-capped raw dot 0.7407 &lt; 0.8 — only the normalised
    /// contract accepts this geometry.
    /// </summary>
    [Fact]
    public void TryDeriveMetacarpalFrame_PinnedRealRigGeometry_ProducesForensicNormalisedDot()
    {
        foreach (bool mirrored in new[] { true, false })
        {
            LimbSide side = mirrored ? LimbSide.Left : LimbSide.Right;
            AuthoredThumbResetGeometry geometry = CreatePalmGeometry(mirrored);

            // The pinned hand Reset key composes wrist_H = -inverse(q_hand^R) × o_hand, so the retained
            // real-rig FK constants must reproduce the measured wrist anchor exactly (XR-002 TR28.2-28.3).
            Vector3 wristInHand = new Basis(geometry.HandResetGlobal.Inverse())
                * (geometry.SkeletonWristPosition - geometry.SkeletonHandPosition);
            Assert.True(
                wristInHand.DistanceTo(new Vector3(0.0f, -0.2050707f, 0.0f)) < 1e-4f,
                $"Expected the pinned wrist anchor (0, -0.2050707, 0) in H, got {wristInHand}.");

            Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
                mirrored
                    ? AuthoredReferenceKeys.LeftThumbMetacarpalReset
                    : AuthoredReferenceKeys.RightThumbMetacarpalReset,
                mirrored
                    ? AuthoredReferenceKeys.LeftThumbMetacarpalFlexion
                    : AuthoredReferenceKeys.RightThumbMetacarpalFlexion,
                "pinned metacarpal",
                out AuthoredThumbAxis axis,
                out string axisError), axisError);
            Assert.True(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(
                geometry, side, out AuthoredThumbPalmPlane palm, out string palmError), palmError);
            Assert.True(AuthoredThumbAxisMath.TryDeriveMetacarpalFrame(
                geometry, axis, palm, side, out AuthoredThumbMetacarpalFrame frame, out string frameError), frameError);

            // Forensic oracles (XR-002 TR28.5), identical on both sides through the exact mirror.
            AssertAngleWithin(
                AuthoredThumbReferenceOracles.RealRigNormalisedPalmDot, frame.PalmAlignmentDot, 0.001f);
            AssertAngleWithin(
                AuthoredThumbReferenceOracles.RealRigLongitudinalPalmAngleDegrees,
                frame.LongitudinalPalmAngleDegrees,
                0.1f);
            AssertAngleWithin(
                AuthoredThumbReferenceOracles.RealRigRawPalmDot, frame.RawPalmAlignmentDot, 0.001f);
        }
    }

    /// <summary>
    /// The sin 20° degeneracy floor fails closed when the thumb longitudinal sits nearly along the palm
    /// normal — <c>|n_perp| &lt; sin 20°</c> — with the measured |n_perp| in the reason, while rho and the
    /// soft-fist direction stay valid so the failure self-attributes to the floor (XR-002 TR28.5, A16).
    /// </summary>
    [Fact]
    public void TryDeriveMetacarpalFrame_LongitudinalNearPalmNormal_FailsPerpendicularFloor()
    {
        AuthoredThumbResetGeometry geometry = CreatePalmGeometry(mirrored: true);
        Assert.True(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(
            geometry, LimbSide.Left, out AuthoredThumbPalmPlane palm, out _));

        // Place l only 10 degrees off the palm normal: |n_perp| = sin 10° ≈ 0.174 < sin 20° ≈ 0.342.
        Vector3 palmNormal = palm.PalmNormalInMetacarpalLocal;
        Vector3 inPlane = palmNormal.Cross(Vector3.Right);
        if (inPlane.LengthSquared() < 1e-6f)
        {
            inPlane = palmNormal.Cross(Vector3.Up);
        }

        inPlane = inPlane.Normalized();
        Vector3 nearNormal = ((palmNormal * Mathf.Cos(Mathf.DegToRad(10.0f)))
            + (inPlane * Mathf.Sin(Mathf.DegToRad(10.0f)))).Normalized();
        AuthoredThumbResetGeometry degenerate = geometry with
        {
            ThumbProximalRestOrigin = nearNormal * 0.040f,
        };

        // The authored axis stays roughly 80 degrees off this l, so rho ≈ sin 80° passes and the floor is
        // the gate that fails.
        AuthoredThumbAxis axis = new(
            inPlane,
            30.0f,
            false,
            new Quaternion(inPlane, Mathf.DegToRad(30.0f)));

        Assert.True(Mathf.Sin(Mathf.DegToRad(10.0f)) < AuthoredThumbAxisMath.MinimumPalmNormalPerpendicularComponent);
        Assert.False(AuthoredThumbAxisMath.TryDeriveMetacarpalFrame(
            degenerate,
            axis,
            palm,
            LimbSide.Left,
            out _,
            out string error));
        Assert.Contains("perpendicular", error, StringComparison.Ordinal);
        Assert.Contains("|n_perp|", error, StringComparison.Ordinal);
        Assert.Contains("sin 20", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sign regression for the normative sigma pair (XR-002 TR28.3): on the real-rig-chirality fixture the
    /// derived palm normal is palmward — near-unity normalised dot and a positive raw dot against the
    /// authored bend — while the inverted pair's normal (the negation) fails the gate with
    /// no per-side sign rescue, on both hands.
    /// </summary>
    [Fact]
    public void TryDeriveMetacarpalFrame_NormativeSigma_PointsPalmwardWhileInvertedSigmaFails()
    {
        foreach (bool mirrored in new[] { true, false })
        {
            LimbSide side = mirrored ? LimbSide.Left : LimbSide.Right;
            AuthoredThumbResetGeometry geometry = CreatePalmGeometry(mirrored);
            Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
                mirrored
                    ? AuthoredReferenceKeys.LeftThumbMetacarpalReset
                    : AuthoredReferenceKeys.RightThumbMetacarpalReset,
                mirrored
                    ? AuthoredReferenceKeys.LeftThumbMetacarpalFlexion
                    : AuthoredReferenceKeys.RightThumbMetacarpalFlexion,
                "metacarpal",
                out AuthoredThumbAxis axis,
                out _));
            Assert.True(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(
                geometry, side, out AuthoredThumbPalmPlane palm, out _));
            Assert.True(AuthoredThumbAxisMath.TryDeriveMetacarpalFrame(
                geometry, axis, palm, side, out AuthoredThumbMetacarpalFrame frame, out _));

            // The authored bend b is the palmward reference: the normative sigma pair makes both the
            // normalised and the raw dot positive; inverting it negates both.
            Assert.True(frame.PalmAlignmentDot > 0.99f,
                $"Expected a palmward normalised dot on {side}, got {frame.PalmAlignmentDot}.");
            Assert.True(frame.RawPalmAlignmentDot > 0.7f,
                $"Expected a palmward raw dot on {side}, got {frame.RawPalmAlignmentDot}.");

            // The inverted sigma pair (sigma_Left = +1 / sigma_Right = -1) negates exactly the normal: that
            // derivation must fail closed with no sign rescue.
            AuthoredThumbPalmPlane invertedNormal = palm with
            {
                PalmNormalInMetacarpalLocal = -palm.PalmNormalInMetacarpalLocal,
            };
            Assert.False(AuthoredThumbAxisMath.TryDeriveMetacarpalFrame(
                geometry,
                axis,
                invertedNormal,
                side,
                out _,
                out string error));
            Assert.Contains("palm alignment", error, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A degenerate thumb-proximal rest origin fails the frame derivation closed (XR-002 TR28.5).
    /// </summary>
    [Fact]
    public void TryDeriveMetacarpalFrame_DegenerateRestOrigin_FailsClosed()
    {
        AuthoredThumbResetGeometry geometry = CreatePalmGeometry(mirrored: true) with
        {
            ThumbProximalRestOrigin = Vector3.Zero,
        };
        Assert.True(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(geometry, LimbSide.Left, out AuthoredThumbPalmPlane palm, out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.LeftThumbMetacarpalReset,
            AuthoredReferenceKeys.LeftThumbMetacarpalFlexion,
            "left metacarpal",
            out AuthoredThumbAxis axis,
            out _));

        Assert.False(AuthoredThumbAxisMath.TryDeriveMetacarpalFrame(
            geometry,
            axis,
            palm,
            LimbSide.Left,
            out _,
            out string error));
        Assert.Contains("degenerate", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two forms of l agree: normalise(o_thumb_proximal) equals the inverse Reset-frame expression of the
    /// FK position difference (XR-002 TR28.2, TR28.4).
    /// </summary>
    [Fact]
    public void ExpressInResetFrame_MatchesForwardKinematicEquivalenceOfL()
    {
        // l = normalise(o_thumb_proximal) is equivalently normalise(inverse(Q_meta^R) × (p_prox^R − p_meta^R))
        // because the Reset forward kinematics composes p_prox = p_meta + Q_meta × o_prox (XR-002 TR28.2/28.4).
        Quaternion metacarpalGlobal = Quaternion.Identity
            * new Quaternion(Vector3.Up, 0.6f)
            * new Quaternion(Vector3.Right, 1.1f);
        Vector3 restOrigin = new(0.004f, -0.0398f, 0.0016f);
        Vector3 metacarpalPosition = new(0.31f, 1.24f, 0.05f);
        Vector3 proximalPosition = metacarpalPosition + (new Basis(metacarpalGlobal) * restOrigin);

        AssertAxisApproximately(
            restOrigin.Normalized(),
            AuthoredThumbAxisMath.ExpressInResetFrame(
                metacarpalGlobal,
                proximalPosition - metacarpalPosition).Normalized());
    }

    /// <summary>
    /// Exactly mirrored geometry passes the bilateral mirror contract with zero residuals (XR-002 TR28.6).
    /// </summary>
    [Fact]
    public void TryValidateBilateralMirror_MirroredGeometry_PassesWithZeroResidual()
    {
        (AuthoredThumbAxis leftMetacarpal, AuthoredThumbAxis leftProximal, AuthoredThumbAxis leftDistal) = DeriveLeftAxes();
        (AuthoredThumbAxis rightMetacarpal, AuthoredThumbAxis rightProximal, AuthoredThumbAxis rightDistal) = DeriveRightAxes();
        AuthoredThumbResetGeometry left = CreatePalmGeometry(mirrored: true);
        AuthoredThumbResetGeometry right = CreatePalmGeometry(mirrored: false);
        Assert.True(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(left, LimbSide.Left, out AuthoredThumbPalmPlane leftPalm, out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(right, LimbSide.Right, out AuthoredThumbPalmPlane rightPalm, out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveMetacarpalFrame(
            left, leftMetacarpal, leftPalm, LimbSide.Left, out AuthoredThumbMetacarpalFrame leftFrame, out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveMetacarpalFrame(
            right, rightMetacarpal, rightPalm, LimbSide.Right, out AuthoredThumbMetacarpalFrame rightFrame, out _));

        Assert.True(AuthoredThumbAxisMath.TryValidateBilateralMirror(
            left,
            leftMetacarpal,
            leftProximal,
            leftDistal,
            leftFrame,
            leftPalm,
            right,
            rightMetacarpal,
            rightProximal,
            rightDistal,
            rightFrame,
            rightPalm,
            out AuthoredThumbMirrorResiduals residuals,
            out string error), error);
        Assert.True(residuals.MaximumAngularDegrees <= AuthoredThumbAxisMath.MaximumMirrorAngularDegrees);
        Assert.True(residuals.MaximumComponentNorm <= AuthoredThumbAxisMath.MaximumMirrorComponentNorm);
    }

    /// <summary>
    /// Angular, component-norm, and reference-angle mirror breaches each fail with the exact reason
    /// (XR-002 TR28.6).
    /// </summary>
    [Fact]
    public void TryValidateBilateralMirror_BrokenMirror_FailsOnEveryResidualClass()
    {
        (AuthoredThumbAxis leftMetacarpal, AuthoredThumbAxis leftProximal, AuthoredThumbAxis leftDistal) = DeriveLeftAxes();
        (AuthoredThumbAxis rightMetacarpal, AuthoredThumbAxis rightProximal, AuthoredThumbAxis rightDistal) = DeriveRightAxes();
        AuthoredThumbResetGeometry left = CreatePalmGeometry(mirrored: true);
        AuthoredThumbResetGeometry right = CreatePalmGeometry(mirrored: false);
        Assert.True(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(left, LimbSide.Left, out AuthoredThumbPalmPlane leftPalm, out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(right, LimbSide.Right, out AuthoredThumbPalmPlane rightPalm, out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveMetacarpalFrame(
            left, leftMetacarpal, leftPalm, LimbSide.Left, out AuthoredThumbMetacarpalFrame leftFrame, out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveMetacarpalFrame(
            right, rightMetacarpal, rightPalm, LimbSide.Right, out AuthoredThumbMetacarpalFrame rightFrame, out _));

        // Angular: rotate the right bend axis 0.2 degrees away from its mirror expectation.
        Quaternion perturbation = new(Vector3.Up, Mathf.DegToRad(0.2f));
        AuthoredThumbMetacarpalFrame rotated = rightFrame with
        {
            Bend = (new Basis(perturbation) * rightFrame.Bend).Normalized(),
        };
        Assert.False(AuthoredThumbAxisMath.TryValidateBilateralMirror(
            left,
            leftMetacarpal,
            leftProximal,
            leftDistal,
            leftFrame,
            leftPalm,
            right,
            rightMetacarpal,
            rightProximal,
            rightDistal,
            rotated,
            rightPalm,
            out _,
            out string angularError));
        Assert.Contains("angular", angularError, StringComparison.Ordinal);

        // Component norm: offset one position by 2e-4 (0.2 mm).
        AuthoredThumbResetGeometry shifted = right with
        {
            SkeletonHandPosition = right.SkeletonHandPosition + new Vector3(2e-4f, 0.0f, 0.0f),
        };
        Assert.False(AuthoredThumbAxisMath.TryValidateBilateralMirror(
            left,
            leftMetacarpal,
            leftProximal,
            leftDistal,
            leftFrame,
            leftPalm,
            shifted,
            rightMetacarpal,
            rightProximal,
            rightDistal,
            rightFrame,
            rightPalm,
            out _,
            out string componentError));
        Assert.Contains("component norm", componentError, StringComparison.Ordinal);

        // Reference-angle difference: 0.2 degrees between sides.
        AuthoredThumbAxis drifted = rightProximal with
        {
            ReferenceAngleDegrees = rightProximal.ReferenceAngleDegrees + 0.2f,
        };
        Assert.False(AuthoredThumbAxisMath.TryValidateBilateralMirror(
            left,
            leftMetacarpal,
            leftProximal,
            leftDistal,
            leftFrame,
            leftPalm,
            right,
            rightMetacarpal,
            drifted,
            rightDistal,
            rightFrame,
            rightPalm,
            out _,
            out string angleError));
        Assert.Contains("reference-angle", angleError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A non-unit metacarpal-global Reset rotation fails closed with the side-attributed unit-rotation reason
    /// before any residual is measured (XR-002 TR28.6).
    /// </summary>
    [Fact]
    public void TryValidateBilateralMirror_NonUnitMetacarpalResetGlobal_FailsClosedWithSideLabel()
    {
        (AuthoredThumbAxis leftMetacarpal, AuthoredThumbAxis leftProximal, AuthoredThumbAxis leftDistal) = DeriveLeftAxes();
        (AuthoredThumbAxis rightMetacarpal, AuthoredThumbAxis rightProximal, AuthoredThumbAxis rightDistal) = DeriveRightAxes();
        AuthoredThumbResetGeometry left = CreatePalmGeometry(mirrored: true);
        AuthoredThumbResetGeometry right = CreatePalmGeometry(mirrored: false);
        Assert.True(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(left, LimbSide.Left, out AuthoredThumbPalmPlane leftPalm, out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveResetPalmPlane(right, LimbSide.Right, out AuthoredThumbPalmPlane rightPalm, out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveMetacarpalFrame(
            left, leftMetacarpal, leftPalm, LimbSide.Left, out AuthoredThumbMetacarpalFrame leftFrame, out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveMetacarpalFrame(
            right, rightMetacarpal, rightPalm, LimbSide.Right, out AuthoredThumbMetacarpalFrame rightFrame, out _));

        // Length 2 instead of 1: the guard must reject it before any residual is measured.
        AuthoredThumbResetGeometry corrupt = left with
        {
            MetacarpalResetGlobal = new Quaternion(2.0f, 0.0f, 0.0f, 0.0f),
        };

        Assert.False(AuthoredThumbAxisMath.TryValidateBilateralMirror(
            corrupt,
            leftMetacarpal,
            leftProximal,
            leftDistal,
            leftFrame,
            leftPalm,
            right,
            rightMetacarpal,
            rightProximal,
            rightDistal,
            rightFrame,
            rightPalm,
            out _,
            out string error));
        Assert.Contains("Left Reset metacarpal global rotation", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// An anchored aim on <c>l</c> — the calibrated neutral — returns the neutral verbatim through the
    /// deterministic parallel branch (XR-002 TR28.7, Requirement 29 identity contract). The anchor is
    /// derived exactly as the offline replay calibrates Q0: the rotation landing the transported neutral
    /// direction on <c>l</c>.
    /// </summary>
    [Fact]
    public void TryMapThumbMetacarpalSwing_AnchoredNeutralAimOnL_ReturnsExactNeutral()
    {
        FingerAnatomicalFrame frame = CreateRuntimeFrame();
        AuthoredThumbCorrespondenceFrame correspondence = CreateIdentityCorrespondenceFrame();
        Quaternion neutral = new(-0.21418673f, 0.67388725f, 0.21418673f, 0.67388725f);

        // The identity relation transports d_w = +Y onto the correspondence longitudinal u; the anchor
        // lands that transported direction on l, so the aim is exactly l — the calibrated neutral.
        Vector3 transportedNeutral =
            (new Basis(neutral.Inverse().Normalized()) * correspondence.Longitudinal).Normalized();
        Assert.True(FingerAnatomicalMath.TryShortestArc(
            transportedNeutral,
            frame.Longitudinal,
            out Quaternion anchor));

        Assert.True(FingerAnatomicalMath.TryMapThumbMetacarpalSwing(
            Quaternion.Identity,
            correspondence,
            LimbSide.Left,
            frame,
            neutral,
            anchor,
            2.0f,
            out Quaternion rotation));
        AssertRotationApproximately(neutral, rotation);
    }

    /// <summary>
    /// A right-composed source +Y roll leaves the metacarpal output unchanged — the transfer reads only
    /// <c>d_w = S × (+Y)</c> and roll maps <c>+Y</c> onto itself (XR-002 TR28.7, A16).
    /// </summary>
    [Fact]
    public void TryMapThumbMetacarpalSwing_RightComposedSourceRoll_IsInvariant()
    {
        FingerAnatomicalFrame frame = CreateRuntimeFrame();
        AuthoredThumbCorrespondenceFrame correspondence = CreateIdentityCorrespondenceFrame();
        Quaternion neutral = new(-0.21418673f, 0.67388725f, 0.21418673f, 0.67388725f);
        Quaternion swingRelation = new Quaternion(_sourceHinge, 0.7f) * new Quaternion(_sourcePalmward, 0.4f);

        Assert.True(FingerAnatomicalMath.TryMapThumbMetacarpalSwing(
            swingRelation,
            correspondence,
            LimbSide.Left,
            frame,
            neutral,
            Quaternion.Identity,
            2.0f,
            out Quaternion swung));
        foreach (float rollAngle in new[] { 0.5f, -1.2f, 2.6f })
        {
            Quaternion rolled = swingRelation * new Quaternion(_sourceLongitudinal, rollAngle);
            Assert.True(FingerAnatomicalMath.TryMapThumbMetacarpalSwing(
                rolled,
                correspondence,
                LimbSide.Left,
                frame,
                neutral,
                Quaternion.Identity,
                2.0f,
                out Quaternion output));
            AssertRotationApproximately(swung, output);
        }
    }

    /// <summary>
    /// The written metacarpal is exactly <c>D = N × R'</c> with
    /// <c>R' = rotation(axis(R), k_eff · angle(R))</c> and <c>R = shortest_arc(l, d_0)</c>, and differs
    /// materially from <c>R' × N</c>, <c>N × Delta</c>, and <c>Delta × N</c> (XR-002 TR28.7, A16).
    /// </summary>
    [Fact]
    public void TryMapThumbMetacarpalSwing_CompositionOrder_IsNormativeLeftNeutral()
    {
        FingerAnatomicalFrame frame = CreateRuntimeFrame();
        AuthoredThumbCorrespondenceFrame correspondence = CreateIdentityCorrespondenceFrame();
        Quaternion neutral = new(-0.21418673f, 0.67388725f, 0.21418673f, 0.67388725f);
        const float gain = 2.0f;
        Quaternion relation = new Quaternion(_sourceHinge, 0.7f) * new Quaternion(_sourcePalmward, 0.4f);

        Assert.True(FingerAnatomicalMath.TryMapThumbMetacarpalSwing(
            relation,
            correspondence,
            LimbSide.Left,
            frame,
            neutral,
            Quaternion.Identity,
            gain,
            out Quaternion written));

        Assert.True(written.Normalized().AngleTo(neutral.Normalized()) > Mathf.DegToRad(5.0f));
        Assert.True(written.AngleTo(neutral.Normalized() * relation.Normalized()) > Mathf.DegToRad(5.0f));
        Assert.True(written.AngleTo(relation.Normalized() * neutral.Normalized()) > Mathf.DegToRad(5.0f));

    }

    /// <summary>
    /// An antiparallel anchored aim fails the metacarpal destination without inventing a roll axis
    /// (XR-002 TR28.7).
    /// </summary>
    [Fact]
    public void TryMapThumbMetacarpalSwing_AntiparallelAim_FailsWithoutInventedAxis()
    {
        FingerAnatomicalFrame frame = CreateRuntimeFrame();

        // The correspondence longitudinal pairs with the source +Y axis, so u = -l transports the identity
        // relation's d_w = +Y onto the antipode of l.
        AuthoredThumbCorrespondenceFrame antiparallel = new(
            -frame.Longitudinal,
            Vector3.Right,
            Vector3.Back);

        Assert.False(FingerAnatomicalMath.TryMapThumbMetacarpalSwing(
            Quaternion.Identity,
            antiparallel,
            LimbSide.Left,
            frame,
            Quaternion.Identity,
            Quaternion.Identity,
            1.0f,
            out _));
    }

    /// <summary>
    /// Signed source hinge angles map through the independent authored proximal/distal axes with the sign
    /// preserved and no shared hinge (XR-002 TR27, A10).
    /// </summary>
    [Fact]
    public void ThumbHingeMapping_SignedAngles_TravelThroughIndependentAuthoredAxes()
    {
        Vector3 proximalAxis = AuthoredThumbReferenceOracles.ProximalLeftAxis;
        Vector3 distalAxis = AuthoredThumbReferenceOracles.DistalLeftAxis;

        foreach (float theta in new[] { 0.8f, -0.45f })
        {
            Quaternion delta = new(_sourceHinge, theta);

            Assert.True(FingerAnatomicalMath.TryMapHingeDestination(
                delta,
                proximalAxis,
                Quaternion.Identity,
                out Quaternion proximal));
            Assert.True(FingerAnatomicalMath.TryMapHingeDestination(
                delta,
                distalAxis,
                Quaternion.Identity,
                out Quaternion distal));

            AssertRotationApproximately(new Quaternion(proximalAxis.Normalized(), theta), proximal);
            AssertRotationApproximately(new Quaternion(distalAxis.Normalized(), theta), distal);

            // Independent axes, never a shared hinge and never the rejected canonical +X: the axes
            // themselves differ materially, and the mapped rotations differ for the same theta.
            Assert.True(proximalAxis.Normalized().AngleTo(_sourceHinge) > Mathf.DegToRad(30.0f));
            Assert.True(distalAxis.Normalized().AngleTo(_sourceHinge) > Mathf.DegToRad(30.0f));
            Assert.True(proximalAxis.Normalized().AngleTo(distalAxis.Normalized()) > Mathf.DegToRad(5.0f));
            Assert.True(proximal.AngleTo(distal) > Mathf.DegToRad(2.0f));
        }
    }

    private static (AuthoredThumbAxis Metacarpal, AuthoredThumbAxis Proximal, AuthoredThumbAxis Distal) DeriveLeftAxes()
    {
        Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.LeftThumbMetacarpalReset,
            AuthoredReferenceKeys.LeftThumbMetacarpalFlexion,
            "left metacarpal",
            out AuthoredThumbAxis metacarpal,
            out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.LeftThumbProximalReset,
            AuthoredReferenceKeys.LeftThumbProximalFlexion,
            "left proximal",
            out AuthoredThumbAxis proximal,
            out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.LeftThumbDistalReset,
            AuthoredReferenceKeys.LeftThumbDistalFlexion,
            "left distal",
            out AuthoredThumbAxis distal,
            out _));
        return (metacarpal, proximal, distal);
    }

    private static (AuthoredThumbAxis Metacarpal, AuthoredThumbAxis Proximal, AuthoredThumbAxis Distal) DeriveRightAxes()
    {
        Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.RightThumbMetacarpalReset,
            AuthoredReferenceKeys.RightThumbMetacarpalFlexion,
            "right metacarpal",
            out AuthoredThumbAxis metacarpal,
            out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.RightThumbProximalReset,
            AuthoredReferenceKeys.RightThumbProximalFlexion,
            "right proximal",
            out AuthoredThumbAxis proximal,
            out _));
        Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
            AuthoredReferenceKeys.RightThumbDistalReset,
            AuthoredReferenceKeys.RightThumbDistalFlexion,
            "right distal",
            out AuthoredThumbAxis distal,
            out _));
        return (metacarpal, proximal, distal);
    }

    /// <summary>
    /// A representative authored metacarpal frame whose vectors live in the Reset metacarpal right-local
    /// factor, matching the synthetic reference geometry (XR-002 TR28.4, TR28.7).
    /// </summary>
    private static FingerAnatomicalFrame CreateRuntimeFrame()
    {
        Vector3 longitudinal = new Vector3(0.10f, -1.0f, 0.04f).Normalized();
        Vector3 authoredMetacarpalAxis = AuthoredThumbReferenceOracles.MetacarpalLeftAxis;
        Vector3 splay = (authoredMetacarpalAxis - (longitudinal * longitudinal.Dot(authoredMetacarpalAxis))).Normalized();
        return new FingerAnatomicalFrame(longitudinal, splay.Cross(longitudinal), splay);
    }

    /// <summary>
    /// An identity-shaped correspondence frame (XR-002 TR28.7): the palm plane axes pair with the source
    /// wrist axes as <c>u = +Y</c>, <c>t = +X</c>, <c>n_palm,H = +Z</c> — right-handed because
    /// <c>t × u = +Z = n</c> before the side-dependent pairing signs apply.
    /// </summary>
    private static AuthoredThumbCorrespondenceFrame CreateIdentityCorrespondenceFrame()
        => new(Vector3.Up, Vector3.Right, Vector3.Back);

    /// <summary>
    /// Synthetic Reset forward-kinematics geometry from the pinned reference keys and the retained real-rig
    /// FK constants (XR-002 TR28.2-28.3), so the fixture's hand-local chirality matches the real rig: the
    /// wrist/hand-parent FK origin lands at −Y in the hand-local frame (because
    /// <c>wrist_H = −inverse(q_hand^R) × o_hand</c> with <c>o_hand = +Y·0.2050707</c>), the four proximal
    /// roots extend +Y from the hand origin, and the thumb-proximal rest origin keeps the imported radial
    /// direction ≈42° out of the palm plane. The collapsed variant aligns the roots with u so the projected
    /// span ratio falls below the gate.
    /// </summary>
    private static AuthoredThumbResetGeometry CreatePalmGeometry(bool mirrored, bool collapsedSpan = false)
    {
        Quaternion upperArm = (mirrored
            ? AuthoredReferenceKeys.LeftUpperArmReset : AuthoredReferenceKeys.RightUpperArmReset).Normalized();
        Quaternion lowerArm = (mirrored
            ? AuthoredReferenceKeys.LeftLowerArmReset : AuthoredReferenceKeys.RightLowerArmReset).Normalized();
        Quaternion handKey = (mirrored
            ? AuthoredReferenceKeys.LeftHandReset : AuthoredReferenceKeys.RightHandReset).Normalized();
        Quaternion metacarpalKey = (mirrored
            ? AuthoredReferenceKeys.LeftThumbMetacarpalReset
            : AuthoredReferenceKeys.RightThumbMetacarpalReset).Normalized();

        Quaternion wristGlobal = upperArm * lowerArm;
        Quaternion handGlobal = wristGlobal * handKey;
        Quaternion metacarpalGlobal = handGlobal * metacarpalKey;

        float side = mirrored ? -1.0f : 1.0f;
        Vector3 wristPosition = new Vector3(0.2f * side, 1.4f, 0.0f)
            + (new Basis(upperArm) * new Vector3(0.0f, -0.25f, 0.0f));

        // Real-rig FK (XR-002 TR28.2): p_hand = p_wrist + Q_wrist × o_hand with o_hand = +Y·0.2050707 in the
        // hand-parent frame, which places the wrist anchor at −Y in the hand-local frame like the real rig.
        Vector3 handPosition = wristPosition
            + (new Basis(wristGlobal) * AuthoredReferenceKeys.HandRestOriginInLowerArm);

        // The retained imported hand-local proximal-root rest origins (+Y, index towards −X on the left);
        // the collapsed variant keeps them nearly collinear with u so the projected span gate fails.
        Vector3[] retainedRoots = AuthoredReferenceKeys.LeftProximalRootRestOrigins;
        var rootOrigins = new Vector3[retainedRoots.Length];
        for (int rootIndex = 0; rootIndex < retainedRoots.Length; rootIndex++)
        {
            rootOrigins[rootIndex] = collapsedSpan
                ? new Vector3((rootIndex - 1.5f) * 0.0004f * side, 0.086f + (0.004f * rootIndex), 0.0f)
                : mirrored
                    ? retainedRoots[rootIndex]
                    : AuthoredThumbAxisMath.MirrorPolar(retainedRoots[rootIndex]);
        }

        Vector3 thumbProximalRestOrigin = mirrored
            ? AuthoredReferenceKeys.LeftThumbProximalRestOrigin
            : AuthoredThumbAxisMath.MirrorPolar(AuthoredReferenceKeys.LeftThumbProximalRestOrigin);

        return new AuthoredThumbResetGeometry(
            handGlobal,
            metacarpalKey,
            metacarpalGlobal,
            thumbProximalRestOrigin,
            wristPosition,
            handPosition,
            handPosition + (new Basis(handGlobal) * rootOrigins[0]),
            handPosition + (new Basis(handGlobal) * rootOrigins[1]),
            handPosition + (new Basis(handGlobal) * rootOrigins[2]),
            handPosition + (new Basis(handGlobal) * rootOrigins[3]));
    }

    private static void AssertAngleWithin(float expectedDegrees, float actualDegrees, float toleranceDegrees)
        => Assert.True(
            Mathf.Abs(expectedDegrees - actualDegrees) <= toleranceDegrees,
            $"Expected {expectedDegrees} degrees within {toleranceDegrees}, got {actualDegrees}.");

    private static void AssertAxisApproximately(Vector3 expected, Vector3 actual)
        => Assert.True(
            expected.Normalized().AngleTo(actual.Normalized()) <= Mathf.DegToRad(AngularEpsilonDegrees),
            $"Expected axis within {AngularEpsilonDegrees} degrees of {expected}, got {actual}.");

    private static void AssertRotationApproximately(Quaternion expected, Quaternion actual)
        => Assert.True(
            expected.Normalized().AngleTo(actual.Normalized()) <= Mathf.DegToRad(AngularEpsilonDegrees),
            $"Expected rotations within {AngularEpsilonDegrees} degrees; got {expected} vs {actual}.");
}

/// <summary>
/// Pinned reference-female authored reference keys (XR-002 TR25-TR26): the exact Reset and Grab-pipe-10
/// single-key values, left side; the right side mirrors as (x, -y, -z, w) modulo the thumb-metacarpal
/// double-cover storage sign.
/// </summary>
internal static class AuthoredReferenceKeys
{
    public static readonly Quaternion LeftUpperArmReset = new(0.013452828f, 0.91521406f, -0.40274343f, 0.0f);

    public static readonly Quaternion LeftLowerArmReset = new(0.23109855f, -0.6676575f, 0.23464988f, 0.6676575f);

    public static readonly Quaternion LeftHandReset = new(1.5382916e-08f, 0.7071067f, -2.6763932e-08f, 0.7071067f);

    public static readonly Quaternion LeftThumbMetacarpalReset =
        new(-0.2141868f, 0.6738872f, 0.21418674f, 0.6738873f);

    public static readonly Quaternion LeftThumbProximalReset =
        new(-2.9802322e-08f, -1.4901161e-08f, 2.9802322e-08f, 1.0f);

    public static readonly Quaternion LeftThumbDistalReset =
        new(1.4901161e-08f, 4.4703484e-08f, 2.9802322e-08f, 1.0f);

    public static readonly Quaternion LeftThumbMetacarpalFlexion =
        new(-0.16021137f, 0.75925297f, 0.30426446f, 0.5525308f);

    public static readonly Quaternion LeftThumbProximalFlexion =
        new(0.16014078f, 0.11171712f, 0.061266482f, 0.9788364f);

    public static readonly Quaternion LeftThumbDistalFlexion =
        new(0.23050137f, 0.14565916f, 0.12815726f, 0.9535346f);

    // The right-side values are the stored (x, -y, -z, w) mirrors of the left side, except the
    // metacarpal Reset key, which carries the quaternion double-cover storage negation (XR-002 TR25).
    public static readonly Quaternion RightUpperArmReset = new(-0.013452828f, 0.91521406f, -0.40274343f, 0.0f);

    public static readonly Quaternion RightLowerArmReset = new(0.23109855f, 0.6676575f, -0.23464988f, 0.6676575f);

    public static readonly Quaternion RightHandReset = new(1.5382916e-08f, -0.7071067f, 2.6763932e-08f, 0.7071067f);

    public static readonly Quaternion RightThumbMetacarpalReset =
        new(0.2141868f, 0.6738872f, 0.21418674f, -0.6738873f);

    public static readonly Quaternion RightThumbProximalReset =
        new(-2.9802322e-08f, 1.4901161e-08f, -2.9802322e-08f, 1.0f);

    public static readonly Quaternion RightThumbDistalReset =
        new(1.4901161e-08f, -4.4703484e-08f, -2.9802322e-08f, 1.0f);

    public static readonly Quaternion RightThumbMetacarpalFlexion =
        new(0.16021137f, 0.75925297f, 0.30426446f, -0.5525308f);

    public static readonly Quaternion RightThumbProximalFlexion =
        new(0.16014078f, -0.11171712f, -0.061266482f, 0.9788364f);

    public static readonly Quaternion RightThumbDistalFlexion =
        new(0.23050137f, -0.14565916f, -0.12815726f, 0.9535346f);

    // Retained real-rig Reset FK geometry (XR-002 TR28.2-28.3): the hand rest origin in the LeftLowerArm
    // frame, the four non-thumb proximal roots' imported hand-local rest origins, and the thumb-proximal
    // imported rest origin in metacarpal-local space. Left side; the right side mirrors through
    // M = diag(-1,+1,+1). The real rig's hand-local chirality places the wrist/hand-parent FK origin at -Y
    // and the proximal roots at +Y.
    public static readonly Vector3 HandRestOriginInLowerArm = new(0.0f, 0.2050707f, 0.0f);

    public static readonly Vector3[] LeftProximalRootRestOrigins =
    [
        new Vector3(-0.036526f, 0.094007f, 0.010289f),
        new Vector3(-0.011721f, 0.096455f, 0.006210f),
        new Vector3(0.007185f, 0.093367f, 0.005249f),
        new Vector3(0.025564f, 0.086033f, 0.009411f),
    ];

    public static readonly Vector3 LeftThumbProximalRestOrigin = new(-0.0201366f, 0.0177871f, -0.0114732f);
}


/// <summary>
/// The pinned reference-female authored-axis oracle (XR-002 TR26): exact axes and reference angles measured
/// from the immutable Reset/Grab-pipe-10 single-frame keys, so asset drift stays diagnosable.
/// </summary>
internal static class AuthoredThumbReferenceOracles
{
    public const float MetacarpalAngleDegrees = 20.86f;

    public const float ProximalAngleDegrees = 23.62f;

    public const float DistalAngleDegrees = 35.07f;

    public static Vector3 MetacarpalLeftAxis => new(-0.1770f, 0.5991f, 0.7809f);

    public static Vector3 ProximalLeftAxis => new(0.7825f, 0.5460f, 0.2994f);

    public static Vector3 DistalLeftAxis => new(0.7651f, 0.4835f, 0.4254f);

    public static Vector3 MetacarpalAxis(bool mirrored)
        => mirrored ? MetacarpalLeftAxis : new Vector3(-0.1770f, -0.5991f, -0.7809f);

    public static Vector3 ProximalAxis(bool mirrored)
        => mirrored ? ProximalLeftAxis : new Vector3(0.7825f, -0.5460f, -0.2994f);

    public static Vector3 DistalAxis(bool mirrored)
        => mirrored ? DistalLeftAxis : new Vector3(0.7651f, -0.4835f, -0.4254f);

    // Forensic real-rig palm-gate oracles (XR-002 TR28.5): measured on the retained reference values with
    // the normative sigma pair and normalised gate. The raw dot sits on its sin(theta) anatomy cap (0.7407)
    // below the 0.8 gate value — only the normalised gate passes.
    public const float RealRigNormalisedPalmDot = 0.99958f;

    public const float RealRigLongitudinalPalmAngleDegrees = 47.8f;

    public const float RealRigRawPalmDot = 0.7407f;
}
