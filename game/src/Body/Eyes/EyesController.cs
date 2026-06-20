using Godot;

namespace AlleyCat.Body.Eyes;

/// <summary>
/// Controls BODY-004 Eyes look and blink parameters on an <see cref="AnimationTree"/>.
/// </summary>
public sealed class EyesController
{
    private const float BlinkAnimationClipLengthSeconds = 0.3f;

    private readonly RandomNumberGenerator _random = new();
    private float _targetHorizontalSeekTime = EyesLookMath.NeutralSeekTimeSeconds;
    private float _targetVerticalSeekTime = EyesLookMath.NeutralSeekTimeSeconds;
    private float _currentHorizontalSeekTime = EyesLookMath.NeutralSeekTimeSeconds;
    private float _currentVerticalSeekTime = EyesLookMath.NeutralSeekTimeSeconds;
    private float _timeUntilBlink;
    private float _blinkElapsed;
    private bool _isBlinking;

    /// <summary>
    /// Initialises a controller bound to the supplied animation tree.
    /// </summary>
    public EyesController(AnimationTree animationTree)
    {
        AnimationTree = animationTree ?? throw new ArgumentNullException(nameof(animationTree));
        _random.Randomize();
        _timeUntilBlink = ResolveNextBlinkInterval();
        WriteLookBlendAmounts();
        WriteLookWeights();
        WriteBlinkTimeScale();
    }

    /// <summary>
    /// Gets the controlled animation tree.
    /// </summary>
    public AnimationTree AnimationTree
    {
        get;
    }

    /// <summary>
    /// Gets or sets the world transform used as the eye/viewpoint origin.
    /// </summary>
    public Transform3D EyeOriginGlobalTransform
    {
        get; set;
    } = Transform3D.Identity;

    /// <summary>
    /// Gets or sets the maximum horizontal eye angle in degrees.
    /// </summary>
    public float MaxHorizontalAngleDegrees { get; set; } = 35f;

    /// <summary>
    /// Gets or sets the maximum vertical eye angle in degrees.
    /// </summary>
    public float MaxVerticalAngleDegrees { get; set; } = 25f;

    /// <summary>
    /// Gets or sets the smoothing time in seconds used for visible eye movement.
    /// </summary>
    public float LookSmoothingTime { get; set; } = 0.08f;

    /// <summary>
    /// Gets or sets the minimum random interval between blinks.
    /// </summary>
    public float MinimumBlinkInterval { get; set; } = 2.5f;

    /// <summary>
    /// Gets or sets the maximum random interval between blinks.
    /// </summary>
    public float MaximumBlinkInterval { get; set; } = 6f;

    /// <summary>
    /// Gets or sets the duration of a blink in seconds.
    /// </summary>
    public float BlinkDuration { get; set; } = 0.3f;

    /// <summary>
    /// Starts a blink immediately while preserving the configured blink duration.
    /// </summary>
    public void TriggerBlink()
    {
        _isBlinking = true;
        _blinkElapsed = 0f;
        WriteBlinkTimeScale();
        AnimationTree.Set(
            EyesAnimationTreePaths.GetBlinkOneShotRequestParameter(),
            (int)AnimationNodeOneShot.OneShotRequest.Fire);
    }

    /// <summary>
    /// Advances look smoothing and autonomous blinking.
    /// </summary>
    public void Update(double deltaSeconds, Vector3 lookPointGlobalPosition)
    {
        float delta = (float)Math.Max(0.0, deltaSeconds);
        WriteLookBlendAmounts();
        ResolveTargetLookWeights(lookPointGlobalPosition);
        UpdateLook(delta);
        UpdateBlink(delta);
    }

    private void ResolveTargetLookWeights(Vector3 lookPointGlobalPosition)
    {
        Vector2 seekTimes = EyesLookMath.ResolveLookSeekTimes(
            EyeOriginGlobalTransform,
            lookPointGlobalPosition,
            Mathf.DegToRad(Mathf.Max(0.1f, MaxHorizontalAngleDegrees)),
            Mathf.DegToRad(Mathf.Max(0.1f, MaxVerticalAngleDegrees)));

        _targetHorizontalSeekTime = seekTimes.X;
        _targetVerticalSeekTime = seekTimes.Y;
    }

    private void UpdateLook(float delta)
    {
        if (LookSmoothingTime <= 0f)
        {
            _currentHorizontalSeekTime = _targetHorizontalSeekTime;
            _currentVerticalSeekTime = _targetVerticalSeekTime;
        }
        else
        {
            float step = delta / LookSmoothingTime;
            _currentHorizontalSeekTime = Mathf.MoveToward(_currentHorizontalSeekTime, _targetHorizontalSeekTime, step);
            _currentVerticalSeekTime = Mathf.MoveToward(_currentVerticalSeekTime, _targetVerticalSeekTime, step);
        }

        WriteLookWeights();
    }

    private void UpdateBlink(float delta)
    {
        if (_isBlinking)
        {
            float duration = Mathf.Max(Mathf.Epsilon, BlinkDuration);
            _blinkElapsed += delta;
            if (_blinkElapsed >= duration)
            {
                _isBlinking = false;
                _blinkElapsed = 0f;
                _timeUntilBlink = ResolveNextBlinkInterval();
            }

            return;
        }

        _timeUntilBlink -= delta;
        if (_timeUntilBlink <= 0f)
        {
            TriggerBlink();
        }
    }

    private float ResolveNextBlinkInterval()
    {
        float minimum = Mathf.Max(0f, MinimumBlinkInterval);
        float maximum = Mathf.Max(minimum, MaximumBlinkInterval);
        return Mathf.IsEqualApprox(minimum, maximum) ? minimum : _random.RandfRange(minimum, maximum);
    }

    private void WriteLookBlendAmounts()
    {
        AnimationTree.Set(EyesAnimationTreePaths.GetHorizontalLookBlendParameter(), 1f);
        AnimationTree.Set(EyesAnimationTreePaths.GetVerticalLookBlendParameter(), 1f);
    }

    private void WriteLookWeights()
    {
        AnimationTree.Set(EyesAnimationTreePaths.GetHorizontalLookSeekParameter(), _currentHorizontalSeekTime);
        AnimationTree.Set(EyesAnimationTreePaths.GetVerticalLookSeekParameter(), _currentVerticalSeekTime);
    }

    private void WriteBlinkTimeScale()
    {
        float duration = Mathf.Max(Mathf.Epsilon, BlinkDuration);
        AnimationTree.Set(EyesAnimationTreePaths.GetBlinkTimeScaleParameter(), BlinkAnimationClipLengthSeconds / duration);
    }
}
