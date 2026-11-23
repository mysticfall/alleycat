namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// The named walking paces of the coordinate navigation tools (AI-015 TR-6) and their NAV-001 UR4 normalised
/// speeds. The model never chooses a raw speed: it names a pace, and an unknown name is refused.
/// </summary>
internal static class NavigationPace
{
    /// <summary>Model-facing name of the slow pace.</summary>
    public const string Stroll = "stroll";

    /// <summary>Model-facing name of the default pace.</summary>
    public const string Walk = "walk";

    /// <summary>Model-facing name of the fast pace.</summary>
    public const string Hurry = "hurry";

    /// <summary>The pace applied when the model names none (AI-015 TR-6: fixed).</summary>
    public const string Default = Walk;

    /// <summary>Documented tuning constant: the normalised speed of <see cref="Stroll" />.</summary>
    public const float StrollNormalisedSpeed = 0.5f;

    /// <summary>Documented tuning constant: the normalised speed of <see cref="Walk" />.</summary>
    public const float WalkNormalisedSpeed = 0.75f;

    /// <summary>Documented tuning constant: the normalised speed of <see cref="Hurry" />.</summary>
    public const float HurryNormalisedSpeed = 1.0f;

    /// <summary>
    /// Resolves an optional model-supplied pace name — trimmed, case-insensitive — to its canonical name and
    /// normalised speed. A null or blank name resolves to <see cref="Default" />.
    /// </summary>
    /// <returns><see langword="false" /> when the name is not one of the three paces.</returns>
    public static bool TryResolve(string? name, out string canonicalName, out float normalisedSpeed)
    {
        string candidate = string.IsNullOrWhiteSpace(name) ? Default : name.Trim().ToLowerInvariant();
        (canonicalName, normalisedSpeed) = candidate switch
        {
            Stroll => (Stroll, StrollNormalisedSpeed),
            Walk => (Walk, WalkNormalisedSpeed),
            Hurry => (Hurry, HurryNormalisedSpeed),
            _ => (string.Empty, float.NaN),
        };
        return canonicalName.Length != 0;
    }
}
