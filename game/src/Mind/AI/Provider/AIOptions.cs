namespace AlleyCat.Mind.AI.Provider;

/// <summary>
/// Options for OpenAI-compatible character mind configuration.
/// </summary>
public sealed class AIOptions
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
    /// Model identifier used for character mind requests.
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
    /// Optional request timeout in seconds.
    /// </summary>
    public int? Timeout
    {
        get;
        init;
    }
}
