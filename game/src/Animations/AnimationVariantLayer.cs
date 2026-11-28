using Godot;

namespace AlleyCat.Animations;

/// <summary>An ordered selection layer authored as a child of the character's catalogue.</summary>
[GlobalClass]
public partial class AnimationVariantLayer : Node
{
    /// <summary>Operations owned by this template layer, applied in array order.</summary>
    [Export]
    public AnimationVariantOperation[] Operations { get; set; } = [];
}
