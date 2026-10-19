using Godot;

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
        AimSmoothingSeconds: 0.07d,
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
/// The geometric feedback data for one orienting evaluation (AI-009 TR 14, HEAD-2), expressed entirely in the
/// declared envelope frame — the character-relative anatomical frame whose up axis is the roll reference. All
/// positions are frame coordinates; bases are proper orthonormal rotations.
/// </summary>
/// <param name="AnchorPosition">Gaze anchor position in envelope-frame coordinates.</param>
/// <param name="SolvedEyeOrigin">Solved eye origin in envelope-frame coordinates. This is the completed
/// evaluation k−1's solved output (attachment-published), never the current solve; before the first completed
/// solve it is the bootstrap authored pose.</param>
/// <param name="SolvedFaceBasis">Solved face rotation in envelope-frame coordinates (k−1 feedback, bootstrap
/// before the first completed solve).</param>
/// <param name="UpstreamHeadOrigin">Upstream head origin h in envelope-frame coordinates, from the immutable
/// upstream snapshot.</param>
/// <param name="PivotPosition">Upstream neck pivot position p in envelope-frame coordinates.</param>
/// <param name="UpstreamFaceBasis">Upstream face rotation U in envelope-frame coordinates.</param>
/// <param name="ViewpointOffset">Authored viewpoint (eye-centre) offset in upstream face-local coordinates.</param>
/// <param name="FoldPrediction">This evaluation's shared fold-family prediction frame in the same envelope
/// frame, when the bound rig and neutral upstream satisfy the fold domain; otherwise null. The geometric
/// solver couples its predicted eye origin to the fold construction exactly when this frame is present,
/// so the policy's commands and the controller's published origins share one geometry.</param>
public readonly record struct GeometricFeedbackSnapshot(
    Vector3 AnchorPosition,
    Vector3 SolvedEyeOrigin,
    Basis SolvedFaceBasis,
    Vector3 UpstreamHeadOrigin,
    Vector3 PivotPosition,
    Basis UpstreamFaceBasis,
    Vector3 ViewpointOffset,
    DownwardFoldPredictionFrame? FoldPrediction = null);

/// <summary>
/// One delta-driven orienting evaluation input: anchor continuity, the evaluation sequence identifying the
/// skeleton evaluation this state belongs to, and the geometric feedback snapshot. Comfort classification and
/// saturation/centring targets are computed from actual solved-face-local geometry (HEAD-2) — never by
/// subtracting commanded angles from measured ones.
/// </summary>
/// <param name="DeltaSeconds">Finite, non-negative elapsed seconds since the previous evaluation.</param>
/// <param name="AnchorState">Anchor-assignment continuity observed this frame.</param>
/// <param name="EvaluationSequence">Monotonic sequence of the skeleton evaluation this input belongs to;
/// temporal state advances at most once per distinct sequence.</param>
/// <param name="Feedback">Geometric feedback in the character envelope frame.</param>
public readonly record struct OrientingEvaluation(
    double DeltaSeconds,
    OrientingAnchorState AnchorState,
    ulong EvaluationSequence,
    GeometricFeedbackSnapshot Feedback);

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
/// Delta-driven, deterministic head-orientation decision seam for AI-009. The policy consumes the geometric
/// feedback snapshot (anchor, k−1 solved face/eye geometry and the upstream snapshot, all in the character
/// envelope frame) plus anchor-assignment continuity, and produces the desired command (yaw, pitch) in that
/// stable anatomical frame plus influence — the EyesLookMath precedent of a pure, Godot-typed seam injectable
/// into unit tests. Comfort classification and saturation/centring targets are geometric (HEAD-2): the anchor
/// direction is measured in the actual solved face frame and resolved by the bounded
/// <see cref="GeometricGazeSaturation"/> helper; per-axis subtraction of commanded angles from measured ones is
/// never used, in classification or in correction.
/// </summary>
/// <remarks>
/// <para>
/// Each evaluation resolves one aim mode by descending strength — sustained centring, saturation, glance hold,
/// then upstream release — and shares state across calls:
/// <list type="number">
/// <item><description>Sustained centring: the same anchor continuously assigned for at least
/// <see cref="OrientingSettings.CentringDelaySeconds"/> aims the head at the anchor through the geometric
/// helper's centring mode — the least command that minimises face-local target eccentricity inside the
/// envelope. The assignment frame counts zero continuous duration, matching the AI-007 dwell
/// convention.</description></item>
/// <item><description>Saturation: an anchor direction outside the comfort cone of the head's actual solved
/// eye-line — classified geometrically in the solved face frame with per-axis hysteresis — aims the head at the
/// least rotation away from the held/reference command that brings the predicted gaze direction just inside the
/// cone boundary (the helper's saturation mode), never full centring.</description></item>
/// <item><description>Glance hold (captured-command Hold): a brief in-cone assignment of a different anchor
/// keeps easing toward the command captured when the glance began (the pre-glance commanded orientation,
/// character-relative), never toward the glance anchor. An out-of-cone glance still assists through
/// saturation. Clear/re-engagement never revives a stale episode command: clearing releases toward the
/// upstream pose, and each new assignment earns its own latches.</description></item>
/// <item><description>Upstream release: no anchor eases the command toward the current upstream face pose
/// (the animation-owned orientation) while influence decays to 0 — never a solved-neutral
/// re-anchor.</description></item>
/// </list>
/// </para>
/// <para>
/// Hysteresis mechanism: saturation is evaluated per axis, on the solved-face-local anchor angles, with an
/// engage threshold at <c>cone + SaturationEngageMarginRadians</c> and a release threshold at
/// <c>cone − SaturationReleaseMarginRadians</c>. Between the thresholds the previous engaged state persists, so
/// a target hovering at the cone boundary cannot flap the aim between the residual and the held command.
/// Saturation state resets on anchor change or clear, so each assignment earns its own latch.
/// </para>
/// <para>
/// Reaction mechanism: the modes form an engagement ladder by declaration order
/// (none &lt; hold &lt; saturation &lt; centring). Whenever the resolved mode strengthens from a
/// non-tracking mode (none or hold), the aim holds still for
/// <see cref="OrientingSettings.ReactionDelaySeconds"/> before it starts toward the newly engaged
/// target, which keeps the head visibly delayed relative to eye seek. Deepening an already
/// tracking engagement — saturation climbing to centring on a continuously assigned anchor — does
/// not re-arm the hold, because the head is mid-motion on the same stimulus and freezing it there
/// reads as a stall. Downgrades and releases are not delayed, so ease back paths start immediately;
/// the influence ramp runs independently of the reaction gate.
/// </para>
/// <para>
/// Motion feel: the command moves per axis by an exponential approach towards the target capped by the per-axis
/// rate limit, so the head never overshoots; influence ramps linearly between 0 and 1 and never steps; and every
/// command is inside the per-axis orientation envelope, which is asymmetric vertically. The command lives in the
/// character envelope frame, so root motion transports it without changing its meaning; existing rate limits and
/// smoothing operate directly on these command coordinates.
/// </para>
/// <para>
/// Advancement discipline: temporal state advances at most once per distinct skeleton evaluation sequence. A
/// repeated evaluation sequence (zero-delta recomposition) returns the previous aim verbatim and ages nothing —
/// assignment, reaction, Hold, smoothing or influence state.
/// </para>
/// <para>
/// Timing resets on anchor change and on clear; both restart centring accumulation and saturation latches.
/// </para>
/// </remarks>
public sealed class OrientingPolicy(OrientingSettings settings)
{
    private readonly OrientingSettings _settings = CreateValidatedSettings(settings);

    private const double DegenerateDirectionSquared = 1e-12;

    private double _anchorElapsedSeconds;
    private double _reactionRemainingSeconds;
    private bool _horizontalSaturationEngaged;
    private bool _verticalSaturationEngaged;
    private double _heldHorizontalRadians;
    private double _heldVerticalRadians;
    private double _destinationHorizontalRadians;
    private double _destinationVerticalRadians;
    private double _aimHorizontalRadians;
    private double _aimVerticalRadians;
    private double _influence;
    private OrientingMode _mode;
    private ulong _lastEvaluationSequence;
    private bool _hasEvaluated;
    private bool _aimBootstrapped;
    private OrientingAim _lastAim;

    /// <summary>
    /// The most recent geometric solve this policy performed (saturation or centring mode), or null on
    /// Hold/None evaluations. Diagnostic only — never a behaviour input; regression oracles use it to
    /// verify the coupled prediction against the published pose.
    /// </summary>
    public GazeSaturationResult? LastSolveResult
    {
        get;
        private set;
    }

    /// <summary>
    /// Advances the policy by one skeleton evaluation and resolves the desired head command and influence from
    /// the geometric feedback. A repeated evaluation sequence returns the previous aim verbatim (zero-delta
    /// recomposition ages nothing).
    /// </summary>
    public OrientingAim Evaluate(OrientingEvaluation evaluation)
    {
        ValidateEvaluation(evaluation);
        if (_hasEvaluated && evaluation.EvaluationSequence == _lastEvaluationSequence)
        {
            return _lastAim;
        }

        _lastEvaluationSequence = evaluation.EvaluationSequence;
        _hasEvaluated = true;
        double deltaSeconds = evaluation.DeltaSeconds;

        if (!_aimBootstrapped)
        {
            // Bootstrap the command state to the current upstream pose (TR 14's marked upstream sample): the
            // command space is absolute in the envelope frame, so an un-bootstrapped zero would command
            // "face character-forward" and yank any rig whose authored rest pose is not the envelope forward.
            (double upstreamYaw, double upstreamPitch) = ExtractUpstreamCommand(evaluation.Feedback);
            _aimHorizontalRadians = ClampHorizontal(upstreamYaw);
            _aimVerticalRadians = ClampVertical(upstreamPitch);
            _heldHorizontalRadians = _aimHorizontalRadians;
            _heldVerticalRadians = _aimVerticalRadians;
            _aimBootstrapped = true;
        }

        UpdateAnchorTiming(evaluation, deltaSeconds);

        bool hasAnchor = evaluation.AnchorState != OrientingAnchorState.None;
        bool isSustained = hasAnchor && _anchorElapsedSeconds >= _settings.CentringDelaySeconds;

        // Geometric comfort classification (HEAD-2, TR 14): the anchor direction measured in the ACTUAL solved
        // face frame — the k−1 solved feedback — never the commanded-angles-subtracted scalar family this
        // replaces. Partial influence and constrained solves classify on the same actual observation.
        (double faceLocalYaw, double faceLocalPitch) = ExtractSolvedFaceLocalTarget(evaluation.Feedback);

        if (hasAnchor)
        {
            _horizontalSaturationEngaged = ResolveSaturationEngaged(
                _horizontalSaturationEngaged,
                faceLocalYaw,
                _settings.ComfortConeHorizontalRadians,
                _settings.ComfortConeHorizontalRadians);
            _verticalSaturationEngaged = ResolveSaturationEngaged(
                _verticalSaturationEngaged,
                faceLocalPitch,
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
            // The reaction hold arms when engagement begins from a non-tracking mode, keeping the
            // head visibly delayed relative to eye seek on a newly engaged target. A head that is
            // already saturation-tracking a continuously assigned anchor and deepens into
            // sustained centring is mid-motion on the same stimulus: re-arming there froze the
            // transit for the whole reaction delay at the centring boundary — the measured
            // ≥150 ms mid-turn stall — so the aim keeps moving through the deepening instead.
            if (_mode is not (OrientingMode.Saturation or OrientingMode.Centring))
            {
                _reactionRemainingSeconds = _settings.ReactionDelaySeconds;
            }
        }
        else if (_reactionRemainingSeconds > 0d)
        {
            _reactionRemainingSeconds = Math.Max(0d, _reactionRemainingSeconds - deltaSeconds);
        }

        bool reactionPending = _reactionRemainingSeconds > 0d;
        (double targetHorizontalRadians, double targetVerticalRadians) = ResolveTargetAim(mode, evaluation);
        _destinationHorizontalRadians = targetHorizontalRadians;
        _destinationVerticalRadians = targetVerticalRadians;

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
        _lastAim = new OrientingAim(
            ClampHorizontal(_aimHorizontalRadians),
            ClampVertical(_aimVerticalRadians),
            _influence);
        return _lastAim;
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
            return;
        }

        if (evaluation.AnchorState == OrientingAnchorState.NewAnchor)
        {
            // Active reassignment retains the last resolved destination, whether Centring, Saturation or Hold,
            // so unfinished easing continues rather than parking at its instantaneous output. After a clear,
            // capture the current releasing output instead: the cleared episode's destination must not revive.
            _heldHorizontalRadians = ClampHorizontal(_mode == OrientingMode.None
                ? _aimHorizontalRadians : _destinationHorizontalRadians);
            _heldVerticalRadians = ClampVertical(_mode == OrientingMode.None
                ? _aimVerticalRadians : _destinationVerticalRadians);
        }

        _anchorElapsedSeconds = 0d;
        _horizontalSaturationEngaged = false;
        _verticalSaturationEngaged = false;
    }

    /// <summary>
    /// Extracts the anchor direction's angles in the actual solved face frame (AI-009 signed forward
    /// convention, elevation form, vertical-ray guarded), the geometric comfort classification input.
    /// </summary>
    private static (double YawRadians, double PitchRadians) ExtractSolvedFaceLocalTarget(
        GeometricFeedbackSnapshot feedback)
    {
        Vector3 toTarget = feedback.AnchorPosition - feedback.SolvedEyeOrigin;
        if (toTarget.LengthSquared() <= DegenerateDirectionSquared)
        {
            return (0d, 0d);
        }

        Basis solvedFace = feedback.SolvedFaceBasis.Orthonormalized();
        Vector3 faceLocal = solvedFace.Transposed() * toTarget.Normalized();
        double horizontal = Math.Sqrt((faceLocal.X * faceLocal.X) + (faceLocal.Z * faceLocal.Z));
        if (horizontal <= double.Epsilon * 4d)
        {
            return (0d, faceLocal.Y >= 0d ? Math.PI / 2d : -Math.PI / 2d);
        }

        return (Math.Atan2(-faceLocal.X, -faceLocal.Z), Math.Atan2(faceLocal.Y, horizontal));
    }

    /// <summary>
    /// Extracts the roll-free command angles of the upstream face pose — the release target: ease toward the
    /// animation-owned orientation while influence decays (TR 12/TR 20), never a solved-neutral re-anchor.
    /// </summary>
    private static (double YawRadians, double PitchRadians) ExtractUpstreamCommand(GeometricFeedbackSnapshot feedback)
    {
        Vector3 forward = -feedback.UpstreamFaceBasis.Orthonormalized().Z;
        double pitch = Math.Asin(Math.Clamp(forward.Y, -1d, 1d));
        double horizontal = Math.Sqrt((forward.X * forward.X) + (forward.Z * forward.Z));
        double yaw = horizontal <= double.Epsilon * 4d ? 0d : Math.Atan2(-forward.X, -forward.Z);
        return (yaw, pitch);
    }

    private (double HorizontalRadians, double VerticalRadians) ResolveTargetAim(
        OrientingMode mode,
        OrientingEvaluation evaluation)
    {
        switch (mode)
        {
            case OrientingMode.None:
                return ExtractUpstreamCommand(evaluation.Feedback);

            case OrientingMode.Centring:
                {
                    GazeSaturationResult result = SolveGeometric(evaluation.Feedback, GazeSaturationMode.Centring);
                    // Authored residual eccentricity deliberately leaves the sustained aim short of full centring;
                    // with the authored default of zero the geometric centring stands unchanged.
                    double centringHorizontal = BackOffEccentricity(
                        result.YawRadians,
                        ClampHorizontal(_aimHorizontalRadians),
                        _settings.ResidualEccentricityHorizontalRadians);
                    double centringVertical = BackOffEccentricity(
                        result.PitchRadians,
                        ClampVertical(_aimVerticalRadians),
                        _settings.ResidualEccentricityVerticalRadians);
                    _heldHorizontalRadians = centringHorizontal;
                    _heldVerticalRadians = centringVertical;
                    return (centringHorizontal, centringVertical);
                }

            case OrientingMode.Saturation:
                {
                    GazeSaturationResult result = SolveGeometric(evaluation.Feedback, GazeSaturationMode.Saturation);
                    return (result.YawRadians, result.PitchRadians);
                }

            case OrientingMode.Hold:
            default:
                // Glance hold: keep easing toward the captured pre-glance command, never toward the glance
                // anchor. The helper's conservative best-effort (infeasible verdict with residual) is still an
                // envelope-valid command, so the aim simply continues toward the held command.
                return (ClampHorizontal(_heldHorizontalRadians), ClampVertical(_heldVerticalRadians));
        }
    }

    /// <summary>
    /// Runs the bounded geometric solver against the feedback snapshot with the current command as the
    /// reference (the least-change base the spec's saturation and centring objectives minimise from). The
    /// evaluation's fold frame, when present, couples the predicted eye origin to the published fold
    /// construction. A degenerate request never throws per-frame: the aim falls back to holding the
    /// current command.
    /// </summary>
    private GazeSaturationResult SolveGeometric(GeometricFeedbackSnapshot feedback, GazeSaturationMode mode)
    {
        try
        {
            GazeSaturationResult result = GeometricGazeSaturation.Solve(new GazeSaturationRequest(
                feedback.UpstreamFaceBasis.Orthonormalized(),
                feedback.UpstreamHeadOrigin,
                feedback.PivotPosition,
                feedback.ViewpointOffset,
                feedback.AnchorPosition,
                GeometricGazeSaturation.ComposeCommandBasis(
                    ClampHorizontal(_aimHorizontalRadians),
                    ClampVertical(_aimVerticalRadians)),
                new AngularBounds(
                    _settings.ComfortConeHorizontalRadians,
                    _settings.ComfortConeHorizontalRadians,
                    _settings.ComfortConeUpRadians,
                    _settings.ComfortConeDownRadians),
                new AngularBounds(
                    _settings.EnvelopeHorizontalRadians,
                    _settings.EnvelopeHorizontalRadians,
                    _settings.EnvelopeUpRadians,
                    _settings.EnvelopeDownRadians),
                mode,
                feedback.FoldPrediction));
            LastSolveResult = result;
            return result;
        }
        catch (ArgumentException)
        {
            // Degenerate geometry (anchor coincident with the predicted eye origin, or a float32-validated
            // basis outside tolerance): hold the current command for this evaluation rather than throwing in
            // the per-frame path.
            return new GazeSaturationResult(
                IsFeasible: false,
                mode,
                GeometricGazeSaturation.ComposeCommandBasis(
                    ClampHorizontal(_aimHorizontalRadians),
                    ClampVertical(_aimVerticalRadians)),
                ClampHorizontal(_aimHorizontalRadians),
                ClampVertical(_aimVerticalRadians),
                RotationFromReferenceRadians: 0d,
                ResidualRadians: double.NaN,
                FaceLocalTargetYawRadians: 0d,
                FaceLocalTargetPitchRadians: 0d,
                PredictedEyeOrigin: feedback.SolvedEyeOrigin,
                Iterations: 0);
        }
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

    /// <summary>
    /// Eases a centred command back toward the reference by the authored residual eccentricity without ever
    /// overshooting past the reference command.
    /// </summary>
    private static double BackOffEccentricity(double centredRadians, double referenceRadians, double eccentricityRadians)
    {
        if (eccentricityRadians <= 0d || centredRadians == referenceRadians)
        {
            return centredRadians;
        }

        double sign = Math.Sign(centredRadians - referenceRadians);
        double backedOff = centredRadians - (sign * eccentricityRadians);
        return sign > 0 ? Math.Max(backedOff, referenceRadians) : Math.Min(backedOff, referenceRadians);
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
