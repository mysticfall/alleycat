using AlleyCat.Mind.Attention;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Mind.Attention;

/// <summary>
/// Narrow first-principles checks of the downward fold-family prototype's pure geometry: exact
/// homogeneous FK relative to rest, fold-invariant chain-root origin, the local outgoing-axis criterion
/// (ancestors never consume a child's cap), the same-stage local-joint oracle that composes ancestor
/// rotation out of final parent/child pairs (DFP-R1), the fixed 33-sample inclusive grid with
/// smaller-alpha ties, and the sagittal fold-sign resolution on a 180°-yawed reference-rig frame.
/// These verify the maths the installed probe relies on; they do not attest installed CCD attainment.
/// </summary>
public sealed class DownwardFoldGeometryTests
{
    private const float Tolerance = 1e-5f;

    // Synthetic chain root→head mirroring the reference female's proportions: identity local rest
    // rotations with upward translations tilted forward (local +Z), like the authored rig.
    private static readonly Transform3D[] _chain =
    [
        new Transform3D(Basis.Identity, new Vector3(0f, 0.98f, 0.01f)),
        new Transform3D(Basis.Identity, new Vector3(0f, 0.0652f, 0.0131f)),
        new Transform3D(Basis.Identity, new Vector3(0f, 0.0894f, 0.0151f)),
        new Transform3D(Basis.Identity, new Vector3(0f, 0.2651f, 0.1225f)),
        new Transform3D(Basis.Identity, new Vector3(0f, 0.1022f, 0.0472f)),
    ];

    private static readonly float[] _caps =
    [
        Mathf.DegToRad(8f),
        Mathf.DegToRad(8f),
        Mathf.DegToRad(14f),
        Mathf.DegToRad(16f),
    ];

    /// <summary>The reference rig's 180°-yaw container frame: local +Z maps to world forward (−Z).</summary>
    private static Transform3D ReferenceParentWorld()
        => new(
            new Basis(
                new Vector3(-1f, 0f, 0f),
                new Vector3(0f, 1f, 0f),
                new Vector3(0f, 0f, -1f)),
            new Vector3(0.12f, 0f, -0.34f));

    private static float[] FoldsAt(float alpha)
    {
        float[] folds = new float[_caps.Length];
        for (int joint = 0; joint < _caps.Length; joint++)
        {
            folds[joint] = alpha * _caps[joint];
        }

        return folds;
    }

    /// <summary>Alpha = 0 FK reconstructs the plain rest composition exactly (sanity anchor).</summary>
    [Fact]
    public void AlphaZero_ReconstructsPlainRestComposition()
    {
        Transform3D parent = ReferenceParentWorld();
        Transform3D expected = parent;
        foreach (Transform3D local in _chain)
        {
            expected *= local;
        }

        var jointWorlds = new Transform3D[_chain.Length];
        Transform3D composed = DownwardFoldGeometry.ComposeFoldedChain(
            parent, _chain, FoldsAt(0f), jointWorlds);

        Assert.True((composed.Origin - expected.Origin).Length() < Tolerance,
            $"alpha=0 head origin {(composed.Origin - expected.Origin).Length()} from plain rest composition");
        Assert.True(composed.Basis.GetRotationQuaternion()
            .AngleTo(expected.Basis.GetRotationQuaternion()) < Tolerance);
    }

    /// <summary>
    /// Rotating a joint basis never rotates its origin about world zero: the chain-root bone's world
    /// origin is fold-invariant, and proximal joint origins move only when their own ancestors fold.
    /// </summary>
    [Fact]
    public void FoldedJointBasis_NeverMovesOriginsAboutWorldZero()
    {
        Transform3D parent = ReferenceParentWorld();
        var zeroWorlds = new Transform3D[_chain.Length];
        _ = DownwardFoldGeometry.ComposeFoldedChain(parent, _chain, FoldsAt(0f), zeroWorlds);

        // Fold only the most distal movable joint (Neck→Head): every origin except the head's stays put.
        float[] distalOnly = [0f, 0f, 0f, _caps[3]];
        var distalWorlds = new Transform3D[_chain.Length];
        _ = DownwardFoldGeometry.ComposeFoldedChain(parent, _chain, distalOnly, distalWorlds);
        for (int i = 0; i < _chain.Length - 1; i++)
        {
            Assert.True((zeroWorlds[i].Origin - distalWorlds[i].Origin).Length() < Tolerance,
                $"joint {i} origin moved {Mathf.RadToDeg((zeroWorlds[i].Origin - distalWorlds[i].Origin).Length())} " +
                "under a distal-only fold");
        }

        // Full co-forward fold at every grid sample keeps the chain-root origin exactly fixed.
        for (int sample = 0; sample <= DownwardFoldGeometry.GridSteps; sample++)
        {
            var worlds = new Transform3D[_chain.Length];
            _ = DownwardFoldGeometry.ComposeFoldedChain(
                parent, _chain, FoldsAt(sample / (float)DownwardFoldGeometry.GridSteps), worlds);
            Assert.True((worlds[0].Origin - zeroWorlds[0].Origin).Length() < Tolerance,
                $"chain-root origin moved at grid sample {sample}");
        }
    }

    /// <summary>
    /// The outgoing-axis criterion is local: the swing of a child joint depends only on its own fold, and
    /// an ancestor folding at its own full cap never consumes the child's cap. Every grid sample stays
    /// within its authored cap. (The local-vs-ancestor distinction with a genuinely composed parent/child
    /// pair is covered by <see cref="SameStageLocalDelta_ComposesAncestorRotationOut_MixedStageDoesNot"/>.)
    /// </summary>
    [Fact]
    public void OutgoingSwing_IsLocalAndWithinCapsForEveryGridSample()
    {
        for (int sample = 0; sample <= DownwardFoldGeometry.GridSteps; sample++)
        {
            float alpha = sample / (float)DownwardFoldGeometry.GridSteps;
            for (int joint = 0; joint < _caps.Length; joint++)
            {
                float fold = alpha * _caps[joint];
                float swing = DownwardFoldGeometry.OutgoingSwingRadians(_chain[joint + 1], fold);
                Assert.True(swing <= _caps[joint] + 1e-6f,
                    $"joint {joint} swing {Mathf.RadToDeg(swing)}° exceeds cap {Mathf.RadToDeg(_caps[joint])}° " +
                    $"at sample {sample}");
                Assert.True(swing <= Mathf.Abs(fold) + 1e-6f,
                    $"joint {joint} swing exceeds its own fold angle at sample {sample}");

                // Hinge departure is zero by construction for this family.
                Assert.True(DownwardFoldGeometry.HingeAxisDepartureRadians(fold) < 1e-6f);
            }
        }
    }

    /// <summary>
    /// The same-stage local oracle composes ancestor rotation out: with nonzero folds on every joint
    /// above the measured one, the final parent/child pair saved in one capture reads ONLY the joint's
    /// own fold, while pairing the same final child with a REST parent — the DFP-R1 mixed-stage bug
    /// that produced the probe's false Neck 17.188° reading — reads the ancestor-contaminated
    /// cumulative rotation. The two readings must differ by roughly the ancestor pitch.
    /// </summary>
    [Fact]
    public void SameStageLocalDelta_ComposesAncestorRotationOut_MixedStageDoesNot()
    {
        Transform3D parent = ReferenceParentWorld();
        const float ancestorAlpha = 0.7f;
        float neckFold = Mathf.DegToRad(10f);
        float[] folds =
        [
            ancestorAlpha * _caps[0],
            ancestorAlpha * _caps[1],
            ancestorAlpha * _caps[2],
            neckFold,
        ];
        var foldedWorlds = new Transform3D[_chain.Length];
        var restWorlds = new Transform3D[_chain.Length];
        _ = DownwardFoldGeometry.ComposeFoldedChain(parent, _chain, folds, foldedWorlds);
        _ = DownwardFoldGeometry.ComposeFoldedChain(parent, _chain, FoldsAt(0f), restWorlds);

        // Joint under test: Neck (root→head index 3); its child outgoing vector is Head's local rest.
        Basis sameStageDelta = DownwardFoldGeometry.SameStageLocalDelta(
            foldedWorlds[2].Basis, foldedWorlds[3].Basis, _chain[3].Basis);
        double sameStageSwing = DownwardFoldGeometry.SwingDegrees(sameStageDelta, _chain[4].Origin);
        double expectedOwnSwing = Mathf.RadToDeg(
            DownwardFoldGeometry.OutgoingSwingRadians(_chain[4], neckFold));
        Assert.Equal(expectedOwnSwing, sameStageSwing, 4);

        double signedFold = DownwardFoldGeometry.SignedHingeDegrees(
            sameStageDelta, _chain[4].Origin, DownwardFoldGeometry.LocalFoldAxis);
        Assert.Equal(Mathf.RadToDeg(neckFold), signedFold, 4);

        // Mixed stage: the identical final Neck basis against UpperChest at REST absorbs the
        // ancestors' cumulative pitch into the alleged local reading.
        Basis mixedDelta = DownwardFoldGeometry.SameStageLocalDelta(
            restWorlds[2].Basis, foldedWorlds[3].Basis, _chain[3].Basis);
        double mixedSwing = DownwardFoldGeometry.SwingDegrees(mixedDelta, _chain[4].Origin);
        double ancestorPitch = Mathf.RadToDeg(ancestorAlpha * (_caps[0] + _caps[1] + _caps[2]));
        Assert.True(mixedSwing > sameStageSwing + (0.5 * ancestorPitch),
            $"mixed-stage swing {mixedSwing}° must inflate beyond the same-stage {sameStageSwing}° by " +
            $"roughly the ancestor pitch {ancestorPitch}°");

        // A zero delta reads zero on both helpers.
        Basis zeroDelta = DownwardFoldGeometry.SameStageLocalDelta(
            restWorlds[2].Basis, restWorlds[3].Basis, _chain[3].Basis);
        Assert.Equal(0d, DownwardFoldGeometry.SwingDegrees(zeroDelta, _chain[4].Origin), 4);
        Assert.Equal(0d, DownwardFoldGeometry.SignedHingeDegrees(
            zeroDelta, _chain[4].Origin, DownwardFoldGeometry.LocalFoldAxis), 4);
    }

    /// <summary>
    /// The off-X rotation content is the rotation distance to the nearest pure local-X rotation:
    /// exactly 0 for identity, for pure X rotations of any angle (including near-zero, where an
    /// axis-direction reading is ill-conditioned), and exactly the Y/Z rotation angle for pure
    /// Y/Z rotations and for X·Y composites.
    /// </summary>
    [Fact]
    public void OffXRotationDegrees_MeasuresStableSubgroupDistance()
    {
        Assert.Equal(0d, DownwardFoldGeometry.OffXRotationDegrees(Basis.Identity), 6);
        Assert.Equal(0d, DownwardFoldGeometry.OffXRotationDegrees(
            new Basis(DownwardFoldGeometry.LocalFoldAxis, Mathf.DegToRad(10f))), 6);
        // Near-identity pure X stays 0: the metric measures content, not axis direction.
        Assert.Equal(0d, DownwardFoldGeometry.OffXRotationDegrees(
            new Basis(DownwardFoldGeometry.LocalFoldAxis, Mathf.DegToRad(0.01f))), 6);

        Assert.Equal(5d, DownwardFoldGeometry.OffXRotationDegrees(
            new Basis(Vector3.Up, Mathf.DegToRad(5f))), 6);
        Assert.Equal(2d, DownwardFoldGeometry.OffXRotationDegrees(
            new Basis(Vector3.Back, Mathf.DegToRad(2f))), 6);

        // Composite X(10°)·Y(3°): the off-X content is exactly the 3° Y component.
        Basis composite = new Basis(DownwardFoldGeometry.LocalFoldAxis, Mathf.DegToRad(10f))
            * new Basis(Vector3.Up, Mathf.DegToRad(3f));
        Assert.Equal(3d, DownwardFoldGeometry.OffXRotationDegrees(composite), 6);
    }

    /// <summary>
    /// Co-forward local-X folds pitch the head downward and forward on the 180°-yawed reference frame,
    /// resolving the fold sign to +1; the sign helper fails closed when folding has no sagittal effect.
    /// </summary>
    [Fact]
    public void ResolveForwardFoldSign_PlusXPitchesHeadDownOnYawedRig()
    {
        Transform3D parent = ReferenceParentWorld();
        float sign = DownwardFoldGeometry.ResolveForwardFoldSign(parent, _chain, _caps, out Vector3 restHead);

        Assert.Equal(1f, sign);
        var worlds = new Transform3D[_chain.Length];
        Transform3D folded = DownwardFoldGeometry.ComposeFoldedChain(
            parent, _chain, FoldsAt(1f), worlds);
        Assert.True(folded.Origin.Y < restHead.Y - 1e-4f, "folded head must drop below its rest origin");
        Assert.True(folded.Origin.Z < restHead.Z - 1e-4f, "folded head must move toward world forward (−Z)");

        // A degenerate chain whose child translations are parallel to the fold axis has no sagittal
        // effect: the helper must fail closed instead of guessing a sign.
        Transform3D[] degenerate =
        [
            new Transform3D(Basis.Identity, Vector3.Zero),
            new Transform3D(Basis.Identity, new Vector3(1f, 0f, 0f)),
            new Transform3D(Basis.Identity, new Vector3(1f, 0f, 0f)),
            new Transform3D(Basis.Identity, new Vector3(1f, 0f, 0f)),
            new Transform3D(Basis.Identity, new Vector3(1f, 0f, 0f)),
        ];
        _ = Assert.Throws<InvalidOperationException>(() =>
            DownwardFoldGeometry.ResolveForwardFoldSign(parent, degenerate, _caps, out _));
    }

    /// <summary>
    /// The fixed grid is 33 samples inclusive; selection returns the sample whose Spine→Head direction
    /// exactly matches an orbit direction constructed from that sample, saturates at the maximum fold for
    /// an orbit steeper than the family reaches, and prefers the smaller alpha on exact ties.
    /// </summary>
    [Fact]
    public void SelectClosestSpineToHeadDirection_MatchesConstructsTiesAndSaturation()
    {
        Assert.Equal(33, DownwardFoldGeometry.GridSampleCount);

        Transform3D parent = ReferenceParentWorld();
        var worlds = new Transform3D[_chain.Length];

        _ = DownwardFoldGeometry.ComposeFoldedChain(
            parent, _chain, FoldsAt(0f), worlds);
        Vector3 spineOrigin = worlds[0].Origin;

        foreach (int target in new[] { 0, 8, 17, 32 })
        {
            Transform3D head = DownwardFoldGeometry.ComposeFoldedChain(
                parent, _chain, FoldsAt(target / (float)DownwardFoldGeometry.GridSteps), worlds);
            float[] grid = new float[DownwardFoldGeometry.GridSampleCount];
            int selected = DownwardFoldGeometry.SelectClosestSpineToHeadDirection(
                parent, _chain, _caps, 1f, spineOrigin, head.Origin, worlds, grid);
            Assert.Equal(target, selected);
            Assert.Equal(0f, grid[target], 4);
        }

        // An orbit direction steeper (straight down) than the family reaches selects the maximum fold.
        Vector3 steepOrbit = spineOrigin + Vector3.Down;
        float[] steepGrid = new float[DownwardFoldGeometry.GridSampleCount];
        int steep = DownwardFoldGeometry.SelectClosestSpineToHeadDirection(
            parent, _chain, _caps, 1f, spineOrigin, steepOrbit, worlds, steepGrid);
        Assert.Equal(DownwardFoldGeometry.GridSteps, steep);

        // Exact tie between two neighbouring samples resolves to the smaller alpha: the direction
        // bisecting samples k and k+1 must select k because strict improvement is required to advance.
        Transform3D headA = DownwardFoldGeometry.ComposeFoldedChain(
            parent, _chain, FoldsAt(12 / (float)DownwardFoldGeometry.GridSteps), worlds);
        Vector3 dirA = (headA.Origin - spineOrigin).Normalized();
        Transform3D headB = DownwardFoldGeometry.ComposeFoldedChain(
            parent, _chain, FoldsAt(13 / (float)DownwardFoldGeometry.GridSteps), worlds);
        Vector3 dirB = (headB.Origin - spineOrigin).Normalized();
        Vector3 bisected = spineOrigin + (dirA + dirB).Normalized();
        float[] tieGrid = new float[DownwardFoldGeometry.GridSampleCount];
        int tie = DownwardFoldGeometry.SelectClosestSpineToHeadDirection(
            parent, _chain, _caps, 1f, spineOrigin, bisected, worlds, tieGrid);
        Assert.Equal(12, tie);
        Assert.Equal(tieGrid[12], tieGrid[13], 4);
    }

    /// <summary>
    /// The fold family's radii stay on or above the conservative radial lower bound implied by the caps
    /// (segment lengths and adjacent rest separations), matching the audit's exclusion geometry: folding
    /// never shortens the Spine→Head distance below what the constrained chain must retain.
    /// </summary>
    [Fact]
    public void FoldedRadii_RespectConservativeRadialLowerBound()
    {
        Transform3D parent = ReferenceParentWorld();
        var worlds = new Transform3D[_chain.Length];

        // Four segments (Spine→Chest→UpperChest→Neck→Head) with three adjacent separations, each bounded
        // by its rest angle plus the respective joint's outgoing swing cap (Chest, UpperChest, Neck);
        // Spine's own cap rotates the chain rigidly and cannot shrink any separation.
        float[] lengths = new float[_caps.Length];
        for (int joint = 0; joint < _caps.Length; joint++)
        {
            lengths[joint] = _chain[joint + 1].Origin.Length();
        }

        float[] adjacentBounds = new float[_caps.Length - 1];
        for (int k = 0; k < adjacentBounds.Length; k++)
        {
            adjacentBounds[k] = _chain[k + 1].Origin.Normalized().AngleTo(_chain[k + 2].Origin.Normalized())
                + _caps[k + 1];
        }

        // R² >= Σ lᵢ² + 2 Σᵢ<ⱼ lᵢ lⱼ cos(min(π, Σₖ bₖ)) over the adjacent bounds between segments i and j.
        double boundSquared = 0d;
        for (int i = 0; i < _caps.Length; i++)
        {
            boundSquared += lengths[i] * lengths[i];
            for (int j = i + 1; j < _caps.Length; j++)
            {
                float separation = 0f;
                for (int k = i; k < j; k++)
                {
                    separation += adjacentBounds[k];
                }

                boundSquared += 2d * lengths[i] * lengths[j] * MathF.Cos(MathF.Min(MathF.PI, separation));
            }
        }

        float bound = MathF.Sqrt((float)boundSquared);
        for (int sample = 0; sample <= DownwardFoldGeometry.GridSteps; sample++)
        {
            Transform3D head = DownwardFoldGeometry.ComposeFoldedChain(
                parent, _chain, FoldsAt(sample / (float)DownwardFoldGeometry.GridSteps), worlds);
            float radius = (head.Origin - worlds[0].Origin).Length();
            Assert.True(radius >= bound - 1e-4f,
                $"grid sample {sample} radius {radius} below conservative bound {bound}");
        }
    }
}
