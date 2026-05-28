namespace AlleyCat.Templating;

/// <summary>
/// Pluggable Handlebars helper/tool used by the template compiler.
/// </summary>
public interface ITemplateTool
{
    /// <summary>
    /// Helper name used inside templates.
    /// </summary>
    string Name
    {
        get;
    }

    /// <summary>
    /// Renders the helper output for the supplied positional arguments.
    /// </summary>
    /// <param name="arguments">Positional Handlebars arguments.</param>
    /// <returns>Helper output text.</returns>
    string Render(IReadOnlyList<object?> arguments);
}
