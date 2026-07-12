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
    /// <returns>Serialised prompt source text.</returns>
    string Write(IReadOnlyCollection<PromptSection> sections);
}
