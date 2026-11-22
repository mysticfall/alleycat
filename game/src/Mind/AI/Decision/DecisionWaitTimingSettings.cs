using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Mind.AI.Decision;

/// <summary>Which deferred wait verdict armed a deadline; each maps to its own configurable timeout.</summary>
internal enum DecisionWaitArm
{
    /// <summary>The subject's speech or action expects a response that has not arrived yet.</summary>
    ResponsePending,

    /// <summary>Nobody waits on the subject, but the scene offers an opening to act.</summary>
    MayAct,

    /// <summary>The subject has no particular reason to act.</summary>
    NoReason,
}

/// <summary>
/// The <c>DecisionWait</c> configuration section: the wait gate's deadline timeouts in seconds per deferred
/// verdict, plus the proportional jitter applied to each armed deadline. Every key is optional; an absent key keeps
/// its default.
/// </summary>
public sealed class DecisionWaitOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "DecisionWait";

    /// <summary>Seconds before the agent starts when its own speech or action still awaits a response.</summary>
    public double? ResponsePendingTimeout
    {
        get; init;
    }

    /// <summary>Seconds before the agent starts when the scene offers it an opening to act.</summary>
    public double? MayActTimeout
    {
        get; init;
    }

    /// <summary>Seconds before the agent starts when it has no particular reason to act.</summary>
    public double? NoReasonTimeout
    {
        get; init;
    }

    /// <summary>Proportional jitter applied to every armed timeout, for example <c>0.2</c> for ±20%.</summary>
    public double? TimeoutJitter
    {
        get; init;
    }

    /// <summary>Reads only <c>DecisionWait</c> keys from the merged shared configuration.</summary>
    public static DecisionWaitOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(SectionName);
        return new DecisionWaitOptions
        {
            ResponsePendingTimeout = Parse(section, nameof(ResponsePendingTimeout)),
            MayActTimeout = Parse(section, nameof(MayActTimeout)),
            NoReasonTimeout = Parse(section, nameof(NoReasonTimeout)),
            TimeoutJitter = Parse(section, nameof(TimeoutJitter)),
        };
    }

    internal DecisionWaitTimingSettings Validate()
    {
        DecisionWaitTimingSettings defaults = DecisionWaitTimingSettings.Default;
        return new DecisionWaitTimingSettings(
            ValidateTimeout(ResponsePendingTimeout ?? defaults.ResponsePendingSeconds, nameof(ResponsePendingTimeout)),
            ValidateTimeout(MayActTimeout ?? defaults.MayActSeconds, nameof(MayActTimeout)),
            ValidateTimeout(NoReasonTimeout ?? defaults.NoReasonSeconds, nameof(NoReasonTimeout)),
            ValidateJitter(TimeoutJitter ?? defaults.JitterRatio));
    }

    private static double? Parse(IConfigurationSection section, string key)
    {
        return section[key] is not string raw
            ? null
            : double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : throw Invalid(key);
    }

    private static double ValidateTimeout(double seconds, string key)
        => double.IsFinite(seconds) && seconds > 0d && seconds <= DecisionWaitTimingSettings.MaximumTimeoutSeconds
            ? seconds
            : throw Invalid(key);

    private static double ValidateJitter(double ratio)
        => double.IsFinite(ratio) && ratio >= 0d && ratio <= DecisionWaitTimingSettings.MaximumJitterRatio
            ? ratio
            : throw Invalid(nameof(TimeoutJitter));

    private static DecisionException Invalid(string key)
        => new(DecisionFailure.Configuration, $"Invalid {SectionName}:{key}.");
}

/// <summary>
/// Resolved wait-gate timing: the deadline each deferred verdict arms, and the proportional jitter that keeps
/// several waiting characters from waking in lockstep.
/// </summary>
/// <param name="ResponsePendingSeconds">Timeout armed while the subject's speech or action awaits a response.</param>
/// <param name="MayActSeconds">Timeout armed when the scene offers the subject an opening.</param>
/// <param name="NoReasonSeconds">Timeout armed when the subject has no particular reason to act.</param>
/// <param name="JitterRatio">Proportional jitter in [0, <see cref="MaximumJitterRatio" />].</param>
internal sealed record DecisionWaitTimingSettings(
    double ResponsePendingSeconds,
    double MayActSeconds,
    double NoReasonSeconds,
    double JitterRatio)
{
    /// <summary>Upper bound for any configured timeout, in seconds.</summary>
    internal const double MaximumTimeoutSeconds = 3600d;

    /// <summary>Upper bound for the proportional jitter, so every jittered timeout stays positive.</summary>
    internal const double MaximumJitterRatio = 0.9d;

    /// <summary>The shipped defaults: 5 s pending, 3 s opening, 10 s no reason, ±20% jitter.</summary>
    public static DecisionWaitTimingSettings Default { get; } = new(5d, 3d, 10d, 0.2d);

    /// <summary>The configured base timeout for one deferred verdict, before jitter.</summary>
    public double BaseSeconds(DecisionWaitArm arm) => arm switch
    {
        DecisionWaitArm.ResponsePending => ResponsePendingSeconds,
        DecisionWaitArm.MayAct => MayActSeconds,
        DecisionWaitArm.NoReason => NoReasonSeconds,
        _ => throw new ArgumentOutOfRangeException(nameof(arm), arm, "Unknown wait arm."),
    };

    /// <summary>
    /// The jittered timeout for one deferred verdict: the base scaled uniformly within ±<see cref="JitterRatio" />
    /// by a unit sample in [0, 1).
    /// </summary>
    public double SampleSeconds(DecisionWaitArm arm, double unitSample)
    {
        double clamped = Math.Clamp(unitSample, 0d, 1d);
        return BaseSeconds(arm) * (1d + (JitterRatio * ((2d * clamped) - 1d)));
    }

    /// <summary>Loads the timing from the <c>DecisionWait</c> section; invalid values fail with a configuration error.</summary>
    public static DecisionWaitTimingSettings Load(IConfiguration configuration)
        => DecisionWaitOptions.FromConfiguration(configuration).Validate();

    /// <summary>
    /// Loads the timing from the running game's configuration, or the defaults when no game configuration is
    /// available (isolated runtimes). Invalid configured values still fail with a configuration error.
    /// </summary>
    public static DecisionWaitTimingSettings LoadOrDefault()
    {
        IConfiguration configuration;
        try
        {
            configuration = Game.Instance.GetRequiredService<IConfiguration>();
        }
        catch (InvalidOperationException)
        {
            return Default;
        }

        return Load(configuration);
    }
}
