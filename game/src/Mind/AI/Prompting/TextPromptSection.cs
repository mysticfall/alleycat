using Godot;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Inline text prompt section authored directly in the Godot editor.
/// </summary>
[GlobalClass]
public partial class TextPromptSection : PromptSection
{
    /// <summary>
    /// Inline prompt text for this section.
    /// </summary>
    [Export(PropertyHint.MultilineText)]
    public string Text { get; set; } = string.Empty;

    /// <inheritdoc />
    public override string GetContent() => Text ?? string.Empty;
}
