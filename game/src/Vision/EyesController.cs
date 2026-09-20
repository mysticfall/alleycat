using Godot;

namespace AlleyCat.Vision;

/// <summary>
/// Controls VISION-001 Eyes look and blink parameters on an <see cref="AnimationTree"/>.
/// </summary>
public sealed class EyesController
{
    private const float BlinkAnimationClipLengthSeconds = 0.3f;

    private readonly RandomNumberGenerator _random = new();
    private float _targetHorizontalSeekTime = EyesLookMath.NeutralSeekTimeSeconds;
    private float _targetVerticalSeekTime = EyesLookMath.NeutralSeekTimeSeconds;
    private float _timeUntilBlink = -1f;
    private float _blinkElapsed;
    private bool _isBlinking;

    /// <summary>
    /// Initialises a controller bound to the supplied animation tree.
    /// </summary>
    /// <param name="animationTree">The animation tree receiving blink and gaze output.</param>
    /// <param name="transformGazeOutput">
    /// Whether gaze output is emitted through transform-driven eye nodes instead of blendshape look
    /// parameters. This is inferred from <see cref="EyesBehaviour"/> eye-node authoring and never
    /// authored directly.
    /// </param>
    public EyesController(AnimationTree animationTree, bool transformGazeOutput = false)
    {
        AnimationTree = animationTree ?? throw new ArgumentNullException(nameof(animationTree));
        TransformGazeOutput = transformGazeOutput;
        _random.Randomize();
        WriteLookBlendAmounts();
        if (!transformGazeOutput)
        {
            WriteLookWeights();
        }

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
    /// Gets whether gaze output is emitted through transform-driven eye nodes, suppressing the
    /// blendshape look blends and seek requests while blinking continues unchanged.
    /// </summary>
    public bool TransformGazeOutput
    {
        get;
    }

    /// <summary>
    /// Gets the horizontal look seek time currently reported by the shared gaze state.
    /// </summary>
    public float CurrentHorizontalSeekTime { get; private set; } = EyesLookMath.NeutralSeekTimeSeconds;

    /// <summary>
    /// Gets the vertical look seek time currently reported by the shared gaze state.
    /// </summary>
    public float CurrentVerticalSeekTime { get; private set; } = EyesLookMath.NeutralSeekTimeSeconds;

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
        float horizontalLimit = Mathf.DegToRad(Mathf.Max(0.1f, MaxHorizontalAngleDegrees));
        float verticalLimit = Mathf.DegToRad(Mathf.Max(0.1f, MaxVerticalAngleDegrees));

        Vector2 seekTimes = TransformGazeOutput
            ? ResolveTransformLookSeekTimes(lookPointGlobalPosition, horizontalLimit, verticalLimit)
            : EyesLookMath.ResolveLookSeekTimes(
                EyeOriginGlobalTransform,
                lookPointGlobalPosition,
                horizontalLimit,
                verticalLimit);

        _targetHorizontalSeekTime = seekTimes.X;
        _targetVerticalSeekTime = seekTimes.Y;
    }

    private Vector2 ResolveTransformLookSeekTimes(
        Vector3 lookPointGlobalPosition,
        float horizontalLimit,
        float verticalLimit)
    {
        Vector2 lookAngles = EyesLookMath.ResolveLookAngles(
            EyeOriginGlobalTransform,
            lookPointGlobalPosition,
            horizontalLimit,
            verticalLimit);

        return new Vector2(
            EyesLookMath.ConvertSignedAngleToReferenceSeek(lookAngles.X, horizontalLimit),
            EyesLookMath.ConvertSignedAngleToReferenceSeek(lookAngles.Y, verticalLimit));
    }

    /// <summary>
    /// Resolves the current smoothed, clamped gaze rotation in the eye-origin frame for the
    /// transform-driven gaze backend.
    /// </summary>
    public Quaternion ResolveCurrentGazeDelta()
    {
        float horizontalLimit = Mathf.DegToRad(Mathf.Max(0.1f, MaxHorizontalAngleDegrees));
        float verticalLimit = Mathf.DegToRad(Mathf.Max(0.1f, MaxVerticalAngleDegrees));
        float horizontalAngle = EyesLookMath.ConvertReferenceSeekToSignedAngle(CurrentHorizontalSeekTime, horizontalLimit);
        float verticalAngle = EyesLookMath.ConvertReferenceSeekToSignedAngle(CurrentVerticalSeekTime, verticalLimit);
        return EyesLookMath.BuildGazeRotation(horizontalAngle, verticalAngle);
    }

    private void UpdateLook(float delta)
    {
        if (LookSmoothingTime <= 0f)
        {
            CurrentHorizontalSeekTime = _targetHorizontalSeekTime;
            CurrentVerticalSeekTime = _targetVerticalSeekTime;
        }
        else
        {
            float step = delta / LookSmoothingTime;
            CurrentHorizontalSeekTime = Mathf.MoveToward(CurrentHorizontalSeekTime, _targetHorizontalSeekTime, step);
            CurrentVerticalSeekTime = Mathf.MoveToward(CurrentVerticalSeekTime, _targetVerticalSeekTime, step);
        }

        if (!TransformGazeOutput)
        {
            WriteLookWeights();
        }
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

        if (_timeUntilBlink < 0f)
        {
            // Arm the first blink from the configured cadence, which consumers apply through object
            // initialisers after the constructor has run, so it must not be resolved at construction time.
            _timeUntilBlink = ResolveNextBlinkInterval();
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
        // Transform-mode gaze suppresses the blendshape look blends; blink output is unaffected.
        float lookBlendAmount = TransformGazeOutput ? 0f : 1f;
        AnimationTree.Set(EyesAnimationTreePaths.GetHorizontalLookBlendParameter(), lookBlendAmount);
        AnimationTree.Set(EyesAnimationTreePaths.GetVerticalLookBlendParameter(), lookBlendAmount);
    }

    private void WriteLookWeights()
    {
        AnimationTree.Set(EyesAnimationTreePaths.GetHorizontalLookSeekParameter(), CurrentHorizontalSeekTime);
        AnimationTree.Set(EyesAnimationTreePaths.GetVerticalLookSeekParameter(), CurrentVerticalSeekTime);
    }

    private void WriteBlinkTimeScale()
    {
        float duration = Mathf.Max(Mathf.Epsilon, BlinkDuration);
        AnimationTree.Set(EyesAnimationTreePaths.GetBlinkTimeScaleParameter(), BlinkAnimationClipLengthSeconds / duration);
    }
}
