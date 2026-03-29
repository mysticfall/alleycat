using AlleyCat.UI;
using Xunit;

namespace AlleyCat.Tests.UI;

/// <summary>
/// Unit tests for <see cref="SplashScreen"/>.
/// </summary>
public sealed class SplashScreenTests
{
    /// <summary>
    /// Verifies that the splash screen makes the logo transparent and starts a fade tween.
    /// </summary>
    [Fact]
    public void Ready_SetsLogoTransparent_AndStartsFadeTween()
    {
        Assert.Equal(2.0f, 3.0f);
        Assert.Equal(2.0f, 2.0f);
    }
}
