using System.Runtime.CompilerServices;
using AlleyCat.Rigging;
using AlleyCat.XR.HandTracking;
using Godot;

namespace AlleyCat.IntegrationTests.XR;

/// <summary>
/// Test-owned canonical hand topology. Expected-value derivation uses only these explicit identities and relations,
/// never the production topology tables or mapping helpers.
/// </summary>
internal static class OpticalFingerTrackingTestTopology
{
    private const int ExpectedSourceNeutralCount = 30;
    private const float QuaternionNormalisationTolerance = 0.001f;

    /// <summary>Default authored reference resource paths (XR-002 TR25).</summary>
    public const string DefaultNeutralReferencePath = "res://assets/characters/reference/female/animations/Reset.tres";

    public const string DefaultFlexionReferencePath = "res://assets/characters/reference/female/animations/Grab-pipe-10.tres";

    public static readonly LimbSide[] Sides =
    [
        LimbSide.Left,
        LimbSide.Right,
    ];

    /// <summary>Canonical per-side destination order: thumb, index, middle, ring, then little chains.</summary>
    public static readonly XRHandJoint[] DestinationJoints =
    [
        XRHandJoint.ThumbMetacarpal,
        XRHandJoint.ThumbProximal,
        XRHandJoint.ThumbDistal,
        XRHandJoint.IndexProximal,
        XRHandJoint.IndexIntermediate,
        XRHandJoint.IndexDistal,
        XRHandJoint.MiddleProximal,
        XRHandJoint.MiddleIntermediate,
        XRHandJoint.MiddleDistal,
        XRHandJoint.RingProximal,
        XRHandJoint.RingIntermediate,
        XRHandJoint.RingDistal,
        XRHandJoint.LittleProximal,
        XRHandJoint.LittleIntermediate,
        XRHandJoint.LittleDistal,
    ];

    /// <summary>Canonical per-side non-thumb calibration and expected-value order.</summary>
    public static readonly XRHandJoint[] NonThumbDestinationJoints =
    [
        XRHandJoint.IndexProximal,
        XRHandJoint.IndexIntermediate,
        XRHandJoint.IndexDistal,
        XRHandJoint.MiddleProximal,
        XRHandJoint.MiddleIntermediate,
        XRHandJoint.MiddleDistal,
        XRHandJoint.RingProximal,
        XRHandJoint.RingIntermediate,
        XRHandJoint.RingDistal,
        XRHandJoint.LittleProximal,
        XRHandJoint.LittleIntermediate,
        XRHandJoint.LittleDistal,
    ];

    /// <summary>
    /// Test-owned identity basis correspondence, K, for the production-profile photobooth oracle.
    /// </summary>
    public static readonly Quaternion ExpectedBasisCorrespondence = Quaternion.Identity;

    /// <summary>
    /// Test-owned per-side metacarpal correspondence records (XR-002 TR45, TR28.7), duplicating the committed
    /// production profile so the thumb output oracle never obtains expected values
    /// through production profile or resource APIs.
    /// </summary>
    public static readonly Quaternion[] ExpectedMetacarpalNeutralAnchors =
    [
        new Quaternion(-0.1273963451385498f, 0.019958913326263428f, 0.25467225909233093f, 0.9583913087844849f),
        new Quaternion(-0.10855846107006073f, 0.020310375839471817f, -0.15915964543819427f, 0.9810559153556824f),
    ];

    /// <summary>The pinned per-side metacarpal swing gains K_meta: Left 2.00, Right 2.25 (XR-002 TR45).</summary>
    public static readonly float[] ExpectedMetacarpalSwingGains = [2.0f, 2.25f];

    public const float ExpectedMetacarpalHingeGateStart = 0.400f;
    public const float ExpectedMetacarpalHingeGateEnd = 0.625f;
    public const float ExpectedMetacarpalBendGateStart = 0.050f;
    public const float ExpectedMetacarpalBendGateEnd = 0.150f;

    /// <summary>
    /// Test-owned delivered non-thumb source frame axes (XR-002 TR17):
    /// longitudinal <c>l_s = +Y</c> (towards the fingertip), palmward <c>b_s = +Z</c>, and flexion hinge
    /// <c>h_s = +X</c> with physical flexion a positive twist about it, applying unchanged to both hands.
    /// </summary>
    public static readonly Vector3 SourceLongitudinal = Vector3.Up;

    public static readonly Vector3 SourcePalmward = Vector3.Back;

    public static readonly Vector3 SourceHinge = Vector3.Right;

    /// <summary>
    /// Test-owned straight-neutral source relations, S0, in canonical side-major destination order (thumb
    /// metacarpal/proximal/distal first, then the non-thumb chains). These constants intentionally duplicate the
    /// committed production profile so the output oracle never obtains expected values through production profile
    /// or resource APIs.
    /// </summary>
    public static readonly ExpectedSourceNeutral[] ExpectedSourceNeutrals =
    [
        new(LimbSide.Left, XRHandJoint.ThumbMetacarpal, new Quaternion(-0.079947028f, 0.5618452f, 0.402148143f, 0.718481256f)),
        new(LimbSide.Left, XRHandJoint.ThumbProximal, new Quaternion(0.16066605f, -0.082452026f, -0.055896017f, 0.981969307f)),
        new(LimbSide.Left, XRHandJoint.ThumbDistal, new Quaternion(-0.105460033f, 0.084218027f, 0.068658022f, 0.988469312f)),
        new(LimbSide.Left, XRHandJoint.IndexProximal, new Quaternion(0.183761645f, 0.020114948f, -0.109258861f, 0.976672692f)),
        new(LimbSide.Left, XRHandJoint.IndexIntermediate, new Quaternion(0.003242671f, -0.025568779f, -0.006858143f, 0.999644281f)),
        new(LimbSide.Left, XRHandJoint.IndexDistal, new Quaternion(-0.025892004f, -0.016465727f, -0.026737638f, 0.999171448f)),
        new(LimbSide.Left, XRHandJoint.MiddleProximal, new Quaternion(0.261092265f, -0.026238159f, -0.081508925f, 0.961508546f)),
        new(LimbSide.Left, XRHandJoint.MiddleIntermediate, new Quaternion(-0.001756608f, -0.011414507f, -0.004352703f, 0.999923836f)),
        new(LimbSide.Left, XRHandJoint.MiddleDistal, new Quaternion(-0.059239228f, -0.037404361f, -0.002833273f, 0.997538771f)),
        new(LimbSide.Left, XRHandJoint.RingProximal, new Quaternion(0.224532459f, -0.064318007f, -0.063592417f, 0.970259951f)),
        new(LimbSide.Left, XRHandJoint.RingIntermediate, new Quaternion(0.005500336f, -0.033803354f, -0.002770696f, 0.999409527f)),
        new(LimbSide.Left, XRHandJoint.RingDistal, new Quaternion(-0.009264074f, -0.003896694f, 0.029607039f, 0.999511088f)),
        new(LimbSide.Left, XRHandJoint.LittleProximal, new Quaternion(0.144475515f, 0.094214467f, 0.064075222f, 0.982926664f)),
        new(LimbSide.Left, XRHandJoint.LittleIntermediate, new Quaternion(0.015157515f, -0.040090018f, -0.042108551f, 0.998193323f)),
        new(LimbSide.Left, XRHandJoint.LittleDistal, new Quaternion(0.001357210f, 0.000917888f, 0.049402216f, 0.998777621f)),
        new(LimbSide.Right, XRHandJoint.ThumbMetacarpal, new Quaternion(-0.006335003f, -0.589948267f, -0.461939209f, 0.6622183f)),
        new(LimbSide.Right, XRHandJoint.ThumbProximal, new Quaternion(0.245730042f, 0.081178014f, 0.047528008f, 0.964763165f)),
        new(LimbSide.Right, XRHandJoint.ThumbDistal, new Quaternion(-0.043338015f, -0.083254029f, -0.063860022f, 0.993535344f)),
        new(LimbSide.Right, XRHandJoint.IndexProximal, new Quaternion(0.240030753f, -0.019368532f, 0.091644959f, 0.966235633f)),
        new(LimbSide.Right, XRHandJoint.IndexIntermediate, new Quaternion(0.004036845f, 0.025799219f, 0.007094232f, 0.999633821f)),
        new(LimbSide.Right, XRHandJoint.IndexDistal, new Quaternion(-0.027432040f, 0.016350422f, 0.026457625f, 0.999139700f)),
        new(LimbSide.Right, XRHandJoint.MiddleProximal, new Quaternion(0.303707296f, 0.030253133f, 0.083450549f, 0.948621438f)),
        new(LimbSide.Right, XRHandJoint.MiddleIntermediate, new Quaternion(-0.001689492f, 0.011366728f, 0.004200596f, 0.999925146f)),
        new(LimbSide.Right, XRHandJoint.MiddleDistal, new Quaternion(-0.057345190f, 0.037602572f, 0.003317672f, 0.997640501f)),
        new(LimbSide.Right, XRHandJoint.RingProximal, new Quaternion(0.272604421f, 0.067725959f, 0.063601906f, 0.957629794f)),
        new(LimbSide.Right, XRHandJoint.RingIntermediate, new Quaternion(0.005467540f, 0.033466948f, 0.002876236f, 0.999420731f)),
        new(LimbSide.Right, XRHandJoint.RingDistal, new Quaternion(-0.017550459f, 0.003737272f, -0.029237573f, 0.999411416f)),
        new(LimbSide.Right, XRHandJoint.LittleProximal, new Quaternion(0.227606673f, -0.092609343f, -0.043664544f, 0.968355369f)),
        new(LimbSide.Right, XRHandJoint.LittleIntermediate, new Quaternion(-0.012834835f, 0.037494415f, 0.043034932f, 0.998287248f)),
        new(LimbSide.Right, XRHandJoint.LittleDistal, new Quaternion(-0.010758433f, -0.001114397f, -0.049043396f, 0.998738084f)),
    ];

    private static IReadOnlyDictionary<(LimbSide Side, XRHandJoint Joint), Quaternion>?
        _expectedSourceNeutralByIdentity;

    /// <summary>Test stimulus order, with every tracked parent preceding its children.</summary>
    public static readonly XRHandJoint[] TrackedJoints =
    [
        XRHandJoint.Wrist,
        XRHandJoint.ThumbMetacarpal,
        XRHandJoint.ThumbProximal,
        XRHandJoint.ThumbDistal,
        XRHandJoint.IndexMetacarpal,
        XRHandJoint.IndexProximal,
        XRHandJoint.IndexIntermediate,
        XRHandJoint.IndexDistal,
        XRHandJoint.MiddleMetacarpal,
        XRHandJoint.MiddleProximal,
        XRHandJoint.MiddleIntermediate,
        XRHandJoint.MiddleDistal,
        XRHandJoint.RingMetacarpal,
        XRHandJoint.RingProximal,
        XRHandJoint.RingIntermediate,
        XRHandJoint.RingDistal,
        XRHandJoint.LittleMetacarpal,
        XRHandJoint.LittleProximal,
        XRHandJoint.LittleIntermediate,
        XRHandJoint.LittleDistal,
    ];

    private static readonly XRHandJoint[] _indexChain =
    [
        XRHandJoint.IndexProximal,
        XRHandJoint.IndexIntermediate,
        XRHandJoint.IndexDistal,
    ];

    private static readonly XRHandJoint[] _middleChain =
    [
        XRHandJoint.MiddleProximal,
        XRHandJoint.MiddleIntermediate,
        XRHandJoint.MiddleDistal,
    ];

    private static readonly XRHandJoint[] _ringChain =
    [
        XRHandJoint.RingProximal,
        XRHandJoint.RingIntermediate,
        XRHandJoint.RingDistal,
    ];

    private static readonly XRHandJoint[] _littleChain =
    [
        XRHandJoint.LittleProximal,
        XRHandJoint.LittleIntermediate,
        XRHandJoint.LittleDistal,
    ];

    public static readonly IReadOnlyList<XRHandJoint>[] NonThumbChains =
    [
        _indexChain,
        _middleChain,
        _ringChain,
        _littleChain,
    ];

    public static bool IsThumbDestination(XRHandJoint joint)
        => joint is XRHandJoint.ThumbMetacarpal or XRHandJoint.ThumbProximal or XRHandJoint.ThumbDistal;

    public static bool IsNonThumbDestination(XRHandJoint joint)
        => joint is XRHandJoint.IndexProximal
            or XRHandJoint.IndexIntermediate
            or XRHandJoint.IndexDistal
            or XRHandJoint.MiddleProximal
            or XRHandJoint.MiddleIntermediate
            or XRHandJoint.MiddleDistal
            or XRHandJoint.RingProximal
            or XRHandJoint.RingIntermediate
            or XRHandJoint.RingDistal
            or XRHandJoint.LittleProximal
            or XRHandJoint.LittleIntermediate
            or XRHandJoint.LittleDistal;

    public static IReadOnlyList<XRHandJoint> GetNonThumbChain(XRHandJoint joint)
        => joint switch
        {
            XRHandJoint.IndexProximal or XRHandJoint.IndexIntermediate or XRHandJoint.IndexDistal => _indexChain,
            XRHandJoint.MiddleProximal or XRHandJoint.MiddleIntermediate or XRHandJoint.MiddleDistal => _middleChain,
            XRHandJoint.RingProximal or XRHandJoint.RingIntermediate or XRHandJoint.RingDistal => _ringChain,
            XRHandJoint.LittleProximal or XRHandJoint.LittleIntermediate or XRHandJoint.LittleDistal => _littleChain,
            XRHandJoint.Wrist
                or XRHandJoint.ThumbMetacarpal
                or XRHandJoint.ThumbProximal
                or XRHandJoint.ThumbDistal
                or XRHandJoint.IndexMetacarpal
                or XRHandJoint.MiddleMetacarpal
                or XRHandJoint.RingMetacarpal
                or XRHandJoint.LittleMetacarpal => throw new ArgumentOutOfRangeException(
                    nameof(joint),
                    joint,
                    "Joint is not a non-thumb destination."),
            _ => throw new ArgumentOutOfRangeException(nameof(joint), joint, "Joint is not a non-thumb destination."),
        };

    public static XRHandJoint? GetDestinationParent(XRHandJoint joint)
        => joint switch
        {
            XRHandJoint.IndexProximal
                or XRHandJoint.MiddleProximal
                or XRHandJoint.RingProximal
                or XRHandJoint.LittleProximal => null,
            XRHandJoint.IndexIntermediate => XRHandJoint.IndexProximal,
            XRHandJoint.IndexDistal => XRHandJoint.IndexIntermediate,
            XRHandJoint.MiddleIntermediate => XRHandJoint.MiddleProximal,
            XRHandJoint.MiddleDistal => XRHandJoint.MiddleIntermediate,
            XRHandJoint.RingIntermediate => XRHandJoint.RingProximal,
            XRHandJoint.RingDistal => XRHandJoint.RingIntermediate,
            XRHandJoint.LittleIntermediate => XRHandJoint.LittleProximal,
            XRHandJoint.LittleDistal => XRHandJoint.LittleIntermediate,
            XRHandJoint.Wrist
                or XRHandJoint.ThumbMetacarpal
                or XRHandJoint.ThumbProximal
                or XRHandJoint.ThumbDistal
                or XRHandJoint.IndexMetacarpal
                or XRHandJoint.MiddleMetacarpal
                or XRHandJoint.RingMetacarpal
                or XRHandJoint.LittleMetacarpal => throw new ArgumentOutOfRangeException(
                    nameof(joint),
                    joint,
                    "Joint is not a non-thumb destination."),
            _ => throw new ArgumentOutOfRangeException(nameof(joint), joint, "Joint is not a non-thumb destination."),
        };

    public static XRHandJoint? GetTrackedParent(XRHandJoint joint)
        => joint switch
        {
            XRHandJoint.Wrist => null,
            XRHandJoint.ThumbMetacarpal => XRHandJoint.Wrist,
            XRHandJoint.ThumbProximal => XRHandJoint.ThumbMetacarpal,
            XRHandJoint.ThumbDistal => XRHandJoint.ThumbProximal,
            XRHandJoint.IndexMetacarpal => XRHandJoint.Wrist,
            XRHandJoint.IndexProximal => XRHandJoint.IndexMetacarpal,
            XRHandJoint.IndexIntermediate => XRHandJoint.IndexProximal,
            XRHandJoint.IndexDistal => XRHandJoint.IndexIntermediate,
            XRHandJoint.MiddleMetacarpal => XRHandJoint.Wrist,
            XRHandJoint.MiddleProximal => XRHandJoint.MiddleMetacarpal,
            XRHandJoint.MiddleIntermediate => XRHandJoint.MiddleProximal,
            XRHandJoint.MiddleDistal => XRHandJoint.MiddleIntermediate,
            XRHandJoint.RingMetacarpal => XRHandJoint.Wrist,
            XRHandJoint.RingProximal => XRHandJoint.RingMetacarpal,
            XRHandJoint.RingIntermediate => XRHandJoint.RingProximal,
            XRHandJoint.RingDistal => XRHandJoint.RingIntermediate,
            XRHandJoint.LittleMetacarpal => XRHandJoint.Wrist,
            XRHandJoint.LittleProximal => XRHandJoint.LittleMetacarpal,
            XRHandJoint.LittleIntermediate => XRHandJoint.LittleProximal,
            XRHandJoint.LittleDistal => XRHandJoint.LittleIntermediate,
            _ => throw new ArgumentOutOfRangeException(nameof(joint), joint, "Unknown hand joint."),
        };

    /// <summary>
    /// Test-owned independent implementation of the per-hand anatomical frame derivation (XR-002 TR18),
    /// written directly from the specification and never delegating to production mapping helpers.
    /// </summary>
    public static TestHandFrame DeriveHandFrame(Skeleton3D skeleton, LimbSide side)
    {
        Vector3 longitudinal = RestSegmentDirection(skeleton, side, XRHandJoint.MiddleProximal, XRHandJoint.MiddleIntermediate);
        Vector3 rootSpan = GlobalRest(skeleton, side, XRHandJoint.IndexProximal).Origin
            - GlobalRest(skeleton, side, XRHandJoint.LittleProximal).Origin;
        Vector3 projectedRootSpan = rootSpan - (longitudinal * longitudinal.Dot(rootSpan));
        Vector3 rootAxis = projectedRootSpan.Normalized();

        Vector3 resultant = Vector3.Zero;
        foreach (IReadOnlyList<XRHandJoint> chain in NonThumbChains)
        {
            Quaternion swing = DeriveChainSwing(skeleton, side, chain[0]);
            Vector3 swungIntermediate = new Basis(swing)
                * RestSegmentDirection(skeleton, side, chain[1], chain[2]);
            Vector3 curvature = swungIntermediate - (longitudinal * longitudinal.Dot(swungIntermediate));
            resultant += curvature.Normalized();
        }

        resultant /= NonThumbChains.Length;
        Vector3 hinge = rootAxis.Cross(longitudinal).Dot(resultant) > 0.0f ? rootAxis : -rootAxis;
        return new TestHandFrame(longitudinal, hinge.Cross(longitudinal), hinge);
    }

    /// <summary>
    /// Test-owned independent chain-neutral swing: the shortest arc rotating one chain's proximal segment
    /// direction onto the same-hand middle proximal direction (XR-002 TR37).
    /// </summary>
    public static Quaternion DeriveChainSwing(Skeleton3D skeleton, LimbSide side, XRHandJoint proximal)
        => IndependentShortestArc(
            RestSegmentDirection(skeleton, side, proximal, GetNonThumbChain(proximal)[1]),
            RestSegmentDirection(skeleton, side, XRHandJoint.MiddleProximal, XRHandJoint.MiddleIntermediate));

    /// <summary>
    /// Test-owned expected desired neutral global orientation <c>Q'_j</c> — the chain-neutral swing applied to
    /// the bone's global rest rotation (XR-002 TR18, TR37).
    /// </summary>
    public static Quaternion ExpectedDesiredGlobal(Skeleton3D skeleton, LimbSide side, XRHandJoint joint)
        => DeriveChainSwing(skeleton, side, GetNonThumbChain(joint)[0])
            * GlobalRestRotation(skeleton, side, joint);

    /// <summary>
    /// Test-owned expected effective neutral <c>N_j</c>: the swung desired globals recursively converted to
    /// parent-local pose rotations (XR-002 TR37).
    /// </summary>
    public static Quaternion ExpectedDestinationNeutral(Skeleton3D skeleton, LimbSide side, XRHandJoint joint)
    {
        Quaternion desired = ExpectedDesiredGlobal(skeleton, side, joint);
        XRHandJoint? destinationParent = GetDestinationParent(joint);
        Quaternion desiredParent = destinationParent is null
            ? GlobalRestRotation(skeleton, side, null)
            : ExpectedDesiredGlobal(skeleton, side, destinationParent.Value);
        return desiredParent.Inverse() * desired;
    }

    /// <summary>
    /// Test-owned sampled Reset local rotation — the thumb neutral source (XR-002 TR29), read directly from the
    /// immutable authored reference, never from a live pose or the production profile.
    /// </summary>
    public static Quaternion ExpectedThumbRestLocal(Skeleton3D skeleton, LimbSide side, XRHandJoint joint)
        => GetAuthoredThumbModel(skeleton).ResetLocal(side, joint);

    /// <summary>
    /// Test-owned independent oracle for the authored-animation Stage 1 thumb mapping (XR-002 TR24-TR29): the
    /// live parent-relative source relation transported through the anchored hand-frame correspondence —
    /// <c>d_w = S × (+Y)</c>, the measured side-dependent pairing of the source wrist axes with the Reset palm
    /// plane, the pinned per-side <c>Q0</c> anchor, and the pinned per-side <c>K_meta</c> swing gain — with
    /// independent signed hinge flexion about each joint's authored axis, using the two immutable authored
    /// references and the bound skeleton's rest geometry loaded directly — never a rest-geometry-derived
    /// destination frame, shared hinge, or synthetic tip.
    /// </summary>
    public static Quaternion ExpectedThumbRotation(
        Skeleton3D skeleton,
        LimbSide side,
        XRHandJoint joint,
        Quaternion sourceRelation,
        Quaternion sourceNeutral)
    {
        Quaternion neutral = ExpectedThumbRestLocal(skeleton, side, joint);
        AuthoredThumbModel model = GetAuthoredThumbModel(skeleton);

        if (joint == XRHandJoint.ThumbMetacarpal)
        {
            // Anchored hand-frame correspondence (XR-002 TR28.7): d_w = S × (+Y); the side-dependent pairing
            // of the source wrist axes with the Reset palm plane (u, t, n_palm,H); d = Q0 × (N⁻¹ × d_h); and
            // the shortest-arc swing from l with its angle scaled by the pinned K_meta gain.
            (Vector3 u, Vector3 t, Vector3 palmNormal) = ExpectedThumbCorrespondenceFrame(skeleton, side);
            (Vector3 l, Vector3 _, Vector3 _) = model.MetacarpalFrames[(int)side];
            int sideIndex = (int)side;
            Vector3 sourceSwing = new Basis(sourceRelation.Normalized()) * SourceLongitudinal;
            float spanSign = side == LimbSide.Left ? -1.0f : 1.0f;
            Vector3 correspondence = (t * (spanSign * sourceSwing.X))
                + (u * sourceSwing.Y)
                + (palmNormal * sourceSwing.Z);
            Vector3 aim = new Basis(ExpectedMetacarpalNeutralAnchors[sideIndex].Normalized())
                * (new Basis(neutral.Inverse().Normalized()) * correspondence);
            float aimDot = Mathf.Clamp(l.Dot(aim.Normalized()), -1.0f, 1.0f);
            if (aimDot >= 1.0f - 1e-6f)
            {
                return neutral;
            }

            Vector3 swingAxis = l.Cross(aim.Normalized()).Normalized();
            float mirroredBend = swingAxis.Dot(model.MetacarpalFrames[sideIndex].Bend)
                * (side == LimbSide.Left ? -1.0f : 1.0f);
            float gate = IndependentSmoothstep(swingAxis.Dot(model.MetacarpalFrames[sideIndex].Splay),
                    ExpectedMetacarpalHingeGateStart, ExpectedMetacarpalHingeGateEnd)
                * IndependentSmoothstep(mirroredBend,
                    ExpectedMetacarpalBendGateStart, ExpectedMetacarpalBendGateEnd);
            float effectiveGain = 1.0f + ((ExpectedMetacarpalSwingGains[sideIndex] - 1.0f) * gate);
            var gainedSwing = new Quaternion(swingAxis, effectiveGain * Mathf.Acos(aimDot));
            return neutral.Normalized() * gainedSwing;
        }

        Quaternion delta = (sourceNeutral.Inverse() * sourceRelation).Normalized();
        delta = delta.W < 0.0f ? new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W) : delta;
        Vector3 axisPart = new(delta.X, delta.Y, delta.Z);
        float p = axisPart.Dot(SourceHinge);
        float m = Mathf.Sqrt((delta.W * delta.W) + (p * p));
        if (m <= 1e-5f)
        {
            throw new InvalidOperationException($"Test thumb oracle hinge extraction degenerated for {side}/{joint}.");
        }

        float theta = 2.0f * Mathf.Atan2(p / m, delta.W / m);
        Vector3 authoredAxis = joint == XRHandJoint.ThumbProximal
            ? model.ProximalAxes[(int)side]
            : model.DistalAxes[(int)side];
        return neutral * new Quaternion(authoredAxis.Normalized(), theta);
    }

    /// <summary>
    /// The test-owned Reset palm-plane correspondence frame <c>(u, t, n_palm,H)</c> of one side in hand-local
    /// space (XR-002 TR28.3): derived from the hand bone's imported rest (the wrist anchor is the hand's own
    /// rest origin mapped through its rest basis, negated) and the four non-thumb proximal roots' imported
    /// hand-local rest origins — direct children of the hand bone on this rig, so their rest origins are
    /// already hand-local — with the normative <c>t × u</c> cross order and the sigma side sign.
    /// </summary>
    public static (Vector3 Longitudinal, Vector3 SpanAxis, Vector3 PalmNormal) ExpectedThumbCorrespondenceFrame(
        Skeleton3D skeleton,
        LimbSide side)
    {
        string prefix = side == LimbSide.Left ? "Left" : "Right";
        int handBone = RequireBone(skeleton, prefix + "Hand");
        Transform3D handRest = skeleton.GetBoneRest(handBone);
        Quaternion handRotation = handRest.Basis.Orthonormalized().GetRotationQuaternion().Normalized();
        Vector3 wrist = -(new Basis(handRotation.Inverse()) * handRest.Origin);

        var roots = new Vector3[4];
        int rootIndex = 0;
        foreach (string suffix in new[] { "IndexProximal", "MiddleProximal", "RingProximal", "LittleProximal" })
        {
            int bone = RequireBone(skeleton, prefix + suffix);
            if (skeleton.GetBoneParent(bone) != handBone)
            {
                throw new InvalidOperationException(
                    $"Test oracle expects {prefix}{suffix} to be a direct child of {prefix}Hand for the " +
                    "hand-local palm plane.");
            }

            roots[rootIndex++] = skeleton.GetBoneRest(bone).Origin;
        }

        Vector3 centroid = (roots[0] + roots[1] + roots[2] + roots[3]) / 4.0f;
        Vector3 reference = wrist * 0.5f;
        Vector3 longitudinal = (centroid - reference).Normalized();
        Vector3 span = roots[0] - roots[3];
        Vector3 spanAxis = (span - (longitudinal * longitudinal.Dot(span))).Normalized();
        Vector3 palmNormal = spanAxis.Cross(longitudinal).Normalized();
        palmNormal *= side == LimbSide.Left ? -1.0f : 1.0f;
        return (longitudinal, spanAxis, palmNormal);
    }

    /// <summary>
    /// The test-owned authored metacarpal frame <c>(l, b, h)</c> of one side, derived independently from the
    /// immutable authored references and the bound skeleton's Reset forward kinematics (XR-002 TR28).
    /// </summary>
    public static (Vector3 Longitudinal, Vector3 Bend, Vector3 Splay) ExpectedThumbMetacarpalFrame(
        Skeleton3D skeleton,
        LimbSide side)
    {
        AuthoredThumbModel model = GetAuthoredThumbModel(skeleton);
        TestThumbFrame frame = model.MetacarpalFrames[(int)side];
        return (frame.Longitudinal, frame.Bend, frame.Splay);
    }

    /// <summary>
    /// The test-owned authored axes of one side, derived independently from the immutable authored references
    /// (XR-002 TR26): metacarpal, proximal, and distal in destination bone-local space.
    /// </summary>
    public static (Vector3 Metacarpal, Vector3 Proximal, Vector3 Distal) ExpectedThumbAuthoredAxes(
        Skeleton3D skeleton,
        LimbSide side)
    {
        AuthoredThumbModel model = GetAuthoredThumbModel(skeleton);
        return (model.MetacarpalAxes[(int)side], model.ProximalAxes[(int)side], model.DistalAxes[(int)side]);
    }

    private static AuthoredThumbModel GetAuthoredThumbModel(Skeleton3D skeleton)
        => _authoredModels.GetValue(skeleton, static key => DeriveAuthoredThumbModel(key));

    private static readonly ConditionalWeakTable<Skeleton3D, AuthoredThumbModel> _authoredModels = [];

    /// <summary>
    /// Derives the authored thumb model with test-owned equations from the two immutable reference resources
    /// loaded directly — no AnimationPlayer, library registration, playback, or live-pose reads (XR-002 TR25-TR28).
    /// </summary>
    private static AuthoredThumbModel DeriveAuthoredThumbModel(Skeleton3D skeleton)
    {
        Animation neutralAnimation = ResourceLoader.Load<Animation>(DefaultNeutralReferencePath)
            ?? throw new InvalidOperationException($"Test oracle could not load {DefaultNeutralReferencePath}.");
        Animation flexionAnimation = ResourceLoader.Load<Animation>(DefaultFlexionReferencePath)
            ?? throw new InvalidOperationException($"Test oracle could not load {DefaultFlexionReferencePath}.");

        var metacarpalAxes = new Vector3[2];
        var proximalAxes = new Vector3[2];
        var distalAxes = new Vector3[2];
        var metacarpalResetLocals = new Quaternion[2];
        var proximalResetLocals = new Quaternion[2];
        var distalResetLocals = new Quaternion[2];
        var frames = new TestThumbFrame[2];

        foreach (LimbSide side in Sides)
        {
            string prefix = side == LimbSide.Left ? "Left" : "Right";
            metacarpalResetLocals[(int)side] = TryReadKey(neutralAnimation, prefix + "ThumbMetacarpal")
                ?? throw new InvalidOperationException($"Test oracle missing {prefix}ThumbMetacarpal Reset key.");
            proximalResetLocals[(int)side] = TryReadKey(neutralAnimation, prefix + "ThumbProximal")
                ?? throw new InvalidOperationException($"Test oracle missing {prefix}ThumbProximal Reset key.");
            distalResetLocals[(int)side] = TryReadKey(neutralAnimation, prefix + "ThumbDistal")
                ?? throw new InvalidOperationException($"Test oracle missing {prefix}ThumbDistal Reset key.");
            metacarpalAxes[(int)side] = AuthoredAxis(neutralAnimation, flexionAnimation, prefix + "ThumbMetacarpal");
            proximalAxes[(int)side] = AuthoredAxis(neutralAnimation, flexionAnimation, prefix + "ThumbProximal");
            distalAxes[(int)side] = AuthoredAxis(neutralAnimation, flexionAnimation, prefix + "ThumbDistal");

            // Metacarpal frame (XR-002 TR28.4): l from the thumb-proximal rest origin, h from the authored
            // axis projected perpendicular to l, b = h × l with no per-side flip. The Reset forward
            // kinematics and its gate margins are production binding gates, not mapping inputs; the palm
            // plane the correspondence consumes is derived separately (see
            // ExpectedThumbCorrespondenceFrame).
            int thumbProximalBone = RequireBone(skeleton, prefix + "ThumbProximal");
            Vector3 l = skeleton.GetBoneRest(thumbProximalBone).Origin.Normalized();
            Vector3 a = metacarpalAxes[(int)side];
            Vector3 h = (a - (l * a.Dot(l))).Normalized();
            frames[(int)side] = new TestThumbFrame(l, h.Cross(l), h);
        }

        return new AuthoredThumbModel(
            metacarpalAxes,
            proximalAxes,
            distalAxes,
            metacarpalResetLocals,
            proximalResetLocals,
            distalResetLocals,
            frames);
    }

    /// <summary>
    /// The test-owned authored-axis derivation (XR-002 TR26):
    /// <c>A = normalise(inverse(R) × hemisphere_align(F, R))</c>, <c>a = normalise(A.xyz)</c>.
    /// </summary>
    private static Vector3 AuthoredAxis(Animation neutral, Animation flexion, string boneName)
    {
        Quaternion resetKey = TryReadKey(neutral, boneName)
            ?? throw new InvalidOperationException($"Test oracle missing {boneName} Reset key.");
        Quaternion flexionKey = TryReadKey(flexion, boneName)
            ?? throw new InvalidOperationException($"Test oracle missing {boneName} flexion key.");
        Quaternion aligned = flexionKey.Normalized().Dot(resetKey.Normalized()) < 0.0f
            ? new Quaternion(-flexionKey.X, -flexionKey.Y, -flexionKey.Z, -flexionKey.W).Normalized()
            : flexionKey.Normalized();
        Quaternion authored = (resetKey.Inverse() * aligned).Normalized();
        return new Vector3(authored.X, authored.Y, authored.Z).Normalized();
    }

    /// <summary>
    /// Reads a bone's single t=0 Rotation3D key at the exact canonical path, or null when the bone carries no
    /// such track (XR-002 TR25.3-25.4).
    /// </summary>
    private static Quaternion? TryReadKey(Animation animation, string boneName)
    {
        string expectedPath = "%GeneralSkeleton:" + boneName;
        for (int track = 0; track < animation.GetTrackCount(); track++)
        {
            if (animation.TrackGetType(track) != Animation.TrackType.Rotation3D
                || !animation.TrackIsEnabled(track)
                || animation.TrackGetPath(track).ToString() != expectedPath)
            {
                continue;
            }

            return animation.TrackGetKeyCount(track) != 1 || Mathf.Abs((float)animation.TrackGetKeyTime(track, 0)) > 1e-6f
                ? throw new InvalidOperationException(
                    $"Test oracle track {expectedPath} violates the single-key t=0 contract.")
                : (Quaternion?)animation.TrackGetKeyValue(track, 0).AsQuaternion().Normalized();

        }

        return null;
    }

    private sealed record AuthoredThumbModel(
        Vector3[] MetacarpalAxes,
        Vector3[] ProximalAxes,
        Vector3[] DistalAxes,
        Quaternion[] MetacarpalResetLocals,
        Quaternion[] ProximalResetLocals,
        Quaternion[] DistalResetLocals,
        TestThumbFrame[] MetacarpalFrames)
    {
        public Quaternion ResetLocal(LimbSide side, XRHandJoint joint)
            => joint == XRHandJoint.ThumbMetacarpal
                ? MetacarpalResetLocals[(int)side]
                : joint == XRHandJoint.ThumbProximal
                    ? ProximalResetLocals[(int)side]
                    : joint == XRHandJoint.ThumbDistal
                        ? DistalResetLocals[(int)side]
                        : throw new ArgumentOutOfRangeException(nameof(joint), joint, "Not a thumb destination.");
    }

    private readonly record struct TestThumbFrame(Vector3 Longitudinal, Vector3 Bend, Vector3 Splay);

    /// <summary>
    /// Test-owned independent oracle for the constrained anatomical non-thumb mapping (XR-002 TR20-TR22):
    /// derives the identity-hemisphere-aligned delta from the source relation and profile S0, then maps a
    /// proximal through the roll-free directional swing and an intermediate/distal through the signed hinge
    /// flexion, all from independently derived frame and rest data. Thumb destinations route through the
    /// authored-animation Stage 1 thumb oracle (XR-002 TR24-TR29).
    /// </summary>
    public static Quaternion ExpectedAnatomicalRotation(
        Skeleton3D skeleton,
        LimbSide side,
        XRHandJoint joint,
        Quaternion sourceRelation,
        Quaternion sourceNeutral)
    {
        if (IsThumbDestination(joint))
        {
            return ExpectedThumbRotation(skeleton, side, joint, sourceRelation, sourceNeutral);
        }

        Quaternion delta = (sourceNeutral.Inverse() * sourceRelation).Normalized();
        delta = delta.W < 0.0f ? new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W) : delta;

        Quaternion neutral = ExpectedDestinationNeutral(skeleton, side, joint);
        if (IsProximalDestination(joint))
        {
            Quaternion desiredProximal = ExpectedDesiredGlobal(skeleton, side, joint);
            TestHandFrame frame = DeriveHandFrame(skeleton, side);
            Vector3 localLongitudinal = new Basis(desiredProximal.Inverse()) * frame.Longitudinal;
            Vector3 localPalmward = new Basis(desiredProximal.Inverse()) * frame.Palmward;
            Vector3 localHinge = new Basis(desiredProximal.Inverse()) * frame.Hinge;
            Vector3 tracked = new Basis(delta) * SourceLongitudinal;
            Vector3 mapped = (localLongitudinal * tracked.Dot(SourceLongitudinal))
                + (localPalmward * tracked.Dot(SourcePalmward))
                + (localHinge * tracked.Dot(SourceHinge));
            // At identity delta the mapped direction equals the local longitudinal exactly, so the roll-free
            // swing is the identity and the expected proximal output is the effective neutral itself.
            if (localLongitudinal.Dot(mapped.Normalized()) >= 1.0f - 1e-6f)
            {
                return neutral;
            }

            Vector3 axis = localLongitudinal.Cross(mapped.Normalized());
            return neutral * new Quaternion(axis.Normalized(), localLongitudinal.AngleTo(mapped));
        }

        Quaternion desired = ExpectedDesiredGlobal(skeleton, side, joint);
        TestHandFrame handFrame = DeriveHandFrame(skeleton, side);
        Vector3 localJointHinge = new Basis(desired.Inverse()) * handFrame.Hinge;
        Vector3 axisPart = new(delta.X, delta.Y, delta.Z);
        float p = axisPart.Dot(SourceHinge);
        float m = Mathf.Sqrt((delta.W * delta.W) + (p * p));
        if (m <= 1e-5f)
        {
            throw new InvalidOperationException($"Test oracle hinge extraction degenerated for {side}/{joint}.");
        }

        float theta = 2.0f * Mathf.Atan2(p / m, delta.W / m);
        return neutral * new Quaternion(localJointHinge.Normalized(), theta);
    }

    /// <summary>Deterministic test-owned shortest arc with a fixed fallback axis for degenerate inputs.</summary>
    public static Quaternion IndependentShortestArc(Vector3 source, Vector3 target)
    {
        Vector3 from = source.Normalized();
        Vector3 to = target.Normalized();
        float dot = Mathf.Clamp(from.Dot(to), -1.0f, 1.0f);
        if (dot >= 1.0f - 1e-6f)
        {
            return Quaternion.Identity;
        }

        Vector3 axis = from.Cross(to);
        if (axis.LengthSquared() <= 1e-10f)
        {
            // The authored integration fixtures are not antiparallel; retain a deterministic independent oracle
            // should a future real-rig rest revision reach that boundary.
            axis = from.Cross(Vector3.Right);
            if (axis.LengthSquared() <= 1e-10f)
            {
                axis = from.Cross(Vector3.Up);
            }
        }

        return new Quaternion(axis.Normalized(), Mathf.Acos(dot)).Normalized();
    }

    private static float IndependentSmoothstep(float value, float start, float end)
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
        return (3.0f * u * u) - (2.0f * u * u * u);
    }

    public static bool IsProximalDestination(XRHandJoint joint)
        => joint is XRHandJoint.IndexProximal
            or XRHandJoint.MiddleProximal
            or XRHandJoint.RingProximal
            or XRHandJoint.LittleProximal;

    public static Transform3D GlobalRest(Skeleton3D skeleton, LimbSide side, XRHandJoint joint)
        => skeleton.GetBoneGlobalRest(RequireBone(skeleton, FingerBoneName(side, joint)));

    public static Vector3 RestSegmentDirection(Skeleton3D skeleton, LimbSide side, XRHandJoint from, XRHandJoint to)
        => (GlobalRest(skeleton, side, to).Origin - GlobalRest(skeleton, side, from).Origin).Normalized();

    public static Quaternion GlobalRestRotation(Skeleton3D skeleton, LimbSide side, XRHandJoint? joint)
    {
        string boneName = joint is { } fingerJoint
            ? FingerBoneName(side, fingerJoint)
            : (side == LimbSide.Left ? "Left" : "Right") + "Hand";
        return skeleton.GetBoneGlobalRest(RequireBone(skeleton, boneName)).Basis
            .Orthonormalized().GetRotationQuaternion().Normalized();
    }

    public static string FingerBoneName(LimbSide side, XRHandJoint joint)
        => (side == LimbSide.Left ? "Left" : "Right") + joint;

    private static int RequireBone(Skeleton3D skeleton, string boneName)
    {
        int boneIndex = skeleton.FindBone(boneName);
        return boneIndex >= 0
            ? boneIndex
            : throw new InvalidOperationException($"Expected skeleton to contain {boneName}.");
    }

    public static void ValidateExpectedSourceNeutralMappings()
        => _expectedSourceNeutralByIdentity ??= BuildExpectedSourceNeutralMap();

    public static Quaternion GetExpectedSourceNeutral(LimbSide side, XRHandJoint joint)
    {
        ValidateExpectedSourceNeutralMappings();
        return _expectedSourceNeutralByIdentity!.TryGetValue((side, joint), out Quaternion sourceNeutral)
            ? sourceNeutral
            : throw new InvalidOperationException($"Test-owned S0 mapping is missing {side}/{joint}.");
    }

    private static IReadOnlyDictionary<(LimbSide Side, XRHandJoint Joint), Quaternion>
        BuildExpectedSourceNeutralMap()
    {
        if (ExpectedSourceNeutrals.Length != ExpectedSourceNeutralCount)
        {
            throw new InvalidOperationException(
                $"Test-owned S0 mapping must contain exactly {ExpectedSourceNeutralCount} records, "
                + $"but contains {ExpectedSourceNeutrals.Length}.");
        }

        var mappings = new Dictionary<(LimbSide Side, XRHandJoint Joint), Quaternion>(ExpectedSourceNeutralCount);
        foreach (ExpectedSourceNeutral record in ExpectedSourceNeutrals)
        {
            if (!IsFiniteNormalised(record.SourceNeutral))
            {
                throw new InvalidOperationException(
                    $"Test-owned S0 mapping {record.Side}/{record.Joint} is non-finite or non-normalised: "
                    + $"{record.SourceNeutral}.");
            }

            if (!mappings.TryAdd((record.Side, record.Joint), record.SourceNeutral))
            {
                throw new InvalidOperationException(
                    $"Test-owned S0 mapping contains duplicate identity {record.Side}/{record.Joint}.");
            }
        }

        int index = 0;
        foreach (LimbSide expectedSide in Sides)
        {
            foreach (XRHandJoint expectedJoint in DestinationJoints)
            {
                if (!mappings.ContainsKey((expectedSide, expectedJoint)))
                {
                    throw new InvalidOperationException(
                        $"Test-owned S0 mapping is missing canonical identity {expectedSide}/{expectedJoint}.");
                }

                ExpectedSourceNeutral record = ExpectedSourceNeutrals[index];
                if (record.Side != expectedSide || record.Joint != expectedJoint)
                {
                    throw new InvalidOperationException(
                        $"Test-owned S0 mapping order is invalid at index {index}: expected "
                        + $"{expectedSide}/{expectedJoint}, found {record.Side}/{record.Joint}.");
                }

                index++;
            }
        }

        return mappings;
    }

    private static bool IsFiniteNormalised(Quaternion value)
        => float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && float.IsFinite(value.W)
            && value.LengthSquared() > 0.0f
            && Mathf.Abs(value.LengthSquared() - 1.0f) <= QuaternionNormalisationTolerance;
}

internal readonly record struct ExpectedSourceNeutral(
    LimbSide Side,
    XRHandJoint Joint,
    Quaternion SourceNeutral);

/// <summary>
/// Test-owned per-hand anatomical frame <c>(L, B, H)</c> derived independently from the specification
/// (XR-002 TR18).
/// </summary>
internal readonly record struct TestHandFrame(Vector3 Longitudinal, Vector3 Palmward, Vector3 Hinge);
