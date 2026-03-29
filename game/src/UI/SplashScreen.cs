using Godot;

namespace AlleyCat.UI;

/// <summary>
/// Displays the opening splash screen and fades the logo in.
/// </summary>
public partial class SplashScreen : Control
{
    private const float FadeDelaySeconds = 2.0f;
    private const float FadeDurationSeconds = 3.0f;

    private TextureRect _logo = null!;

    /// <summary>
    /// Starts the logo fade-in when the scene enters the tree.
    /// </summary>
    public override void _Ready()
    {
        _logo = GetLogoNode();

        Color modulate = ReadLogoModulate(_logo);
        modulate.A = 0.0f;
        WriteLogoModulate(_logo, modulate);

        TweenLogoAlpha(_logo, 1.0f, FadeDelaySeconds, FadeDurationSeconds);
    }

    /// <summary>
    /// Resolves the logo node shown on the splash screen.
    /// </summary>
    protected virtual TextureRect GetLogoNode() => GetNode<TextureRect>("Logo");

    /// <summary>
    /// Reads the current modulate colour from the logo.
    /// </summary>
    /// <param name="logo">The logo control.</param>
    /// <returns>The logo modulate colour.</returns>
    protected virtual Color ReadLogoModulate(TextureRect logo) => logo.Modulate;

    /// <summary>
    /// Writes the modulate colour to the logo.
    /// </summary>
    /// <param name="logo">The logo control.</param>
    /// <param name="modulate">The modulate colour to assign.</param>
    protected virtual void WriteLogoModulate(TextureRect logo, Color modulate) => logo.Modulate = modulate;

    /// <summary>
    /// Starts a tween that animates the logo alpha value.
    /// </summary>
    /// <param name="logo">The logo control.</param>
    /// <param name="targetAlpha">The alpha value to tween to.</param>
    /// <param name="delaySeconds">The delay before fade starts, in seconds.</param>
    /// <param name="durationSeconds">The fade duration in seconds.</param>
    protected virtual void TweenLogoAlpha(TextureRect logo, float targetAlpha, float delaySeconds, float durationSeconds)
    {
        Tween tween = CreateTween();
        _ = tween
            .TweenProperty(logo, "modulate:a", targetAlpha, durationSeconds)
            .SetDelay(delaySeconds);
    }
}
