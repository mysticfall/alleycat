using Godot;

namespace AlleyCat.Core.Content;

/// <summary>
/// Describes the default content pack used when no pack is explicitly requested.
/// </summary>
[GlobalClass]
public partial class ContentManifest : Resource
{
    /// <summary>Identifier of the default content pack under <c>res://content/</c>.</summary>
    [Export]
    public string DefaultPackId { get; set; } = string.Empty;
}
