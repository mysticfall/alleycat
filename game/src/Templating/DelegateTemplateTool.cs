namespace AlleyCat.Templating;

/// <summary>
/// Template tool backed by a delegate.
/// </summary>
public sealed class DelegateTemplateTool(string name, Func<IReadOnlyList<object?>, string> render) : ITemplateTool
{
    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public string Render(IReadOnlyList<object?> arguments) => render(arguments);
}
