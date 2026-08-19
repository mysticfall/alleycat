namespace AlleyCat.Mind.Attention;

/// <summary>
/// Anchor-assignment continuity for one orienting evaluation step, as observed by the adapter that owns the
/// vision look target.
/// </summary>
public enum OrientingAnchorState
{
    /// <summary>No anchor is assigned this frame; the look target is clear.</summary>
    None,

    /// <summary>The anchor assigned this frame is the same anchor as the previous evaluation.</summary>
    SameAnchor,

    /// <summary>The anchor assigned this frame is a different anchor from the previous evaluation.</summary>
    NewAnchor,
}

/// <summary>
/// Outcome of validating <see cref="OrientingSettings"/> authoring before activation.
/// </summary>
/// <param name="IsValid">
/// <see langword="true"/> when the settings satisfy every tuning contract; otherwise <see langword="false"/>.
/// </param>
/// <param name="FailureReason">
/// Human-readable description of the first failed contract, in the AI-007 settings-validation message style;
/// <see langword="null"/> when the settings are valid.
/// </param>
public readonly record struct OrientingSettingsValidation(bool IsValid, string? FailureReason);

/// <summary>
/// Authoring carrier for <see cref="OrientingPolicy"/> tuning. Instances are deliberately constructible without
/// validation so a Godot-side controller can hold exported values before checking them; call
/// <see cref="Validate"/> for a non-throwing pass/fail verdict, or construct an <see cref="OrientingPolicy"/>,
/// which rejects invalid settings.
/// </summary>
/// <param name="ComfortConeHorizontalRadians">Symmetric horizontal eye comfort cone half-angle in radians.</param>
/// <param name="ComfortConeUpRadians">Upward eye comfort cone angle in radians.</param>
/// <param name="ComfortConeDownRadians">Downward eye comfort cone angle in radians.</param>
/// <param name="EnvelopeHorizontalRadians">Symmetric horizontal head orientation envelope in radians.</param>
/// <param name="EnvelopeUpRadians">Upward head orientation envelope in radians.</param>
/// <param name="EnvelopeDownRadians">Downward head orientation envelope in radians.</param>
/// <param name="CentringDelaySeconds">
/// Continuous same-anchor assignment required before sustained centring engages. Must exceed the AI-007 secondary
/// dwell default (0.5 s) with a safe margin so brief secondary glances never become centring.
/// </param>
/// <param name="ReactionDelaySeconds">Engagement pause before the head starts toward a newly engaged aim.</param>
/// <param name="MaxHorizontalRateRadiansPerSecond">Horizontal aim rate cap.</param>
/// <param name="MaxVerticalRateRadiansPerSecond">Vertical aim rate cap.</param>
/// <param name="AimSmoothingSeconds">
/// Exponential approach time constant for aim smoothing; smaller values track the target more directly. The rate
/// caps remain the authoritative speed limit.
/// </param>
/// <param name="InfluenceEngagePerSecond">Influence ramp rate while an anchor is assigned.</param>
/// <param name="InfluenceReleasePerSecond">Influence ramp rate while no anchor is assigned.</param>
/// <param name="SaturationEngageMarginRadians">
/// Extra angle beyond the comfort cone required to engage saturation on an axis.
/// </param>
/// <param name="SaturationReleaseMarginRadians">
/// Angle back inside the comfort cone required to release an already-engaged saturation axis.
/// </param>
/// <param name="ResidualEccentricityHorizontalRadians">
/// Horizontal angle the sustained aim deliberately leaves short of full centring, keeping the eyes slightly
/// eccentric; zero centres the anchor fully onto the eye-neutral axis.
/// </param>
/// <param name="ResidualEccentricityVerticalRadians">Vertical counterpart of the horizontal residual eccentricity.</param>
public sealed record OrientingSettings(
    double ComfortConeHorizontalRadians,
    double ComfortConeUpRadians,
    double ComfortConeDownRadians,
    double EnvelopeHorizontalRadians,
    double EnvelopeUpRadians,
    double EnvelopeDownRadians,
    double CentringDelaySeconds,
    double ReactionDelaySeconds,
    double MaxHorizontalRateRadiansPerSecond,
    double MaxVerticalRateRadiansPerSecond,
    double AimSmoothingSeconds,
    double InfluenceEngagePerSecond,
    double InfluenceReleasePerSecond,
    double SaturationEngageMarginRadians,
    double SaturationReleaseMarginRadians,
    double ResidualEccentricityHorizontalRadians,
    double ResidualEccentricityVerticalRadians)
{
    /// <summary>
    /// Gets the physiological tuning defaults declared by AI-009: comfort cone of ±15° horizontal and 10° up /
    /// 15° down, orientation envelope of ±75° horizontal and 40° up / 55° down, centring delay of 0.6 s
    /// (exceeding the AI-007 default secondary dwell of 0.5 s by a 0.1 s margin), reaction delay of 0.18 s,
    /// head rates materially slower than eye seek (eyes use a 0.08 s seek smoothing), and modest hysteresis and
    /// ramp values chosen for smooth engagement.
    /// </summary>
    public static OrientingSettings Default
    {
        get;
    } = new(
        ComfortConeHorizontalRadians: DegreesToRadians(15d),
        ComfortConeUpRadians: DegreesToRadians(10d),
        ComfortConeDownRadians: DegreesToRadians(15d),
        EnvelopeHorizontalRadians: DegreesToRadians(75d),
        EnvelopeUpRadians: DegreesToRadians(40d),
        EnvelopeDownRadians: DegreesToRadians(55d),
        CentringDelaySeconds: 0.6d,
        ReactionDelaySeconds: 0.18d,
        MaxHorizontalRateRadiansPerSecond: 2.5d,
        MaxVerticalRateRadiansPerSecond: 1.8d,
        AimSmoothingSeconds: 0.12d,
        InfluenceEngagePerSecond: 4d,
        InfluenceReleasePerSecond: 3d,
        SaturationEngageMarginRadians: DegreesToRadians(2d),
        SaturationReleaseMarginRadians: DegreesToRadians(2d),
        ResidualEccentricityHorizontalRadians: 0d,
        ResidualEccentricityVerticalRadians: 0d);

    /// <summary>
    /// Checks every tuning contract in a deterministic order and reports the first failure, or passes. Contracts:
    /// all angles and durations finite; cone, envelope, delays, rates, ramps, and smoothing finite and positive;
    /// hysteresis margins finite and non-negative with combined margins leaving a positive cone per axis; each
    /// envelope angle exceeding the corresponding comfort-cone angle per axis; and residual eccentricity finite,
    /// non-negative, and smaller than the corresponding comfort-cone bound.
    /// </summary>
    public OrientingSettingsValidation Validate()
    {
        string? failure =
            NotFinitePositive(ComfortConeHorizontalRadians, nameof(ComfortConeHorizontalRadians))
            ?? NotFinitePositive(ComfortConeUpRadians, nameof(ComfortConeUpRadians))
            ?? NotFinitePositive(ComfortConeDownRadians, nameof(ComfortConeDownRadians))
            ?? NotFinitePositive(EnvelopeHorizontalRadians, nameof(EnvelopeHorizontalRadians))
            ?? NotFinitePositive(EnvelopeUpRadians, nameof(EnvelopeUpRadians))
            ?? NotFinitePositive(EnvelopeDownRadians, nameof(EnvelopeDownRadians))
            ?? MustExceed(
                EnvelopeHorizontalRadians,
                nameof(EnvelopeHorizontalRadians),
                ComfortConeHorizontalRadians,
                nameof(ComfortConeHorizontalRadians))
            ?? MustExceed(EnvelopeUpRadians, nameof(EnvelopeUpRadians), ComfortConeUpRadians, nameof(ComfortConeUpRadians))
            ?? MustExceed(
                EnvelopeDownRadians,
                nameof(EnvelopeDownRadians),
                ComfortConeDownRadians,
                nameof(ComfortConeDownRadians))
            ?? NotFinitePositive(CentringDelaySeconds, nameof(CentringDelaySeconds))
            ?? NotFinitePositive(ReactionDelaySeconds, nameof(ReactionDelaySeconds))
            ?? NotFinitePositive(MaxHorizontalRateRadiansPerSecond, nameof(MaxHorizontalRateRadiansPerSecond))
            ?? NotFinitePositive(MaxVerticalRateRadiansPerSecond, nameof(MaxVerticalRateRadiansPerSecond))
            ?? NotFinitePositive(AimSmoothingSeconds, nameof(AimSmoothingSeconds))
            ?? NotFinitePositive(InfluenceEngagePerSecond, nameof(InfluenceEngagePerSecond))
            ?? NotFinitePositive(InfluenceReleasePerSecond, nameof(InfluenceReleasePerSecond))
            ?? NotFiniteNonNegative(SaturationEngageMarginRadians, nameof(SaturationEngageMarginRadians))
            ?? NotFiniteNonNegative(SaturationReleaseMarginRadians, nameof(SaturationReleaseMarginRadians))
            ?? MarginsLeavePositiveCone()
            ?? EccentricityBelowCone(
                ResidualEccentricityHorizontalRadians,
                nameof(ResidualEccentricityHorizontalRadians),
                ComfortConeHorizontalRadians,
                nameof(ComfortConeHorizontalRadians))
            ?? EccentricityBelowCone(
                ResidualEccentricityVerticalRadians,
                nameof(ResidualEccentricityVerticalRadians),
                Math.Min(ComfortConeUpRadians, ComfortConeDownRadians),
                "the smaller vertical comfort-cone angle");

        return failure is null
            ? new OrientingSettingsValidation(true, null)
            : new OrientingSettingsValidation(false, failure);
    }

    private string? MarginsLeavePositiveCone()
    {
        double combinedMargins = SaturationEngageMarginRadians + SaturationReleaseMarginRadians;
        return ComfortConeHorizontalRadians > combinedMargins
            && ComfortConeUpRadians > combinedMargins
            && ComfortConeDownRadians > combinedMargins
            ? null
            : $"Combined saturation hysteresis margins '{combinedMargins}' must leave a positive comfort cone on every axis.";
    }

    private static string? NotFinitePositive(double value, string settingName)
        => !double.IsFinite(value) || value <= 0d
            ? $"{settingName} must be finite and positive, but found '{value}'."
            : null;

    private static string? NotFiniteNonNegative(double value, string settingName)
        => !double.IsFinite(value) || value < 0d
            ? $"{settingName} must be finite and non-negative, but found '{value}'."
            : null;

    private static string? MustExceed(double value, string valueName, double bound, string boundName)
        => value <= bound
            ? $"{valueName} must exceed {boundName} '{bound}', but found '{value}'."
            : null;

    private static string? EccentricityBelowCone(double eccentricity, string eccentricityName, double cone, string coneName)
        => !double.IsFinite(eccentricity) || eccentricity < 0d || eccentricity >= cone
            ? $"{eccentricityName} must be finite, non-negative, and smaller than {coneName} '{cone}', but found '{eccentricity}'."
            : null;

    private static double DegreesToRadians(double degrees)
        => degrees * Math.PI / 180d;
}

/// <summary>
/// One delta-driven orienting evaluation input, expressed in the current solved head and eye-line frame. All
/// angles are radians. Positive values mean the head must rotate positively to centre the anchor: positive
/// horizontal is yaw in the anchor's direction from the eye-neutral axis, and positive vertical is upward pitch.
/// The adapter owns every transform conversion.
/// </summary>
/// <param name="DeltaSeconds">Finite, non-negative elapsed seconds since the previous evaluation.</param>
/// <param name="AnchorState">Anchor-assignment continuity observed this frame.</param>
/// <param name="HorizontalErrorRadians">Signed horizontal angular error of the anchor direction.</param>
/// <param name="VerticalErrorRadians">Signed vertical angular error of the anchor direction.</param>
public readonly record struct OrientingEvaluation(
    double DeltaSeconds,
    OrientingAnchorState AnchorState,
    double HorizontalErrorRadians,
    double VerticalErrorRadians);

/// <summary>
/// Desired head orientation intent for one frame: per-axis aim in radians using the evaluation sign convention,
/// plus the desired influence in 0..1 that blends this intent into the head orientation path.
/// </summary>
/// <param name="HorizontalRadians">Desired horizontal head aim in radians.</param>
/// <param name="VerticalRadians">Desired vertical head aim in radians.</param>
/// <param name="Influence">Desired influence in the inclusive range 0..1.</param>
public readonly record struct OrientingAim(
    double HorizontalRadians,
    double VerticalRadians,
    double Influence);

/// <summary>
/// Delta-driven, deterministic head-orientation decision seam for AI-009. The policy is pure C# without Godot
/// types: it consumes per-axis angular errors of the anchor direction relative to the current head and eye-line
/// frame, plus anchor-assignment continuity, and produces the desired per-axis head aim and influence. The
/// Godot-side <c>OrientingController</c> adapter (not this type) converts transforms to and from these
/// angles, forming the closed-loop servo described by AI-009: as the head turns, the adapter-fed residual
/// shrinks.
/// </summary>
/// <remarks>
/// <para>
/// Each evaluation resolves one aim mode by descending strength — sustained centring, saturation, glance hold,
/// then neutral release — and shares state across calls:
/// <list type="number">
/// <item><description>Sustained centring: the same anchor continuously assigned for at least
/// <see cref="OrientingSettings.CentringDelaySeconds"/> aims the head at full centring (the live error minus the
/// residual eccentricity), even well inside the comfort cone. The assignment frame counts zero continuous
/// duration, matching the AI-007 dwell convention.</description></item>
/// <item><description>Saturation: per axis, an anchor direction outside the comfort cone aims the head at only
/// the residual that brings the direction back just inside the cone boundary — never full
/// centring.</description></item>
/// <item><description>Glance hold: a brief in-cone assignment of a different anchor keeps easing toward the last
/// sustained centring aim (or neutral when none exists), never toward the glance anchor.</description></item>
/// <item><description>Neutral release: no anchor eases the aim to neutral and ramps influence to
/// 0.</description></item>
/// </list>
/// </para>
/// <para>
/// Hysteresis mechanism: saturation is evaluated per axis with an engage threshold at
/// <c>cone + SaturationEngageMarginRadians</c> and a release threshold at
/// <c>cone − SaturationReleaseMarginRadians</c>. Between the thresholds the previous engaged state persists, so
/// a target hovering at the cone boundary cannot flap the aim between the residual and the held aim. Saturation
/// state resets on anchor change or clear, so each assignment earns its own latch.
/// </para>
/// <para>
/// Reaction mechanism: the modes form an engagement ladder by declaration order
/// (none &lt; hold &lt; saturation &lt; centring). Whenever the resolved mode strengthens, the aim holds still
/// for <see cref="OrientingSettings.ReactionDelaySeconds"/> before it starts toward the newly engaged target,
/// which keeps the head visibly delayed relative to eye seek. Downgrades and releases are not delayed, so ease
/// back paths start immediately; the influence ramp runs independently of the reaction gate.
/// </para>
/// <para>
/// Motion feel: aim movement per axis is an exponential approach towards the target capped by the per-axis rate
/// limit, so the head never overshoots; influence ramps linearly between 0 and 1 and never steps; and every aim
/// is clamped to the per-axis orientation envelope, which is asymmetric vertically.
/// </para>
/// <para>
/// Timing resets on anchor change and on clear; both restart centring accumulation and saturation latches.
/// </para>
/// </remarks>
public sealed class OrientingPolicy(OrientingSettings settings)
{
    private readonly OrientingSettings _settings = CreateValidatedSettings(settings);

    private double _anchorElapsedSeconds;
    private double _reactionRemainingSeconds;
    private bool _horizontalSaturationEngaged;
    private bool _verticalSaturationEngaged;
    private double _heldHorizontalRadians;
    private double _heldVerticalRadians;
    private double _aimHorizontalRadians;
    private double _aimVerticalRadians;
    private double _influence;
    private OrientingMode _mode;

    /// <summary>
    /// Advances the policy by one frame and resolves the desired head aim and influence for the supplied
    /// angular errors and anchor continuity.
    /// </summary>
    public OrientingAim Evaluate(OrientingEvaluation evaluation)
    {
        ValidateEvaluation(evaluation);
        double deltaSeconds = evaluation.DeltaSeconds;

        UpdateAnchorTiming(evaluation, deltaSeconds);

        bool hasAnchor = evaluation.AnchorState != OrientingAnchorState.None;
        bool isSustained = hasAnchor && _anchorElapsedSeconds >= _settings.CentringDelaySeconds;
        if (hasAnchor)
        {
            _horizontalSaturationEngaged = ResolveSaturationEngaged(
                _horizontalSaturationEngaged,
                evaluation.HorizontalErrorRadians,
                _settings.ComfortConeHorizontalRadians,
                _settings.ComfortConeHorizontalRadians);
            _verticalSaturationEngaged = ResolveSaturationEngaged(
                _verticalSaturationEngaged,
                evaluation.VerticalErrorRadians,
                _settings.ComfortConeUpRadians,
                _settings.ComfortConeDownRadians);
        }

        OrientingMode mode = !hasAnchor
            ? OrientingMode.None
            : isSustained
                ? OrientingMode.Centring
                : _horizontalSaturationEngaged || _verticalSaturationEngaged
                    ? OrientingMode.Saturation
                    : OrientingMode.Hold;

        if (mode == OrientingMode.None)
        {
            _reactionRemainingSeconds = 0d;
        }
        else if ((int)mode > (int)_mode)
        {
            _reactionRemainingSeconds = _settings.ReactionDelaySeconds;
        }
        else if (_reactionRemainingSeconds > 0d)
        {
            _reactionRemainingSeconds = Math.Max(0d, _reactionRemainingSeconds - deltaSeconds);
        }

        bool reactionPending = _reactionRemainingSeconds > 0d;
        (double targetHorizontalRadians, double targetVerticalRadians) = ResolveTargetAim(mode, evaluation);

        if (!reactionPending)
        {
            _aimHorizontalRadians = MoveTowardSmoothed(
                _aimHorizontalRadians,
                targetHorizontalRadians,
                _settings.MaxHorizontalRateRadiansPerSecond,
                deltaSeconds);
            _aimVerticalRadians = MoveTowardSmoothed(
                _aimVerticalRadians,
                targetVerticalRadians,
                _settings.MaxVerticalRateRadiansPerSecond,
                deltaSeconds);
        }

        _influence = mode != OrientingMode.None
            ? Math.Min(1d, _influence + (_settings.InfluenceEngagePerSecond * deltaSeconds))
            : Math.Max(0d, _influence - (_settings.InfluenceReleasePerSecond * deltaSeconds));

        _mode = mode;
        return new OrientingAim(
            ClampHorizontal(_aimHorizontalRadians),
            ClampVertical(_aimVerticalRadians),
            _influence);
    }

    private static OrientingSettings CreateValidatedSettings(OrientingSettings? candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        OrientingSettingsValidation validation = candidate.Validate();
        return validation.IsValid
            ? candidate
            : throw new InvalidOperationException($"Orienting policy settings are invalid: {validation.FailureReason}");
    }

    private void UpdateAnchorTiming(OrientingEvaluation evaluation, double deltaSeconds)
    {
        if (evaluation.AnchorState == OrientingAnchorState.SameAnchor)
        {
            _anchorElapsedSeconds += deltaSeconds;
        }
        else
        {
            _anchorElapsedSeconds = 0d;
            _horizontalSaturationEngaged = false;
            _verticalSaturationEngaged = false;
        }
    }

    private (double HorizontalRadians, double VerticalRadians) ResolveTargetAim(
        OrientingMode mode,
        OrientingEvaluation evaluation)
    {
        if (mode == OrientingMode.None)
        {
            return (0d, 0d);
        }

        if (mode == OrientingMode.Centring)
        {
            double centringHorizontal = ResolveCentringAim(
                evaluation.HorizontalErrorRadians,
                _settings.ResidualEccentricityHorizontalRadians,
                _settings.EnvelopeHorizontalRadians,
                _settings.EnvelopeHorizontalRadians);
            double centringVertical = ResolveCentringAim(
                evaluation.VerticalErrorRadians,
                _settings.ResidualEccentricityVerticalRadians,
                _settings.EnvelopeUpRadians,
                _settings.EnvelopeDownRadians);
            _heldHorizontalRadians = centringHorizontal;
            _heldVerticalRadians = centringVertical;
            return (centringHorizontal, centringVertical);
        }

        double glanceHorizontal = _horizontalSaturationEngaged
            ? ClampHorizontal(ResolveSaturationAim(
                evaluation.HorizontalErrorRadians,
                _settings.ComfortConeHorizontalRadians,
                _settings.ComfortConeHorizontalRadians))
            : _heldHorizontalRadians;
        double glanceVertical = _verticalSaturationEngaged
            ? ClampVertical(ResolveSaturationAim(
                evaluation.VerticalErrorRadians,
                _settings.ComfortConeUpRadians,
                _settings.ComfortConeDownRadians))
            : _heldVerticalRadians;
        return (glanceHorizontal, glanceVertical);
    }

    private bool ResolveSaturationEngaged(
        bool currentlyEngaged,
        double errorRadians,
        double positiveConeRadians,
        double negativeConeRadians)
    {
        double magnitude = Math.Abs(errorRadians);
        double coneBound = errorRadians >= 0d ? positiveConeRadians : negativeConeRadians;
        return currentlyEngaged
            ? magnitude > coneBound - _settings.SaturationReleaseMarginRadians
            : magnitude >= coneBound + _settings.SaturationEngageMarginRadians;
    }

    private static double ResolveSaturationAim(double errorRadians, double positiveConeRadians, double negativeConeRadians)
    {
        double coneBound = errorRadians >= 0d ? positiveConeRadians : negativeConeRadians;
        double residual = Math.Max(0d, Math.Abs(errorRadians) - coneBound);
        return Math.Sign(errorRadians) * residual;
    }

    private static double ResolveCentringAim(
        double errorRadians,
        double residualEccentricityRadians,
        double positiveEnvelopeRadians,
        double negativeEnvelopeRadians)
    {
        double centring = Math.Sign(errorRadians) * Math.Max(0d, Math.Abs(errorRadians) - residualEccentricityRadians);
        return Math.Clamp(centring, -negativeEnvelopeRadians, positiveEnvelopeRadians);
    }

    private double ClampHorizontal(double radians)
        => Math.Clamp(radians, -_settings.EnvelopeHorizontalRadians, _settings.EnvelopeHorizontalRadians);

    private double ClampVertical(double radians)
        => Math.Clamp(radians, -_settings.EnvelopeDownRadians, _settings.EnvelopeUpRadians);

    private double MoveTowardSmoothed(
        double currentRadians,
        double targetRadians,
        double maxRateRadiansPerSecond,
        double deltaSeconds)
    {
        double difference = targetRadians - currentRadians;
        if (difference == 0d || deltaSeconds <= 0d)
        {
            return currentRadians;
        }

        double approach = Math.Abs(difference) * (1d - Math.Exp(-deltaSeconds / _settings.AimSmoothingSeconds));
        double cappedStep = Math.Min(approach, maxRateRadiansPerSecond * deltaSeconds);
        return currentRadians + (Math.Sign(difference) * cappedStep);
    }

    private static void ValidateEvaluation(OrientingEvaluation evaluation)
    {
        if (!double.IsFinite(evaluation.DeltaSeconds) || evaluation.DeltaSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(evaluation),
                evaluation.DeltaSeconds,
                "Orienting evaluation delta must be finite and non-negative.");
        }

        if (!double.IsFinite(evaluation.HorizontalErrorRadians))
        {
            throw new ArgumentOutOfRangeException(
                nameof(evaluation),
                evaluation.HorizontalErrorRadians,
                "Orienting evaluation horizontal error must be finite.");
        }

        if (!double.IsFinite(evaluation.VerticalErrorRadians))
        {
            throw new ArgumentOutOfRangeException(
                nameof(evaluation),
                evaluation.VerticalErrorRadians,
                "Orienting evaluation vertical error must be finite.");
        }

        if (!Enum.IsDefined(evaluation.AnchorState))
        {
            throw new ArgumentOutOfRangeException(
                nameof(evaluation),
                evaluation.AnchorState,
                "Orienting evaluation anchor state is not a defined value.");
        }
    }

    private enum OrientingMode
    {
        None = 0,
        Hold = 1,
        Saturation = 2,
        Centring = 3,
    }
}
