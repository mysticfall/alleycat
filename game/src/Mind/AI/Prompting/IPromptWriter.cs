namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Serialises prompt sections into a single prompt source string.
/// </summary>
public interface IPromptWriter
{
    /// <summary>
    /// Writes the supplied prompt sections in their collection enumeration order.
    /// </summary>
    /// <param name="sections">Prompt sections to write.</param>
    /// <param name="buildContext">Services and scene state for prompt-section construction.</param>
    /// <param name="cancellationToken">Cancellation token for asynchronous prompt building.</param>
    /// <returns>Serialised prompt source text.</returns>
    Task<string> WriteAsync(
        IReadOnlyCollection<PromptSection> sections,
        PromptSectionBuildContext buildContext,
        CancellationToken cancellationToken = default);
}
