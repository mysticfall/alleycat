using AlleyCat.Mind.Attention;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Mind.Attention;

/// <summary>
/// First-principles invariants of the non-recursive aimed-origin construction (AI-009 TR 21, HEAD-1):
/// D = U yields target = h; infinitesimal rotation approaches zero displacement; constant upstream
/// geometry and command keep the target invariant; the no-pivot fallback publishes the upstream head
/// origin. The superseded recursive formula — p + R·(solvedHead − p) — is reproduced evidence-only to
/// demonstrate that solved output advances it where the upstream construction stays invariant.
/// </summary>
public sealed class OrientingAimGeometryTests
{
    private const float FloatTolerance = 1e-5f;

    private static readonly Vector3 _headOrigin = new(0.31f, 1.52f, -0.08f);
    private static readonly Vector3 _pivotPosition = new(0.05f, 1.34f, 0.02f);
    private static readonly Basis _upstreamFaceBasis =
        Basis.LookingAt(new Vector3(0.2f, -0.1f, -1f).Normalized(), Vector3.Up);

    /// <summary>D = U yields target = h for any pivot configuration (TR 21).</summary>
    [Fact]
    public void CommandEqualToUpstream_YieldsHeadOrigin()
    {
        Vector3 target = OrientingAimGeometry.ConstructAimedTarget(
            _upstreamFaceBasis,
            _upstreamFaceBasis,
            _headOrigin,
            _pivotPosition);

        Assert.True((target - _headOrigin).Length() < FloatTolerance, $"D = U must return h, got {target}.");

        // The same holds through the snapshot overload with a rest snapshot (the rest/stationary case).
        OrientingUpstreamSnapshot rest = new(
            Generation: 1,
            EvaluationSequence: 1,
            DeltaSeconds: 1d / 60d,
            EvaluationMode: "process",
            SkeletonIdentity: 123,
            CharacterGlobalTransform: Transform3D.Identity,
            SkeletonGlobalTransform: Transform3D.Identity,
            HeadOriginWorld: _headOrigin,
            PivotPositionWorld: _pivotPosition,
            UpstreamFaceBasisWorld: _upstreamFaceBasis,
            HasPivot: true);
        Vector3 snapshotTarget = OrientingAimGeometry.ConstructAimedTarget(rest, _upstreamFaceBasis);
        Assert.True(
            (snapshotTarget - _headOrigin).Length() < FloatTolerance,
            $"D = U through the snapshot must return h, got {snapshotTarget}.");
    }

    /// <summary>
    /// An infinitesimal command rotation displaces the target by at most the infinitesimal rotation of
    /// the head-about-pivot lever arm (first-order bound; TR 21).
    /// </summary>
    [Theory]
    [InlineData(0.001f)]
    [InlineData(0.0001f)]
    [InlineData(-0.0005f)]
    public void InfinitesimalRotation_ApproachesZeroDisplacement(float epsilonRadians)
    {
        Basis command = _upstreamFaceBasis * new Basis(Vector3.Up, epsilonRadians);
        Vector3 target = OrientingAimGeometry.ConstructAimedTarget(
            command,
            _upstreamFaceBasis,
            _headOrigin,
            _pivotPosition);

        float leverArm = (_headOrigin - _pivotPosition).Length();
        float displacement = (target - _headOrigin).Length();
        Assert.True(
            displacement <= Mathf.Abs(epsilonRadians) * leverArm * 1.001f,
            $"An epsilon rotation must displace the target by at most epsilon * |h - p| "
            + $"({Mathf.Abs(epsilonRadians) * leverArm:F6} m), but moved {displacement:F6} m.");
    }

    /// <summary>
    /// Constant upstream geometry and command keep the target exactly invariant while the solved head
    /// converges — the HEAD-1 non-recursion property. The superseded formula
    /// <c>p + R·(solvedHead − p)</c> is fed the same convergence to show it advances with the solve.
    /// </summary>
    [Fact]
    public void StationaryUpstreamAndCommand_TargetInvariantUnderSolvedConvergence()
    {
        Basis command = _upstreamFaceBasis * new Basis(Vector3.Up, Mathf.DegToRad(30f));
        // The rotation factor of the construction: R = D * inverse(U), identical for both formulas here
        // (the superseded construction's tracked neutral starts at the upstream rest basis).
        Basis solvedRotation = command.Orthonormalized() * _upstreamFaceBasis.Orthonormalized().Inverse();

        Vector3? upstreamTarget = null;
        float worstLegacyAdvance = 0f;
        for (int step = 0; step <= 20; step++)
        {
            // Simulated solved head converging towards the command: the solved origin swings about the
            // pivot as the solve progresses, exactly the feedback the recursive construction consumed.
            Basis partial = new(Vector3.Up, Mathf.DegToRad(30f) * step / 20f);
            Vector3 solvedHead = _pivotPosition + (partial * (_headOrigin - _pivotPosition));

            Vector3 target = OrientingAimGeometry.ConstructAimedTarget(
                command,
                _upstreamFaceBasis,
                _headOrigin,
                _pivotPosition);
            if (upstreamTarget is null)
            {
                upstreamTarget = target;
            }
            else
            {
                Assert.True(
                    (target - upstreamTarget.Value).Length() < FloatTolerance,
                    $"The upstream target must stay invariant under solved convergence, but moved from "
                    + $"{upstreamTarget.Value} to {target} at step {step}.");
            }

            // Evidence-only superseded formula: R is relative to the tracked neutral (rest here), and the
            // displacement operand is the SOLVED head — so its target advances with the solve.
            Vector3 legacyTarget = _pivotPosition + (solvedRotation * (solvedHead - _pivotPosition));
            worstLegacyAdvance = Mathf.Max(worstLegacyAdvance, (legacyTarget - upstreamTarget.Value).Length());
        }

        Assert.True(
            worstLegacyAdvance > 0.01f,
            $"The superseded recursive formula must demonstrably advance with the solved head "
            + $"(advance {worstLegacyAdvance:F4} m), or this discriminator is vacuous.");
    }

    /// <summary>Without a valid pivot the snapshot construction publishes the upstream head origin (TR 22).</summary>
    [Fact]
    public void NoPivot_FallsBackToUpstreamHeadOrigin()
    {
        OrientingUpstreamSnapshot noPivot = new(
            Generation: 1,
            EvaluationSequence: 1,
            DeltaSeconds: 1d / 60d,
            EvaluationMode: "process",
            SkeletonIdentity: 123,
            CharacterGlobalTransform: Transform3D.Identity,
            SkeletonGlobalTransform: Transform3D.Identity,
            HeadOriginWorld: _headOrigin,
            PivotPositionWorld: _headOrigin,
            UpstreamFaceBasisWorld: _upstreamFaceBasis,
            HasPivot: false);
        Basis command = _upstreamFaceBasis * new Basis(Vector3.Up, Mathf.DegToRad(45f));

        Vector3 target = OrientingAimGeometry.ConstructAimedTarget(noPivot, command);

        Assert.True(
            (target - _headOrigin).Length() < FloatTolerance,
            $"The no-pivot fallback must publish the upstream head origin, got {target}.");
    }

    /// <summary>
    /// The rest/stationary case (plan HEAD-1 closure): at an identity rest basis with a rest-relative
    /// command, the construction reproduces the pivot swing analytically — the pure rotation of the
    /// rest lever arm. (A tilted upstream basis conjugates the aim through U; the invariance test
    /// covers tilted geometry, so the analytic oracle here uses the trivially analysable rest frame.)
    /// </summary>
    [Fact]
    public void RestStationaryCommand_SwingsRestLeverArmAnalytically()
    {
        Basis identityRest = Basis.Identity;
        Basis command = new(Vector3.Up, Mathf.DegToRad(25f));
        Vector3 leverArm = _headOrigin - _pivotPosition;
        Vector3 expected = _pivotPosition + (new Basis(Vector3.Up, Mathf.DegToRad(25f)) * leverArm);

        Vector3 target = OrientingAimGeometry.ConstructAimedTarget(
            command,
            identityRest,
            _headOrigin,
            _pivotPosition);

        Assert.True(
            (target - expected).Length() < FloatTolerance,
            $"The rest construction must equal the analytic pivot swing {expected}, got {target}.");
    }
}
