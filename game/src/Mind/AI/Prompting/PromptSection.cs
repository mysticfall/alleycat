using Godot;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Base Godot-authorable resource for a named prompt section.
/// </summary>
[GlobalClass]
public abstract partial class PromptSection : Resource
{
    /// <summary>
    /// Human-readable section name used to derive the generated prompt section tag.
    /// </summary>
    [Export]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Returns the section content contributed to a prompt stack.
    /// </summary>
    /// <returns>The prompt text for this section.</returns>
    public abstract string GetContent();
}
