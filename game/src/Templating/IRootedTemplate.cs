namespace AlleyCat.Templating;

/// <summary>
/// Optional capability of a compiled template that renders with a caller-supplied object as the root model so its
/// members resolve as top-level template properties, while named values join the render scope alongside it.
/// </summary>
/// <remarks>
/// This is the mechanism behind AI-003's direct record rendering contract: concrete observation records pass to the
/// compiler untouched and their fragment-visible properties resolve without any renderer-side projection.
/// </remarks>
public interface IRootedTemplate : ITemplate
{
    /// <summary>
    /// Renders the template with the supplied object as the root context.
    /// </summary>
    /// <param name="root">Root model whose properties resolve at the template's top level.</param>
    /// <param name="namedValues">Named context values merged into the render scope next to the root model.</param>
    /// <returns>The rendered text.</returns>
    ValueTask<string> RenderRootedAsync(object root, IReadOnlyDictionary<string, object?> namedValues);
}
