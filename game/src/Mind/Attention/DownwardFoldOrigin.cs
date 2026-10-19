using Godot;

namespace AlleyCat.Mind.Attention;

/// <summary>
/// One evaluation of the production downward fold-family aim-origin construction. Diagnostic record
/// only — never an input to controller behaviour.
/// </summary>
/// <param name="Applied">Whether the fold family fully governed this publication (downward regime).</param>
/// <param name="Reason">Short machine-readable outcome: <c>fold_governed</c>, <c>engagement_blend</c>,
/// <c>orbit_path</c>, <c>degenerate_orbit_direction</c>, <c>not_bound</c>, or a frame-build reason.</param>
/// <param name="AlphaIndex">Nearest fixed-grid sample index of the continuous selection (diagnostic).</param>
/// <param name="Alpha">Selected continuous fold fraction in [0, 1].</param>
/// <param name="DirectionErrorDegrees">Angle between the selected Spine→Head direction and the orbit direction.</param>
/// <param name="DirectionOptimalIndex">Direction-optimal grid index before the predeclared deepening.</param>
/// <param name="DemandExtraSteps">Predeclared demand-weighted grid steps added to
/// <paramref name="DirectionOptimalIndex"/> to reach <paramref name="AlphaIndex"/>.</param>
/// <param name="OrbitSpineRadiusMillimetres">Distance from the Spine origin to the old orbit target.</param>
/// <param name="FoldSpineRadiusMillimetres">Distance from the Spine origin to the published fold origin.</param>
/// <param name="Alpha0ReconstructionMillimetres">Distance between the alpha=0 FK head origin and the captured
/// upstream head origin (sanity: both must agree to numerical precision in the supported neutral domain).</param>
/// <param name="OrbitTargetWorld">The old TR 21 orbit target this evaluation's selection referenced (direction only).</param>
/// <param name="SpineOriginWorld">Chain-root (Spine) bone origin in world space; folds never move it.</param>
/// <param name="PublishedOriginWorld">The published intent origin after pitch engagement and yaw composition.</param>
/// <param name="GridAnglesDegrees">Direction error of every fixed-grid sample, index 0…32 inclusive.</param>
/// <param name="SaturatedAtMaxAlpha">Whether the selection landed on the maximum-fold grid endpoint.</param>
internal sealed record DownwardFoldEvaluation(
    bool Applied,
    string Reason,
    int AlphaIndex,
    double Alpha,
    double DirectionErrorDegrees,
    double OrbitSpineRadiusMillimetres,
    double FoldSpineRadiusMillimetres,
    double Alpha0ReconstructionMillimetres,
    Vector3 OrbitTargetWorld,
    Vector3 SpineOriginWorld,
    Vector3 PublishedOriginWorld,
    float[] GridAnglesDegrees,
    bool SaturatedAtMaxAlpha,
    int DirectionOptimalIndex,
    int DemandExtraSteps);

/// <summary>
/// Exact homogeneous forward kinematics and fixed-grid selection for the co-forward local fold family,
/// relative to rest, in joint-local frames. Pure geometry over plain transform data — no scene access —
/// so every invariant is independently unit-testable.
/// </summary>
/// <remarks>
/// <para>
/// For chain joints ordered root→head with local rest transforms <c>T_i = (q_rest_i, t_i)</c>, one family
/// member at fold fraction <c>alpha</c> composes <c>q_i(alpha) = q_rest_i * R_i(alpha)</c> with
/// <c>R_i(alpha)</c> a rotation by <c>foldSign * alpha * cap_i</c> about the authored local X axis, and
/// local translations unchanged: <c>world_i = world_parent * Transform(q_i(alpha), t_i)</c>. Rotating a
/// joint basis never rotates its origin about world zero — each origin stays fixed in its parent frame.
/// </para>
/// <para>
/// The outgoing-axis legality criterion is local by construction: the local delta
/// <c>d_i = q_rest_i^-1 * q_i = R_i</c> acts on the child's local rest translation unit direction, so an
/// ancestor's fold never consumes a child's cap, and <c>angle(R_i * u, u) &lt;= |fold angle| &lt;= cap_i</c>
/// for every grid sample.
/// </para>
/// </remarks>
internal static class DownwardFoldGeometry
{
    /// <summary>
    /// Intersects the native incoming-local X rotation arc with the authored REST outgoing cone.
    /// For u = childRest/|childRest| and D = rest^-1 * incoming, the cone inequality is
    /// u·D·Rx(sign*t)·u = a + b*cos(t) + c*sin(t) >= cos(cap). The connected interval
    /// containing t=0 ends at phase+acos((cos(cap)-a)/radius). No Euler subtraction or
    /// acceptance allowance enters the domain. This bounds a witness, not the installed CCD result.
    /// </summary>
    internal static bool TryRestBoundedIncrement(
        Basis rest, Basis incoming, Vector3 outgoingRest, float cap, float sign, out float increment)
    {
        increment = 0f;
        if (!float.IsFinite(cap) || cap <= 0f || cap >= Mathf.Pi / 2f
            || !outgoingRest.IsFinite() || outgoingRest.LengthSquared() < 1e-12f)
        {
            return false;
        }

        Basis delta = rest.Orthonormalized().Inverse() * incoming.Orthonormalized();
        Vector3 u = outgoingRest.Normalized();
        Vector3 parallel = Vector3.Right * u.X;
        double a = Dot(u, delta * parallel);
        double b = Dot(u, delta * (u - parallel));
        double c = Dot(u, delta * (Vector3.Right.Cross(u) * sign));
        double threshold = Math.Cos(cap);
        if (!double.IsFinite(a + b + c) || a + b < threshold)
        {
            return false;
        }

        double radius = Math.Sqrt((b * b) + (c * c));
        if (radius < 1e-12d || threshold <= a - radius)
        {
            increment = cap;
            return true;
        }

        double endpoint = Math.Atan2(c, b) + Math.Acos(Math.Clamp((threshold - a) / radius, -1d, 1d));
        increment = (float)Math.Clamp(endpoint, 0d, cap);
        return true;

        static double Dot(Vector3 left, Vector3 right)
        {
            return (left.X * (double)right.X) + (left.Y * (double)right.Y) + (left.Z * (double)right.Z);
        }
    }

    /// <summary>Fixed grid step count; the verified sample count is 33 inclusive of both endpoints.</summary>
    public const int GridSteps = 32;

    /// <summary>Number of fixed-grid samples evaluated per selection, inclusive of both endpoints.</summary>
    public const int GridSampleCount = GridSteps + 1;

    /// <summary>The authored sagittal fold axis (local X — the hinge axis of the reference chain).</summary>
    public static readonly Vector3 LocalFoldAxis = Vector3.Right;

    /// <summary>
    /// Composes one fold-family member root→head with exact homogeneous transforms over the AUTHORED
    /// REST poses. Local translations are unchanged; each joint basis is the rest basis
    /// right-multiplied by its local fold rotation.
    /// </summary>
    /// <param name="chainParentWorld">World transform of the chain root's parent bone.</param>
    /// <param name="localRestRootToHead">Local rest transforms of the chain bones, root→head.</param>
    /// <param name="foldRadiansPerJoint">Fold rotation per movable joint (length chain count − 1, root→head;
    /// the head endpoint takes no fold).</param>
    /// <param name="jointWorldsRootToHead">Output buffer receiving each joint's folded world transform.</param>
    /// <returns>The folded head (chain end) world transform.</returns>
    public static Transform3D ComposeFoldedChain(
        Transform3D chainParentWorld,
        Transform3D[] localRestRootToHead,
        float[] foldRadiansPerJoint,
        Transform3D[] jointWorldsRootToHead)
        => ComposePosedFoldedChain(chainParentWorld, localRestRootToHead, foldRadiansPerJoint, jointWorldsRootToHead);

    /// <summary>
    /// Composes one fold-family member root→head over ARBITRARY local joint poses (the animation-owned
    /// upstream): each joint basis is the current local pose basis right-multiplied by its local fold
    /// rotation, and local translations stay the animation-owned values. The family re-anchors onto
    /// the supplied pose — the fold composes on top of whatever the animation currently holds, and
    /// alpha = 0 reconstructs the plain upstream composition exactly.
    /// </summary>
    /// <param name="chainParentWorld">World transform of the chain root's parent bone.</param>
    /// <param name="localPosesRootToHead">Current local poses of the chain bones, root→head
    /// (animation-owned upstream values, never solved output).</param>
    /// <param name="foldRadiansPerJoint">Fold rotation per movable joint (length chain count − 1).</param>
    /// <param name="jointWorldsRootToHead">Output buffer receiving each joint's folded world transform.</param>
    /// <returns>The folded head (chain end) world transform.</returns>
    public static Transform3D ComposePosedFoldedChain(
        Transform3D chainParentWorld,
        Transform3D[] localPosesRootToHead,
        float[] foldRadiansPerJoint,
        Transform3D[] jointWorldsRootToHead)
    {
        Transform3D world = chainParentWorld;
        for (int i = 0; i < localPosesRootToHead.Length; i++)
        {
            Basis poseBasis = localPosesRootToHead[i].Basis.Orthonormalized();
            Basis folded = i < foldRadiansPerJoint.Length && foldRadiansPerJoint[i] != 0f
                ? poseBasis * new Basis(LocalFoldAxis, foldRadiansPerJoint[i])
                : poseBasis;
            world *= new Transform3D(folded, localPosesRootToHead[i].Origin);
            jointWorldsRootToHead[i] = world;
        }

        return world;
    }

    /// <summary>
    /// Scans the fixed 33-sample grid and returns the index whose folded Spine→Head direction is closest to
    /// the orbit direction. Ties resolve to the smaller alpha (strict improvement required to advance), and
    /// no monotonicity or unimodality is assumed — every sample is evaluated.
    /// </summary>
    /// <param name="chainParentWorld">World transform of the chain root's parent bone.</param>
    /// <param name="localRestRootToHead">Local rest transforms of the chain bones, root→head.</param>
    /// <param name="halfCapRadiansPerJoint">Authored half-caps of the movable joints, root→head.</param>
    /// <param name="foldSign">Co-forward fold sign about the local axis (+1 folds the chain downward).</param>
    /// <param name="spineOriginWorld">Chain-root (Spine) bone origin in world space.</param>
    /// <param name="orbitTargetWorld">Old orbit target; only its direction from the Spine origin is used.</param>
    /// <param name="jointWorldsRootToHead">Scratch buffer for compositions.</param>
    /// <param name="gridAnglesDegrees">Output receiving every sample's direction error in degrees.</param>
    /// <returns>The selected grid index, 0…32 inclusive.</returns>
    public static int SelectClosestSpineToHeadDirection(
        Transform3D chainParentWorld,
        Transform3D[] localRestRootToHead,
        float[] halfCapRadiansPerJoint,
        float foldSign,
        Vector3 spineOriginWorld,
        Vector3 orbitTargetWorld,
        Transform3D[] jointWorldsRootToHead,
        float[] gridAnglesDegrees)
    {
        Vector3 orbitDirection = orbitTargetWorld - spineOriginWorld;
        if (orbitDirection.LengthSquared() < 1e-12f)
        {
            throw new InvalidOperationException(
                "Downward fold prototype: orbit target coincides with the Spine origin; direction undefined.");
        }

        orbitDirection = orbitDirection.Normalized();
        float[] folds = new float[halfCapRadiansPerJoint.Length];
        int best = 0;
        float bestAngle = float.MaxValue;
        for (int sample = 0; sample < GridSampleCount; sample++)
        {
            float alpha = sample / (float)GridSteps;
            for (int joint = 0; joint < folds.Length; joint++)
            {
                folds[joint] = foldSign * alpha * halfCapRadiansPerJoint[joint];
            }

            Transform3D head = ComposeFoldedChain(chainParentWorld, localRestRootToHead, folds, jointWorldsRootToHead);
            Vector3 direction = (head.Origin - spineOriginWorld).Normalized();
            float angle = direction.AngleTo(orbitDirection);
            gridAnglesDegrees[sample] = Mathf.RadToDeg(angle);
            if (angle < bestAngle)
            {
                bestAngle = angle;
                best = sample;
            }
        }

        return best;
    }

    /// <summary>
    /// Measures the local outgoing swing of one joint fold: the angle between the child's local rest
    /// translation unit direction and that direction rotated by the joint's local fold delta. This is the
    /// local criterion — ancestors' folds never enter it.
    /// </summary>
    /// <param name="childLocalRest">The child bone's local rest transform (its origin is the outgoing vector).</param>
    /// <param name="foldRadians">The joint's local fold rotation angle.</param>
    /// <returns>The outgoing swing in radians (0 for a degenerate zero-length outgoing vector).</returns>
    public static float OutgoingSwingRadians(Transform3D childLocalRest, float foldRadians)
    {
        Vector3 translation = childLocalRest.Origin;
        if (translation.LengthSquared() < 1e-18f)
        {
            return 0f;
        }

        Vector3 rest = translation.Normalized();
        Vector3 folded = (new Basis(LocalFoldAxis, foldRadians) * rest).Normalized();
        return rest.AngleTo(folded);
    }

    /// <summary>
    /// Measures the hinge-axis departure of one joint fold: the angle between the fold rotation's axis and
    /// the authored local X hinge axis, ignoring axis sign. Zero by construction for this family.
    /// </summary>
    /// <param name="foldRadians">The joint's local fold rotation angle.</param>
    /// <returns>The hinge-axis departure in radians.</returns>
    public static float HingeAxisDepartureRadians(float foldRadians)
    {
        if (Mathf.Abs(foldRadians) < 1e-9f)
        {
            return 0f;
        }

        Quaternion fold = new(LocalFoldAxis, foldRadians);
        Vector3 axis = new(fold.X, fold.Y, fold.Z);
        if (axis.LengthSquared() < 1e-18f)
        {
            return 0f;
        }

        axis = axis.Normalized();
        return Mathf.Min(axis.AngleTo(LocalFoldAxis), axis.AngleTo(-LocalFoldAxis));
    }

    /// <summary>
    /// Composes one joint's same-stage local delta from FINAL bases captured in ONE observer
    /// callback: <c>restLocal⁻¹ · (parentFinal⁻¹ · jointFinal)</c>. Both bases must come from the
    /// same snapshot — mixing a final child basis with a restored-upstream live parent getter folds
    /// ancestor rotation into the alleged local measurement (the DFP-R1 false Neck-cap oracle).
    /// </summary>
    /// <param name="parentFinal">The parent bone's final basis (same capture stage as
    /// <paramref name="jointFinal"/>).</param>
    /// <param name="jointFinal">The joint bone's final basis.</param>
    /// <param name="jointLocalRest">The joint bone's local rest basis.</param>
    /// <returns>The local rest-relative rotation delta acting in the joint's local rest frame.</returns>
    public static Basis SameStageLocalDelta(Basis parentFinal, Basis jointFinal, Basis jointLocalRest)
        => jointLocalRest.Orthonormalized().Inverse()
            * (parentFinal.Orthonormalized().Inverse() * jointFinal.Orthonormalized());

    /// <summary>
    /// Unsigned local swing of a joint delta on its outgoing axis: the angle from the child's local
    /// rest translation unit direction to its rotated image. Returns 0 for a degenerate zero-length
    /// outgoing vector.
    /// </summary>
    /// <param name="localDelta">A local rest-relative rotation delta (see
    /// <see cref="SameStageLocalDelta"/>).</param>
    /// <param name="outgoingLocalRest">The child bone's local rest translation (the outgoing vector),
    /// expressed in the joint's local rest frame.</param>
    /// <returns>The swing in degrees.</returns>
    public static double SwingDegrees(Basis localDelta, Vector3 outgoingLocalRest)
    {
        if (outgoingLocalRest.LengthSquared() < 1e-18f)
        {
            return 0d;
        }

        Vector3 unit = outgoingLocalRest.Normalized();
        return Mathf.RadToDeg(unit.AngleTo((localDelta * unit).Normalized()));
    }

    /// <summary>
    /// Signed swing of a joint delta about a hinge axis, measured on the outgoing vector:
    /// <c>atan2((u × delta·u)·axis, u·(delta·u))</c>. Positive when the delta rotates the outgoing
    /// vector by the right-hand rule about <paramref name="axis"/>. Returns 0 for a degenerate
    /// outgoing vector or an axis-parallel outgoing vector.
    /// </summary>
    /// <param name="localDelta">A local rest-relative rotation delta (see
    /// <see cref="SameStageLocalDelta"/>).</param>
    /// <param name="outgoingLocalRest">The child bone's local rest translation (the outgoing vector),
    /// expressed in the same frame <paramref name="localDelta"/> acts in.</param>
    /// <param name="axis">The hinge axis in that same frame (the authored local X for this rig).</param>
    /// <returns>The signed swing in degrees.</returns>
    public static double SignedHingeDegrees(Basis localDelta, Vector3 outgoingLocalRest, Vector3 axis)
    {
        if (outgoingLocalRest.LengthSquared() < 1e-18f || axis.LengthSquared() < 1e-18f)
        {
            return 0d;
        }

        Vector3 unit = outgoingLocalRest.Normalized();
        Vector3 rotated = (localDelta * unit).Normalized();
        Vector3 cross = unit.Cross(rotated);
        return cross.LengthSquared() < 1e-18f
            ? 0d
            : Mathf.RadToDeg(Mathf.Atan2(cross.Dot(axis.Normalized()), unit.Dot(rotated)));
    }

    /// <summary>
    /// Rotation distance from a joint delta's rotation to the nearest pure rotation about the
    /// authored local X axis: for unit quaternion <c>(w, x, y, z)</c>,
    /// <c>2·atan2(hypot(y, z), hypot(w, x))</c>. Unlike an axis-direction comparison this is
    /// well-defined at and near identity (it measures the OFF-X rotation content itself, which is 0
    /// for a pure X rotation of any angle and θ for a pure Y/Z rotation of θ), so complete windows
    /// including near-zero transit rotations can be judged without discarding phases.
    /// </summary>
    /// <param name="localDelta">A local rest-relative rotation delta (see
    /// <see cref="SameStageLocalDelta"/>).</param>
    /// <returns>The off-X rotation content in degrees.</returns>
    public static double OffXRotationDegrees(Basis localDelta)
    {
        Quaternion q = localDelta.Orthonormalized().GetRotationQuaternion();
        double offAxis = Math.Sqrt((double)(q.Y * q.Y) + (double)(q.Z * q.Z));
        double onAxis = Math.Sqrt((double)(q.W * q.W) + (double)(q.X * q.X));
        return 2d * Mathf.RadToDeg(Mathf.Atan2((float)offAxis, (float)onAxis));
    }

    /// <summary>
    /// Resolves the co-forward fold sign from the actual rig geometry: positive when rotating the movable
    /// joints about their authored local +X axis pitches the folded head origin downward in the world
    /// frame, negative when the authored axis maps the other way, and fails closed when the axis is not
    /// sagittal for the rig (no vertical change at full fold).
    /// </summary>
    /// <param name="chainParentWorld">World transform of the chain root's parent bone.</param>
    /// <param name="localRestRootToHead">Local rest transforms of the chain bones, root→head.</param>
    /// <param name="halfCapRadiansPerJoint">Authored half-caps of the movable joints, root→head.</param>
    /// <param name="restHeadOrigin">Unfolded (alpha = 0) head world origin for comparison.</param>
    /// <returns>The fold sign, +1 or −1.</returns>
    public static float ResolveForwardFoldSign(
        Transform3D chainParentWorld,
        Transform3D[] localRestRootToHead,
        float[] halfCapRadiansPerJoint,
        out Vector3 restHeadOrigin)
    {
        float[] zeroFolds = new float[halfCapRadiansPerJoint.Length];
        var jointWorlds = new Transform3D[localRestRootToHead.Length];
        restHeadOrigin = ComposeFoldedChain(chainParentWorld, localRestRootToHead, zeroFolds, jointWorlds).Origin;

        float[] fullFolds = new float[halfCapRadiansPerJoint.Length];
        for (int joint = 0; joint < fullFolds.Length; joint++)
        {
            fullFolds[joint] = halfCapRadiansPerJoint[joint];
        }

        Vector3 foldedHead = ComposeFoldedChain(chainParentWorld, localRestRootToHead, fullFolds, jointWorlds).Origin;
        return foldedHead.Y < restHeadOrigin.Y - 1e-7f
            ? 1f
            : foldedHead.Y > restHeadOrigin.Y + 1e-7f
            ? -1f
            : throw new InvalidOperationException(
            "Downward fold prototype: folding about the authored local X axis does not pitch the head downward " +
            "or upward for this rig; the sagittal fold direction is undefined (unsupported domain).");
    }
}

/// <summary>
/// One continuous fold-family selection shared by gaze prediction and intent publication: the outcome
/// of <see cref="DownwardFoldPredictionFrame.TryPredictHeadOrigin"/> for one candidate command.
/// </summary>
/// <param name="Applied">Whether the fold family fully governs this publication (the pitch engagement
/// blend is complete); partial engagement and the orbit regime report <see langword="false"/>.</param>
/// <param name="Reason">Short machine-readable outcome: <c>fold_governed</c>, <c>engagement_blend</c>,
/// <c>orbit_path</c> or <c>degenerate_orbit_direction</c>.</param>
/// <param name="Alpha">Selected continuous fold fraction in [0, 1] (direction-matched interpolation
/// plus the predeclared continuous demand deepening).</param>
/// <param name="AlphaIndex">Nearest fixed-grid sample index of <paramref name="Alpha"/> (diagnostic).</param>
/// <param name="DirectionOptimalAlpha">Direction-matched continuous fraction before the deepening.</param>
/// <param name="DirectionOptimalIndex">Nearest grid index of <paramref name="DirectionOptimalAlpha"/> (diagnostic).</param>
/// <param name="DemandExtraAlpha">Continuous demand-weighted deepening fraction added to
/// <paramref name="DirectionOptimalAlpha"/> (the strength-12 rule at its full value).</param>
/// <param name="DemandExtraSteps">Nearest-grid-step equivalent of <paramref name="DemandExtraAlpha"/> (diagnostic).</param>
/// <param name="DirectionErrorDegrees">Angle between the selected Spine→Head direction and the pitch-orbit reference direction.</param>
/// <param name="SaturatedAtMaxAlpha">Whether the selection reached the maximum-fold endpoint.</param>
/// <param name="EngageBlend">Pitch engagement s blending pitch-orbit and fold displacements before yaw,
/// continuous from zero (orbit) to one (fold) over the engagement band.</param>
/// <param name="CommandYawRadians">The candidate command yaw this selection resolved.</param>
/// <param name="CommandPitchRadians">The candidate command pitch this selection resolved.</param>
/// <param name="HeadOriginEnvelope">The blended, yaw-composed head origin in the frame's declared coordinates.</param>
/// <param name="FoldPointEnvelope">The pure fold-family FK member at <paramref name="Alpha"/> (diagnostic).</param>
/// <param name="OrbitTargetEnvelope">The separate full TR 21 orbit target for the same command (diagnostic).</param>
/// <param name="SpineOriginEnvelope">Chain-root (Spine) bone origin in the frame's declared coordinates.</param>
public readonly record struct DownwardFoldSelection(
    bool Applied,
    string Reason,
    double Alpha,
    int AlphaIndex,
    double DirectionOptimalAlpha,
    int DirectionOptimalIndex,
    double DemandExtraAlpha,
    int DemandExtraSteps,
    double DirectionErrorDegrees,
    bool SaturatedAtMaxAlpha,
    double EngageBlend,
    double CommandYawRadians,
    double CommandPitchRadians,
    Vector3 HeadOriginEnvelope,
    Vector3 FoldPointEnvelope,
    Vector3 OrbitTargetEnvelope,
    Vector3 SpineOriginEnvelope);

/// <summary>
/// Per-evaluation shared geometry built from the constrained chain's co-forward fold family, expressed in
/// the character envelope frame over the ANIMATION-OWNED upstream pose. This is the SINGLE
/// construction both the coupled gaze prediction (<see cref="GeometricGazeSaturation"/>) and the
/// controller's published intent origin consume: for the same candidate command they resolve the same
/// origin by construction, so predicted eye geometry and the published pose can never diverge onto
/// different virtual geometries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Animated re-anchoring.</b> Local poses and translations remain animation-owned. Each incoming
/// X-rotation arc is intersected with its authored rest-relative outgoing cone before constructing
/// the family. Alpha zero reconstructs upstream; the resulting FK witness does not guarantee that
/// the unchanged position-only CCD solver selects those same local rotations. Final copy/twist
/// safety and actual Head attainment require independent installed measurements.
/// </para>
/// <para>
/// <b>Continuous selection.</b> The fold fraction is continuous in the command: the direction-matched
/// fraction interpolates between the two fixed-grid samples whose sagittal sweep angle brackets the
/// pitch-orbit reference direction (the 33-sample grid certifies the family and supplies the sweep
/// curve), then the predeclared user-selected strength-14 demand deepening adds up to
/// <see cref="ExtraStepsAtFullDemand"/> grid steps' worth of continuous fraction, ramped from the
/// authored eye-comfort band to the authored head-envelope boundary. The grid's discrete steps are
/// therefore interpolation knots, not published poses: the resolved origin moves continuously with
/// the command.
/// </para>
/// <para>
/// <b>Compatibility publication.</b> Engagement blends the pitch-orbit displacement with the selected
/// fold-point displacement about the pivot, then yaw rotates that blend. Non-downward commands retain
/// the orbit. The blended/yawed publication is not generally a family FK point: neither a rest-safe
/// positional witness nor final-pose safety is established by this compatibility recovery.
/// </para>
/// </remarks>
public sealed class DownwardFoldPredictionFrame
{
    /// <summary>
    /// Predeclared user-selected deepening strength: grid steps' worth of continuous fold fraction
    /// added to the direction-matched selection at the authored head-envelope demand (fewer,
    /// proportionally, for shallower commands beyond the authored eye-comfort band; none within it).
    /// Fixed implementation strength for the user-requested modest forward-bend refinement; not a
    /// runtime tuning input.
    /// </summary>
    public const int ExtraStepsAtFullDemand = 14;

    /// <summary>
    /// Pitch engagement band: the displacement blend uses the orbit for non-downward pitch and reaches
    /// the selected fold displacement at this downward magnitude, before yaw composition.
    /// </summary>
    public const double PitchEngageRadians = 0.0087266458; // 0.5°

    /// <summary>
    /// Resolves the shared head origin for one candidate command over this frame's animation-anchored
    /// family. The resolution is defined for EVERY roll-free command; a degenerate pitch-orbit
    /// reference direction (pathological geometry) reports false and the caller keeps its unchanged
    /// pivot-orbit construction.
    /// </summary>
    /// <param name="candidateYawRadians">Candidate command yaw in the envelope frame.</param>
    /// <param name="candidatePitchRadians">Candidate command pitch in the envelope frame.</param>
    /// <param name="headOrigin">The resolved head origin in the frame's coordinates.</param>
    /// <param name="selection">The full selection record (orbit/fold diagnostics included).</param>
    /// <returns>Whether the frame's resolution applies (false only for degenerate reference geometry).</returns>
    public bool TryPredictHeadOrigin(
        double candidateYawRadians,
        double candidatePitchRadians,
        out Vector3 headOrigin,
        out DownwardFoldSelection selection)
    {
        selection = ResolveSelection(candidateYawRadians, candidatePitchRadians, gridAnglesDegrees: null);
        headOrigin = selection.HeadOriginEnvelope;
        return selection.Reason != "degenerate_orbit_direction";
    }

    /// <summary>
    /// Publication-path variant of <see cref="TryPredictHeadOrigin"/> that also fills every grid
    /// sample's sweep angle in degrees for diagnostics; the selection itself is identical.
    /// </summary>
    /// <param name="candidateYawRadians">Command yaw in the envelope frame.</param>
    /// <param name="candidatePitchRadians">Command pitch in the envelope frame.</param>
    /// <param name="headOrigin">The resolved head origin in the frame's coordinates.</param>
    /// <param name="selection">The full selection record.</param>
    /// <param name="gridAnglesDegrees">Buffer (33 entries) receiving each sample's sweep angle.</param>
    /// <returns>Whether the frame's resolution applies.</returns>
    public bool TryPredictHeadOriginDetailed(
        double candidateYawRadians,
        double candidatePitchRadians,
        out Vector3 headOrigin,
        out DownwardFoldSelection selection,
        float[] gridAnglesDegrees)
    {
        selection = ResolveSelection(candidateYawRadians, candidatePitchRadians, gridAnglesDegrees);
        headOrigin = selection.HeadOriginEnvelope;
        return selection.Reason != "degenerate_orbit_direction";
    }

    /// <summary>Chain-root (Spine) bone origin in the frame's coordinates; folds never move it.</summary>
    public Vector3 SpineOrigin
    {
        get;
    }

    /// <summary>Fixed-grid fold-family head origins, sample 0…32, in the frame's coordinates.</summary>
    public IReadOnlyList<Vector3> GridHeadOrigins => _gridHeadOrigins;

    /// <summary>Upstream head origin <c>h</c> in the frame's coordinates.</summary>
    public Vector3 UpstreamHeadOrigin
    {
        get;
    }

    /// <summary>Upstream pivot position <c>p</c> in the frame's coordinates.</summary>
    public Vector3 PivotPosition
    {
        get;
    }

    /// <summary>Upstream face rotation <c>U</c> in the frame's coordinates.</summary>
    public Basis UpstreamFaceBasis
    {
        get;
    }

    /// <summary>
    /// The exact inverse transport used to build this frame: maps frame coordinates back to world. The
    /// published intent origin is <c>WorldFromEnvelope * HeadOriginEnvelope</c>, so publication and
    /// prediction share one geometry without any second evaluation.
    /// </summary>
    public Transform3D WorldFromEnvelope
    {
        get;
    }

    /// <summary>Alpha = 0 FK reconstruction error against the captured upstream head origin, millimetres.</summary>
    public double Alpha0ReconstructionMillimetres
    {
        get;
    }

    /// <summary>
    /// Bounded cost diagnostics: number of selection scans performed by this frame (each a fixed
    /// 33-sample bracket comparison plus one continuous-alpha FK composition — never an adaptive
    /// search).
    /// </summary>
    public long SelectionScans
    {
        get;
        private set;
    }

    private readonly Transform3D[] _localPoses;
    private readonly float[] _halfCapRadians;
    private readonly Vector3[] _gridHeadOrigins;
    private readonly double[] _gridUnitDirections;
    private readonly double[] _gridSweepAngles;
    private readonly Transform3D[] _scratchJointWorlds;
    private readonly float[] _scratchFolds;
    private readonly Vector3 _upstreamHeadOrigin;
    private readonly Vector3 _pivotPosition;
    private readonly Basis _upstreamFaceBasis;
    private readonly double _comfortConeDownRadians;
    private readonly double _envelopeDownRadians;

    internal Transform3D ChainParentEnvelope
    {
        get;
    }
    internal IReadOnlyList<Transform3D> LocalPoses => _localPoses;
    internal IReadOnlyList<float> AvailableIncrements => _halfCapRadians;
    internal float FoldSign
    {
        get;
    }

    internal DownwardFoldPredictionFrame(
        Transform3D chainParentEnvelope,
        Transform3D[] localPoses,
        float[] halfCapRadians,
        float foldSign,
        Vector3[] gridHeadOrigins,
        double[] gridUnitDirections,
        double[] gridSweepAngles,
        Vector3 spineOrigin,
        Vector3 upstreamHeadOrigin,
        Vector3 pivotPosition,
        Basis upstreamFaceBasis,
        Transform3D worldFromEnvelope,
        double comfortConeDownRadians,
        double envelopeDownRadians,
        double alpha0ReconstructionMillimetres)
    {
        ChainParentEnvelope = chainParentEnvelope;
        _localPoses = localPoses;
        _halfCapRadians = halfCapRadians;
        FoldSign = foldSign;
        _gridHeadOrigins = gridHeadOrigins;
        _gridUnitDirections = gridUnitDirections;
        _gridSweepAngles = gridSweepAngles;
        _scratchJointWorlds = new Transform3D[localPoses.Length];
        _scratchFolds = new float[halfCapRadians.Length];
        _upstreamHeadOrigin = upstreamHeadOrigin;
        _pivotPosition = pivotPosition;
        _upstreamFaceBasis = upstreamFaceBasis;
        _comfortConeDownRadians = comfortConeDownRadians;
        _envelopeDownRadians = envelopeDownRadians;
        SpineOrigin = spineOrigin;
        UpstreamHeadOrigin = upstreamHeadOrigin;
        PivotPosition = pivotPosition;
        UpstreamFaceBasis = upstreamFaceBasis;
        WorldFromEnvelope = worldFromEnvelope;
        Alpha0ReconstructionMillimetres = alpha0ReconstructionMillimetres;
    }

    /// <summary>Composes the family FK at a continuous fold fraction (exact joint-local composition).</summary>
    internal Vector3 ComposeFamilyOrigin(double alpha)
    {
        for (int joint = 0; joint < _scratchFolds.Length; joint++)
        {
            _scratchFolds[joint] = FoldSign * (float)alpha * _halfCapRadians[joint];
        }

        return DownwardFoldGeometry.ComposePosedFoldedChain(
            ChainParentEnvelope, _localPoses, _scratchFolds, _scratchJointWorlds).Origin;
    }

    private DownwardFoldSelection ResolveSelection(
        double candidateYawRadians,
        double candidatePitchRadians,
        float[]? gridAnglesDegrees)
    {
        SelectionScans++;

        // Pitch-only orbit displacement V = Rx(pitch) · Uᵀ · (h − p): the TR 21 lever rotation with the
        // command yaw peeled off, so the full orbit target is p + Ry(yaw) · V for every command.
        (double ox, double oy, double oz) = ApplyUpstreamTransposedThenPitch(
            candidatePitchRadians, _upstreamHeadOrigin - _pivotPosition);

        // Sagittal reference direction for the direction-matched fraction: the pitch-only orbit target
        // seen from the chain-root origin (yaw-free by construction).
        double refX = _pivotPosition.X + ox - SpineOrigin.X;
        double refY = _pivotPosition.Y + oy - SpineOrigin.Y;
        double refZ = _pivotPosition.Z + oz - SpineOrigin.Z;
        double refLengthSquared = (refX * refX) + (refY * refY) + (refZ * refZ);
        if (refLengthSquared < 1e-12d)
        {
            return Degenerate(candidateYawRadians, candidatePitchRadians);
        }

        double inverseRef = 1d / Math.Sqrt(refLengthSquared);
        refX *= inverseRef;
        refY *= inverseRef;
        refZ *= inverseRef;

        // Sweep angle of the reference direction against the alpha = 0 family direction, signed about
        // the envelope X axis: the same parameter the grid sweep curve is tabulated in.
        double referenceSweep = SignedSweepAngle(_gridUnitDirections, 0, refX, refY, refZ);

        // Continuous direction-matched fraction: linear interpolation of the monotone sweep curve
        // between the bracketing grid samples, clamped to the certified family range.
        double directionOptimal = InterpolateSweepToAlpha(referenceSweep, gridAnglesDegrees);

        // Predeclared user-selected demand deepening (strength 14), continuous in the DOWNWARD pitch
        // demand only — upward commands never deepen the downward family.
        double downwardRadians = Math.Max(0d, -candidatePitchRadians);
        double bandRadians = downwardRadians - _comfortConeDownRadians;
        double bandSpanRadians = _envelopeDownRadians - _comfortConeDownRadians;
        double ramp = Math.Clamp(bandRadians / bandSpanRadians, 0d, 1d);
        double extraFraction = ExtraStepsAtFullDemand / (double)DownwardFoldGeometry.GridSteps * ramp;
        double alpha = Math.Clamp(directionOptimal + extraFraction, 0d, 1d);

        double engage = Math.Clamp(-candidatePitchRadians / PitchEngageRadians, 0d, 1d);
        Vector3 foldPoint = ComposeFamilyOrigin(alpha);

        // Compatibility recovery: retain the selected family alpha before engagement, blend the
        // pitch-orbit and fold displacements, then apply yaw about the pivot. This published point
        // is not generally an FK family member and supplies no rest-safe publication witness.
        (double px, double py, double pz) = ApplyYaw(candidateYawRadians,
            ox + (engage * (foldPoint.X - _pivotPosition.X - ox)),
            oy + (engage * (foldPoint.Y - _pivotPosition.Y - oy)),
            oz + (engage * (foldPoint.Z - _pivotPosition.Z - oz)));
        Vector3 published = new(
            (float)(_pivotPosition.X + px),
            (float)(_pivotPosition.Y + py),
            (float)(_pivotPosition.Z + pz));

        (double tx, double ty, double tz) = ApplyYaw(candidateYawRadians, ox, oy, oz);
        Vector3 orbitTarget = new(
            (float)(_pivotPosition.X + tx),
            (float)(_pivotPosition.Y + ty),
            (float)(_pivotPosition.Z + tz));

        // Direction error of the selected family member (diagnostic).
        double fx = foldPoint.X - SpineOrigin.X;
        double fy = foldPoint.Y - SpineOrigin.Y;
        double fz = foldPoint.Z - SpineOrigin.Z;
        double fLength = Math.Sqrt((fx * fx) + (fy * fy) + (fz * fz));
        double directionError = fLength < 1e-9d
            ? 0d
            : Mathf.RadToDeg((float)Math.Acos(Math.Clamp(
                ((fx * refX) + (fy * refY) + (fz * refZ)) / fLength,
                -1d,
                1d)));

        bool governed = engage >= 1d;
        return new DownwardFoldSelection(
            governed,
            governed ? "fold_governed" : engage > 0d ? "engagement_blend" : "orbit_path",
            alpha,
            (int)Math.Round(alpha * DownwardFoldGeometry.GridSteps, MidpointRounding.AwayFromZero),
            directionOptimal,
            (int)Math.Round(directionOptimal * DownwardFoldGeometry.GridSteps, MidpointRounding.AwayFromZero),
            extraFraction,
            (int)Math.Round(extraFraction * DownwardFoldGeometry.GridSteps, MidpointRounding.AwayFromZero),
            directionError,
            alpha >= 1d - 1e-9d,
            engage,
            candidateYawRadians,
            candidatePitchRadians,
            published,
            foldPoint,
            orbitTarget,
            SpineOrigin);
    }

    private DownwardFoldSelection Degenerate(double yaw, double pitch)
        => new(
            false,
            "degenerate_orbit_direction",
            0d,
            0,
            0d,
            0,
            0d,
            0,
            0d,
            false,
            0d,
            yaw,
            pitch,
            _upstreamHeadOrigin,
            _upstreamHeadOrigin,
            _upstreamHeadOrigin,
            SpineOrigin);

    /// <summary>
    /// Interpolates the monotone sweep curve to the reference sweep angle: the continuous fold
    /// fraction whose family direction matches the reference. Below/above the tabulated range the
    /// fraction clamps to 0/1.
    /// </summary>
    private double InterpolateSweepToAlpha(double referenceSweep, float[]? gridAnglesDegrees)
    {
        if (referenceSweep <= _gridSweepAngles[0])
        {
            FillGridAngles(gridAnglesDegrees);
            return 0d;
        }

        for (int sample = 1; sample <= DownwardFoldGeometry.GridSteps; sample++)
        {
            double sweep = _gridSweepAngles[sample];
            if (referenceSweep <= sweep)
            {
                double lower = _gridSweepAngles[sample - 1];
                double span = sweep - lower;
                double fraction = span <= 1e-12d ? 0d : (referenceSweep - lower) / span;
                FillGridAngles(gridAnglesDegrees);
                return (sample - 1 + fraction) / DownwardFoldGeometry.GridSteps;
            }
        }

        FillGridAngles(gridAnglesDegrees);
        return 1d;
    }

    private void FillGridAngles(float[]? gridAnglesDegrees)
    {
        if (gridAnglesDegrees is not { } angles)
        {
            return;
        }

        for (int sample = 0; sample < _gridSweepAngles.Length; sample++)
        {
            angles[sample] = (float)(_gridSweepAngles[sample] * 180d / Math.PI);
        }
    }

    /// <summary>
    /// Signed sweep angle of a unit direction against the alpha = 0 family direction about the
    /// envelope X axis: positive in the co-forward (downward-folding) sense, matching the tabulated
    /// grid sweep.
    /// </summary>
    private static double SignedSweepAngle(double[] gridUnitDirections, int sample, double ux, double uy, double uz)
    {
        double bx = gridUnitDirections[(sample * 3) + 0];
        double by = gridUnitDirections[(sample * 3) + 1];
        double bz = gridUnitDirections[(sample * 3) + 2];
        double crossX = (by * uz) - (bz * uy);
        double cosine = (bx * ux) + (by * uy) + (bz * uz);
        // About the envelope −X axis: positive in the co-forward (downward-folding) sense.
        return Math.Atan2(-crossX, cosine);
    }

    /// <summary>Applies <c>Uᵀ</c> then the pitch-only rotation <c>Rx(pitch)</c> to a lever vector, in doubles.</summary>
    private (double X, double Y, double Z) ApplyUpstreamTransposedThenPitch(double pitchRadians, Vector3 lever)
    {
        // Uᵀ · v: the COLUMNS of U become the rows — (Uᵀ·v).x = X·v, .y = Y·v, .z = Z·v.
        double wx = (_upstreamFaceBasis.X.X * (double)lever.X)
            + (_upstreamFaceBasis.X.Y * (double)lever.Y)
            + (_upstreamFaceBasis.X.Z * (double)lever.Z);
        double wy = (_upstreamFaceBasis.Y.X * (double)lever.X)
            + (_upstreamFaceBasis.Y.Y * (double)lever.Y)
            + (_upstreamFaceBasis.Y.Z * (double)lever.Z);
        double wz = (_upstreamFaceBasis.Z.X * (double)lever.X)
            + (_upstreamFaceBasis.Z.Y * (double)lever.Y)
            + (_upstreamFaceBasis.Z.Z * (double)lever.Z);

        // Rx(pitch): right-handed rotation about +X — for the AI-009 convention (positive pitch up,
        // forward −Z) a positive pitch raises the forward axis: y' = y·cos − z·sin, z' = y·sin + z·cos
        // evaluated on the envelope-frame lever.
        double cosPitch = Math.Cos(pitchRadians);
        double sinPitch = Math.Sin(pitchRadians);
        return (wx, (wy * cosPitch) - (wz * sinPitch), (wy * sinPitch) + (wz * cosPitch));
    }

    /// <summary>Applies the yaw-only rotation <c>Ry(yaw)</c> to a vector, in doubles.</summary>
    private static (double X, double Y, double Z) ApplyYaw(double yawRadians, double x, double y, double z)
    {
        double cosYaw = Math.Cos(yawRadians);
        double sinYaw = Math.Sin(yawRadians);
        // Ry columns: X = (cos, 0, −sin), Y = (0, 1, 0), Z = (sin, 0, cos).
        return ((x * cosYaw) + (z * sinYaw), y, (-x * sinYaw) + (z * cosYaw));
    }
}

internal sealed class DownwardFoldChainAuthoring
{
    private const float RestRotationToleranceRadians = 1e-4f;
    private const float UniformScaleTolerance = 1e-4f;
    private const float OrthogonalityTolerance = 1e-4f;
    private const float ReconstructionToleranceMillimetres = 0.1f;
    // Numerical-noise tolerance for the forward-model legality verification arithmetic itself (float32
    // vector angles deviate by ~1e-6 rad); it bounds verification rounding, never the pose — measured
    // swings in the installed probe's outcome checks are judged against the authored caps under the
    // task's user-approved 0.1° acceptance allowance (excess beyond it stays red).
    private const float LegalityToleranceRadians = 1e-5f;
    private const int AxisKindX = 0;
    private const int AxisKindAll = 3;
    private const int MaxAncestorWalk = 32;

    private readonly int[] _bones;
    private readonly Transform3D[] _localRest;
    private readonly string[] _boneNames;
    private readonly float[] _halfCapRadians;
    private readonly int[] _axisKinds;
    private readonly Transform3D[] _scratchJointWorlds;
    private readonly float[] _scratchFolds;

    private DownwardFoldChainAuthoring(
        int[] bones,
        string[] boneNames,
        Transform3D[] localRest,
        float[] halfCapRadians,
        int[] axisKinds)
    {
        _bones = bones;
        _localRest = localRest;
        _boneNames = boneNames;
        _halfCapRadians = halfCapRadians;
        _axisKinds = axisKinds;
        _scratchJointWorlds = new Transform3D[localRest.Length];
        _scratchFolds = new float[halfCapRadians.Length];
    }

    /// <summary>Chain bone names root→head of the bound authored chain, for diagnostics.</summary>
    public IReadOnlyList<string> ChainBoneNames => _boneNames;

    /// <summary>Authored cone half-caps (radians) of the movable joints, root→head.</summary>
    public IReadOnlyList<float> AuthoredHalfCapRadians => _halfCapRadians;

    /// <summary>Authored rotation-axis kinds of the movable joints, root→head.</summary>
    public IReadOnlyList<int> AuthoredAxisKinds => _axisKinds;

    /// <summary>
    /// Attempts to bind the fold-family chain authoring for one skeleton and Head bone. Every
    /// unsupported-authoring condition that the superseded prototype threw on is reported as a precise
    /// reason instead; the caller keeps its unchanged orbit construction with that diagnostic.
    /// </summary>
    /// <param name="skeleton">The upstream skeleton.</param>
    /// <param name="headBoneIndex">The bound Head bone index.</param>
    /// <param name="unsupportedReason">The first unsupported-authoring reason, when binding failed.</param>
    /// <returns>The bound authoring, or <see langword="null"/> with a reason.</returns>
    public static DownwardFoldChainAuthoring? TryBind(
        Skeleton3D skeleton,
        int headBoneIndex,
        out string? unsupportedReason)
    {
        unsupportedReason = null;
        string headBoneName = skeleton.GetBoneName(headBoneIndex);
        CCDIKSetting? setting = DiscoverCcdikSetting(skeleton, headBoneName, out string? discoveryFailure);
        if (setting is null)
        {
            unsupportedReason = discoveryFailure ?? "no_authored_head_chain";
            return null;
        }

        // Canonical parent chain: walk ancestors from the Head bone and require the authored root bone to
        // sit exactly jointCount bones up the parent chain.
        List<int> headToRoot = [headBoneIndex];
        int walker = headBoneIndex;
        while (skeleton.GetBoneParent(walker) >= 0 && headToRoot.Count < MaxAncestorWalk)
        {
            walker = skeleton.GetBoneParent(walker);
            headToRoot.Add(walker);
        }

        int rootPosition = headToRoot.IndexOf(setting.RootBone);
        if (rootPosition < 0 || rootPosition + 1 != setting.JointCount)
        {
            unsupportedReason =
                $"chain_mismatch:{skeleton.GetBoneName(setting.RootBone)}:{setting.JointCount}:{rootPosition + 1}";
            return null;
        }

        int chainLength = setting.JointCount;
        int movableCount = chainLength - 1;
        int[] bones = new int[chainLength];
        string[] boneNames = new string[chainLength];
        var localRest = new Transform3D[chainLength];
        for (int i = 0; i < chainLength; i++)
        {
            bones[i] = headToRoot[chainLength - 1 - i];
            boneNames[i] = skeleton.GetBoneName(bones[i]);
            localRest[i] = skeleton.GetBoneRest(bones[i]);
        }

        if (bones[0] != setting.RootBone || bones[^1] != headBoneIndex)
        {
            unsupportedReason = "chain_span_mismatch";
            return null;
        }

        // The end bone must be the inert, non-extended endpoint (authored limitation absent, extension off).
        if (setting.EndJointHasLimitation || setting.ExtendEndBone)
        {
            unsupportedReason = "extended_or_limited_end_bone";
            return null;
        }

        for (int i = 0; i < chainLength; i++)
        {
            float restRotation = localRest[i].Basis.Orthonormalized().GetRotationQuaternion()
                .AngleTo(Quaternion.Identity);
            if (restRotation > RestRotationToleranceRadians)
            {
                unsupportedReason = $"non_identity_rest_rotation:{boneNames[i]}";
                return null;
            }

            if (i > 0 && localRest[i].Origin.LengthSquared() < 1e-12f)
            {
                unsupportedReason = $"degenerate_outgoing_translation:{boneNames[i]}";
                return null;
            }
        }

        // Every movable joint must fold legally about the authored local X axis across the whole grid.
        float[] caps = setting.HalfCapRadians;
        for (int sample = 0; sample < DownwardFoldGeometry.GridSampleCount; sample++)
        {
            float alpha = sample / (float)DownwardFoldGeometry.GridSteps;
            for (int joint = 0; joint < movableCount; joint++)
            {
                float fold = alpha * caps[joint];
                if (DownwardFoldGeometry.OutgoingSwingRadians(localRest[joint + 1], fold)
                    > caps[joint] + LegalityToleranceRadians
                    || DownwardFoldGeometry.HingeAxisDepartureRadians(fold) > LegalityToleranceRadians)
                {
                    unsupportedReason = $"illegal_grid_sample:{sample}:{boneNames[joint]}";
                    return null;
                }
            }
        }

        return new DownwardFoldChainAuthoring(bones, boneNames, localRest, caps, setting.AxisKinds[..movableCount]);
    }

    /// <summary>
    /// Builds the per-evaluation shared prediction frame in the character envelope frame, or reports why
    /// the fold family is outside its supported domain for this evaluation (animated upstream, non-rigid
    /// skeleton basis, sagittal-direction failure, reconstruction drift, missing pivot).
    /// </summary>
    /// <param name="skeleton">The bound upstream skeleton.</param>
    /// <param name="snapshot">This evaluation's captured upstream snapshot (world coordinates).</param>
    /// <param name="envelope">The character envelope basis (orthonormal).</param>
    /// <param name="characterOrigin">The owning character's world origin the envelope transport uses.</param>
    /// <param name="comfortConeDownRadians">Authored downward eye-comfort bound of the demand ramp.</param>
    /// <param name="envelopeDownRadians">Authored downward head-envelope bound of the demand ramp.</param>
    /// <param name="notBuiltReason">Machine-readable reason when no frame was built.</param>
    /// <returns>The frame, or <see langword="null"/> with a reason (the caller keeps the orbit path).</returns>
    public DownwardFoldPredictionFrame? TryBuildFrame(
        Skeleton3D skeleton,
        OrientingUpstreamSnapshot snapshot,
        Basis envelope,
        Vector3 characterOrigin,
        double comfortConeDownRadians,
        double envelopeDownRadians,
        out string? notBuiltReason)
    {
        notBuiltReason = null;
        if (!snapshot.HasPivot)
        {
            notBuiltReason = "no_pivot";
            return null;
        }

        if (envelopeDownRadians <= comfortConeDownRadians)
        {
            notBuiltReason = "degenerate_demand_band";
            return null;
        }

        if (!IsRigidUniformlyScaled(skeleton.GlobalTransform.Basis))
        {
            notBuiltReason = "non_rigid_skeleton_basis";
            return null;
        }

        // Capture the ANIMATION-OWNED upstream poses this evaluation re-anchors onto: native bone
        // poses (the channel the engine restores between skeleton updates), never solved attachment
        // globals. Animated and partially posed inputs are the ordinary operating mode.
        var localPoses = new Transform3D[_bones.Length];
        for (int i = 0; i < _bones.Length; i++)
        {
            localPoses[i] = skeleton.GetBonePose(_bones[i]);
        }

        // One exact envelope transport, shared by the frame and inverted by the publication path. The
        // posed family composes under the identity parent in frame coordinates (the transport is
        // already folded into the captured chain-parent pose below).
        Transform3D worldFromEnvelope = new(envelope, characterOrigin);
        Transform3D envelopeFromWorld = worldFromEnvelope.AffineInverse();
        Transform3D chainParentEnvelope = envelopeFromWorld
            * (skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(skeleton.GetBoneParent(_bones[0])));
        var transportedPoses = new Transform3D[localPoses.Length];
        for (int i = 0; i < localPoses.Length; i++)
        {
            // Local joint poses are frame-invariant (local transforms compose identically under any
            // parent transport), so the animation-owned poses carry into the envelope frame unchanged;
            // only the chain-parent pose is transported, and the fold sign is re-resolved per frame.
            transportedPoses[i] = new Transform3D(localPoses[i].Basis.Orthonormalized(), localPoses[i].Origin);
        }

        // Sagittal fold sign on the CURRENT upstream pose, in the envelope frame: folding must pitch
        // the head origin downward along the character's up axis at full fold.
        float foldSign = DownwardFoldGeometry.ResolveForwardFoldSign(
            chainParentEnvelope, transportedPoses, _halfCapRadians, out Vector3 posedHeadOrigin);
        float[] availableIncrements = new float[_halfCapRadians.Length];
        for (int joint = 0; joint < availableIncrements.Length; joint++)
        {
            if (!DownwardFoldGeometry.TryRestBoundedIncrement(
                _localRest[joint].Basis, transportedPoses[joint].Basis,
                _localRest[joint + 1].Origin, _halfCapRadians[joint], foldSign,
                out availableIncrements[joint]))
            {
                notBuiltReason = $"upstream_outside_rest_domain:{_boneNames[joint]}";
                return null;
            }
        }
        for (int joint = 0; joint < _scratchFolds.Length; joint++)
        {
            _scratchFolds[joint] = foldSign * availableIncrements[joint];
        }

        Vector3 fullFoldHead = DownwardFoldGeometry.ComposePosedFoldedChain(
            chainParentEnvelope, transportedPoses, _scratchFolds, _scratchJointWorlds).Origin;
        if (fullFoldHead.Y >= posedHeadOrigin.Y - 1e-7f)
        {
            notBuiltReason = "non_sagittal_fold_direction";
            return null;
        }

        // Fixed-grid FK over the posed family: the 33 certified sample origins, the fold-invariant
        // chain-root origin, and the monotone sagittal sweep-angle curve the continuous selection
        // interpolates.
        var gridOrigins = new Vector3[DownwardFoldGeometry.GridSampleCount];
        double[] gridUnitDirections = new double[DownwardFoldGeometry.GridSampleCount * 3];
        double[] gridSweepAngles = new double[DownwardFoldGeometry.GridSampleCount];
        Vector3 spineOrigin = Vector3.Zero;
        for (int sample = 0; sample < gridOrigins.Length; sample++)
        {
            float alpha = sample / (float)DownwardFoldGeometry.GridSteps;
            for (int joint = 0; joint < _scratchFolds.Length; joint++)
            {
                _scratchFolds[joint] = foldSign * alpha * availableIncrements[joint];
            }

            Transform3D head = DownwardFoldGeometry.ComposePosedFoldedChain(
                chainParentEnvelope, transportedPoses, _scratchFolds, _scratchJointWorlds);
            gridOrigins[sample] = head.Origin;
            if (sample == 0)
            {
                spineOrigin = _scratchJointWorlds[0].Origin;
            }
        }

        for (int sample = 0; sample < gridOrigins.Length; sample++)
        {
            double dx = gridOrigins[sample].X - (double)spineOrigin.X;
            double dy = gridOrigins[sample].Y - (double)spineOrigin.Y;
            double dz = gridOrigins[sample].Z - (double)spineOrigin.Z;
            double length = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            if (length < 1e-9d)
            {
                notBuiltReason = "degenerate_spine_head_direction";
                return null;
            }

            gridUnitDirections[(sample * 3) + 0] = dx / length;
            gridUnitDirections[(sample * 3) + 1] = dy / length;
            gridUnitDirections[(sample * 3) + 2] = dz / length;
        }

        // Sweep curve: signed sagittal angle from the alpha = 0 direction per sample, about the
        // envelope X axis, in the co-forward (downward-folding) sense. The curve must be monotone for
        // bracketed interpolation to be well-defined; a non-monotone family refuses the frame.
        for (int sample = 0; sample < gridOrigins.Length; sample++)
        {
            gridSweepAngles[sample] = SignedSweepAngle(
                gridUnitDirections,
                0,
                gridUnitDirections[(sample * 3) + 0],
                gridUnitDirections[(sample * 3) + 1],
                gridUnitDirections[(sample * 3) + 2]);
        }

        for (int sample = 1; sample < gridSweepAngles.Length; sample++)
        {
            if (gridSweepAngles[sample] <= gridSweepAngles[sample - 1] + 1e-9d)
            {
                notBuiltReason = $"non_monotone_family:{sample}";
                return null;
            }
        }

        Vector3 upstreamHeadEnvelope = envelopeFromWorld * snapshot.HeadOriginWorld;
        Vector3 pivotEnvelope = envelopeFromWorld * snapshot.PivotPositionWorld;
        Basis upstreamFaceEnvelope = (envelope.Inverse() * snapshot.UpstreamFaceBasisWorld).Orthonormalized();

        // Structural re-anchoring check: alpha = 0 over the captured poses reconstructs the snapshot's
        // upstream head origin (both are the plain upstream composition).
        double reconstructionMillimetres =
            (gridOrigins[0] - upstreamHeadEnvelope).Length() * 1000d;
        if (reconstructionMillimetres > ReconstructionToleranceMillimetres)
        {
            notBuiltReason = $"reconstruction_drift:{reconstructionMillimetres:F4}mm";
            return null;
        }

        return new DownwardFoldPredictionFrame(
            chainParentEnvelope,
            transportedPoses,
            availableIncrements,
            foldSign,
            gridOrigins,
            gridUnitDirections,
            gridSweepAngles,
            spineOrigin,
            upstreamHeadEnvelope,
            pivotEnvelope,
            upstreamFaceEnvelope,
            worldFromEnvelope,
            comfortConeDownRadians,
            envelopeDownRadians,
            reconstructionMillimetres);
    }

    /// <summary>
    /// Signed sagittal sweep angle of a unit direction against a base direction about the envelope X
    /// axis (shared by the frame's sweep curve and the selection's reference measurement).
    /// </summary>
    private static double SignedSweepAngle(
        double[] gridUnitDirections,
        int baseSample,
        double ux,
        double uy,
        double uz)
    {
        double bx = gridUnitDirections[(baseSample * 3) + 0];
        double by = gridUnitDirections[(baseSample * 3) + 1];
        double bz = gridUnitDirections[(baseSample * 3) + 2];
        double crossX = (by * uz) - (bz * uy);
        double cosine = (bx * ux) + (by * uy) + (bz * uz);
        // About the envelope −X axis: positive in the co-forward (downward-folding) sense.
        return Math.Atan2(-crossX, cosine);
    }

    private static bool IsRigidUniformlyScaled(Basis basis)
    {
        float sx = basis.X.Length();
        float sy = basis.Y.Length();
        float sz = basis.Z.Length();
        return Math.Abs(sx - sy) <= UniformScaleTolerance
            && Math.Abs(sy - sz) <= UniformScaleTolerance
            && Math.Abs(sx - sz) <= UniformScaleTolerance
            && Math.Abs(basis.X.Dot(basis.Y)) <= OrthogonalityTolerance
            && Math.Abs(basis.Y.Dot(basis.Z)) <= OrthogonalityTolerance
            && Math.Abs(basis.X.Dot(basis.Z)) <= OrthogonalityTolerance
            && sx > 0f
            && basis.Determinant() > 0f;
    }

    private static CCDIKSetting? DiscoverCcdikSetting(
        Skeleton3D skeleton,
        string headBoneName,
        out string? failure)
    {
        CCDIKSetting? matched = null;
        foreach (Node child in skeleton.GetChildren())
        {
            if (child is not SkeletonModifier3D modifier)
            {
                continue;
            }

            // The authored constrained chain lives on the CCDIK solver node; other bone-constraint
            // modifiers under the same skeleton (transform copy, twist disperser) also author an end
            // bone and joint metadata and must not be mistaken for the chain.
            if (modifier.GetClass() != "CCDIK3D")
            {
                continue;
            }

            int settingCount = ReadInt(modifier, "setting_count", -1);
            for (int setting = 0; setting < Math.Max(0, settingCount); setting++)
            {
                string endBoneName = ReadString(modifier, $"settings/{setting}/end_bone_name", string.Empty);
                if (endBoneName != headBoneName)
                {
                    continue;
                }

                // Only iterative-chain solver settings structurally carry a joint chain; other
                // bone-constraint modifiers (transform copy, twist) also author an end bone and must
                // not be mistaken for the constrained chain.
                int jointCount = ReadInt(modifier, $"settings/{setting}/joint_count", -1);
                if (jointCount < 2)
                {
                    continue;
                }

                if (matched is not null)
                {
                    failure = "ambiguous_head_chain_settings";
                    return null;
                }

                CCDIKSetting? read = ReadSetting(skeleton, modifier, setting, out string? readFailure);
                if (read is null)
                {
                    failure = readFailure;
                    return null;
                }

                matched = read;
            }
        }

        failure = matched is null ? $"no_authored_head_chain:{headBoneName}" : null;
        return matched;
    }

    private static CCDIKSetting? ReadSetting(
        Skeleton3D skeleton,
        SkeletonModifier3D modifier,
        int setting,
        out string? failure)
    {
        failure = null;
        string rootBoneName = ReadString(modifier, $"settings/{setting}/root_bone_name", string.Empty);
        int rootBone = skeleton.FindBone(rootBoneName);
        if (rootBone < 0)
        {
            failure = $"missing_root_bone:{rootBoneName}";
            return null;
        }

        int jointCount = ReadInt(modifier, $"settings/{setting}/joint_count", -1);
        if (jointCount < 2)
        {
            failure = "joint_count_below_two";
            return null;
        }

        int[] axisKinds = new int[jointCount];
        float[] halfCaps = new float[jointCount - 1];
        bool endJointHasLimitation = false;
        for (int joint = 0; joint < jointCount; joint++)
        {
            int axisKind = ReadInt(modifier, $"settings/{setting}/joints/{joint}/rotation_axis", -1);
            if (axisKind is not (AxisKindX or AxisKindAll))
            {
                failure = $"unsupported_rotation_axis:{joint}:{axisKind}";
                return null;
            }

            axisKinds[joint] = axisKind;
            if (modifier
                .Get($"settings/{setting}/joints/{joint}/limitation").AsGodotObject() is not Resource limitation)
            {
                if (joint == jointCount - 1)
                {
                    endJointHasLimitation = false;
                    continue;
                }

                failure = $"missing_joint_limitation:{joint}";
                return null;
            }

            if (joint == jointCount - 1)
            {
                endJointHasLimitation = true;
                continue;
            }

            if (limitation.GetClass() != "JointLimitationCone3D")
            {
                failure = $"unsupported_limitation_class:{joint}:{limitation.GetClass()}";
                return null;
            }

            int rightAxis = ReadInt(modifier, $"settings/{setting}/joints/{joint}/limitation/right_axis", -1);
            if (rightAxis != 0)
            {
                failure = $"unsupported_right_axis:{joint}:{rightAxis}";
                return null;
            }

            Quaternion offset = modifier
                .Get($"settings/{setting}/joints/{joint}/limitation/rotation_offset").AsQuaternion();
            if (offset.AngleTo(Quaternion.Identity) > 1e-6f)
            {
                failure = $"unsupported_limitation_offset:{joint}";
                return null;
            }

            float coneAngle = limitation.Get("angle").AsSingle();
            if (!float.IsFinite(coneAngle) || coneAngle <= 0f || coneAngle > Mathf.Pi)
            {
                failure = $"unsupported_cone_angle:{joint}:{coneAngle}";
                return null;
            }

            halfCaps[joint] = coneAngle * 0.5f;
        }

        bool extendEndBone = ReadBool(modifier, $"settings/{setting}/extend_end_bone", true);
        return new CCDIKSetting(rootBone, jointCount, axisKinds, halfCaps, endJointHasLimitation, extendEndBone);
    }

    private static int ReadInt(GodotObject node, string property, int fallback)
    {
        Variant value = node.Get(property);
        return value.VariantType == Variant.Type.Int ? value.AsInt32() : fallback;
    }

    private static string ReadString(GodotObject node, string property, string fallback)
    {
        Variant value = node.Get(property);
        return value.VariantType == Variant.Type.String ? value.AsString() : fallback;
    }

    private static bool ReadBool(GodotObject node, string property, bool fallback)
    {
        Variant value = node.Get(property);
        return value.VariantType == Variant.Type.Bool ? value.AsBool() : fallback;
    }

    private sealed record CCDIKSetting(
        int RootBone,
        int JointCount,
        int[] AxisKinds,
        float[] HalfCapRadians,
        bool EndJointHasLimitation,
        bool ExtendEndBone);
}
