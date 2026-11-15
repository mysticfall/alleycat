using AlleyCat.Contact;
using Xunit;

namespace AlleyCat.Tests.Contact;

/// <summary>
/// Unit coverage of the classification policy contract (INTR-005 TR17): every threshold and window is finite
/// and validated before use; the tunables author on the hosting monitor's exported classification properties,
/// which default to the shipped code defaults. Values are non-normative.
/// </summary>
public sealed class ContactClassificationPolicyTests
{
    /// <summary>The shipped defaults are finite, positive, and satisfy the cross-threshold constraint.</summary>
    [Fact]
    public void Default_IsFinitePositiveAndSelfConsistent()
    {
        ContactClassificationPolicy policy = ContactClassificationPolicy.Default;

        Assert.True(float.IsFinite(policy.ImpactApproachSpeedThreshold) && policy.ImpactApproachSpeedThreshold > 0f);
        Assert.True(float.IsFinite(policy.PressDepthThreshold) && policy.PressDepthThreshold > 0f);
        Assert.True(float.IsFinite(policy.PressRateThreshold) && policy.PressRateThreshold > 0f);
        Assert.True(float.IsFinite(policy.PressConfirmationSeconds) && policy.PressConfirmationSeconds > 0f);
        Assert.True(float.IsFinite(policy.CaressSlipSpeedThreshold) && policy.CaressSlipSpeedThreshold > 0f);
        Assert.True(float.IsFinite(policy.CaressConfirmationSeconds) && policy.CaressConfirmationSeconds > 0f);
        Assert.True(float.IsFinite(policy.RestDelaySeconds) && policy.RestDelaySeconds > 0f);
        Assert.True(float.IsFinite(policy.RestSettledSpeedThreshold) && policy.RestSettledSpeedThreshold > 0f);
        Assert.True(policy.RestSettledSpeedThreshold < policy.CaressSlipSpeedThreshold);
    }

    /// <summary>Non-positive confirmation windows are malformed and rejected (TR17, AC-TR11).</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-0.25f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Constructor_RejectsNonPositiveWindows(float malformedWindow)
    {
        _ = Assert.Throws<ArgumentException>(
            () => new ContactClassificationPolicy(pressConfirmationSeconds: malformedWindow));
        _ = Assert.Throws<ArgumentException>(
            () => new ContactClassificationPolicy(caressConfirmationSeconds: malformedWindow));
        _ = Assert.Throws<ArgumentException>(() => new ContactClassificationPolicy(restDelaySeconds: malformedWindow));
    }

    /// <summary>Non-finite or non-positive thresholds are rejected before any classification decision uses them.</summary>
    [Fact]
    public void Constructor_RejectsMalformedThresholds()
    {
        float[] malformed = [0f, -1f, float.NaN, float.NegativeInfinity];

        foreach (float value in malformed)
        {
            _ = Assert.Throws<ArgumentException>(
                () => new ContactClassificationPolicy(impactApproachSpeedThreshold: value));
            _ = Assert.Throws<ArgumentException>(() => new ContactClassificationPolicy(pressDepthThreshold: value));
            _ = Assert.Throws<ArgumentException>(() => new ContactClassificationPolicy(pressRateThreshold: value));
            _ = Assert.Throws<ArgumentException>(() => new ContactClassificationPolicy(caressSlipSpeedThreshold: value));
            _ = Assert.Throws<ArgumentException>(() => new ContactClassificationPolicy(restSettledSpeedThreshold: value));
        }
    }

    /// <summary>The settled threshold must sit strictly below the caress slip threshold: a contact cannot be
    /// settled and caressing at once.</summary>
    [Theory]
    [InlineData(0.2f, 0.2f)]
    [InlineData(0.3f, 0.2f)]
    public void Constructor_RejectsASettledThresholdAtOrAboveTheCaressSlipThreshold(
        float settledSpeed,
        float caressSlipSpeed)
    {
        _ = Assert.Throws<ArgumentException>(() => new ContactClassificationPolicy(
            caressSlipSpeedThreshold: caressSlipSpeed,
            restSettledSpeedThreshold: settledSpeed));
    }
}
