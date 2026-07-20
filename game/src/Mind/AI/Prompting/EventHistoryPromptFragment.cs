using Godot;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Godot-authorable template fragment selected by an observation's exact semantic key.
/// </summary>
[GlobalClass]
public sealed partial class EventHistoryPromptFragment : Resource
{
    /// <summary>Exact, case-sensitive observation semantic key.</summary>
    [Export]
    public string TypeKey { get; set; } = string.Empty;

    /// <summary>Handlebars source rendered with the concrete observation as current context.</summary>
    [Export(PropertyHint.MultilineText)]
    public string Source { get; set; } = string.Empty;
}
