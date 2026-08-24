namespace AlleyCat.Templating;

/// <summary>
/// Reusable compiled template.
/// </summary>
public interface ITemplate
{
    /// <summary>
    /// Renders the template with the supplied key/value context.
    /// </summary>
    /// <param name="context">Template data keyed by template variable name.</param>
    /// <returns>The rendered text.</returns>
    ValueTask<string> RenderAsync(IReadOnlyDictionary<string, object?> context);
}
