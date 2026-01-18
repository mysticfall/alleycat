using Godot;

namespace AlleyCat.Animation.BlendShape;

[GlobalClass]
public partial class BlendShapeSet : Resource
{
    [Export] public string[] BlendShapes { get; set; } = [];
}