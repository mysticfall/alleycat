using AlleyCat.IK.Pose;
using Godot;
using Xunit;

namespace AlleyCat.Tests.IK.Pose;

/// <summary>
/// Unit coverage for per-axis hip-translation authority blending and standing-continuum defaults.
/// </summary>
public sealed class HipTranslationAuthorityTests
{
    private const float Tolerance = 1e-4f;

    /// <summary>
    /// Full authority preserves the legacy behaviour where IK fully replaces animated translation.
    /// </summary>
    [Fact]
    public void Apply_FullAuthority_MatchesReconciledTarget()
    {
        Vector3 animated = new(1f, 2f, 3f);
        Vector3 target = new(10f, 20f, 30f);

        Vector3 result = HipTranslationAuthority.Full.Apply(
            animated,
            target,
            Vector3.Left,
            Vector3.Up,
            Vector3.Back);

        AssertClose(target, result);
    }

    /// <summary>
    /// Reduced horizontal authority keeps authored animation sway/travel while applying vertical crouch movement.
    /// </summary>
    [Fact]
    public void Apply_ReducedHorizontalAuthority_PreservesAnimatedHorizontalAndAppliesVerticalTarget()
    {
        Vector3 animated = new(1f, 2f, 3f);
        Vector3 target = new(10f, 20f, 30f);
        HipTranslationAuthority authority = new(Lateral: 0f, Vertical: 1f, Forward: 0f);

        Vector3 result = authority.Apply(
            animated,
            target,
            Vector3.Left,
            Vector3.Up,
            Vector3.Back);

        AssertClose(new Vector3(animated.X, target.Y, animated.Z), result);
    }

    /// <summary>
    /// The standing continuum dynamically blends only horizontal authority while retaining vertical authority.
    /// </summary>
    [Fact]
    public void ComputeHipTranslationAuthority_StandingContinuum_BlendsHorizontalAuthorityOnly()
    {
        HipTranslationAuthority result = StandingPoseState.ComputeHipTranslationAuthority(
            poseBlend: 0.25f,
            uprightHorizontalAuthority: 0f,
            fullCrouchHorizontalAuthority: 0.8f,
            verticalAuthority: 1f);

        AssertClose(new Vector3(0.2f, 1f, 0.2f), new Vector3(result.Lateral, result.Vertical, result.Forward));
    }

    /// <summary>
    /// Transition authority interpolation supports smooth state-owned dynamic blending.
    /// </summary>
    [Fact]
    public void Lerp_TransitionAuthority_BlendsPerAxisSmoothly()
    {
        HipTranslationAuthority source = HipTranslationAuthority.Full;
        HipTranslationAuthority target = new(Lateral: 0f, Vertical: 1f, Forward: 0f);

        var result = HipTranslationAuthority.Lerp(source, target, 0.5f);

        AssertClose(new Vector3(0.5f, 1f, 0.5f), new Vector3(result.Lateral, result.Vertical, result.Forward));
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.True(
            expected.DistanceTo(actual) <= Tolerance,
            $"Expected {expected}, got {actual}.");
    }
}
