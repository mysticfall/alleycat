using Godot;

namespace AlleyCat.Common;

[GlobalClass, Tool]
public partial class VectorRange2 : Resource
{
    [Export] public Vector2 Min { get; set; }

    [Export] public Vector2 Max { get; set; }

    public VectorRange2()
    {
    }

    public VectorRange2(Vector2 min, Vector2 max)
    {
        Min = min;
        Max = max;
    }

    public Vector2 Clamp(Vector2 vector) => vector.Clamp(Min, Max);
}