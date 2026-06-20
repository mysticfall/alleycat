namespace AlleyCat.Speech.Generation;

/// <summary>
/// Options for OpenAI-compatible speech generation configuration.
/// </summary>
public sealed class TTSOptions
{
    /// <summary>
    /// OpenAI-compatible API base URL.
    /// </summary>
    public string? Host
    {
        get;
        init;
    }

    /// <summary>
    /// Speech generation model identifier.
    /// </summary>
    public string? Model
    {
        get;
        init;
    }

    /// <summary>
    /// Voice identifier requested from the backend.
    /// </summary>
    public string? Voice
    {
        get;
        init;
    }

    /// <summary>
    /// Optional API key for the configured endpoint.
    /// </summary>
    public string? ApiKey
    {
        get;
        init;
    }

    /// <summary>
    /// Requested audio format.
    /// </summary>
    public string? Format
    {
        get;
        init;
    }

    /// <summary>
    /// Optional speech speed multiplier.
    /// </summary>
    public float? SpeedRatio
    {
        get;
        init;
    }

    /// <summary>
    /// Optional request timeout in seconds.
    /// </summary>
    public int? Timeout
    {
        get;
        init;
    }
}
