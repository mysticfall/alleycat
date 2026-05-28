namespace AlleyCat.Templating;

/// <summary>
/// Compiles template source into reusable templates.
/// </summary>
public interface ITemplateCompiler
{
    /// <summary>
    /// Compiles the supplied template source.
    /// </summary>
    /// <param name="source">Template source text.</param>
    /// <returns>A reusable compiled template.</returns>
    ITemplate Compile(string source);
}
