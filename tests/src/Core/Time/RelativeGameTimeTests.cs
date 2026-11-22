using AlleyCat.Core.Time;
using Xunit;

namespace AlleyCat.Tests.Core.Time;

/// <summary>
/// Deterministic coverage for the shared model-facing relative-time phrasing: seconds, minutes with and without
/// seconds, hours with and without minutes, nearest-second rounding, and the composition-skew guards.
/// </summary>
public sealed class RelativeGameTimeTests
{
    /// <summary>Ages under a second — including zero, negative, and non-finite skew — phrase as just now.</summary>
    [Theory]
    [InlineData(0d, "just now")]
    [InlineData(-5d, "just now")]
    [InlineData(0.4d, "just now")]
    [InlineData(0.9d, "just now")]
    [InlineData(double.NaN, "just now")]
    public void FormatAgo_UnderOneSecond_PhrasesAsJustNow(double ageSeconds, string expected)
        => Assert.Equal(expected, RelativeGameTime.FormatAgo(ageSeconds));

    /// <summary>Ages below a minute round to the nearest second.</summary>
    [Theory]
    [InlineData(1d, "1 sec ago")]
    [InlineData(45.2d, "45 sec ago")]
    [InlineData(59.4d, "59 sec ago")]
    public void FormatAgo_BelowAMinute_RoundsToNearestSecond(double ageSeconds, string expected)
        => Assert.Equal(expected, RelativeGameTime.FormatAgo(ageSeconds));

    /// <summary>Minutes carry their remaining seconds, omitting a zero seconds component.</summary>
    [Theory]
    [InlineData(59.7d, "1 min ago")]
    [InlineData(90d, "1 min 30 sec ago")]
    [InlineData(125.4d, "2 min 5 sec ago")]
    [InlineData(3540d, "59 min ago")]
    public void FormatAgo_BelowAnHour_RendersMinutesAndSeconds(double ageSeconds, string expected)
        => Assert.Equal(expected, RelativeGameTime.FormatAgo(ageSeconds));

    /// <summary>Hours carry their remaining minutes, omitting a zero minutes component.</summary>
    [Theory]
    [InlineData(3600d, "1 hr ago")]
    [InlineData(5412d, "1 hr 30 min ago")]
    [InlineData(12000d, "3 hr 20 min ago")]
    public void FormatAgo_AnHourAndBeyond_RendersHoursAndMinutes(double ageSeconds, string expected)
        => Assert.Equal(expected, RelativeGameTime.FormatAgo(ageSeconds));

    /// <summary>An exact hour boundary omits the minutes component entirely.</summary>
    [Fact]
    public void FormatAgo_ExactHour_OmitsMinutes()
        => Assert.Equal("2 hr ago", RelativeGameTime.FormatAgo(7200d));
}
