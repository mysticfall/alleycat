using AlleyCat.TestFramework;
using AlleyCat.Testing;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Vision;

/// <summary>
/// TEST-3 fail-closed setup validation for the bilateral immutable authored eye references: when the
/// authored-reference extraction is missing a node, a transform, or receives degenerate data, it must
/// fail loudly naming the input — never silently fall back to an identity or neutral reference. These
/// are synthetic checks against the real female template's scene state; no rendering is involved.
/// </summary>
[Headless]
public sealed class BilateralEyeReferenceIntegrityTests
{
    /// <summary>Original photobooth >14° contribution must reject either independently under-contributing eye.</summary>
    [Fact]
    public void PhotoboothContribution_SingleEyeBelowFourteenDegrees_CannotPassBilateralAcceptance()
    {
        Basis left = DirectFixationReferences.LoadAuthoredEyeNeutral(
            DirectFixationReferences.FemaleTemplatePath, "GeneralSkeleton/Head/Eyes/LeftEye");
        Basis right = DirectFixationReferences.LoadAuthoredEyeNeutral(
            DirectFixationReferences.FemaleTemplatePath, "GeneralSkeleton/Head/Eyes/RightEye");
        Basis transported = new(Vector3.Up, Mathf.DegToRad(40));
        foreach (bool leftFault in new[] { false, true })
        {
            float leftDegrees = leftFault ? 13 : 16;
            float rightDegrees = leftFault ? 16 : 13;
            Vector2 leftMeasured = Ai009OrientingPhotoboothDriver.MeasureAuthoredEyeContribution(
                transported, transported, left, transported * new Basis(Vector3.Up, Mathf.DegToRad(leftDegrees)) * left);
            Vector2 rightMeasured = Ai009OrientingPhotoboothDriver.MeasureAuthoredEyeContribution(
                transported, transported, right, transported * new Basis(Vector3.Up, Mathf.DegToRad(rightDegrees)) * right);
            // Expected signed optical yaw comes from prescribed independent pure yaw rotations,
            // not from production gaze math or neutral recapture. Either single-eye fault stays red.
            Assert.InRange(leftMeasured.X, leftDegrees - 0.001f, leftDegrees + 0.001f);
            Assert.InRange(rightMeasured.X, rightDegrees - 0.001f, rightDegrees + 0.001f);
            Assert.False(leftMeasured.X > 14 && rightMeasured.X > 14);
        }
        Vector2 healthy = Ai009OrientingPhotoboothDriver.MeasureAuthoredEyeContribution(
            transported, transported, right, transported * new Basis(Vector3.Up, Mathf.DegToRad(16)) * right);
        Assert.True(healthy.X > 14);
    }

    /// <summary>Both eyes' authored neutrals load from one template scene state and carry equal and
    /// opposite ±3.5° yaw (the authored toe-out mirror) — the positive bilateral control. The
    /// assertion is on magnitude and mutual opposition, not an absolute sign convention.</summary>
    [Fact]
    public void LoadAuthoredEyeNeutral_LoadsBothEyesFromOneTemplateState()
    {
        Basis left = DirectFixationReferences.LoadAuthoredEyeNeutral(
            DirectFixationReferences.FemaleTemplatePath, "GeneralSkeleton/Head/Eyes/LeftEye");
        Basis right = DirectFixationReferences.LoadAuthoredEyeNeutral(
            DirectFixationReferences.FemaleTemplatePath, "GeneralSkeleton/Head/Eyes/RightEye");

        float leftToeOutDegrees = Mathf.RadToDeg(left.Orthonormalized().GetRotationQuaternion().GetEuler().Y);
        float rightToeOutDegrees = Mathf.RadToDeg(right.Orthonormalized().GetRotationQuaternion().GetEuler().Y);

        Assert.Equal(3.5d, Math.Abs(leftToeOutDegrees), 1);
        Assert.Equal(3.5d, Math.Abs(rightToeOutDegrees), 1);
        Assert.Equal(0d, leftToeOutDegrees + rightToeOutDegrees, 1);
    }

    /// <summary>A node path that matches nothing fails closed, naming the missing input.</summary>
    [Fact]
    public void LoadAuthoredEyeNeutral_MissingNodeFailsClosed()
    {
        _ = Assert.Throws<InvalidOperationException>(
            () => DirectFixationReferences.LoadAuthoredEyeNeutral(
                DirectFixationReferences.FemaleTemplatePath, "GeneralSkeleton/Head/Eyes/ThirdEye"));
    }

    /// <summary>
    /// A node that exists but authors no transform property fails closed — the exact hole that
    /// previously fell back silently to an identity basis (the NeckSpineIK modifier node in the female
    /// template authors no transform).
    /// </summary>
    [Fact]
    public void LoadAuthoredEyeNeutral_NodeWithoutAuthoredTransformFailsClosed()
    {
        _ = Assert.Throws<InvalidOperationException>(
            () => DirectFixationReferences.LoadAuthoredEyeNeutral(
                DirectFixationReferences.FemaleTemplatePath, "GeneralSkeleton/NeckSpineIK"));
    }

    /// <summary>Degenerate (zero) and non-finite bases fail closed; a valid authored basis passes.</summary>
    [Fact]
    public void EnsureAuthoredEyeNeutral_DegenerateOrNonFiniteFailsClosed()
    {
        _ = Assert.Throws<InvalidOperationException>(
            () => DirectFixationReferences.EnsureAuthoredEyeNeutral(
                new Basis(Vector3.Zero, Vector3.Zero, Vector3.Zero), "synthetic zero basis"));
        _ = Assert.Throws<InvalidOperationException>(
            () => DirectFixationReferences.EnsureAuthoredEyeNeutral(
                new Basis(Vector3.Right, new Vector3(float.NaN, 1f, 0f), Vector3.Back),
                "synthetic non-finite basis"));

        Basis valid = DirectFixationReferences.LoadAuthoredEyeNeutral(
            DirectFixationReferences.FemaleTemplatePath, "GeneralSkeleton/Head/Eyes/RightEye");
        Assert.Equal(valid, DirectFixationReferences.EnsureAuthoredEyeNeutral(valid, "valid authored basis"));
    }

    /// <summary>A captured neutral diverging from the authored scene state fails closed, naming the eye.</summary>
    [Fact]
    public void ValidateCapturedAgainstAuthored_DivergenceFailsClosed()
    {
        Basis authored = DirectFixationReferences.LoadAuthoredEyeNeutral(
            DirectFixationReferences.FemaleTemplatePath, "GeneralSkeleton/Head/Eyes/RightEye");
        Quaternion divergedRotation = authored.Orthonormalized().GetRotationQuaternion()
            * Quaternion.FromEuler(new Vector3(0f, Mathf.DegToRad(5f), 0f));
        Basis diverged = new(divergedRotation);

        _ = Assert.Throws<InvalidOperationException>(
            () => DirectFixationReferences.ValidateCapturedAgainstAuthored(diverged, authored, "RightEye"));
        DirectFixationReferences.ValidateCapturedAgainstAuthored(authored, authored, "RightEye");
    }
}
