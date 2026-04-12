using AlleyCat.Common;
using Godot;

namespace AlleyCat.UI;

/// <summary>
/// Displays the opening splash screen, fades the logo in, then fades it out.
/// </summary>
[GlobalClass]
public partial class SplashScreen : Control
{
    private const float TargetLogoWidthRatio = 0.5f;

    /// <summary>
    /// Delay in seconds before the logo fade begins.
    /// </summary>
    [Export(PropertyHint.Range, "0.0,10.0,0.1,or_greater")]
    public float FadeInDelaySeconds { get; set; } = 2.0f;

    /// <summary>
    /// Duration in seconds for the logo fade animation.
    /// </summary>
    [Export(PropertyHint.Range, "0.1,10.0,0.1,or_greater")]
    public float FadeDurationSeconds { get; set; } = 2.0f;

    /// <summary>
    /// Delay in seconds after fade-in before fade-out starts.
    /// </summary>
    [Export(PropertyHint.Range, "0.0,10.0,0.1,or_greater")]
    public float FadeOutDelaySeconds { get; set; } = 3.0f;

    /// <summary>
    /// Emitted once the splash fade-out has completed.
    /// </summary>
    [Signal]
    private delegate void SplashFinishedEventHandler();

    private Sprite2D? _logo;

    /// <inheritdoc />
    public override void _Ready()
    {
        _logo = this.RequireNode<Sprite2D>("Logo/Image");

        ScaleLogoToViewportWidth();

        Color modulate = _logo.Modulate;
        modulate.A = 0.0f;

        _logo.Modulate = modulate;

        Tween tween = CreateTween();
        _ = tween
            .TweenProperty(_logo, "modulate:a", 1.0, FadeDurationSeconds)
            .SetDelay(FadeInDelaySeconds);

        _ = tween.TweenInterval(FadeOutDelaySeconds);
        _ = tween.TweenProperty(_logo, "modulate:a", 0.0, FadeDurationSeconds);
        _ = tween.TweenCallback(Callable.From(EmitSplashFinished));
    }

    private void EmitSplashFinished()
        => EmitSignal(SignalName.SplashFinished);

    private void ScaleLogoToViewportWidth()
    {
        Texture2D? texture = _logo!.Texture;

        float viewportWidth = GetViewportRect().Size.X;

        if (texture is null || viewportWidth <= 0.0f)
        {
            return;
        }

        float textureWidth = texture.GetSize().X;
        if (textureWidth <= 0.0f)
        {
            return;
        }

        float uniformScale = viewportWidth * TargetLogoWidthRatio / textureWidth;
        _logo.Scale = new Vector2(uniformScale, uniformScale);
    }
}
