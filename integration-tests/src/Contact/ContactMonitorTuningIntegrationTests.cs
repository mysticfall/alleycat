using AlleyCat.Contact;
using AlleyCat.TestFramework;
using Xunit;

namespace AlleyCat.IntegrationTests.Contact;

/// <summary>
/// Exported classification tunable coverage (INTR-005 TR17): the <see cref="ContactMonitor" />'s exported
/// classification properties are the authored tuning surface. Unauthored monitors build the shipped code
/// default policy, authored values override exactly the tunables they name, and malformed authored values —
/// non-finite, non-positive, or settling the rest threshold at or above the caress slip threshold — are
/// rejected fail-fast at <c>_Ready</c> through the shared <see cref="ContactClassificationPolicy" />
/// validation, before any classification decision runs.
/// </summary>
public sealed class ContactMonitorTuningIntegrationTests
{
    /// <summary>
    /// An unauthored monitor — every exported tunable left at its default — builds exactly the shipped code
    /// default policy, so template-hosted monitors with no authored tuning keep the defaults.
    /// </summary>
    [Headless]
    [Fact]
    public void UnauthoredTunables_BuildTheShippedDefaultPolicy()
    {
        ContactMonitor monitor = new()
        {
            Name = "TuningMonitor"
        };

        monitor._Ready();

        ContactClassificationPolicy defaults = ContactClassificationPolicy.Default;
        Assert.Equal(defaults.ImpactApproachSpeedThreshold, monitor.ClassificationPolicy.ImpactApproachSpeedThreshold);
        Assert.Equal(defaults.PressDepthThreshold, monitor.ClassificationPolicy.PressDepthThreshold);
        Assert.Equal(defaults.PressRateThreshold, monitor.ClassificationPolicy.PressRateThreshold);
        Assert.Equal(defaults.PressConfirmationSeconds, monitor.ClassificationPolicy.PressConfirmationSeconds);
        Assert.Equal(defaults.CaressSlipSpeedThreshold, monitor.ClassificationPolicy.CaressSlipSpeedThreshold);
        Assert.Equal(defaults.CaressConfirmationSeconds, monitor.ClassificationPolicy.CaressConfirmationSeconds);
        Assert.Equal(defaults.RestDelaySeconds, monitor.ClassificationPolicy.RestDelaySeconds);
        Assert.Equal(defaults.RestSettledSpeedThreshold, monitor.ClassificationPolicy.RestSettledSpeedThreshold);
    }

    /// <summary>
    /// Authored valid tunables override exactly the values they name; every tunable left unauthored keeps its
    /// shipped code default.
    /// </summary>
    [Headless]
    [Fact]
    public void AuthoredValidTunables_OverrideThePolicyAndAreUsed()
    {
        ContactMonitor monitor = new()
        {
            Name = "TuningMonitor",
            ImpactApproachSpeedThreshold = 2.5f,
            CaressConfirmationSeconds = 0.5f,
        };

        monitor._Ready();

        Assert.Equal(2.5f, monitor.ClassificationPolicy.ImpactApproachSpeedThreshold, 5);
        Assert.Equal(0.5f, monitor.ClassificationPolicy.CaressConfirmationSeconds, 5);
        Assert.Equal(
            ContactClassificationPolicy.Default.PressDepthThreshold,
            monitor.ClassificationPolicy.PressDepthThreshold);
        Assert.Equal(
            ContactClassificationPolicy.Default.RestDelaySeconds,
            monitor.ClassificationPolicy.RestDelaySeconds);
    }

    /// <summary>
    /// Authored tunables that are non-finite or non-positive are rejected fail-fast at <c>_Ready</c> — the
    /// documented validation point — with the same <see cref="ArgumentException" /> the policy constructor
    /// throws today.
    /// </summary>
    [Headless]
    [Theory]
    [InlineData(0f)]
    [InlineData(-0.25f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void MalformedAuthoredTunables_FailFastAtReady(float malformedValue)
    {
        ContactMonitor monitor = new()
        {
            Name = "TuningMonitor",
            RestDelaySeconds = malformedValue,
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(monitor._Ready);

        Assert.Contains("rest settled window", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cross-threshold constraint holds on the authored surface: a settled speed threshold at or above the
    /// caress slip threshold is rejected fail-fast at <c>_Ready</c> — a contact cannot be settled and
    /// caressing at once.
    /// </summary>
    [Headless]
    [Theory]
    [InlineData(0.2f, 0.2f)]
    [InlineData(0.3f, 0.2f)]
    public void SettledAtOrAboveCaressSlip_FailFastAtReady(float settledSpeed, float caressSlipSpeed)
    {
        ContactMonitor monitor = new()
        {
            Name = "TuningMonitor",
            CaressSlipSpeedThreshold = caressSlipSpeed,
            RestSettledSpeedThreshold = settledSpeed,
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(monitor._Ready);

        Assert.Contains("strictly below", exception.Message, StringComparison.Ordinal);
    }
}
