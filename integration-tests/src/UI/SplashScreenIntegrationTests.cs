using System.Globalization;
using System.Reflection;
using AlleyCat.UI;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.UI;

/// <summary>
/// Integration coverage for splash-screen timing and layout behaviour.
/// </summary>
public sealed class SplashScreenIntegrationTests : IAsyncLifetime
{
    private const double AcceleratedTimeScale = 10.0;
    private const string SplashScreenScenePath = "res://assets/ui/splash_screen.tscn";
    private const string DelayRangeHint = "0.0,10.0,0.1,or_greater";
    private const string DurationRangeHint = "0.1,10.0,0.1,or_greater";
    private const float ExpectedLogoWidthRatio = 0.50f;
    private const float LogoWidthRatioTolerance = 0.05f;

    private static readonly FadeTimingFixture[] _timingFixtures =
    [
        new FadeTimingFixture("Default timings", 2.0f, 3.0f),
        new FadeTimingFixture("Short timings", 0.6f, 1.2f),
    ];

    private double _previousTimeScale;

    private readonly record struct FadeTimingFixture(
        string Name,
        float DelaySeconds,
        float DurationSeconds);

    // Engine.TimeScale is global engine state; this test scopes changes to lifecycle and restores it in DisposeAsync.
    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _previousTimeScale = Engine.TimeScale;

        SceneTree sceneTree = GetSceneTree();
        await WaitForNextFrameAsync(sceneTree);
        await WaitForNextFrameAsync(sceneTree);

        Engine.TimeScale = AcceleratedTimeScale;
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        Engine.TimeScale = _previousTimeScale;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies splash-screen layout contracts for viewport fit and logo scale.
    /// </summary>
    [Fact]
    public async Task SplashScreen_Layout_MatchesViewportContracts()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForNextFrameAsync(sceneTree);

        Node splashScreen = Assert.IsAssignableFrom<Node>(sceneTree.CurrentScene);
        Assert.Equal(typeof(SplashScreen).FullName, splashScreen.GetType().FullName);

        Control splashControl = Assert.IsAssignableFrom<Control>(splashScreen);
        Sprite2D logo = splashScreen.GetNode<Sprite2D>("Logo/Image");

        float viewportWidth = splashControl.GetViewport().GetVisibleRect().Size.X;
        Assert.True(viewportWidth > 0.0f, "Viewport width must be positive before logo ratio checks.");

        float logoRenderedWidth = logo.GetRect().Size.X * Mathf.Abs(logo.GlobalScale.X);
        float logoWidthRatio = logoRenderedWidth / viewportWidth;
        // Keep a modest tolerance for viewport/render import variance while enforcing the ~50% spec intent.
        Assert.InRange(
            logoWidthRatio,
            ExpectedLogoWidthRatio - LogoWidthRatioTolerance,
            ExpectedLogoWidthRatio + LogoWidthRatioTolerance);
    }

    /// <summary>
    /// Verifies fade timing behaviour across representative exported-property fixtures.
    /// </summary>
    [Fact]
    public async Task SplashScreen_FadeTimingFixtures_MatchConfiguredDelayAndDuration()
    {
        SceneTree sceneTree = GetSceneTree();
        AssertExportedTimingProperties();

        foreach (FadeTimingFixture fixture in _timingFixtures)
        {
            Node splashScreen = await InstantiateSplashScreenAsync(sceneTree, fixture);
            Sprite2D logo = splashScreen.GetNode<Sprite2D>("Logo/Image");

            try
            {
                Assert.InRange(logo.Modulate.A, 0.0f, 0.01f);

                double preFadeWaitSeconds = Math.Max(0.05, fixture.DelaySeconds * 0.85);
                await WaitForSecondsAsync(sceneTree, preFadeWaitSeconds);
                Assert.InRange(
                    logo.Modulate.A,
                    0.0f,
                    0.08f);

                double midFadeWaitSeconds = Math.Max(0.05, fixture.DelaySeconds + (fixture.DurationSeconds * 0.35) - preFadeWaitSeconds);
                await WaitForSecondsAsync(sceneTree, midFadeWaitSeconds);
                Assert.InRange(
                    logo.Modulate.A,
                    0.08f,
                    0.70f);

                double nearEndWaitSeconds = Math.Max(0.05, fixture.DurationSeconds * 0.80);
                await WaitForSecondsAsync(sceneTree, nearEndWaitSeconds);
                Assert.InRange(
                    logo.Modulate.A,
                    0.95f,
                    1.0f);
            }
            catch (Xunit.Sdk.XunitException assertionError)
            {
                throw new Xunit.Sdk.XunitException($"{fixture.Name} failed: {assertionError.Message}");
            }
            finally
            {
                splashScreen.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }
    }

    private static async Task<Node> InstantiateSplashScreenAsync(
        SceneTree sceneTree,
        FadeTimingFixture fixture)
    {
        PackedScene splashScene = LoadPackedScene(SplashScreenScenePath);
        Node splashScreen = splashScene.Instantiate();
        splashScreen.Set(nameof(SplashScreen.FadeDelaySeconds), fixture.DelaySeconds);
        splashScreen.Set(nameof(SplashScreen.FadeDurationSeconds), fixture.DurationSeconds);

        sceneTree.Root.AddChild(splashScreen);
        await WaitForFramesAsync(sceneTree, 2);

        return splashScreen;
    }

    private static void AssertExportedTimingProperties()
    {
        PropertyInfo? fadeDelayProperty = typeof(SplashScreen).GetProperty(nameof(SplashScreen.FadeDelaySeconds));
        PropertyInfo? fadeDurationProperty = typeof(SplashScreen).GetProperty(nameof(SplashScreen.FadeDurationSeconds));

        Assert.NotNull(fadeDelayProperty);
        Assert.NotNull(fadeDurationProperty);

        ExportAttribute? fadeDelayExport = fadeDelayProperty.GetCustomAttribute<ExportAttribute>();
        ExportAttribute? fadeDurationExport = fadeDurationProperty.GetCustomAttribute<ExportAttribute>();

        Assert.NotNull(fadeDelayExport);
        Assert.NotNull(fadeDurationExport);

        Assert.Equal(PropertyHint.Range, fadeDelayExport.Hint);
        Assert.Equal(PropertyHint.Range, fadeDurationExport.Hint);

        Assert.Equal(DelayRangeHint, fadeDelayExport.HintString);
        Assert.Equal(DurationRangeHint, fadeDurationExport.HintString);

        AssertHintContainsDefaultValue(DelayRangeHint, 2.0f);
        AssertHintContainsDefaultValue(DurationRangeHint, 3.0f);
    }

    private static void AssertHintContainsDefaultValue(string hintString, float defaultValue)
    {
        string[] tokens = hintString.Split(',');
        Assert.True(tokens.Length >= 2, "Range hint should include at least minimum and maximum values.");

        float minimum = float.Parse(tokens[0], CultureInfo.InvariantCulture);
        float maximum = float.Parse(tokens[1], CultureInfo.InvariantCulture);

        Assert.InRange(defaultValue, minimum, maximum);
    }
}
