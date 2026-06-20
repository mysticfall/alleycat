namespace AlleyCat.Speech.Transcription;

/// <summary>
/// Options for OpenAI-compatible speech transcription configuration.
/// </summary>
public sealed class STTOptions
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
    /// Speech transcription model identifier.
    /// </summary>
    public string? Model
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
    /// Optional source language hint.
    /// </summary>
    public string? Language
    {
        get;
        init;
    }

    /// <summary>
    /// Optional transcription prompt.
    /// </summary>
    public string? Prompt
    {
        get;
        init;
    }

    /// <summary>
    /// Optional transcription sampling temperature.
    /// </summary>
    public float? Temperature
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
