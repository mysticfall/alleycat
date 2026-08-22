namespace AlleyCat.Speech.Generation.Supertonic;

/// <summary>
/// Outcome of resolving a requested Supertonic voice style to a concrete JSON file.
/// </summary>
/// <param name="RequestedName">Voice style name derived from the configured override or default voice.</param>
/// <param name="ResolvedName">Voice style name whose file should be loaded.</param>
/// <param name="FilePath">Absolute candidate path of the resolved voice-style JSON file.</param>
public sealed record SupertonicVoiceStyleResolution(
    string RequestedName,
    string ResolvedName,
    string FilePath)
{
    /// <summary>
    /// Gets a value indicating whether the requested style was unavailable and a fallback was applied.
    /// </summary>
    public bool UsedFallback => !string.Equals(RequestedName, ResolvedName, StringComparison.Ordinal);
}

/// <summary>
/// Resolves requested voice-style names onto preset <c>voice_styles</c> JSON files with warn-and-fallback semantics.
/// </summary>
public static class SupertonicVoiceStyleResolver
{
    /// <summary>
    /// Default preset used when no voice is configured or the requested style cannot be resolved.
    /// </summary>
    public const string FallbackVoiceName = "M1";

    private const string StyleFileExtension = ".json";

    /// <summary>
    /// Determines the effective voice style name using the override > voice > fallback precedence.
    /// </summary>
    /// <param name="voiceOverride">Optional per-request voice override.</param>
    /// <param name="voice">Configured default voice.</param>
    /// <returns>The trimmed effective voice name, never empty.</returns>
    public static string ResolveEffectiveName(string? voiceOverride, string voice)
        => Clean(voiceOverride) ?? Clean(voice) ?? FallbackVoiceName;

    /// <summary>
    /// Resolves the requested voice style to its file path, falling back to
    /// <see cref="FallbackVoiceName" /> when the requested style has no matching file.
    /// </summary>
    /// <param name="voiceStylesDirectory">Absolute directory containing the preset voice-style JSON files.</param>
    /// <param name="voiceOverride">Optional per-request voice override.</param>
    /// <param name="voice">Configured default voice.</param>
    /// <returns>The resolution outcome including the candidate file path.</returns>
    public static SupertonicVoiceStyleResolution Resolve(string voiceStylesDirectory, string? voiceOverride, string voice)
    {
        ArgumentNullException.ThrowIfNull(voiceStylesDirectory);

        string requestedName = ResolveEffectiveName(voiceOverride, voice);
        string requestedPath = Path.Combine(voiceStylesDirectory, requestedName + StyleFileExtension);

        return File.Exists(requestedPath)
            ? new SupertonicVoiceStyleResolution(requestedName, requestedName, requestedPath)
            : new SupertonicVoiceStyleResolution(
                requestedName,
                FallbackVoiceName,
                Path.Combine(voiceStylesDirectory, FallbackVoiceName + StyleFileExtension));
    }

    private static string? Clean(string? value)
    {
        string? text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
