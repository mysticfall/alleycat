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

    /// <summary>
    /// Optional reasoning-effort request control: <c>none</c>, <c>low</c>, <c>medium</c>, <c>high</c>, or <c>xhigh</c>.
    /// </summary>
    /// <remarks>
    /// Parsed case-insensitively after trimming; blank values behave as unset. This is a forwarded provider request
    /// parameter, not a guarantee of reasoning behaviour: backend and model support varies.
    /// </remarks>
    public string? ReasoningEffort
    {
        get;
        init;
    }

    /// <summary>
    /// Optional reasoning-summary detail: <c>auto</c>, <c>concise</c>, or <c>detailed</c>.
    /// </summary>
    /// <remarks>
    /// Parsed case-insensitively after trimming; blank values behave as unset. This requests the detail level of
    /// the reasoning summary the provider may return alongside its answer — it never controls answer length, and
    /// the Responses endpoint alone supports it. Providers are not guaranteed to return summaries at all.
    /// </remarks>
    public string? ReasoningVerbosity
    {
        get;
        init;
    }
}
