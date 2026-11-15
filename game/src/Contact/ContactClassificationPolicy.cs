namespace AlleyCat.Contact;

/// <summary>
/// Tunable classification thresholds and windows (INTR-005 TR6 indicative semantics, TR17). Every value is
/// finite and validated before use; the values themselves are not normative. The tunables author on the
/// hosting <see cref="ContactMonitor" />'s exported classification properties, whose defaults are the shipped
/// code defaults below.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Validation.</strong> The constructor rejects non-finite or non-positive thresholds and windows, and
/// requires the settled speed threshold to sit strictly below the caress slip speed threshold — a contact
/// cannot be settled and caressing at once. Validation failures throw before any classification decision uses
/// the value; the monitor builds its policy through this constructor at <c>_Ready</c>, so malformed authored
/// tunables fail fast when the monitor enters the tree.
/// </para>
/// </remarks>
public sealed class ContactClassificationPolicy
{
    /// <summary>Creates a validated policy.</summary>
    /// <param name="impactApproachSpeedThreshold">
    /// Approach normal speed at or above which a contact begins as Impact/Hit; in m/s, positive.
    /// </param>
    /// <param name="pressDepthThreshold">
    /// Sustained penetration depth that keeps a press condition alive; in m, positive.
    /// </param>
    /// <param name="pressRateThreshold">
    /// Normal closing speed that counts as increasing pressure; in m/s, positive.
    /// </param>
    /// <param name="pressConfirmationSeconds">
    /// Continuous press condition required before Press is confirmed; positive.
    /// </param>
    /// <param name="caressSlipSpeedThreshold">
    /// Tangential slip speed at or above which motion counts as a stroke; in m/s, positive.
    /// </param>
    /// <param name="caressConfirmationSeconds">
    /// Continuous stroke condition required before an earlier Touch is revised to Caress/Stroke; positive.
    /// </param>
    /// <param name="restDelaySeconds">
    /// Continuous settled time required before contact settles into Rest/Lean; positive.
    /// </param>
    /// <param name="restSettledSpeedThreshold">
    /// Relative speed at or below which contact counts as settled; in m/s, positive and strictly below
    /// <paramref name="caressSlipSpeedThreshold" />.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when any value violates its contract.</exception>
    public ContactClassificationPolicy(
        float impactApproachSpeedThreshold = 1.5f,
        float pressDepthThreshold = 0.02f,
        float pressRateThreshold = 0.05f,
        float pressConfirmationSeconds = 0.15f,
        float caressSlipSpeedThreshold = 0.2f,
        float caressConfirmationSeconds = 0.25f,
        float restDelaySeconds = 2.0f,
        float restSettledSpeedThreshold = 0.05f)
    {
        if (!float.IsFinite(impactApproachSpeedThreshold) || impactApproachSpeedThreshold <= 0f)
        {
            throw new ArgumentException(
                $"The impact approach speed threshold must be finite and positive; got {impactApproachSpeedThreshold:R} m/s.");
        }

        if (!float.IsFinite(pressDepthThreshold) || pressDepthThreshold <= 0f)
        {
            throw new ArgumentException(
                $"The press depth threshold must be finite and positive; got {pressDepthThreshold:R} m.");
        }

        if (!float.IsFinite(pressRateThreshold) || pressRateThreshold <= 0f)
        {
            throw new ArgumentException(
                $"The press rate threshold must be finite and positive; got {pressRateThreshold:R} m/s.");
        }

        if (!float.IsFinite(pressConfirmationSeconds) || pressConfirmationSeconds <= 0f)
        {
            throw new ArgumentException(
                $"The press confirmation window must be finite and positive; got {pressConfirmationSeconds:R}s.");
        }

        if (!float.IsFinite(caressSlipSpeedThreshold) || caressSlipSpeedThreshold <= 0f)
        {
            throw new ArgumentException(
                $"The caress slip speed threshold must be finite and positive; got {caressSlipSpeedThreshold:R} m/s.");
        }

        if (!float.IsFinite(caressConfirmationSeconds) || caressConfirmationSeconds <= 0f)
        {
            throw new ArgumentException(
                $"The caress confirmation window must be finite and positive; got {caressConfirmationSeconds:R}s.");
        }

        if (!float.IsFinite(restDelaySeconds) || restDelaySeconds <= 0f)
        {
            throw new ArgumentException(
                $"The rest settled window must be finite and positive; got {restDelaySeconds:R}s.");
        }

        if (!float.IsFinite(restSettledSpeedThreshold) || restSettledSpeedThreshold <= 0f)
        {
            throw new ArgumentException(
                $"The rest settled speed threshold must be finite and positive; got {restSettledSpeedThreshold:R} m/s.");
        }

        if (restSettledSpeedThreshold >= caressSlipSpeedThreshold)
        {
            throw new ArgumentException(
                $"The rest settled speed threshold ({restSettledSpeedThreshold:R} m/s) must sit strictly below the " +
                $"caress slip speed threshold ({caressSlipSpeedThreshold:R} m/s); a contact cannot be settled and " +
                "caressing at once.");
        }

        ImpactApproachSpeedThreshold = impactApproachSpeedThreshold;
        PressDepthThreshold = pressDepthThreshold;
        PressRateThreshold = pressRateThreshold;
        PressConfirmationSeconds = pressConfirmationSeconds;
        CaressSlipSpeedThreshold = caressSlipSpeedThreshold;
        CaressConfirmationSeconds = caressConfirmationSeconds;
        RestDelaySeconds = restDelaySeconds;
        RestSettledSpeedThreshold = restSettledSpeedThreshold;
    }

    /// <summary>Approach normal speed at or above which a contact begins as Impact/Hit, in m/s.</summary>
    public float ImpactApproachSpeedThreshold
    {
        get;
    }

    /// <summary>Sustained penetration depth that keeps a press condition alive, in m.</summary>
    public float PressDepthThreshold
    {
        get;
    }

    /// <summary>Normal closing speed that counts as increasing pressure, in m/s.</summary>
    public float PressRateThreshold
    {
        get;
    }

    /// <summary>Continuous press condition required before Press is confirmed, in seconds.</summary>
    public float PressConfirmationSeconds
    {
        get;
    }

    /// <summary>Tangential slip speed at or above which motion counts as a stroke, in m/s.</summary>
    public float CaressSlipSpeedThreshold
    {
        get;
    }

    /// <summary>
    /// Continuous stroke condition required before an earlier Touch is revised to Caress/Stroke, in seconds.
    /// </summary>
    public float CaressConfirmationSeconds
    {
        get;
    }

    /// <summary>Continuous settled time required before contact settles into Rest/Lean, in seconds.</summary>
    public float RestDelaySeconds
    {
        get;
    }

    /// <summary>Relative speed at or below which contact counts as settled, in m/s.</summary>
    public float RestSettledSpeedThreshold
    {
        get;
    }

    /// <summary>The shipped default policy; every value is non-normative (INTR-005 TR17).</summary>
    public static ContactClassificationPolicy Default { get; } = new();
}
