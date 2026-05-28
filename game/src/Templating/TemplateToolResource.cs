using Godot;

namespace AlleyCat.Templating;

/// <summary>
/// Godot-authorable template tool base resource.
/// </summary>
[Tool]
[GlobalClass]
public partial class TemplateToolResource : Resource, ITemplateTool
{
    /// <inheritdoc />
    [Export]
    public virtual string Name { get; set; } = string.Empty;

    /// <inheritdoc />
    public virtual string Render(IReadOnlyList<object?> arguments)
        => throw new NotSupportedException(
            $"Template tool resource '{GetType().FullName}' must override Render before it can be used.");
}
